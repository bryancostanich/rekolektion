module Rekolektion.Viz.Core.Visibility

type LayerKey = int * int       // (number, datatype)

type ToggleState = {
    Layers        : Map<LayerKey, bool>
    Nets          : Map<string, bool>
    Blocks        : Map<string, bool>
    HighlightNet  : string option       // dim everything else when set
    IsolatedBlock : string option       // when set, hide other blocks
}

let empty : ToggleState = {
    Layers = Map.empty
    Nets = Map.empty
    Blocks = Map.empty
    HighlightNet = None
    IsolatedBlock = None
}

let isLayerVisible (s: ToggleState) (key: LayerKey) : bool =
    Map.tryFind key s.Layers |> Option.defaultValue true

let isNetVisible (s: ToggleState) (name: string) : bool =
    Map.tryFind name s.Nets |> Option.defaultValue true

let isNetDimmed (s: ToggleState) (name: string) : bool =
    match s.HighlightNet with
    | Some h -> h <> name
    | None -> false

let isBlockVisible (s: ToggleState) (name: string) : bool =
    let explicit = Map.tryFind name s.Blocks |> Option.defaultValue true
    let isolated =
        match s.IsolatedBlock with
        | Some iso -> iso = name
        | None -> true
    explicit && isolated

let toggleLayer (key: LayerKey) (visible: bool) (s: ToggleState) : ToggleState =
    { s with Layers = Map.add key visible s.Layers }

let toggleNet (name: string) (visible: bool) (s: ToggleState) : ToggleState =
    { s with Nets = Map.add name visible s.Nets }

let toggleBlock (name: string) (visible: bool) (s: ToggleState) : ToggleState =
    { s with Blocks = Map.add name visible s.Blocks }

let highlightNet (net: string option) (s: ToggleState) : ToggleState =
    { s with HighlightNet = net }

let isolateBlock (block: string option) (s: ToggleState) : ToggleState =
    { s with IsolatedBlock = block }
