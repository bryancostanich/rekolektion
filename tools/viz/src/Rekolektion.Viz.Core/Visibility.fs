module Rekolektion.Viz.Core.Visibility

type LayerKey = int * int       // (number, datatype)

type ToggleState = {
    Layers          : Map<LayerKey, bool>
    /// Per-layer DRC overlay visibility. Independent of `Layers` —
    /// turning a layer's polygons off does not hide its DRC tiles
    /// and vice versa. Lookup defaults to true via
    /// `defaultValue true` in `isDrcVisibleForLayer`, so the map
    /// only carries entries the user has actively touched (matches
    /// the `Layers` convention).
    DrcVisibleLayers: Map<LayerKey, bool>
    /// "Other" bucket for DRC violations whose touched layers
    /// don't intersect any layer shown in the Layers panel
    /// (e.g. rules on label-only layers, or custom rules from a
    /// YAML override on layers the panel doesn't surface). Default
    /// true — the user can hide this bucket via the "Other" row
    /// at the bottom of the Layers list. Per-layer toggles do not
    /// affect this; only `setDrcVisibleOther` flips it.
    DrcVisibleOther : bool
    Nets            : Map<string, bool>
    Blocks          : Map<string, bool>
    /// Set of nets currently polygon-highlighted. Polys whose net
    /// is NOT in this set get dimmed in the renderer when the set
    /// is non-empty. Empty set = no dimming (default at-rest).
    /// Independent from `VisibleRatlines` — the user can light a
    /// net's polygons without showing its ratline and vice versa.
    HighlightedNets : Set<string>
    /// Set of nets whose ratlines are drawn. Empty = no ratlines.
    /// The TopBar / W key "all ratlines" master toggle flips this
    /// between all-net set and empty.
    VisibleRatlines : Set<string>
    IsolatedBlock   : string option
    /// Layer that receives interactive edits (routing, drag, etc.).
    /// `None` means no layer is focused for editing.
    /// Invariant: when `Some k`, layer `k` must be visible in
    /// `Layers`. Set only via `setActiveLayer`, which enforces this.
    ActiveLayer     : LayerKey option
}

let empty : ToggleState = {
    Layers = Map.empty
    DrcVisibleLayers = Map.empty
    DrcVisibleOther = true
    Nets = Map.empty
    Blocks = Map.empty
    HighlightedNets = Set.empty
    VisibleRatlines = Set.empty
    IsolatedBlock = None
    ActiveLayer = None
}

let isLayerVisible (s: ToggleState) (key: LayerKey) : bool =
    Map.tryFind key s.Layers |> Option.defaultValue true

let isNetVisible (s: ToggleState) (name: string) : bool =
    Map.tryFind name s.Nets |> Option.defaultValue true

let isNetHighlighted (s: ToggleState) (name: string) : bool =
    s.HighlightedNets.Contains name

let isNetDimmed (s: ToggleState) (name: string) : bool =
    not s.HighlightedNets.IsEmpty && not (s.HighlightedNets.Contains name)

let isRatlineVisible (s: ToggleState) (name: string) : bool =
    s.VisibleRatlines.Contains name

let isBlockVisible (s: ToggleState) (name: string) : bool =
    let explicit = Map.tryFind name s.Blocks |> Option.defaultValue true
    let isolated =
        match s.IsolatedBlock with
        | Some iso -> iso = name
        | None -> true
    explicit && isolated

let toggleLayer (key: LayerKey) (visible: bool) (s: ToggleState) : ToggleState =
    { s with Layers = Map.add key visible s.Layers }

let setAllLayers (keys: LayerKey seq) (visible: bool) (s: ToggleState) : ToggleState =
    let layers =
        keys |> Seq.fold (fun acc k -> Map.add k visible acc) s.Layers
    { s with Layers = layers }

let isDrcVisibleForLayer (s: ToggleState) (key: LayerKey) : bool =
    Map.tryFind key s.DrcVisibleLayers |> Option.defaultValue true

let setDrcVisibleLayer (key: LayerKey) (visible: bool) (s: ToggleState) : ToggleState =
    { s with DrcVisibleLayers = Map.add key visible s.DrcVisibleLayers }

let setAllDrcVisible (keys: LayerKey seq) (visible: bool) (s: ToggleState) : ToggleState =
    let next =
        keys |> Seq.fold (fun acc k -> Map.add k visible acc) s.DrcVisibleLayers
    { s with DrcVisibleLayers = next }

let isDrcVisibleOther (s: ToggleState) : bool =
    s.DrcVisibleOther

let setDrcVisibleOther (visible: bool) (s: ToggleState) : ToggleState =
    { s with DrcVisibleOther = visible }

/// Master "all DRC on/off" affordance. Sets every per-layer DRC
/// entry in `keys` to `visible` AND the layerless-bucket toggle to
/// the same value. The Layers panel's tri-state master uses this
/// so one click hides every DRC tile regardless of bucket.
let setAllDrcIncludingOther
        (keys: LayerKey seq) (visible: bool) (s: ToggleState) : ToggleState =
    s |> setAllDrcVisible keys visible |> setDrcVisibleOther visible

let toggleNet (name: string) (visible: bool) (s: ToggleState) : ToggleState =
    { s with Nets = Map.add name visible s.Nets }

let toggleBlock (name: string) (visible: bool) (s: ToggleState) : ToggleState =
    { s with Blocks = Map.add name visible s.Blocks }

/// Flip the membership of `name` in HighlightedNets.
let toggleNetHighlight (name: string) (s: ToggleState) : ToggleState =
    let next =
        if s.HighlightedNets.Contains name then s.HighlightedNets.Remove name
        else s.HighlightedNets.Add name
    { s with HighlightedNets = next }

/// Replace the highlighted-nets set wholesale (master "all/none").
let setHighlightedNets (nets: Set<string>) (s: ToggleState) : ToggleState =
    { s with HighlightedNets = nets }

/// Flip the membership of `name` in VisibleRatlines.
let toggleNetRatline (name: string) (s: ToggleState) : ToggleState =
    let next =
        if s.VisibleRatlines.Contains name then s.VisibleRatlines.Remove name
        else s.VisibleRatlines.Add name
    { s with VisibleRatlines = next }

/// Replace the visible-ratlines set wholesale (master "all/none"
/// + W hotkey).
let setVisibleRatlines (nets: Set<string>) (s: ToggleState) : ToggleState =
    { s with VisibleRatlines = nets }

let isolateBlock (block: string option) (s: ToggleState) : ToggleState =
    { s with IsolatedBlock = block }

/// Set (or clear) the active edit layer. When setting to `Some k`,
/// the layer is also forced visible — focus implies visible. Pass
/// `None` to clear focus; visibility of any other layer is untouched.
let setActiveLayer (layer: LayerKey option) (s: ToggleState) : ToggleState =
    match layer with
    | Some k ->
        { s with
            ActiveLayer = Some k
            Layers = Map.add k true s.Layers }
    | None ->
        { s with ActiveLayer = None }
