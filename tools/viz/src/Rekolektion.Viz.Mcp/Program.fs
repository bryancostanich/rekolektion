/// MCP (Model Context Protocol) stdio JSON-RPC 2.0 server for the
/// rekolektion-viz toolkit. Reads one JSON-RPC request per line on
/// stdin, writes one response per line on stdout. Implements the
/// MCP handshake (initialize, tools/list, tools/call) plus the
/// notifications that don't need a response.
///
/// Exposes 7 tools to AI agents (Claude Code, Codex, etc.):
///   - rekolektion_viz_screenshot       (UDS GET /screenshot, PNG)
///   - rekolektion_viz_open             (UDS POST /open)
///   - rekolektion_viz_toggle_layer     (UDS POST /toggle/layer)
///   - rekolektion_viz_highlight_net    (UDS POST /highlight/net)
///   - rekolektion_viz_set_tab          (UDS POST /tab)
///   - rekolektion_viz_render           (spawn rekolektion-viz viz-render)
///   - rekolektion_viz_run_macro        (spawn rekolektion macro CLI)
///
/// Six tools speak HTTP/1.1 over a Unix Domain Socket at
/// `~/.rekolektion/viz.sock` to the live rekolektion-viz desktop
/// app. Two spawn subprocesses (the headless viz-render and the
/// Python rekolektion macro generator).
module Rekolektion.Viz.Mcp.Program

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Text
open System.Text.Json

/// A tool's return payload. JSON-shaped responses use `TextResult`;
/// binary image responses use `ImageResult` with the raw bytes +
/// mime type so the dispatcher can serialize them into MCP's
/// `content: [{ type: "image", data: <base64>, mimeType }]` shape.
type ToolResult =
    | TextResult  of json: string
    | ImageResult of mimeType: string * data: byte[]

// ---------------------------------------------------------------
// Viz UDS path + helpers
// ---------------------------------------------------------------

/// Absolute path to the live viz desktop app's Unix domain socket.
/// The App's `ScreenshotListener` binds here at startup.
let private vizSocket : string =
    let dir = Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                ".rekolektion")
    Path.Combine(dir, "viz.sock")

/// Send an HTTP/1.1 request over the viz UDS and return the
/// response body as raw bytes (binary-safe). Caller already knows
/// whether the response is text (POST commands return JSON) or
/// binary (GET /screenshot returns PNG bytes), so we don't try to
/// guess the encoding here. Critical: PNG bytes are NOT valid
/// UTF-8 — we must scan for the `\r\n\r\n` header terminator at
/// the byte level and never round-trip the body through a string.
let private udsRequest
        (method: string)
        (path: string)
        (body: string option)
        : byte[] =
    use sock =
        new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
    sock.Connect(UnixDomainSocketEndPoint vizSocket)
    use stream = new NetworkStream(sock, ownsSocket = false)
    let bodyBytes =
        body |> Option.map Encoding.UTF8.GetBytes |> Option.defaultValue [||]
    let header =
        sprintf
            "%s %s HTTP/1.1\r\nHost: viz\r\nContent-Length: %d\r\nConnection: close\r\n\r\n"
            method path bodyBytes.Length
    let headerBytes = Encoding.ASCII.GetBytes header
    stream.Write(headerBytes, 0, headerBytes.Length)
    if bodyBytes.Length > 0 then
        stream.Write(bodyBytes, 0, bodyBytes.Length)
    use ms = new MemoryStream()
    stream.CopyTo ms
    let raw = ms.ToArray()
    // Find \r\n\r\n by byte scan — DO NOT go through a string.
    let mutable idx = -1
    let mutable i = 0
    while idx < 0 && i <= raw.Length - 4 do
        if raw.[i] = 13uy && raw.[i+1] = 10uy
           && raw.[i+2] = 13uy && raw.[i+3] = 10uy then
            idx <- i
        i <- i + 1
    if idx < 0 then raw
    else raw.[idx + 4 ..]

