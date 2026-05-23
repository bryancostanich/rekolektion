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
    Structure       : string
    Layer           : int
    DataType        : int
    Index           : int
    /// Distinguishes physical instances that share the same source
    /// (Structure, Index) — without it, a single PolyId collides
    /// across instances and a polygon labeled SIGN in one instance
    /// gets claimed by drn_R via another instance's label, making
    /// foreign features invisible to the walkaround.
    TopInstanceIndex: int option
}

/// Per-polygon claim set. Each PolyId (which now disambiguates
/// physical instances via TopInstanceIndex) maps to the set of
/// nets whose flood reached it. A wire's startNet counts the
/// polygon as "ours" if startNet is in the set — even when other
/// nets also flooded to it through contacts. That last case
/// indicates electrical connectivity (a polygon shared across
/// multiple labels, e.g. a FET source touching two diff regions);
/// for routing purposes the wire is allowed to extend across any
/// polygon that floods from the start net's label.
type NetIndex = private NetIndex of Map<PolyId, Set<string>>

let private polyIdOf (p : PolygonRef) : PolyId =
    { Structure        = p.Structure
      Layer            = p.Layer
      DataType         = p.DataType
      Index            = p.Index
      TopInstanceIndex = p.TopInstanceIndex }

let private flatPolyId (fp : FlatPolygon) : PolyId =
    { Structure        = fp.SourceStructure
      Layer            = fp.Layer
      DataType         = fp.DataType
      Index            = fp.SourceIndex
      TopInstanceIndex = fp.TopInstanceIndex }

// Cache for buildNetIndex keyed by Map reference identity. The
// canvas holds the same `nets` Map until LabelFlood re-derives
// (doc change), so caching here turns the per-walkaround-frame
// rebuild (76 ms with 1k+ polygons in one net) into a single hit.
// Trim to the last few entries to bound memory.
let private indexCache : System.Collections.Generic.Dictionary<obj, NetIndex> =
    System.Collections.Generic.Dictionary<obj, NetIndex>(HashIdentity.Reference)

let private buildNetIndexFresh (nets : Map<string, NetEntry>) : NetIndex =
    let mutable m : Map<PolyId, Set<string>> = Map.empty
    let addClaim (pRef : PolygonRef) (name : string) =
        let pid = polyIdOf pRef
        let existing =
            match Map.tryFind pid m with
            | Some s -> s
            | None -> Set.empty
        m <- Map.add pid (Set.add name existing) m
    for KeyValue (netName, entry) in nets do
        for pRef in entry.Polygons do
            addClaim pRef netName
    for KeyValue (netName, entry) in nets do
        for pRef in entry.SeedPolygons do
            addClaim pRef netName
    NetIndex m

/// Memoised view of `buildNetIndexFresh`. Reference-identity cache
/// on the `nets` Map — same Map instance returns the same NetIndex
/// without rebuilding. Live-draw cost drops from 76 ms/frame (the
/// fresh build dominates the per-frame walkaround compute) to a
/// dictionary lookup. Cache is trimmed when it grows; safe to call
/// from any thread since the canvas only ever passes ONE Map per
/// active document.
let buildNetIndex (nets : Map<string, NetEntry>) : NetIndex =
    let key = box nets
    match indexCache.TryGetValue(key) with
    | true, idx -> idx
    | _ ->
        let idx = buildNetIndexFresh nets
        if indexCache.Count >= 4 then indexCache.Clear()
        indexCache.[key] <- idx
        idx

/// True when `startNet` is among the polygon's claimants. With
/// multi-claim semantics, a polygon counts as "ours" as soon as
/// startNet's flood touched it — even if another net's flood
/// reached it too. That handles FET source/drain regions where
/// the layout legitimately shares a polygon across labels.
let isOurs (NetIndex m) (startNet : string) (fp : FlatPolygon) : bool =
    match Map.tryFind (flatPolyId fp) m with
    | Some claimants -> Set.contains startNet claimants
    | None -> false

