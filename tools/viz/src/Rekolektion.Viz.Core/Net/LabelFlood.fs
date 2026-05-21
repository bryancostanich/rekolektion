module Rekolektion.Viz.Core.Net.LabelFlood

open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.Core.Layout.Picking

let private bbox (pts: Point list) : (int64 * int64 * int64 * int64) =
    let xs = pts |> List.map (fun p -> p.X)
    let ys = pts |> List.map (fun p -> p.Y)
    List.min xs, List.min ys, List.max xs, List.max ys

let private bboxOverlap a b =
    let (ax0, ay0, ax1, ay1) = a
    let (bx0, by0, bx1, by1) = b
    not (ax1 < bx0 || bx1 < ax0 || ay1 < by0 || by1 < ay0)

/// Two polygons on the SAME layer "touch" if their bboxes overlap and
/// at least one vertex of either lies inside (or on the edge of) the
/// other. Coarse but correct for the rectilinear shapes rekolektion
/// emits.
let private touch (a: Point list) (b: Point list) : bool =
    bboxOverlap (bbox a) (bbox b)
    && (
        a |> List.exists (fun p -> pointInPolygon p b)
        || b |> List.exists (fun p -> pointInPolygon p a)
    )

let private classOfName (n: string) : NetClass =
    let upper = n.ToUpperInvariant()
    if   upper = "VPWR" || upper = "VDD"      then Power
    elif upper = "VGND" || upper = "VSS"      then Ground
    elif upper.StartsWith "CLK"               then Clock
    else Signal

/// Stack connectivity: each contact/via layer bridges a set of
/// routing layers. `licon` (66/44) is the diff/poly/li bridge;
/// `mcon` and the via series are routing-to-routing.
///
/// Two routing polys on different layers share a net iff there's
/// a contact-layer polygon that touches BOTH. The flood-fill
/// below uses this map to step across the stack — without it the
/// flood was same-layer-only and a label on li1 never propagated
/// up to met1 / met2 / met3, so any bus segment routed on a
/// higher metal showed up as "no net" in the inspector.
let private contactBridges : Map<int * int, (int * int) list> =
    [ (66, 44), [ (65, 20); (65, 44); (66, 20); (67, 20) ]  // licon  → diff/tap/poly/li1
      (67, 44), [ (67, 20); (68, 20) ]                       // mcon   → li1/met1
      (68, 44), [ (68, 20); (69, 20) ]                       // via    → met1/met2
      (69, 44), [ (69, 20); (70, 20) ]                       // via2   → met2/met3
      (70, 44), [ (70, 20); (71, 20) ]                       // via3   → met3/met4
      (71, 44), [ (71, 20); (72, 20) ] ]                     // via4   → met4/met5
    |> Map.ofList

/// Inverse: which contact layers does a given routing layer touch?
let private routingContacts : Map<int * int, (int * int) list> =
    contactBridges
    |> Map.toList
    |> List.collect (fun (c, rs) -> rs |> List.map (fun r -> r, c))
    |> List.groupBy fst
    |> List.map (fun (r, l) -> r, l |> List.map snd |> List.distinct)
    |> Map.ofList

