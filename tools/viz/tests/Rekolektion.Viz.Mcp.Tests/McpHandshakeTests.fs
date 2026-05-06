module Rekolektion.Viz.Mcp.Tests.McpHandshakeTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FsUnit.Xunit

/// Spawn the MCP binary, drive a single request/response over its
/// stdio JSON-RPC interface, return the parsed response and the
/// captured stderr.
///
/// Each test spawns its own process — keeps tests isolated and
/// matches how an MCP client (Claude Code, Codex) actually drives
/// the server. We talk via `dotnet run --project ...` rather than
/// the built binary so the tests work whether or not someone has
/// done a Release build.

let private mcpProjectDir =
    let here =
        Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location)
    let mutable dir = here
    let target = Path.Combine("tools", "viz", "src", "Rekolektion.Viz.Mcp")
    let mutable found = false
    while not found && not (isNull dir) do
        if Directory.Exists(Path.Combine(dir, target)) then found <- true
        else dir <- Path.GetDirectoryName dir
    if not found then failwith "could not locate Rekolektion.Viz.Mcp project"
    Path.Combine(dir, target)

let private spawn () : Process =
    let psi = ProcessStartInfo "dotnet"
    psi.ArgumentList.Add "run"
    psi.ArgumentList.Add "--project"
    psi.ArgumentList.Add mcpProjectDir
    psi.ArgumentList.Add "--no-build"
    psi.RedirectStandardInput  <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError  <- true
    psi.UseShellExecute        <- false
    Process.Start psi

let private sendAndReceive
        (proc: Process)
        (request: string)
        : JsonElement =
    proc.StandardInput.WriteLine request
    proc.StandardInput.Flush ()
    let line = proc.StandardOutput.ReadLine()
    if isNull line then
        let stderr = proc.StandardError.ReadToEnd ()
        failwithf "MCP process closed stdout before responding. stderr=%s" stderr
    let doc = JsonDocument.Parse line
    doc.RootElement.Clone()

[<Fact>]
let ``initialize handshake returns protocol version + serverInfo`` () =
    use proc = spawn ()
    try
        let req = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}"""
        let resp = sendAndReceive proc req
        resp.GetProperty("jsonrpc").GetString() |> should equal "2.0"
        resp.GetProperty("id").GetInt32()       |> should equal 1
        let result = resp.GetProperty("result")
        result.GetProperty("protocolVersion").GetString()
        |> should equal "2025-06-18"
        let info = result.GetProperty "serverInfo"
        info.GetProperty("name").GetString() |> should equal "rekolektion-viz"
    finally
        try proc.Kill true with _ -> ()

[<Fact>]
let ``tools/list reports the seven viz tools`` () =
    use proc = spawn ()
    try
        sendAndReceive proc """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}"""
        |> ignore
        let resp =
            sendAndReceive proc
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""
        let tools = resp.GetProperty("result").GetProperty("tools")
        tools.ValueKind |> should equal JsonValueKind.Array
        let names =
            tools.EnumerateArray()
            |> Seq.map (fun t -> t.GetProperty("name").GetString())
            |> Set.ofSeq
        let expected =
            Set.ofList [
                "rekolektion_viz_screenshot"
                "rekolektion_viz_open"
                "rekolektion_viz_toggle_layer"
                "rekolektion_viz_highlight_net"
                "rekolektion_viz_set_tab"
                "rekolektion_viz_render"
                "rekolektion_viz_run_macro"
            ]
        Set.isSubset expected names |> should equal true
    finally
        try proc.Kill true with _ -> ()

[<Fact>]
let ``unknown method returns JSON-RPC -32601`` () =
    use proc = spawn ()
    try
        sendAndReceive proc """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}"""
        |> ignore
        let resp =
            sendAndReceive proc
                """{"jsonrpc":"2.0","id":3,"method":"does/not/exist"}"""
        let err = resp.GetProperty "error"
        err.GetProperty("code").GetInt32() |> should equal -32601
    finally
        try proc.Kill true with _ -> ()

[<Fact>]
let ``unknown tools/call name returns JSON-RPC -32601`` () =
    use proc = spawn ()
    try
        sendAndReceive proc """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}"""
        |> ignore
        let resp =
            sendAndReceive proc
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"not_a_tool","arguments":{}}}"""
        let err = resp.GetProperty "error"
        err.GetProperty("code").GetInt32() |> should equal -32601
    finally
        try proc.Kill true with _ -> ()

// NOTE: end-to-end live-socket tools (screenshot, open, toggle,
// highlight, set_tab) are not exercised here — invoking them
// would spawn an Avalonia desktop window via `dotnet run` and
// take 30+ seconds to boot. They get manual verification from
// the actual MCP client (`rekolektion_viz_open` from a Claude
// Code session in another project).
