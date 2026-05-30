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
/// v1 scope: layer visibility + open tabs.  Future: camera state
/// per macro, etc.
/// Window geometry — size + screen position. Persisted so a viz
/// restart lands the user in the same shape + spot.
type WindowBounds = {
    Width  : float
    Height : float
    X      : int
    Y      : int
}

type State = {
    /// Layer visibility — only stores EXPLICITLY-toggled keys.
    /// Layers not in the list inherit their default (visible).
    /// Each entry: (layer number, datatype, visible).
    Layers : (int * int * bool) list
    /// Paths of the tabs that were open at last save, in display
    /// order.  On launch the App re-opens each path so the user
    /// lands back where they left off.  Missing files are skipped
    /// silently (no point trying to open a path that's been
    /// moved or deleted between sessions).
    OpenPaths : string list
    /// The active tab at last save.  None when nothing was open.
    /// Used to pick which tab to focus after the reopens settle.
    ActivePath : string option
    /// Saved window bounds.  None on first launch (use the
    /// hard-coded default in `MainWindow`).  Multi-monitor clamp
    /// happens at the `MainWindow` consumer, not here.
    Window : WindowBounds option
}

let empty : State = {
    Layers = []
    OpenPaths = []
    ActivePath = None
    Window = None
}

let private homeDir =
    Environment.GetFolderPath Environment.SpecialFolder.UserProfile

let private sessionPath =
    Path.Combine(homeDir, ".rekolektion", "session.json")

let private ensureDir () =
    let dir = Path.GetDirectoryName sessionPath
    if not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore

/// Parse a session-state JSON document.  Returns `empty` on any
/// parse failure.  Extracted so unit tests can exercise the
/// round-trip without touching `~/.rekolektion`.
let parse (json: string) : State =
    try
        use doc = System.Text.Json.JsonDocument.Parse(json)
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
        let openPaths =
            let mutable arr = Unchecked.defaultof<System.Text.Json.JsonElement>
            if root.TryGetProperty("openPaths", &arr)
               && arr.ValueKind = System.Text.Json.JsonValueKind.Array then
                [ for entry in arr.EnumerateArray() -> entry.GetString() ]
            else []
        let activePath =
            let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>
            if root.TryGetProperty("activePath", &v)
               && v.ValueKind = System.Text.Json.JsonValueKind.String then
                Some (v.GetString())
            else None
        let window =
            let mutable w = Unchecked.defaultof<System.Text.Json.JsonElement>
            if root.TryGetProperty("window", &w)
               && w.ValueKind = System.Text.Json.JsonValueKind.Object then
                try
                    Some {
                        Width  = w.GetProperty("w").GetDouble()
                        Height = w.GetProperty("h").GetDouble()
                        X      = w.GetProperty("x").GetInt32()
                        Y      = w.GetProperty("y").GetInt32()
                    }
                with _ -> None
            else None
        { Layers = layers
          OpenPaths = openPaths
          ActivePath = activePath
          Window = window }
    with _ -> empty

/// Read the persisted session state. Missing or malformed file
/// returns `empty` (caller carries on with defaults — visibility
/// will fall back to the "everything visible" baseline).
let load () : State =
    if not (File.Exists sessionPath) then empty
    else
        try
            use sr = new StreamReader(sessionPath)
            parse (sr.ReadToEnd())
        with _ -> empty

/// Serialize a session-state to JSON.  Extracted so unit tests can
/// exercise the round-trip without touching `~/.rekolektion`.
let serialize (state: State) : string =
    let escape (s: string) =
        System.Text.Json.JsonEncodedText.Encode(s).ToString()
    let sb = System.Text.StringBuilder()
    sb.Append "{\"layers\":[" |> ignore
    state.Layers
    |> List.iteri (fun i (n, d, v) ->
        if i > 0 then sb.Append "," |> ignore
        sb.AppendFormat(
            "{{\"n\":{0},\"d\":{1},\"v\":{2}}}",
            n, d, (if v then "true" else "false")) |> ignore)
    sb.Append "],\"openPaths\":[" |> ignore
    state.OpenPaths
    |> List.iteri (fun i p ->
        if i > 0 then sb.Append "," |> ignore
        sb.AppendFormat("\"{0}\"", escape p) |> ignore)
    sb.Append "]" |> ignore
    match state.ActivePath with
    | Some p ->
        sb.AppendFormat(",\"activePath\":\"{0}\"", escape p) |> ignore
    | None -> ()
    match state.Window with
    | Some w ->
        sb.AppendFormat(
            ",\"window\":{{\"w\":{0},\"h\":{1},\"x\":{2},\"y\":{3}}}",
            w.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            w.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            w.X, w.Y) |> ignore
    | None -> ()
    sb.Append "}" |> ignore
    sb.ToString()

/// Persist the session state. Best-effort — failures don't bubble
/// (we don't want a disk hiccup to crash the app). Logs every
/// write so a regression where the file gets stomped with a
/// near-empty Map surfaces in the viz log.
let save (state: State) : unit =
    try
        ensureDir ()
        File.WriteAllText(sessionPath, serialize state)
        Rekolektion.Viz.App.Services.Logger.log "session.save"
            {| count = state.Layers.Length
               openPaths = state.OpenPaths.Length
               activePath = state.ActivePath
               layers =
                   state.Layers
                   |> List.map (fun (n, d, v) ->
                       sprintf "%d/%d=%b" n d v)
                   |> String.concat " " |}
    with ex ->
        try
            Rekolektion.Viz.App.Services.Logger.log "session.save.fail"
                {| error = ex.Message |}
        with _ -> ()

/// Project the current Model into a session-state snapshot and
/// persist it.  Centralises the "what goes in session.json" mapping
/// so each call site doesn't have to enumerate every field —
/// adding a new persisted slice (camera, zoom, etc.) means
/// updating this function, not every site.
///
/// `Window` is preserved verbatim from disk — it's owned by the
/// MainWindow lifecycle (set on Closing), not by the Elmish model,
/// so model-driven saves must NOT overwrite it.
let persistFromModel (model: Rekolektion.Viz.App.Model.Model.Model) : unit =
    let current = load ()
    save {
        Layers =
            model.Toggle.Layers
            |> Map.toList
            |> List.map (fun ((n, d), v) -> (n, d, v))
        OpenPaths =
            model.OpenMacros
            |> List.map (fun m -> m.OriginalPath)
        ActivePath = model.ActiveMacroPath
        Window = current.Window
    }

/// Persist only the window bounds, preserving every other field
/// from the on-disk session.  Called from `MainWindow.Closing`
/// (and on layout-change events if we ever wire them).
let saveWindowBounds (bounds: WindowBounds) : unit =
    let current = load ()
    save { current with Window = Some bounds }