/// All nets that claim this polygon. Diagnostic helper.
let claimantsOf (NetIndex m) (fp : FlatPolygon) : Set<string> =
    match Map.tryFind (flatPolyId fp) m with
    | Some s -> s
    | None -> Set.empty

/// Deprecated single-net lookup (returns one of the claimants if
/// any). Kept so older callers that don't care about ambiguity
/// keep compiling. New code should prefer `isOurs` /
/// `claimantsOf`.
let netOf (NetIndex m) (fp : FlatPolygon) : string option =
    match Map.tryFind (flatPolyId fp) m with
    | Some s when not (Set.isEmpty s) -> Some (Set.minElement s)
    | _ -> None

/// Axis-aligned region in DBU. Used by `obstaclesInRegion` to clip
/// the obstacle set to a local neighbourhood for interactive
/// routing — the walk-around doesn't need to know about a foreign
/// licon 50 µm away when the wire is only 2 µm long.
[<Struct>]
type Region = { XMin : int64; YMin : int64; XMax : int64; YMax : int64 }

let private polyBbox (fp : FlatPolygon) : int64 * int64 * int64 * int64 =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    for pt in fp.Points do
        if pt.X < xMin then xMin <- pt.X
        if pt.X > xMax then xMax <- pt.X
        if pt.Y < yMin then yMin <- pt.Y
        if pt.Y > yMax then yMax <- pt.Y
    xMin, yMin, xMax, yMax

let private polyIntersectsRegion (fp : FlatPolygon) (r : Region) : bool =
    let (xMin, yMin, xMax, yMax) = polyBbox fp
    not (xMax < r.XMin || xMin > r.XMax || yMax < r.YMin || yMin > r.YMax)

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
            else not (isOurs idx startNet fp))

/// Region-bounded obstacle set. Same classification as `obstaclesFor`
/// but only returns polygons whose bbox intersects `region`. The
/// visibility-graph build is O(N²·M) where M = obstacles; clipping
/// to a local neighbourhood is what makes continuous walk-around
/// affordable on real cells. The caller picks the region — typically
/// a bbox around (start, cursor) with a margin so the search can
/// still find detours.
let obstaclesInRegion
    (layer    : LayerKey)
    (startNet : string)
    (idx      : NetIndex)
    (region   : Region)
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
            elif not (polyIntersectsRegion fp region) then false
            else not (isOurs idx startNet fp))

// =========================================================================
// Obstacle snapshot + uniform-grid spatial index
//
// `obstaclesInRegion` is called once per walk-around frame (every cursor
// move). It used to iterate the FULL FlatPolygon array and run isOurs +
// layer/bridge checks per polygon. For identity-stable inputs (the
// canvas keeps the same FlatPolygons + NetMap until a commit lands),
// the FULL obstacle set is the same across frames — only the region
// clip changes. ObstacleSet caches the filtered obstacles + their bboxes
// + a uniform grid keyed by reference identity of the inputs, so:
//   - First frame: O(allFlatPolygons) to build the snapshot.
//   - Subsequent frames: O(obstacles in region) via the grid.
// =========================================================================

/// One obstacle's axis-aligned bbox, paired with the polygon it belongs
/// to. Stored contiguously inside `ObstacleSet` so the grid query
/// returns indices that resolve in one array lookup.
[<Struct>]
type private ObstacleBbox = {
    XMin : int64
    YMin : int64
    XMax : int64
    YMax : int64
}

/// Uniform-grid spatial index over an obstacle set's bboxes. Each
/// grid cell holds the indices of obstacles whose bbox overlaps that
/// cell. Query iterates only the cells covered by the query region
/// and unions their index lists.
///
/// Cell size is heuristic: roughly the macro span / sqrt(N) so the
/// average cell holds ~1 obstacle. Capped to a sensible minimum so
/// degenerate cases (tiny macros, single obstacle) don't blow up.
type private SpatialGrid = {
    CellSize : int64
    OriginX  : int64
    OriginY  : int64
    Cols     : int
    Rows     : int
    /// `Cells.[col + row*Cols]` = obstacle indices overlapping that cell.
    /// `null` for empty cells to save the allocation.
    Cells    : int[] array
}

