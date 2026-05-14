module Rekolektion.Viz.App.Services.Config

open System
open System.IO
open YamlDotNet.RepresentationModel

/// Grid + snap settings the viz tool reads at startup. Loaded
/// once from `~/.rekolektion-viz/config.yml`; missing file or
/// missing keys fall back to the defaults below. A future
/// settings dialog will write this back.
type Settings = {
    /// Major grid-dot spacing in micrometers (e.g. 5.0 µm). Drawn
    /// brighter than minor dots so the eye picks up the major
    /// rhythm even at high zoom.
    GridMajorUm   : float
    /// Minor grid-dot spacing in micrometers (e.g. 1.0 µm).
    GridMinorUm   : float
    /// Snap step in micrometers when S-key snap is on AND no
    /// modifier is held (e.g. 1.0 µm).
    SnapDefaultUm : float
    /// Snap step in micrometers when S-key snap is on AND Alt
    /// is held during the drag (e.g. 0.5 µm).
    SnapAltUm     : float
}

let defaults : Settings = {
    GridMajorUm   = 5.0
    GridMinorUm   = 1.0
    SnapDefaultUm = 0.5
    SnapAltUm     = 0.1
}

let private configPath =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
        ".rekolektion-viz",
        "config.yml")

/// Walk a YAML mapping by dotted-path key (e.g. "grid.major_um")
/// and return the leaf scalar's parsed float, or None.
let private readFloat
        (root: YamlMappingNode)
        (path: string)
        : float option =
    let parts = path.Split '.'
    let rec walk (node: YamlNode) (i: int) : YamlNode option =
        if i >= parts.Length then Some node
        else
            match node with
            | :? YamlMappingNode as m ->
                let key = YamlScalarNode(parts.[i]) :> YamlNode
                match m.Children.TryGetValue key with
                | true, child -> walk child (i + 1)
                | _ -> None
            | _ -> None
    match walk (root :> YamlNode) 0 with
    | Some (:? YamlScalarNode as s) ->
        match Double.TryParse(s.Value, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None
    | _ -> None

/// Load the YAML config file. Missing file or parse error returns
/// `defaults`. Per-key parse misses fall back to the field's
/// default (caller gets a fully-populated record).
let load () : Settings =
    if not (File.Exists configPath) then defaults
    else
        try
            use reader = new StreamReader(configPath)
            let yaml = YamlStream()
            yaml.Load reader
            if yaml.Documents.Count = 0 then defaults
            else
                match yaml.Documents.[0].RootNode with
                | :? YamlMappingNode as root ->
                    let pick key fallback =
                        readFloat root key |> Option.defaultValue fallback
                    { GridMajorUm   = pick "grid.major_um"   defaults.GridMajorUm
                      GridMinorUm   = pick "grid.minor_um"   defaults.GridMinorUm
                      SnapDefaultUm = pick "snap.default_um" defaults.SnapDefaultUm
                      SnapAltUm     = pick "snap.alt_um"     defaults.SnapAltUm }
                | _ -> defaults
        with _ -> defaults

/// Loaded once at App startup; readers (canvas, snap helpers) read
/// from here without threading the value through every call site.
/// Kept mutable so a future settings dialog can rewrite + reload
/// without a restart.
let mutable current : Settings = defaults
