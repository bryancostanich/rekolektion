module Rekolektion.Viz.App.Services.ScreenshotListener

// Despite the file/module name, this listener now dispatches on
// HTTP method + path:
//   GET  ...           -> render PNG screenshot of the live window
//   POST <path>        -> delegate to `CommandListener.handle` for
//                         agent-driven Msg dispatch
//   anything else      -> 404
//
// The module is kept named `ScreenshotListener` because it still
// owns the UDS bind/accept loop on `~/.rekolektion/viz.sock`;
// CommandListener is a pure dispatcher that doesn't touch sockets.

open System
open System.IO
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Media.Imaging
open Avalonia.Threading
open Rekolektion.Viz.App.Model

/// Render the top-level window's current Visual tree into a PNG
/// byte array. Uses Avalonia's `RenderTargetBitmap` — same pixels
/// the compositor would paint, so screenshots reflect the Elmish
/// model's current render output (not a replay of source data).
///
/// Must run on the UI thread — callers marshal via
/// `Dispatcher.UIThread.InvokeAsync`.
let private renderWindowPng (window: TopLevel) : byte[] =
    // PixelSize is the *rendered* size including DPI scale. We keep
    // the physical size here so screenshots match what the user
    // sees on their screen rather than a logical-DPI downsample.
    let width  = max 1 (int window.Bounds.Width)
    let height = max 1 (int window.Bounds.Height)
    let px = PixelSize(width, height)
    // 96 DPI for a 1:1 logical-to-physical ratio. If the window's
    // actual render scale ends up different, the bitmap is still
    // the correct pixel count — DPI just affects the metadata.
    let dpi = Vector(96.0, 96.0)
    use rtb = new RenderTargetBitmap(px, dpi)
    rtb.Render(window)
    use ms = new MemoryStream()
    rtb.Save ms
    ms.ToArray()

/// Minimal HTTP/1.1 response helper. Builds the headers + body
/// into a single byte payload we blat onto the socket in one
/// write.
let private httpResponse (status: string) (contentType: string) (body: byte[]) : byte[] =
    let header =
        sprintf "HTTP/1.1 %s\r\nContent-Type: %s\r\nContent-Length: %d\r\nConnection: close\r\n\r\n"
            status contentType body.Length
    let headerBytes = Encoding.ASCII.GetBytes header
    Array.append headerBytes body

/// Parse the very first line of a raw HTTP request: `METHOD PATH HTTP/1.1`.
/// Returns `(method, path)` — query string is preserved verbatim, we don't
/// split it because the listener's only path consumers are exact-string
/// matches in `CommandListener.handle`.
let private parseRequestLine (raw: string) : (string * string) option =
    let firstLine =
        raw.Split([| "\r\n" |], 2, StringSplitOptions.None) |> Array.head
    let parts = firstLine.Split(' ')
    if parts.Length >= 2 then Some (parts.[0], parts.[1]) else None

/// Find a `Content-Length: N` header in the raw header section
/// (case-insensitive, trims whitespace). Used to size the body
/// drain for POST requests.
let private parseContentLength (headerSection: string) : int =
    let lines = headerSection.Split([| "\r\n" |], StringSplitOptions.None)
    let mutable result = 0
    for line in lines do
        let idx = line.IndexOf(':')
        if idx > 0 then
            let name = line.Substring(0, idx).Trim()
            if String.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) then
                let value = line.Substring(idx + 1).Trim()
                match Int32.TryParse value with
                | true, n -> result <- n
                | _ -> ()
    result

/// Read the HTTP request from the socket: drain headers until we
/// see `\r\n\r\n`, then read `Content-Length` more bytes for the
/// body (POST). Returns `(headerText, body)` where `headerText`
/// is the raw header section and `body` is the post-terminator
/// payload as a string (UTF-8). For GET, body is "".
let private drainRequest
        (stream: NetworkStream)
        (ct: CancellationToken)
        : Async<string * string> = async {
    let buffer = Array.zeroCreate 4096
    let sb = StringBuilder()
    let mutable keepGoing = true
    let mutable terminatorIdx = -1
    while keepGoing && not ct.IsCancellationRequested do
        let! n =
            stream.ReadAsync(buffer, 0, buffer.Length, ct)
            |> Async.AwaitTask
        if n <= 0 then keepGoing <- false
        else
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n)) |> ignore
            let s = sb.ToString()
            let idx = s.IndexOf("\r\n\r\n")
            if idx >= 0 then
                terminatorIdx <- idx
                keepGoing <- false
    let raw = sb.ToString()
    if terminatorIdx < 0 then
        // Malformed / truncated — treat the whole thing as headers,
        // empty body. The dispatcher will likely 404 or fail to parse.
        return raw, ""
    else
        let headerText = raw.Substring(0, terminatorIdx)
        let alreadyHaveBody = raw.Substring(terminatorIdx + 4)
        let contentLen = parseContentLength headerText
        let bodyBuilder = StringBuilder(alreadyHaveBody)
        let mutable remaining = contentLen - alreadyHaveBody.Length
        while remaining > 0 && not ct.IsCancellationRequested do
            let! n =
                stream.ReadAsync(buffer, 0, min buffer.Length remaining, ct)
                |> Async.AwaitTask
            if n <= 0 then
                remaining <- 0
            else
                bodyBuilder.Append(Encoding.ASCII.GetString(buffer, 0, n)) |> ignore
                remaining <- remaining - n
        return headerText, bodyBuilder.ToString()
}