/// Snapshot of every obstacle in the macro for a given
/// (Layer, StartNet) plus the inputs that drive obstacle classification
/// (FlatPolygons identity, NetMap identity). Cached and reused across
/// frames; invalidated whenever either input reference flips.
type ObstacleSet = private {
    Polygons : FlatPolygon array
    Bboxes   : ObstacleBbox array
    Grid     : SpatialGrid
}

let private bboxOf (fp : FlatPolygon) : ObstacleBbox =
    let (xMin, yMin, xMax, yMax) = polyBbox fp
    { XMin = xMin; YMin = yMin; XMax = xMax; YMax = yMax }

let private buildGrid (bboxes : ObstacleBbox array) : SpatialGrid =
    let n = bboxes.Length
    if n = 0 then
        { CellSize = 1L; OriginX = 0L; OriginY = 0L
          Cols = 0; Rows = 0; Cells = [||] }
    else
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for b in bboxes do
            if b.XMin < xMin then xMin <- b.XMin
            if b.YMin < yMin then yMin <- b.YMin
            if b.XMax > xMax then xMax <- b.XMax
            if b.YMax > yMax then yMax <- b.YMax
        let spanX = max 1L (xMax - xMin)
        let spanY = max 1L (yMax - yMin)
        // Target ~1 obstacle per cell. sqrt(N) cells per axis means
        // span/sqrt(N) DBU per cell. Floor at 100 DBU (0.1 µm in
        // sky130) so the grid stays sane on small macros.
        let sqrtN = max 1.0 (sqrt (float n))
        let target = int64 (max 100.0 (float (max spanX spanY) / sqrtN))
        let cellSize = max 100L target
        let cols = int ((spanX + cellSize - 1L) / cellSize) |> max 1
        let rows = int ((spanY + cellSize - 1L) / cellSize) |> max 1
        let buckets =
            Array.init (cols * rows) (fun _ -> ResizeArray<int>())
        for i in 0 .. n - 1 do
            let b = bboxes.[i]
            let c0 = int ((b.XMin - xMin) / cellSize) |> max 0 |> min (cols - 1)
            let c1 = int ((b.XMax - xMin) / cellSize) |> max 0 |> min (cols - 1)
            let r0 = int ((b.YMin - yMin) / cellSize) |> max 0 |> min (rows - 1)
            let r1 = int ((b.YMax - yMin) / cellSize) |> max 0 |> min (rows - 1)
            for r in r0 .. r1 do
                for c in c0 .. c1 do
                    buckets.[c + r * cols].Add(i)
        let cells =
            buckets
            |> Array.map (fun b ->
                if b.Count = 0 then null else b.ToArray())
        { CellSize = cellSize; OriginX = xMin; OriginY = yMin
          Cols = cols; Rows = rows; Cells = cells }

let private buildObstacleSetFresh
        (layer    : LayerKey)
        (startNet : string)
        (idx      : NetIndex)
        (flat     : FlatPolygon array) : ObstacleSet =
    if not (isRoutingLayer layer) then
        { Polygons = [||]
          Bboxes   = [||]
          Grid     = buildGrid [||] }
    else
        let bridges = Set.ofList (bridgesOf layer)
        let onLayer (fp : FlatPolygon) =
            fp.Layer = layer.Number && fp.DataType = layer.DataType
        let onBridge (fp : FlatPolygon) =
            bridges |> Set.contains { Number = fp.Layer; DataType = fp.DataType }
        let kept = ResizeArray<FlatPolygon>()
        let boxes = ResizeArray<ObstacleBbox>()
        for fp in flat do
            if (onLayer fp || onBridge fp) && not (isOurs idx startNet fp) then
                kept.Add(fp)
                boxes.Add(bboxOf fp)
        let polys = kept.ToArray()
        let bbs   = boxes.ToArray()
        { Polygons = polys; Bboxes = bbs; Grid = buildGrid bbs }