/// Tiny JSON string escaper for the small bodies we POST. Avoids
/// pulling in JsonSerializer for one-off payloads.
let private jsonEscape (s: string) : string =
    let sb = StringBuilder(s.Length + 2)
    for c in s do
        match c with
        | '"'  -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n"  |> ignore
        | '\r' -> sb.Append "\\r"  |> ignore
        | '\t' -> sb.Append "\\t"  |> ignore
        | c when int c < 0x20 ->
            sb.AppendFormat("\\u{0:x4}", int c) |> ignore
        | c -> sb.Append c |> ignore
    sb.ToString()

/// Wrap any tool exception into a TextResult carrying a structured
/// error payload — the JSON-RPC call still succeeds, but the
/// agent sees `{"ok":false,"error":"..."}` and can react.
let private toolError (msg: string) : ToolResult =
    TextResult (sprintf "{\"ok\":false,\"error\":\"%s\"}" (jsonEscape msg))

// ---------------------------------------------------------------
// Tool handlers (7)
// ---------------------------------------------------------------

/// Tool: rekolektion_viz_screenshot — GET /screenshot on the live
/// viz UDS, returns a PNG. No arguments.
let private toolScreenshot (_args: JsonElement) : ToolResult =
    try
        let bytes = udsRequest "GET" "/screenshot" None
        ImageResult ("image/png", bytes)
    with ex ->
        toolError (sprintf "screenshot failed: %s" ex.Message)

/// Tool: rekolektion_viz_open { path } — POST /open with the GDS
/// path in JSON body.
let private toolOpen (args: JsonElement) : ToolResult =
    try
        let path = args.GetProperty("path").GetString()
        let body = sprintf "{\"path\":\"%s\"}" (jsonEscape path)
        let resp = udsRequest "POST" "/open" (Some body)
        TextResult (Encoding.UTF8.GetString resp)
    with ex ->
        toolError (sprintf "open failed: %s" ex.Message)

/// Tool: rekolektion_viz_toggle_layer { name, visible } —
/// POST /toggle/layer with { name, visible }.
let private toolToggleLayer (args: JsonElement) : ToolResult =
    try
        let name    = args.GetProperty("name").GetString()
        let visible = args.GetProperty("visible").GetBoolean()
        let body =
            sprintf "{\"name\":\"%s\",\"visible\":%s}"
                (jsonEscape name)
                (if visible then "true" else "false")
        let resp = udsRequest "POST" "/toggle/layer" (Some body)
        TextResult (Encoding.UTF8.GetString resp)
    with ex ->
        toolError (sprintf "toggle_layer failed: %s" ex.Message)

/// Tool: rekolektion_viz_highlight_net { name } — POST
/// /highlight/net. `name` may be a string OR null (null clears
/// the current highlight).
let private toolHighlightNet (args: JsonElement) : ToolResult =
    try
        let nameElem = args.GetProperty "name"
        let body =
            match nameElem.ValueKind with
            | JsonValueKind.Null   -> "{\"name\":null}"
            | JsonValueKind.String ->
                sprintf "{\"name\":\"%s\"}" (jsonEscape (nameElem.GetString()))
            | k ->
                failwithf "name must be string or null, got %A" k
        let resp = udsRequest "POST" "/highlight/net" (Some body)
        TextResult (Encoding.UTF8.GetString resp)
    with ex ->
        toolError (sprintf "highlight_net failed: %s" ex.Message)

/// Tool: rekolektion_viz_set_tab { tab } — POST /tab with
/// { tab: "2D" | "3D" }.
let private toolSetTab (args: JsonElement) : ToolResult =
    try
        let tab = args.GetProperty("tab").GetString()
        let body = sprintf "{\"tab\":\"%s\"}" (jsonEscape tab)
        let resp = udsRequest "POST" "/tab" (Some body)
        TextResult (Encoding.UTF8.GetString resp)
    with ex ->
        toolError (sprintf "set_tab failed: %s" ex.Message)

