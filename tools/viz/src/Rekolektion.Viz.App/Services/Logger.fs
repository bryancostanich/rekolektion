module Rekolektion.Viz.App.Services.Logger

open System
open System.IO
open System.Text.Json

/// Always-on JSONL log at `~/.rekolektion/viz.log`. Each call appends
/// one line: `{"t":<ISO-8601 UTC>,"cat":<category>,...payload}`.
///
/// Categories used so far:
///   msg          — every Elmish Msg dispatched, with name + sketch of args
///   route.tool   — route-edit tool state transitions
///   route.emit   — geometry emitted by a route helper, with rule decisions
///   route.warn   — DRC / axis / enclosure warnings during routing
///   save / load  — persistence outcomes
///   error        — unhandled exception or hard failure
///
/// Rotation: when the file passes ~10 MB we move it to `viz.log.1`
/// (overwriting the previous archive) and start a fresh `viz.log`.
/// One generation only; we're optimising for "what happened in the
/// last few minutes" not long-term history.
///
/// Levels: `info` (default) emits everything except `*.trace`;
/// `debug` (set `VIZ_LOG=debug`) also emits the `*.trace`
/// categories — raw pointer events, frame-by-frame snap deltas, etc.
let private homeDir =
    Environment.GetFolderPath Environment.SpecialFolder.UserProfile

let private logDir = Path.Combine(homeDir, ".rekolektion")
let logFilePath = Path.Combine(logDir, "viz.log")
let private archivePath = Path.Combine(logDir, "viz.log.1")

let private rotateThresholdBytes = 10L * 1024L * 1024L

let private envLevel =
    match Environment.GetEnvironmentVariable "VIZ_LOG" with
    | null | "" -> "info"
    | s -> s.Trim().ToLowerInvariant()

let isDebug = envLevel = "debug" || envLevel = "trace"

let private writeLock = obj()

let private serializerOpts =
    let o = JsonSerializerOptions()
    o.WriteIndented <- false
    o

let private ensureDir () =
    if not (Directory.Exists logDir) then
        Directory.CreateDirectory logDir |> ignore

let private rotate () =
    let info = FileInfo logFilePath
    if info.Exists && info.Length > rotateThresholdBytes then
        try
            if File.Exists archivePath then File.Delete archivePath
            File.Move(logFilePath, archivePath)
        with _ -> ()

/// Emit one JSONL record. The wider envelope adds `t` (UTC ISO-8601)
/// and `cat`; everything in `payload` is serialised inline at the
/// same level so callers don't have to wrap in a `data` field.
/// Never throws — a logging failure must not break the app.
let private write (cat: string) (payload: obj) : unit =
    try
        ensureDir ()
        // Build a JsonObject so the envelope merges flat with the
        // payload's own keys (no nested `data:` wrapper).
        let envelope = System.Text.Json.Nodes.JsonObject()
        envelope.["t"] <- System.Text.Json.Nodes.JsonValue.Create(
            DateTime.UtcNow.ToString("o"))
        envelope.["cat"] <- System.Text.Json.Nodes.JsonValue.Create cat
        // Serialize the payload, parse back as a JsonObject, copy
        // each property into the envelope. Cheap relative to disk
        // I/O; keeps the API surface (caller passes any record /
        // anonymous record / Map<string,obj>) ergonomic.
        if not (isNull payload) then
            let payloadJson = JsonSerializer.Serialize(payload, serializerOpts)
            let parsed = System.Text.Json.Nodes.JsonNode.Parse payloadJson
            match parsed with
            | :? System.Text.Json.Nodes.JsonObject as po ->
                let keys =
                    po
                    |> Seq.map (fun kv -> kv.Key)
                    |> Seq.toList
                for k in keys do
                    let v = po.[k]
                    po.Remove k |> ignore
                    envelope.[k] <- v
            | _ -> ()
        let line = envelope.ToJsonString serializerOpts
        lock writeLock (fun () ->
            rotate ()
            File.AppendAllText(logFilePath, line + "\n"))
    with _ -> ()

/// Always-on log. Use for any category the agent / user will want
/// to see during a normal debugging session.
let log (cat: string) (payload: obj) : unit = write cat payload

/// Debug-only log — suppressed unless `VIZ_LOG=debug` is set.
/// Use for high-volume signals (raw pointer events, per-frame
/// snap deltas) so the default file stays small.
let trace (cat: string) (payload: obj) : unit =
    if isDebug then write cat payload