/// Build NetMap from labels in the document. Operates on
/// `Layout.Flatten`'s world-coord polys + labels so a label authored
/// at the TOP cell can anchor to a polygon living inside an SRef'd
/// child cell — that case (e.g. `drn_L` placed at top against a FET
/// drain pin) was silently dropping out of the Nets panel under the
/// previous local-frame implementation.
///
/// For each label: find the world-coord polygon on the same layer
/// that contains the label point, then flood across (a) same-layer
/// touching polygons AND (b) cross-layer routing connected via
/// contact/via polys (licon → mcon → via → via2 …). Output
/// `PolyRef`s deduplicate so multiple instances of the same source
/// polygon collapse to one entry — the Nets panel only needs to
/// know the net exists.
let derive (doc: Document) : Map<string, NetEntry> =
    let polys = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
    let labels = Rekolektion.Viz.Core.Layout.Flatten.flattenLabels doc

    // --- One-time per-polygon caches ---------------------------------
    // The previous implementation called `Array.toList p.Points` per
    // `touch` test — for an N-poly cell that's N² fresh List<Point>
    // allocations in the flood-fill, the GC dominator. Cache them.
    // Also classify each polygon as an axis-aligned rectangle —
    // when both polys of a touch test are rectangles, the bbox
    // overlap IS the touch result (no vertex-in-polygon walk needed).
    // For SKY130 the vast majority of routing geometry (wires,
    // pads, contacts, vias) is axis-aligned rectangles, so this
    // short-circuits the hot path almost everywhere.
    let n = polys.Length
    let cachedPts : Point list array = Array.zeroCreate n
    let cachedBbox : (int64 * int64 * int64 * int64) array = Array.zeroCreate n
    let isRect : bool array = Array.zeroCreate n
    for i in 0 .. n - 1 do
        let p = polys.[i]
        cachedPts.[i] <- Array.toList p.Points
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for pt in p.Points do
            if pt.X < xMin then xMin <- pt.X
            if pt.X > xMax then xMax <- pt.X
            if pt.Y < yMin then yMin <- pt.Y
            if pt.Y > yMax then yMax <- pt.Y
        cachedBbox.[i] <- (xMin, yMin, xMax, yMax)
        // Axis-aligned rectangle iff every point sits at one of the
        // two bbox X-extremes AND one of the two bbox Y-extremes.
        // Cheap to verify: walk once and check membership. Holds for
        // both 4-point open and 5-point closed (first=last) forms.
        let mutable rectish = p.Points.Length > 0
        let mutable i' = 0
        while rectish && i' < p.Points.Length do
            let pt = p.Points.[i']
            if not ((pt.X = xMin || pt.X = xMax)
                    && (pt.Y = yMin || pt.Y = yMax))
            then rectish <- false
            i' <- i' + 1
        // Also require both bbox dimensions to be positive — a
        // degenerate 1D rect (line) is not a meaningful "rectangle"
        // for the touch shortcut.
        isRect.[i] <- rectish && xMax > xMin && yMax > yMin

    // Bbox-first touch, indexed. When BOTH polys are axis-aligned
    // rectangles, bbox overlap already encodes touch — skip the
    // vertex-in-polygon walk entirely. Falls back to the original
    // semantics (bbox overlap + at least one vertex inside the
    // other) for non-rectilinear polygons.
    let touchIdx (a: int) (b: int) : bool =
        if not (bboxOverlap cachedBbox.[a] cachedBbox.[b]) then false
        elif isRect.[a] && isRect.[b] then true
        else
            let aPts = cachedPts.[a]
            let bPts = cachedPts.[b]
            aPts |> List.exists (fun p -> pointInPolygon p bPts)
            || bPts |> List.exists (fun p -> pointInPolygon p aPts)

    // Buckets:
    //   `byLayer`  — by (Layer, DataType), used by the flood for
    //                same-layer + contact-layer + cross-routing
    //                neighbor search.
    //   `byLayerNum` — by Layer number ONLY, used by the seed
    //                  lookup since labels and their target polys
    //                  match on Layer but typically differ on the
    //                  datatype/texttype axis (e.g. drawing=20 vs
    //                  label=5). The old code scanned every poly
    //                  per label; this restricts to the right layer.
    let byLayer : Map<int * int, int array> =
        polys
        |> Array.mapi (fun i p -> i, p)
        |> Array.groupBy (fun (_, p) -> p.Layer, p.DataType)
        |> Array.map (fun (k, arr) -> k, arr |> Array.map fst)
        |> Map.ofArray
    let byLayerNum : Map<int, int array> =
        polys
        |> Array.mapi (fun i p -> i, p)
        |> Array.groupBy (fun (_, p) -> p.Layer)
        |> Array.map (fun (k, arr) -> k, arr |> Array.map fst)
        |> Map.ofArray

    // Per-(layer, datatype) uniform-grid spatial index. The flood
    // step "find neighbors of poly X on layer L" used to walk
    // every poly on L (O(layer-size) per step); the index gives
    // just the polys whose bbox sits in X's grid cells
    // (O(local-density) per step). One index per layer so the
    // query returns only candidates on the right layer.
    let cellSize = Spatial.UniformGrid.suggestCellSize cachedBbox
    let layerIndex : Map<int * int, Spatial.UniformGrid.Index> =
        byLayer
        |> Map.map (fun _ idxs ->
            let bboxes = idxs |> Array.map (fun i -> cachedBbox.[i])
            // Build over a synthetic "indices in this layer" array;
            // map back via the bucket array.
            let layerLocalIndex =
                Spatial.UniformGrid.build cellSize bboxes
            layerLocalIndex)
    /// Visit every poly index on `layerKey` whose bbox overlaps
    /// `queryBbox`. Returns global poly indices (into `polys`),
    /// not layer-local indices.
    let visitNeighbors (layerKey: int * int) (queryBbox: int64 * int64 * int64 * int64)
                       (callback: int -> unit) : unit =
        match Map.tryFind layerKey byLayer, Map.tryFind layerKey layerIndex with
        | Some layerArr, Some idx ->
            Spatial.UniformGrid.queryBbox idx queryBbox (fun localI ->
                callback layerArr.[localI])
        | _ -> ()

    labels
    |> Array.fold (fun (acc: Map<string, NetEntry>) (lbl: Rekolektion.Viz.Core.Layout.Flatten.FlatLabel) ->
        // Skip DeviceTerminal labels — those are FET port annotations
        // (D / G / S / B), not net names. Treating them as nets would
        // collapse every device's gate into one fake "G" entry.
        if lbl.Text = "" || lbl.Kind <> NetName then acc else
        // Seed lookup: only walk polys on lbl.Layer, and bbox-reject
        // before the more expensive pointInPolygon test.
        let seedIdx =
            match Map.tryFind lbl.Layer byLayerNum with
            | None -> None
            | Some arr ->
                let mutable found = -1
                let mutable i = 0
                while found < 0 && i < arr.Length do
                    let idx = arr.[i]
                    let (xMin, yMin, xMax, yMax) = cachedBbox.[idx]
                    if lbl.Origin.X >= xMin && lbl.Origin.X <= xMax
                       && lbl.Origin.Y >= yMin && lbl.Origin.Y <= yMax
                       && pointInPolygon lbl.Origin cachedPts.[idx] then
                        found <- idx
                    i <- i + 1
                if found < 0 then None else Some found
        match seedIdx with
        | None -> acc
        | Some i0 ->
            let visited = System.Collections.Generic.HashSet<int>()
            let queue = System.Collections.Generic.Queue<int>()
            queue.Enqueue i0 |> ignore
            visited.Add i0 |> ignore
            let collected = System.Collections.Generic.List<Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon>()
            while queue.Count > 0 do
                let curIdx = queue.Dequeue()
                let cur = polys.[curIdx]
                collected.Add cur
                let curKey = cur.Layer, cur.DataType
                let curBbox = cachedBbox.[curIdx]
                // Same-(layer, datatype) flood: spatial-index query
                // returns only polys whose bbox overlaps the current
                // poly's bbox on the same layer.
                visitNeighbors curKey curBbox (fun cIdx ->
                    if not (visited.Contains cIdx)
                       && touchIdx curIdx cIdx then
                        visited.Add cIdx |> ignore
                        queue.Enqueue cIdx |> ignore)
                // Cross-layer flood via the contact/via stack.
                // For every contact layer that bridges this routing
                // layer, query the index for contact polys near the
                // current poly. Each contact poly that actually
                // touches the current poly then radiates to OTHER
                // routing layers via the same contact, again via
                // the spatial index.
                match Map.tryFind curKey routingContacts with
                | Some contactKeys ->
                    for contactKey in contactKeys do
                        visitNeighbors contactKey curBbox (fun contactIdx ->
                            if touchIdx curIdx contactIdx then
                                let contactBbox = cachedBbox.[contactIdx]
                                match Map.tryFind contactKey contactBridges with
                                | Some routingKeys ->
                                    for routingKey in routingKeys do
                                        if routingKey <> curKey then
                                            visitNeighbors routingKey contactBbox (fun oIdx ->
                                                if not (visited.Contains oIdx)
                                                   && touchIdx contactIdx oIdx then
                                                    visited.Add oIdx |> ignore
                                                    queue.Enqueue oIdx |> ignore)
                                | None -> ())
                | None -> ()
            let polyRefs =
                collected
                |> Seq.map (fun p ->
                    { Structure = p.SourceStructure
                      Layer = p.Layer
                      DataType = p.DataType
                      Index = p.SourceIndex })
                |> Seq.distinct
                |> Seq.toList
            let entry =
                match Map.tryFind lbl.Text acc with
                | Some existing ->
                    { existing with
                        Polygons = existing.Polygons @ polyRefs |> List.distinct }
                | None ->
                    { Name = lbl.Text
                      Class = classOfName lbl.Text
                      Polygons = polyRefs }
            Map.add lbl.Text entry acc) Map.empty