/// Try a JsonElement property by name, returning Some if present
/// AND a non-null value. Helps keep optional-arg parsing terse.
let private tryProp (elem: JsonElement) (name: string) : JsonElement option =
    let mutable v = Unchecked.defaultof<JsonElement>
    if elem.ValueKind = JsonValueKind.Object
       && elem.TryGetProperty(name, &v)
       && v.ValueKind <> JsonValueKind.Null
    then Some v
    else None

/// Locate the worktree root by walking up from this assembly's
/// directory until we find a `tools/viz/src/Rekolektion.Viz.Cli`
/// folder. Used for `dotnet run --project <CLI>` fallback when
/// no built binary exists yet.
let private worktreeRoot () : string =
    let mutable dir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    let target = Path.Combine("tools", "viz", "src", "Rekolektion.Viz.Cli")
    let mutable found = false
    while not found && not (isNull dir) do
        if Directory.Exists(Path.Combine(dir, target)) then found <- true
        else dir <- Path.GetDirectoryName dir
    if found then dir else Environment.CurrentDirectory

/// Resolve the rekolektion-viz CLI invocation: prefer the built
/// binary if present (faster startup), else fall back to
/// `dotnet run --project tools/viz/src/Rekolektion.Viz.Cli`.
/// Returns (executable, leadingArgs).
let private resolveVizCli () : string * string list =
    let root = worktreeRoot ()
    let cliProj = Path.Combine(root, "tools", "viz", "src", "Rekolektion.Viz.Cli")
    let builtBin =
        Path.Combine(cliProj, "bin", "Debug", "net10.0", "rekolektion-viz")
    if File.Exists builtBin then
        builtBin, []
    else
        "dotnet", [ "run"; "--project"; cliProj; "--" ]

/// Spawn a child process and wait up to `timeoutMs`. Returns
/// (exitCode, stderrText) where exitCode = -1 means "timed out
/// and was killed". Captures stderr (only) to surface failures.
let private runProcess
        (exe: string)
        (args: string seq)
        (timeoutMs: int)
        : int * string =
    let psi = ProcessStartInfo(exe)
    for a in args do psi.ArgumentList.Add a
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError  <- true
    psi.UseShellExecute        <- false
    use proc = Process.Start psi
    // Drain stdout in the background so a chatty child can't fill
    // its pipe buffer and deadlock. We don't surface stdout in the
    // success payload, but we must read it to avoid the hang.
    let _ = proc.StandardOutput.ReadToEndAsync()
    let stderrTask = proc.StandardError.ReadToEndAsync()
    let exited = proc.WaitForExit(timeoutMs)
    if not exited then
        try proc.Kill true with _ -> ()
        -1, sprintf "process timed out after %d ms" timeoutMs
    else
        let stderr = stderrTask.Result
        proc.ExitCode, stderr

/// Tool: rekolektion_viz_render { gds, output?, toggleLayers?,
/// highlightNet?, tab?, width?, height?, holdMs? } — spawn the
/// CLI's `viz-render` subcommand to produce a PNG without a live
/// viz session.
let private toolVizRender (args: JsonElement) : ToolResult =
    try
        let gds =
            match tryProp args "gds" with
            | Some v -> v.GetString()
            | None   -> failwith "gds is required"
        let output =
            match tryProp args "output" with
            | Some v -> v.GetString()
            | None ->
                let tmp = Path.GetTempFileName()
                tmp + ".png"
        let exe, lead = resolveVizCli ()
        let argList = ResizeArray<string>(lead)
        argList.Add "viz-render"
        argList.Add "--gds";    argList.Add gds
        argList.Add "--output"; argList.Add output
        match tryProp args "toggleLayers" with
        | Some arr when arr.ValueKind = JsonValueKind.Array ->
            for entry in arr.EnumerateArray() do
                let name    = entry.GetProperty("name").GetString()
                let visible = entry.GetProperty("visible").GetBoolean()
                argList.Add "--toggle-layer"
                argList.Add (sprintf "%s=%s" name (if visible then "on" else "off"))
        | _ -> ()
        match tryProp args "highlightNet" with
        | Some v -> argList.Add "--highlight-net"; argList.Add (v.GetString())
        | None   -> ()
        match tryProp args "tab" with
        | Some v -> argList.Add "--tab"; argList.Add (v.GetString())
        | None   -> ()
        match tryProp args "width" with
        | Some v -> argList.Add "--width"; argList.Add (string (v.GetInt32()))
        | None   -> ()
        match tryProp args "height" with
        | Some v -> argList.Add "--height"; argList.Add (string (v.GetInt32()))
        | None   -> ()
        match tryProp args "holdMs" with
        | Some v -> argList.Add "--hold-ms"; argList.Add (string (v.GetInt32()))
        | None   -> ()
        let exit, stderr = runProcess exe argList 60_000
        if exit = 0 then
            TextResult (sprintf "{\"ok\":true,\"output\":\"%s\"}" (jsonEscape output))
        else
            TextResult (
                sprintf "{\"ok\":false,\"exit\":%d,\"stderr\":\"%s\"}"
                    exit (jsonEscape stderr))
    with ex ->
        toolError (sprintf "viz_render invocation failed: %s" ex.Message)

