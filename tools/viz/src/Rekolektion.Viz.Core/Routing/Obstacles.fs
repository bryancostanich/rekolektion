/// Per-layer obstacle map for the walk-around router (ADR-0006).
///
/// Given:
///   - the cell's flat-polygon array,
///   - the cell's net membership (`Macro.Nets`, populated either from
///     sidecar JSON or from `Net.LabelFlood.derive`),
///   - the routing layer the wire is on,
///   - the start net of the in-progress route,
/// this module returns the set of flat polygons the walk-around must
/// route around. Two polygon classes contribute:
///
///   1. Same-layer foreign-net polygons. The wire shares its layer
///      with these; overlap = electrical short.
///   2. Cross-layer features that bridge to a foreign net through the
///      routing layer. For li1 that's foreign-net licon1 (touches
///      li1 from below) and mcon (touches li1 from above); for met1+
///      it's the vias above and below. Wire-over-foreign-via = short.
///
/// DRC-rule violations (spacing, enclosure, min-area) are NOT in the
/// obstacle set. Those continue to fire through the live-DRC overlay
/// (ADR-0003); the walk-around handles electrical shorts, the DRC
/// handles geometric rules. The two pipelines are independent.
module Rekolektion.Viz.Core.Routing.Obstacles

open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Sidecar.Types

/// A bridge layer is a layer that, when its polygons overlap a wire
/// on the routing layer, electrically connects the wire to whatever
/// the bridge polygon's net is. Contacts and vias are the universe
/// of bridge layers in SKY130.
type LayerKey = { Number : int; DataType : int }

let private key n d : LayerKey = { Number = n; DataType = d }

/// SKY130 layer constants used by the walk-around. Centralised here
/// so the obstacle pipeline is the only place that hard-codes layer
/// numbers (renderer and DRC consult `Layout.Layer` separately).
let private li1   = key 67 20
let private mcon  = key 67 44
let private met1  = key 68 20
let private via1  = key 68 44
let private met2  = key 69 20
let private via2  = key 69 44
let private met3  = key 70 20
let private via3  = key 70 44
let private met4  = key 71 20
let private via4  = key 71 44
let private licon = key 66 44   // poly / diff contact to li1

/// Routing-layer bridge sets. For a wire on layer L, its bridge
/// layers are the contacts / vias that touch L on its top and
/// bottom faces. A foreign-net polygon on any of these layers,
/// physically overlapping the wire, creates a short.
let private bridgesOf (layer : LayerKey) : LayerKey list =
    match layer.Number, layer.DataType with
    | 67, 20 -> [ licon; mcon ]
    | 68, 20 -> [ mcon;  via1 ]
    | 69, 20 -> [ via1;  via2 ]
    | 70, 20 -> [ via2;  via3 ]
    | 71, 20 -> [ via3;  via4 ]
    | _      -> []

/// True if `layer` is a layer the walk-around understands. Other
/// layers fall through to the existing straight-L behaviour.
let isRoutingLayer (layer : LayerKey) : bool =
    match layer.Number, layer.DataType with
    | (67 | 68 | 69 | 70 | 71), 20 -> true
    | _ -> false

/// Reverse index: a polygon's identity in the source structure maps
/// to the net it belongs to. Built once from `Macro.Nets` and reused
/// for every obstacle query until the net map or geometry changes.
///
/// Two FlatPolygons that descend from the same (Structure, Index)
/// — e.g., two SRefs of the same subcell — share a net. The reverse
/// index keys on (Structure, Layer, DataType, Index) so both look
/// up to the same name.
[<Struct>]
type private PolyId = {
    Structure : string
    Layer     : int
    DataType  : int
    Index     : int
}

type NetIndex = private NetIndex of Map<PolyId, string>

let private polyIdOf (p : PolygonRef) : PolyId =
    { Structure = p.Structure
      Layer     = p.Layer
      DataType  = p.DataType
      Index     = p.Index }

let private flatPolyId (fp : FlatPolygon) : PolyId =
    { Structure = fp.SourceStructure
      Layer     = fp.Layer
      DataType  = fp.DataType
      Index     = fp.SourceIndex }

/// Build the reverse index. O(Σ |net.Polygons|) — one pass over the
/// net map. Cached by the caller; rebuilt on `Macro.Nets` change.
let buildNetIndex (nets : Map<string, NetEntry>) : NetIndex =
    let mutable m = Map.empty
    for KeyValue (netName, entry) in nets do
        for pRef in entry.Polygons do
            m <- Map.add (polyIdOf pRef) netName m
    NetIndex m

/// Look up the net a flat polygon belongs to, if any. Polygons not
/// claimed by any `NetEntry` return `None` and are treated as
/// obstacles only when they share the routing layer (defensive
/// default — better to route around an unknown-net feature than to
/// short into it).
let netOf (NetIndex m) (fp : FlatPolygon) : string option =
    Map.tryFind (flatPolyId fp) m

/// The obstacle set for a wire of net `startNet` on layer `layer`.
/// Returns the FlatPolygons (subset of `flat`) the walk-around must
/// route around. Order matches input order; callers that need a
/// spatial index over the result build one separately.
///
/// A polygon p is an obstacle when:
///   - p.Layer == layer AND netOf(p) ≠ startNet, OR
///   - p.Layer ∈ bridgesOf(layer) AND netOf(p) ≠ startNet
///
/// Polygons whose net is unknown (`netOf` returns `None`) are
/// treated as foreign by default — the walk-around cannot prove
/// they're safe, so it routes around them.
let obstaclesFor
    (layer    : LayerKey)
    (startNet : string)
    (idx      : NetIndex)
    (flat     : FlatPolygon array)
    : FlatPolygon array =
    if not (isRoutingLayer layer) then [||]
    else
        let bridges = Set.ofList (bridgesOf layer)
        let onLayer (fp : FlatPolygon) =
            fp.Layer = layer.Number && fp.DataType = layer.DataType
        let onBridge (fp : FlatPolygon) =
            bridges |> Set.contains { Number = fp.Layer; DataType = fp.DataType }
        flat
        |> Array.filter (fun fp ->
            if not (onLayer fp || onBridge fp) then false
            else
                match netOf idx fp with
                | Some net -> net <> startNet
                | None     -> true)   // unknown net → defensive obstacle