/// Render a screenshot on the UI thread and wrap into an HTTP/1.1
/// `200 image/png` response — or `500 text/plain` if the window
/// isn't ready yet / RenderTargetBitmap throws.
let private handleScreenshot
        (windowProvider: unit -> TopLevel option)
        : Async<byte[]> = async {
    try
        let tcs = TaskCompletionSource<Result<byte[], string>>()
        Dispatcher.UIThread.InvokeAsync(fun () ->
            try
                match windowProvider () with
                | Some w -> tcs.SetResult(Ok (renderWindowPng w))
                | None   -> tcs.SetResult(Error "no MainWindow (Viz not yet initialized)")
            with ex ->
                tcs.SetResult(Error ex.Message))
        |> ignore
        let! result = tcs.Task |> Async.AwaitTask
        match result with
        | Ok png ->
            return httpResponse "200 OK" "image/png" png
        | Error msg ->
            let body = Encoding.UTF8.GetBytes("screenshot failed: " + msg)
            return httpResponse "500 Internal Server Error" "text/plain; charset=utf-8" body
    with ex ->
        let body = Encoding.UTF8.GetBytes("screenshot failed: " + ex.Message)
        return httpResponse "500 Internal Server Error" "text/plain; charset=utf-8" body
}

/// Handle one accepted client: drain the request, dispatch by
/// method+path (GET=screenshot, POST=command, else=404), send
/// HTTP/1.1 response.
let private handleClient
        (windowProvider: unit -> TopLevel option)
        (dispatch: Msg.Msg -> unit)
        (client: Socket)
        (ct: CancellationToken)
        : Async<unit> = async {
    try
        use stream = new NetworkStream(client, ownsSocket = true)
        let! headerText, body = drainRequest stream ct
        let methodAndPath = parseRequestLine headerText
        let! response =
            async {
                match methodAndPath with
                | Some (m, _) when m.Equals("GET", StringComparison.OrdinalIgnoreCase) ->
                    return! handleScreenshot windowProvider
                | Some (m, p) when m.Equals("POST", StringComparison.OrdinalIgnoreCase) ->
                    let respBody = CommandListener.handle p body dispatch
                    let bytes = Encoding.UTF8.GetBytes respBody
                    return httpResponse "200 OK" "application/json; charset=utf-8" bytes
                | _ ->
                    let bytes = Encoding.UTF8.GetBytes "not found"
                    return httpResponse "404 Not Found" "text/plain; charset=utf-8" bytes
            }
        do! stream.WriteAsync(response, 0, response.Length, ct) |> Async.AwaitTask
        stream.Flush()
    with _ -> ()          // Client dropped the connection mid-write; nothing to do.
}

/// Bind to the Viz's unix domain socket and accept connections
/// on a background task, serving screenshot + command requests
/// until `cancel` fires.
///
/// Returns an `IDisposable` that closes the socket and cancels
/// the accept loop.
let start
        (socketPath: string)
        (windowProvider: unit -> TopLevel option)
        (dispatch: Msg.Msg -> unit)
        : IDisposable =
    // Remove any stale socket file from a previous launch that
    // didn't clean up (crash, kill -9, etc). bind() fails with
    // EADDRINUSE on a leftover file even if no process is listening.
    if File.Exists socketPath then
        try File.Delete socketPath with _ -> ()

    let listener =
        new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
    listener.Bind(UnixDomainSocketEndPoint socketPath)
    listener.Listen 8

    let cts = new CancellationTokenSource()
    let ct  = cts.Token

    let acceptLoop () = async {
        while not ct.IsCancellationRequested do
            try
                let! client =
                    listener.AcceptAsync ct
                    |> fun vt -> vt.AsTask() |> Async.AwaitTask
                // Fire-and-forget each client. Concurrent screenshots
                // are rare but would serialize naturally on the UI
                // thread via Dispatcher.InvokeAsync.
                Async.Start(handleClient windowProvider dispatch client ct, ct)
            with
            | :? OperationCanceledException -> ()
            | _ -> ()                          // Keep accepting after transient socket errors.
    }
    Async.Start(acceptLoop (), ct)

    { new IDisposable with
        member _.Dispose() =
            cts.Cancel()
            try listener.Close() with _ -> ()
            try File.Delete socketPath with _ -> ()
            cts.Dispose() }