/// Tool: rekolektion_viz_run_macro { cell, words, bits, mux,
/// output, spice?, verilog?, lef?, liberty? } — spawn the Python
/// `rekolektion macro` CLI. All boolean flags default true on the
/// Python side; we only emit `--no-X` when the agent explicitly
/// asks for false.
let private toolRunMacro (args: JsonElement) : ToolResult =
    try
        let cell   = args.GetProperty("cell").GetString()
        let words  = args.GetProperty("words").GetInt32()
        let bits   = args.GetProperty("bits").GetInt32()
        let mux    = args.GetProperty("mux").GetInt32()
        let output = args.GetProperty("output").GetString()
        let argList = ResizeArray<string>()
        argList.Add "macro"
        argList.Add "--cell";  argList.Add cell
        argList.Add "--words"; argList.Add (string words)
        argList.Add "--bits";  argList.Add (string bits)
        argList.Add "--mux";   argList.Add (string mux)
        argList.Add "-o";      argList.Add output
        let addBoolFlag (name: string) (yesFlag: string) (noFlag: string) =
            match tryProp args name with
            | Some v when v.ValueKind = JsonValueKind.False ->
                argList.Add noFlag
            | Some v when v.ValueKind = JsonValueKind.True ->
                argList.Add yesFlag
            | _ -> ()
        addBoolFlag "spice"   "--spice"   "--no-spice"
        addBoolFlag "verilog" "--verilog" "--no-verilog"
        addBoolFlag "lef"     "--lef"     "--no-lef"
        addBoolFlag "liberty" "--liberty" "--no-liberty"
        let exit, stderr = runProcess "rekolektion" argList 600_000
        if exit = 0 then
            TextResult (sprintf "{\"ok\":true,\"output\":\"%s\"}" (jsonEscape output))
        else
            TextResult (
                sprintf "{\"ok\":false,\"exit\":%d,\"stderr\":\"%s\"}"
                    exit (jsonEscape stderr))
    with ex ->
        toolError (sprintf "run_macro invocation failed: %s" ex.Message)

// ---------------------------------------------------------------
// Dispatch table + tool schemas
// ---------------------------------------------------------------

let private toolHandlers
        : Map<string, JsonElement -> ToolResult> =
    Map.ofList [
        "rekolektion_viz_screenshot",     toolScreenshot
        "rekolektion_viz_open",           toolOpen
        "rekolektion_viz_toggle_layer",   toolToggleLayer
        "rekolektion_viz_highlight_net",  toolHighlightNet
        "rekolektion_viz_set_tab",        toolSetTab
        "rekolektion_viz_render",         toolVizRender
        "rekolektion_viz_run_macro",      toolRunMacro
    ]

