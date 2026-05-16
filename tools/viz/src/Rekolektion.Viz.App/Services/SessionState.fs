module Rekolektion.Viz.App.Services.SessionState

open System
open System.IO

/// Persistent per-user UI state that should survive app relaunches
/// but isn't a "setting" the user explicitly edits — layer
/// visibility, ratline mode, etc. Stored as JSONL alongside the
/// log so the existing `~/.rekolektion` dir doubles as session
/// storage. Separate from `Config.fs` (which is for setting-style
/// values that get edited in a dialog).
///
/// v1 scope: layer visibility only. Future: tab paths, camera
/// state per macro, etc.
type State = {
    /// Layer visibility — only stores EXPLICITLY-toggled keys.
    /// Layers not in the list inherit their default (visible).
    /// Each entry: (layer number, datatype, visible).
    Layers : (int * int * bool) list
}

let empty : State = { Layers = [] }

let private homeDir =
    Environment.GetFolderPath Environment.SpecialFolder.UserProfile

let private sessionPath =
    Path.Combine(homeDir, ".rekolektion", "session.json")

let private ensureDir () =
    let dir = Path.GetDirectoryName sessionPath
    if not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore

/// Read the persisted session state. Missing or malformed file
/// returns `empty` (caller carries on with defaults — visibility
/// will fall back to the "everything visible" baseline).
let load () : State =
    if not (File.Exists sessionPath) then empty
    else
        try
            use sr = new StreamReader(sessionPath)
            use doc = System.Text.Json.JsonDocument.Parse(sr.ReadToEnd())
            let root = doc.RootElement
            let layers =
                let mutable arr = Unchecked.defaultof<System.Text.Json.JsonElement>
                if root.TryGetProperty("layers", &arr)
                   && arr.ValueKind = System.Text.Json.JsonValueKind.Array then
                    [ for entry in arr.EnumerateArray() ->
                        let n = entry.GetProperty("n").GetInt32()
                        let d = entry.GetProperty("d").GetInt32()
                        let v = entry.GetProperty("v").GetBoolean()
                        (n, d, v) ]
                else []
            { Layers = layers }
        with _ -> empty

/// Persist the session state. Best-effort — failures don't bubble
/// (we don't want a disk hiccup to crash the app).
let save (state: State) : unit =
    try
        ensureDir ()
        let sb = System.Text.StringBuilder()
        sb.Append "{\"layers\":[" |> ignore
        state.Layers
        |> List.iteri (fun i (n, d, v) ->
            if i > 0 then sb.Append "," |> ignore
            sb.AppendFormat(
                "{{\"n\":{0},\"d\":{1},\"v\":{2}}}",
                n, d, (if v then "true" else "false")) |> ignore)
        sb.Append "]}" |> ignore
        File.WriteAllText(sessionPath, sb.ToString())
    with _ -> ()
