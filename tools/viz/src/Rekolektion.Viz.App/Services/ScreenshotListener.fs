module Rekolektion.Viz.App.Services.ScreenshotListener

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

/// Read the HTTP request line + headers from the socket until we
/// see the blank line terminator (\r\n\r\n). We don't actually
/// parse anything — the listener has one endpoint, any GET to any
/// path is interpreted as a screenshot request. We read until the
/// terminator so the client-side write completes cleanly before
/// we respond.
let private drainRequest (stream: NetworkStream) (ct: CancellationToken) : Async<string> = async {
    let buffer = Array.zeroCreate 4096
    let sb = StringBuilder()
    let mutable keepGoing = true
    while keepGoing && not ct.IsCancellationRequested do
        let! n =
            stream.ReadAsync(buffer, 0, buffer.Length, ct)
            |> Async.AwaitTask
        if n <= 0 then keepGoing <- false
        else
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n)) |> ignore
            if sb.ToString().Contains "\r\n\r\n" then keepGoing <- false
    return sb.ToString()
}

/// Handle one accepted client: drain the request, render a PNG on
/// the UI thread, send HTTP/1.1 response. Errors in rendering are
/// surfaced to the caller as HTTP 500 with a plain-text body so
/// `curl` / the MCP tool can see the failure reason instead of a
/// truncated connection.
let private handleClient
        (windowProvider: unit -> TopLevel option)
        (client: Socket)
        (ct: CancellationToken)
        : Async<unit> = async {
    try
        use stream = new NetworkStream(client, ownsSocket = true)
        let! _requestText = drainRequest stream ct
        // Render on UI thread. `InvokeAsync` returns `Task<'T>`;
        // awaiting it from our thread-pool handler is safe because
        // the Avalonia dispatcher is already running its loop.
        let! response =
            async {
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
                    | Ok png    -> return httpResponse "200 OK" "image/png" png
                    | Error msg ->
                        let body = Encoding.UTF8.GetBytes("screenshot failed: " + msg)
                        return httpResponse "500 Internal Server Error" "text/plain; charset=utf-8" body
                with ex ->
                    let body = Encoding.UTF8.GetBytes("screenshot failed: " + ex.Message)
                    return httpResponse "500 Internal Server Error" "text/plain; charset=utf-8" body
            }
        do! stream.WriteAsync(response, 0, response.Length, ct) |> Async.AwaitTask
        stream.Flush()
    with _ -> ()          // Client dropped the connection mid-write; nothing to do.
}

/// Bind to the Viz's unix domain socket and accept connections
/// on a background task, serving screenshot requests until
/// `cancel` fires. The listener is intentionally single-purpose:
/// one connection = one screenshot. No routing — the GET path is
/// ignored.
///
/// Returns an `IDisposable` that closes the socket and cancels
/// the accept loop.
let start
        (socketPath: string)
        (windowProvider: unit -> TopLevel option)
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
                Async.Start(handleClient windowProvider client ct, ct)
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