/// Static tool schema list for MCP's `tools/list` response. Each
/// entry is boxed to obj so the heterogeneous inputSchema shapes
/// (some empty, some with required[], some with enum[]) don't
/// have to unify into a single anonymous-record type.
let private toolList : obj =
    {| tools = [|
        box {| name = "rekolektion_viz_screenshot"
               description =
                   "Capture a PNG screenshot of the running rekolektion-viz \
                    desktop window. Requires the viz app to be running and \
                    listening on ~/.rekolektion/viz.sock."
               inputSchema =
                   box {| ``type`` = "object"
                          properties = obj()
                          additionalProperties = false |} |}
        box {| name = "rekolektion_viz_open"
               description =
                   "Open a GDS file in the running rekolektion-viz desktop app."
               inputSchema =
                   box {| ``type`` = "object"
                          properties =
                              {| path =
                                  {| ``type`` = "string"
                                     description = "Absolute path to a .gds file to open" |} |}
                          required = [| "path" |] |} |}
        box {| name = "rekolektion_viz_toggle_layer"
               description =
                   "Show or hide a named layer in the running viz app."
               inputSchema =
                   box {| ``type`` = "object"
                          properties =
                              {| name    = box {| ``type`` = "string" |}
                                 visible = box {| ``type`` = "boolean" |} |}
                          required = [| "name"; "visible" |] |} |}
        box {| name = "rekolektion_viz_highlight_net"
               description =
                   "Highlight a net by name in the viz app, or pass null \
                    to clear the current highlight."
               inputSchema =
                   box {| ``type`` = "object"
                          properties =
                              {| name =
                                  {| ``type`` = [| "string"; "null" |] |} |}
                          required = [| "name" |] |} |}
        box {| name = "rekolektion_viz_set_tab"
               description =
                   "Switch the viz app's active tab between 2D and 3D."
               inputSchema =
                   box {| ``type`` = "object"
                          properties =
                              {| tab =
                                  {| ``type`` = "string"
                                     ``enum`` = [| "2D"; "3D" |] |} |}
                          required = [| "tab" |] |} |}
        box {| name = "rekolektion_viz_render"
               description =
                   "Render a GDS file to a PNG headlessly via the \
                    rekolektion-viz CLI's viz-render subcommand. Does NOT \
                    require a live viz desktop session."
               inputSchema =
                   box {| ``type`` = "object"
                          properties =
                              {| gds          = box {| ``type`` = "string" |}
                                 output       = box {| ``type`` = "string" |}
                                 toggleLayers =
                                     box {| ``type`` = "array"
                                            items =
                                                {| ``type`` = "object"
                                                   properties =
                                                       {| name    = box {| ``type`` = "string" |}
                                                          visible = box {| ``type`` = "boolean" |} |}
                                                   required = [| "name"; "visible" |] |} |}
                                 highlightNet = box {| ``type`` = "string" |}
                                 tab          =
                                     box {| ``type`` = "string"
                                            ``enum`` = [| "2D"; "3D" |] |}
                                 width        = box {| ``type`` = "integer" |}
                                 height       = box {| ``type`` = "integer" |}
                                 holdMs       = box {| ``type`` = "integer" |} |}
                          required = [| "gds" |] |} |}
        box {| name = "rekolektion_viz_run_macro"
               description =
                   "Spawn the Python `rekolektion macro` CLI to generate \
                    SRAM macro artifacts (GDS, optionally SPICE/Verilog/LEF/Liberty)."
               inputSchema =
                   box {| ``type`` = "object"
                          properties =
                              {| cell    =
                                     box {| ``type`` = "string"
                                            ``enum`` = [| "foundry"; "lr" |] |}
                                 words   = box {| ``type`` = "integer" |}
                                 bits    = box {| ``type`` = "integer" |}
                                 mux     =
                                     box {| ``type`` = "integer"
                                            ``enum`` = [| 1; 2; 4; 8 |] |}
                                 output  = box {| ``type`` = "string" |}
                                 spice   = box {| ``type`` = "boolean" |}
                                 verilog = box {| ``type`` = "boolean" |}
                                 lef     = box {| ``type`` = "boolean" |}
                                 liberty = box {| ``type`` = "boolean" |} |}
                          required = [| "cell"; "words"; "bits"; "mux"; "output" |] |} |}
    |] |}