// Composite key on (Layer.Number, Layer.DataType, StartNet) plus
// reference identity for FlatPolygons and the upstream net map.
// Stored as a tuple so the cache dictionary keys on structural
// equality of the layer+net + reference identity of the arrays.
[<Struct>]
type private ObstacleSetKey = {
    Layer    : LayerKey
    StartNet : string
    FlatRef  : obj
    IdxRef   : obj
}

let private obstacleSetCache : System.Collections.Generic.Dictionary<ObstacleSetKey, ObstacleSet> =
    System.Collections.Generic.Dictionary<ObstacleSetKey, ObstacleSet>(HashIdentity.Structural)

/// Memoised obstacle snapshot for `(layer, startNet, flat, idx)`.
/// Cache key uses reference identity for `flat` and the NetIndex's
/// underlying Map, so a doc edit (which re-flattens) or a re-derive
/// (which produces a new NetIndex) invalidates the entry. Cache
/// trimmed when it grows; safe across threads since the canvas
/// passes ONE active set per draft.
let obstacleSet
        (layer    : LayerKey)
        (startNet : string)
        (netMap   : Map<string, NetEntry>)
        (idx      : NetIndex)
        (flat     : FlatPolygon array) : ObstacleSet =
    let key : ObstacleSetKey =
        { Layer = layer; StartNet = startNet
          FlatRef = box flat; IdxRef = box netMap }
    match obstacleSetCache.TryGetValue(key) with
    | true, s -> s
    | _ ->
        let s = buildObstacleSetFresh layer startNet idx flat
        if obstacleSetCache.Count >= 8 then obstacleSetCache.Clear()
        obstacleSetCache.[key] <- s
        s

/// Same semantic as `obstaclesInRegion`, served from the cached
/// snapshot via the uniform grid. First call builds the snapshot;
/// subsequent calls clip via the grid in O(obstacles in region).
let obstaclesInRegionCached
        (set    : ObstacleSet)
        (region : Region) : FlatPolygon array =
    let g = set.Grid
    if g.Cols = 0 || g.Rows = 0 || set.Polygons.Length = 0 then [||]
    else
        let c0 = int ((region.XMin - g.OriginX) / g.CellSize) |> max 0 |> min (g.Cols - 1)
        let c1 = int ((region.XMax - g.OriginX) / g.CellSize) |> max 0 |> min (g.Cols - 1)
        let r0 = int ((region.YMin - g.OriginY) / g.CellSize) |> max 0 |> min (g.Rows - 1)
        let r1 = int ((region.YMax - g.OriginY) / g.CellSize) |> max 0 |> min (g.Rows - 1)
        // Dedup via a visited bitmap — obstacles spanning multiple
        // cells appear in several buckets. n is the snapshot size
        // (small), so a bool array is cheaper than a HashSet.
        let visited = Array.zeroCreate<bool> set.Polygons.Length
        let out = ResizeArray<FlatPolygon>()
        for r in r0 .. r1 do
            for c in c0 .. c1 do
                let bucket = g.Cells.[c + r * g.Cols]
                if not (isNull bucket) then
                    for idx in bucket do
                        if not visited.[idx] then
                            visited.[idx] <- true
                            let b = set.Bboxes.[idx]
                            // Final bbox-vs-region check — the grid
                            // bucket is conservative (covers cell,
                            // not the obstacle), so an obstacle in
                            // the cell might still miss the region.
                            if not (b.XMax < region.XMin
                                    || b.XMin > region.XMax
                                    || b.YMax < region.YMin
                                    || b.YMin > region.YMax) then
                                out.Add(set.Polygons.[idx])
        out.ToArray()
