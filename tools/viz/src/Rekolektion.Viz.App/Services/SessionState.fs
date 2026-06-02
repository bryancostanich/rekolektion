module Rekolektion.Viz.App.Services.SessionState

open System
open System.IO
open Rekolektion.Viz.Core

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
    /// Layer visibility + per-layer DRC-overlay visibility. Only
    /// stores EXPLICITLY-toggled keys; layers not in the list
    /// inherit their defaults (visible = true, drc = true).
    /// Each entry: (layer number, datatype, visible, drc).
    /// On parse, a legacy entry without `drc` reads as `true` to
    /// preserve the "default DRC-on" behaviour for session files
    /// written before the column was added.
    Layers : (int * int * bool * bool) list
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
    /// "Other" DRC bucket visibility — true means render
    /// violations whose layers don't appear in the Layers panel.
    /// Default true (matches `Visibility.empty.DrcVisibleOther`).
    /// Persisted as a top-level `drcOther` field so it survives
    /// app restart alongside the per-layer entries.
    DrcOther : bool
    /// User-facing display toggles persisted across restarts.
    /// Defaults mirror `Model.empty` so a missing field on disk
    /// (legacy session file) parses to the same value the app
    /// starts with on first launch.
    SnapEnabled    : bool   // S key — default false
    ShowAxes       : bool   // L key — default true (origin-anchored axes)
    ShowGrid       : bool   // G key — default true
    ShowLabels     : bool   // default true
    RatlinesArmed  : bool   // U / TopBar — default false
    ShowDrc        : bool   // R key — default false
    ShowDrcLabels  : bool   // Shift+R — default true (DRC tooltip labels)
    ShowDimensions : bool   // D key — default false
}

let empty : State = {
    Layers = []
    OpenPaths = []
    ActivePath = None
    Window = None
    DrcOther = true
    SnapEnabled    = false
    ShowAxes       = true
    ShowGrid       = true
    ShowLabels     = true
    RatlinesArmed  = false
    ShowDrc        = false
    ShowDrcLabels  = true
    ShowDimensions = false
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
                    // Legacy entries (pre-DRC-column) lack a "drc"
                    // field — default true to preserve the existing
                    // "all DRC tiles visible" behaviour.
                    let drc =
                        let mutable r =
                            Unchecked.defaultof<System.Text.Json.JsonElement>
                        if entry.TryGetProperty("drc", &r)
                           && r.ValueKind = System.Text.Json.JsonValueKind.True
                                || r.ValueKind = System.Text.Json.JsonValueKind.False
                        then r.GetBoolean()
                        else true
                    (n, d, v, drc) ]
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
        let drcOther =
            let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>
            if root.TryGetProperty("drcOther", &v)
               && (v.ValueKind = System.Text.Json.JsonValueKind.True
                   || v.ValueKind = System.Text.Json.JsonValueKind.False)
            then v.GetBoolean()
            else true   // legacy files default to "Other on"
        // Display-toggle fields. Legacy files (pre-toggle persistence)
        // lack each key; default to the same value `Model.empty` uses
        // so a missing field reads as a fresh-install default.
        let boolField (key: string) (fallback: bool) : bool =
            let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>
            if root.TryGetProperty(key, &v)
               && (v.ValueKind = System.Text.Json.JsonValueKind.True
                   || v.ValueKind = System.Text.Json.JsonValueKind.False)
            then v.GetBoolean()
            else fallback
        // Legacy session files (pre-rename, 2026-06-02) wrote the
        // origin-axes toggle under "showRuler". Prefer the new
        // "showAxes" key and fall back to the legacy one so an
        // existing user's "I hid the axes" preference survives
        // the upgrade.
        let showAxes =
            let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>
            if root.TryGetProperty("showAxes", &v)
               && (v.ValueKind = System.Text.Json.JsonValueKind.True
                   || v.ValueKind = System.Text.Json.JsonValueKind.False)
            then v.GetBoolean()
            else boolField "showRuler" true
        { Layers = layers
          OpenPaths = openPaths
          ActivePath = activePath
          Window = window
          DrcOther = drcOther
          SnapEnabled    = boolField "snapEnabled"    false
          ShowAxes       = showAxes
          ShowGrid       = boolField "showGrid"       true
          ShowLabels     = boolField "showLabels"     true
          RatlinesArmed  = boolField "ratlinesArmed"  false
          ShowDrc        = boolField "showDrc"        false
          ShowDrcLabels  = boolField "showDrcLabels"  true
          ShowDimensions = boolField "showDimensions" false }
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
    |> List.iteri (fun i (n, d, v, drc) ->
        if i > 0 then sb.Append "," |> ignore
        sb.AppendFormat(
            "{{\"n\":{0},\"d\":{1},\"v\":{2},\"drc\":{3}}}",
            n, d,
            (if v then "true" else "false"),
            (if drc then "true" else "false")) |> ignore)
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
    sb.AppendFormat(
        ",\"drcOther\":{0}",
        (if state.DrcOther then "true" else "false")) |> ignore
    let appendBool (key: string) (v: bool) =
        sb.AppendFormat(",\"{0}\":{1}", key, (if v then "true" else "false")) |> ignore
    appendBool "snapEnabled"    state.SnapEnabled
    appendBool "showAxes"       state.ShowAxes
    appendBool "showGrid"       state.ShowGrid
    appendBool "showLabels"     state.ShowLabels
    appendBool "ratlinesArmed"  state.RatlinesArmed
    appendBool "showDrc"        state.ShowDrc
    appendBool "showDrcLabels"  state.ShowDrcLabels
    appendBool "showDimensions" state.ShowDimensions
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
                   |> List.map (fun (n, d, v, drc) ->
                       sprintf "%d/%d=v%b/drc%b" n d v drc)
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
    // Union of layer keys that the user has explicitly toggled in
    // EITHER column. Persisting only `Layers.keys` would lose any
    // entry where the user toggled DRC but left polygon visibility
    // at the default. (Same the other way around: a layer with
    // DRC at the default but a non-default polygon visibility
    // would lose its DRC=true marker on reload — innocuous today
    // because true is the default, but the union keeps the
    // serialization symmetric and future-proof.)
    let allKeys =
        Set.union
            (model.Toggle.Layers |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
            (model.Toggle.DrcVisibleLayers |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
    save {
        Layers =
            allKeys
            |> Set.toList
            |> List.map (fun (n, d) ->
                let v =
                    Visibility.isLayerVisible model.Toggle (n, d)
                let drc =
                    Visibility.isDrcVisibleForLayer model.Toggle (n, d)
                (n, d, v, drc))
        OpenPaths =
            model.OpenMacros
            |> List.map (fun m -> m.OriginalPath)
        ActivePath = model.ActiveMacroPath
        Window = current.Window
        DrcOther = Visibility.isDrcVisibleOther model.Toggle
        SnapEnabled    = model.SnapEnabled
        ShowAxes       = model.ShowAxes
        ShowGrid       = model.ShowGrid
        ShowLabels     = model.ShowLabels
        RatlinesArmed  = model.RatlinesArmed
        ShowDrc        = model.ShowDrc
        ShowDrcLabels  = model.ShowDrcLabels
        ShowDimensions = model.ShowDimensions
    }

/// Persist only the window bounds, preserving every other field
/// from the on-disk session.  Called from `MainWindow.Closing`
/// (and on layout-change events if we ever wire them).
let saveWindowBounds (bounds: WindowBounds) : unit =
    let current = load ()
    save { current with Window = Some bounds }