// ---------------------------------------------------------------
// Main loop
// ---------------------------------------------------------------

[<EntryPoint>]
let main _argv =
    let reader = Console.In
    let writer = Console.Out

    /// Build the MCP `content` array for a tool's result. Text
    /// results pass the raw JSON body straight through as a "text"
    /// block; image results base64-encode the bytes into an
    /// "image" block.
    let contentOf (r: ToolResult) : obj =
        match r with
        | TextResult json ->
            {| content = [|
                box {| ``type`` = "text"; text = json |}
              |] |} :> obj
        | ImageResult (mime, bytes) ->
            let b64 = Convert.ToBase64String bytes
            {| content = [|
                box {| ``type`` = "image"; data = b64; mimeType = mime |}
              |] |} :> obj

    let mutable line = reader.ReadLine()
    while not (isNull line) do
        try
            use req = JsonDocument.Parse line
            let methodName = req.RootElement.GetProperty("method").GetString()
            // Per JSON-RPC 2.0 / MCP: notifications have no `id`
            // and MUST NOT receive a response. Requests have an
            // `id` and DO. The id can be int OR string OR null —
            // we never inspect its value, just clone and echo back.
            let mutable idElem = Unchecked.defaultof<JsonElement>
            let hasId = req.RootElement.TryGetProperty("id", &idElem)
            if not hasId then
                ()  // notification: silently accept
            else
                let idClone = idElem.Clone()
                let writeResult (result: obj) =
                    let resp =
                        {| jsonrpc = "2.0"
                           id = idClone
                           result = result |}
                    writer.WriteLine(JsonSerializer.Serialize resp)
                    writer.Flush ()
                let writeError (code: int) (message: string) =
                    let resp =
                        {| jsonrpc = "2.0"
                           id = idClone
                           error = {| code = code; message = message |} |}
                    writer.WriteLine(JsonSerializer.Serialize resp)
                    writer.Flush ()
                match methodName with
                | "initialize" ->
                    let pv =
                        let mutable p  = Unchecked.defaultof<JsonElement>
                        let mutable v  = Unchecked.defaultof<JsonElement>
                        if req.RootElement.TryGetProperty("params", &p)
                           && p.TryGetProperty("protocolVersion", &v)
                           && v.ValueKind = JsonValueKind.String then
                            v.GetString()
                        else "2025-06-18"
                    writeResult (
                        {| protocolVersion = pv
                           capabilities = {| tools = {| listChanged = false |} |}
                           serverInfo =
                               {| name    = "rekolektion-viz"
                                  version = "0.1.0" |} |} :> obj)
                | "tools/list" ->
                    writeResult toolList
                | "tools/call" ->
                    let p = req.RootElement.GetProperty("params")
                    let toolName = p.GetProperty("name").GetString()
                    // arguments may be missing on no-arg tools.
                    let mutable argsElem = Unchecked.defaultof<JsonElement>
                    let args =
                        if p.TryGetProperty("arguments", &argsElem)
                        then argsElem.Clone()
                        else
                            // Synthesize an empty object so handlers
                            // can use TryGetProperty uniformly.
                            (JsonDocument.Parse "{}").RootElement.Clone()
                    match Map.tryFind toolName toolHandlers with
                    | Some h ->
                        let r = h args
                        writeResult (contentOf r)
                    | None ->
                        writeError -32601 (sprintf "unknown tool: %s" toolName)
                | _ ->
                    writeError -32601 (sprintf "method not found: %s" methodName)
        with ex ->
            // Catch-all: write a generic error WITHOUT an id (we
            // may not have parsed one). MCP clients tolerate this.
            let msg = JsonSerializer.Serialize ex.Message
            let resp =
                sprintf
                    "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":%s}}"
                    msg
            writer.WriteLine resp
            writer.Flush ()
        line <- reader.ReadLine()
    0
