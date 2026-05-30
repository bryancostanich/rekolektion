/// Wire-as-first-class-thing helpers.
///
/// Wires don't exist in the on-disk schema — a wire is a set of
/// RectEls that share a `WireId`, stored as a `Property` with key
/// `"wire-id"` on each RectEl. The Property round-trips through the
/// `.rkt` reader/writer for free; this module exists so the rest of
/// the app addresses wires through a typed API instead of poking
/// magic strings into the Properties list.
///
/// Why first-class WireId at all: the MCP surface needs addressable
/// wires. Verbs like "drag wire 42 by 40 nm" or "delete the third
/// segment of wire 17" collapse under inference-based identity
/// because inferred IDs change every time RectEls get renumbered.
/// First-class IDs survive undo/redo, file round-trip, and
/// re-derive cycles. See `tools/viz/docs/route_editing_plan.md`.
module Rekolektion.Viz.Core.Routing.Wire

open Rekolektion.Viz.Core.Rkt.Types

/// Property key under which a wire's identifier lives. Reader /
/// writer already handle Properties; nothing else changes to make
/// the field round-trip.
[<Literal>]
let wireIdKey = "wire-id"

/// Read the WireId from a rectangle, if any. Returns `None` for
/// rectangles authored before WireIds existed or for one-off
/// geometry that isn't part of a wire (e.g., a hand-drawn rect
/// that the user didn't route).
let getWireId (r : Rectangle) : int option =
    r.Props
    |> List.tryPick (fun p ->
        if p.Key = wireIdKey then
            match p.Value with
            | PvInt n -> Some (int n)
            | _ -> None
        else None)

/// Stamp a WireId onto a rectangle. Replaces any existing wire-id
/// property; preserves all other Props. Caller assigns IDs (this
/// module doesn't allocate).
let setWireId (id : int) (r : Rectangle) : Rectangle =
    let stripped =
        r.Props |> List.filter (fun p -> p.Key <> wireIdKey)
    let next =
        { Key = wireIdKey; Value = PvInt (int64 id) }
    { r with Props = stripped @ [ next ] }

/// Highest WireId currently in use across every RectEl in every
/// cell of `doc`. Returns `0` when no wired rectangle exists, so
/// the natural "next id" is `maxWireId doc + 1`.
let maxWireId (doc : Document) : int =
    let mutable hi = 0
    for c in doc.Cells do
        for el in c.Elements do
            match el with
            | RectEl r ->
                match getWireId r with
                | Some n when n > hi -> hi <- n
                | _ -> ()
            | _ -> ()
    hi

/// Next available WireId for `doc`. Monotonic — never reuses an
/// id, even after deletes. Cheap O(elements) scan; commits are
/// infrequent enough that caching the counter on the Document
/// isn't worth the bookkeeping.
let nextWireId (doc : Document) : int =
    maxWireId doc + 1

/// ⚠ TECH DEBT — remove (or comment out) once topology-aware
/// routing lands.  See `docs/topology_aware_routing.md`.
///
/// This is a byte-identical-bbox dedup backstop: it cleans up
/// duplicates AFTER they're emitted instead of preventing them at
/// route-plan time.  The proper fix (Phase 1: pin-level sharing at
/// commit, Phase 2: net-aware path planning) recognises shared
/// anchors / spans during routing and never emits the dupes in the
/// first place.  Once Phase 1 ships, this function should still be
/// removable — Phase 1 covers the byte-identical case too — but
/// keep it as a one-shot `TidyRoutingGeometry` fallback for files
/// authored under earlier routers if the cleanup story isn't
/// otherwise covered.
///
/// Collapse RectEls that share an identical (layer, normalised
/// bbox) within each cell.  The FIRST occurrence in document
/// order wins — its props, including its wire-id, are kept; every
/// subsequent duplicate is dropped.
///
/// Used to clean up the routing-emit pattern where two wires
/// (different wire-ids) sharing a physical endpoint each paint
/// their own complete via stack — fully overlapped mcon + via1
/// + pad geometry that DRC flags as spacing-zero violations.
/// `commitRouteWith` runs this after every route commit so new
/// dupes don't accumulate; the `TidyRoutingGeometry` Msg runs it
/// on demand to clean up files authored before the fix.
///
/// Provenance loss: a duplicate's wire-id annotation is dropped
/// silently.  That wire's `segmentsOf` query returns one fewer
/// rect at the shared endpoint, but the physical connectivity is
/// preserved (same rect, both wires terminate on it).  Multi-id
/// provenance via a (wire-ids n1 n2 …) list prop is a future
/// schema extension; out of scope for this pass.
let dedupCoincidentRects (doc : Document) : Document =
    let layerKey (r : Rectangle) : int * int =
        Rekolektion.Viz.Core.Rkt.ToGds.layerToGds r.Layer
    let bboxKey (r : Rectangle) =
        let (l, d) = layerKey r
        let xLo = min r.X1 r.X2
        let xHi = max r.X1 r.X2
        let yLo = min r.Y1 r.Y2
        let yHi = max r.Y1 r.Y2
        (l, d, xLo, yLo, xHi, yHi)
    let updatedCells =
        doc.Cells
        |> List.map (fun c ->
            let seen =
                System.Collections.Generic.HashSet<
                    int * int * int64 * int64 * int64 * int64>()
            let kept =
                c.Elements
                |> List.choose (fun el ->
                    match el with
                    | RectEl r ->
                        if seen.Add (bboxKey r) then Some el
                        else None
                    | _ -> Some el)
            { c with Elements = kept })
    { doc with Cells = updatedCells }

/// All rectangles in `doc` belonging to wire `id`, in document
/// order. Empty when no rectangle carries that id. Used by
/// segment-drag and vertex-edit operations to find the wire's
/// full set of segments.
let segmentsOf (id : int) (doc : Document) : (Cell * int * Rectangle) list =
    [ for c in doc.Cells do
          for idx, el in List.indexed c.Elements do
              match el with
              | RectEl r when getWireId r = Some id -> yield (c, idx, r)
              | _ -> () ]

/// Axis a segment runs along. Decided by the larger bbox extent:
/// horizontal = span on X is greater than span on Y. Square (the
/// rare 1-DBU stub) is treated as horizontal — there's no
/// perpendicular-drag meaning for a true square, and the caller
/// shouldn't be hitting one in routing scenarios.
type SegmentAxis = Horizontal | Vertical

let segmentAxis (r : Rectangle) : SegmentAxis =
    let dx = abs (r.X2 - r.X1)
    let dy = abs (r.Y2 - r.Y1)
    if dx >= dy then Horizontal else Vertical

/// True when the world point `(x, y)` falls inside the rect's
/// bbox (inclusive). The wire hit-test the canvas uses on
/// mouse-down for segment pickup — bbox-inclusive matches the
/// visible filled rect the user sees.
let containsPoint (x : int64) (y : int64) (r : Rectangle) : bool =
    let xLo = min r.X1 r.X2
    let xHi = max r.X1 r.X2
    let yLo = min r.Y1 r.Y2
    let yHi = max r.Y1 r.Y2
    x >= xLo && x <= xHi && y >= yLo && y <= yHi

/// Find the topmost rect in `doc` whose bbox contains `(x, y)`.
/// Returns `(wireId?, cellName, rectIndexInCell, rectangle)` of
/// the hit rect, or `None`. WireId is `None` when the rect was
/// authored without a wire tag (hand-edited geometry, pre-WireId
/// files); segment-drag treats those as single-rect "wires" with
/// no neighbour lookup. Walks document order; ties go to the
/// later-authored rect (renderer paints later rects on top, so
/// the hit-test matches the visible top).
let findSegmentAt (x : int64) (y : int64) (doc : Document)
                  : (int option * string * int * Rectangle) option =
    let mutable result : (int option * string * int * Rectangle) option = None
    for c in doc.Cells do
        for idx, el in List.indexed c.Elements do
            match el with
            | RectEl r when containsPoint x y r ->
                result <- Some (getWireId r, c.Name, idx, r)
            | _ -> ()
    result

/// True when two rects are "collinear and abutting" — they are
/// effectively one logical segment that's stored as multiple
/// rects (common when a wire is drawn as several adjacent legs).
/// Requirements:
///   - same layer
///   - same perpendicular axis bounds (a horizontal pair shares Y1
///     and Y2 exactly; a vertical pair shares X1 and X2)
///   - long-axis ranges overlap or touch
///   - if both carry a WireId, they must match (untagged + tagged
///     mixes are allowed; the merge picks the tagged id)
let private sameLayer (a : Rectangle) (b : Rectangle) =
    a.Layer = b.Layer

let private wireIdsCompatible (a : Rectangle) (b : Rectangle) =
    match getWireId a, getWireId b with
    | Some x, Some y -> x = y
    | _ -> true

let collinearAbut (a : Rectangle) (b : Rectangle) : bool =
    if not (sameLayer a b) then false
    elif not (wireIdsCompatible a b) then false
    else
        let axA = segmentAxis a
        let axB = segmentAxis b
        if axA <> axB then false
        else
            let (aXLo, aYLo, aXHi, aYHi) =
                min a.X1 a.X2, min a.Y1 a.Y2, max a.X1 a.X2, max a.Y1 a.Y2
            let (bXLo, bYLo, bXHi, bYHi) =
                min b.X1 b.X2, min b.Y1 b.Y2, max b.X1 b.X2, max b.Y1 b.Y2
            match axA with
            | Horizontal ->
                aYLo = bYLo && aYHi = bYHi
                && aXHi >= bXLo && bXHi >= aXLo
            | Vertical ->
                aXLo = bXLo && aXHi = bXHi
                && aYHi >= bYLo && bYHi >= aYLo

/// Transitive closure of `collinearAbut` starting from the rect at
/// `(cellName, seedIdx)`. Returns all rects (with their indices)
/// that form one logical segment with the seed. Always includes
/// the seed itself. Used by segment-drag to treat a chain of
/// collinear abutting rects as one virtual segment for both
/// pickup hit-test and commit (the chain is replaced by a single
/// merged rect).
let collinearGroupOf
        (cellName : string)
        (seedIdx : int)
        (doc : Document) : (int * Rectangle) list =
    let cellOpt = doc.Cells |> List.tryFind (fun c -> c.Name = cellName)
    match cellOpt with
    | None -> []
    | Some c ->
        let indexed = c.Elements |> List.indexed
        let seedRect =
            indexed
            |> List.tryPick (fun (i, el) ->
                if i = seedIdx then
                    match el with RectEl r -> Some r | _ -> None
                else None)
        match seedRect with
        | None -> []
        | Some seed ->
            // BFS over the indexed rects, queue = indices to expand.
            let visited = System.Collections.Generic.HashSet<int>()
            let result = System.Collections.Generic.List<int * Rectangle>()
            let queue = System.Collections.Generic.Queue<int * Rectangle>()
            visited.Add seedIdx |> ignore
            queue.Enqueue (seedIdx, seed)
            result.Add (seedIdx, seed)
            while queue.Count > 0 do
                let (_, cur) = queue.Dequeue()
                for (i, el) in indexed do
                    if not (visited.Contains i) then
                        match el with
                        | RectEl r when collinearAbut cur r ->
                            visited.Add i |> ignore
                            result.Add (i, r)
                            queue.Enqueue (i, r)
                        | _ -> ()
            result |> List.ofSeq

/// True when two rects' bboxes touch or overlap (inclusive
/// boundaries). Used by wire selection to walk the connected
/// component of top-cell rects from a picked rect — corner
/// touching, edge-to-edge abutting, and full overlap all count.
let bboxesTouch (a : Rectangle) (b : Rectangle) : bool =
    let aXLo = min a.X1 a.X2
    let aYLo = min a.Y1 a.Y2
    let aXHi = max a.X1 a.X2
    let aYHi = max a.Y1 a.Y2
    let bXLo = min b.X1 b.X2
    let bYLo = min b.Y1 b.Y2
    let bXHi = max b.X1 b.X2
    let bYHi = max b.Y1 b.Y2
    aXHi >= bXLo && bXHi >= aXLo
    && aYHi >= bYLo && bYHi >= aYLo

/// Connected component of bbox-touching rects in one cell, starting
/// from `seedIdx`. Two predicates:
///   - `keep i r`: include rect `i` in the result set
///   - `propagate i r`: expand the BFS from rect `i` to its
///     neighbours. Returning `false` makes `r` a terminus —
///     it stays in the result but its neighbours aren't visited
///     through it. Used for pin polygons in wire selection: the
///     pin is part of the wire that terminates at it, but the
///     wire on the OTHER side of the pin (different wire that
///     shares the same physical anchor) is NOT reached.
///
/// Returns rect indices in document order. Empty when the seed
/// itself fails `keep`, or when the cell isn't in the document.
let connectedComponent
        (cellName : string)
        (seedIdx : int)
        (keep : int -> Rectangle -> bool)
        (propagate : int -> Rectangle -> bool)
        (doc : Document) : int list =
    let cellOpt = doc.Cells |> List.tryFind (fun c -> c.Name = cellName)
    match cellOpt with
    | None -> []
    | Some c ->
        let indexed = c.Elements |> List.indexed
        let seedRect =
            indexed
            |> List.tryPick (fun (i, el) ->
                if i = seedIdx then
                    match el with RectEl r -> Some r | _ -> None
                else None)
        match seedRect with
        | None -> []
        | Some seed when not (keep seedIdx seed) -> []
        | Some seed ->
            let visited = System.Collections.Generic.HashSet<int>()
            let result = System.Collections.Generic.List<int>()
            let queue = System.Collections.Generic.Queue<int * Rectangle>()
            visited.Add seedIdx |> ignore
            result.Add seedIdx
            // Seed always seeds expansion, even if it's a pin —
            // otherwise clicking a pin selects only the pin, not
            // the wire connected to it. Subsequent pins reached
            // by the BFS DO terminate (per `propagate`), so wire
            // selection still stops at the OTHER side of a shared
            // pin.
            queue.Enqueue (seedIdx, seed)
            while queue.Count > 0 do
                let (curIdx, cur) = queue.Dequeue()
                for (i, el) in indexed do
                    if not (visited.Contains i) then
                        match el with
                        | RectEl r when keep i r && bboxesTouch cur r ->
                            visited.Add i |> ignore
                            result.Add i
                            // Only enqueue if THIS rect (the one
                            // we just added) is allowed to
                            // propagate. The check ignores curIdx
                            // — propagation is a property of the
                            // ADDED rect, not the source.
                            if propagate i r then
                                queue.Enqueue (i, r)
                        | _ -> ()
            result |> List.ofSeq

/// True when `n`'s long-axis endpoint touches `picked`'s body,
/// i.e., `n` is a perpendicular cross-wire whose tip lands inside
/// `picked`'s extent. The connection point for "stretch attached
/// cross-wire when picked wire moves." Naive `bboxesTouch` was
/// too permissive — it grabbed every rect overlapping the picked
/// wire's bbox, including foreign-net cell rects and chip-boundary
/// rails. This restricts to actual cross-wire endpoint touches.
let private endpointTouches (picked : Rectangle) (n : Rectangle) : bool =
    let pickedAxis = segmentAxis picked
    let nAxis = segmentAxis n
    if pickedAxis = nAxis then false
    else
        let pxLo = min picked.X1 picked.X2
        let pxHi = max picked.X1 picked.X2
        let pyLo = min picked.Y1 picked.Y2
        let pyHi = max picked.Y1 picked.Y2
        let nxLo = min n.X1 n.X2
        let nxHi = max n.X1 n.X2
        let nyLo = min n.Y1 n.Y2
        let nyHi = max n.Y1 n.Y2
        match pickedAxis with
        | Horizontal ->
            // Vertical N: a Y-endpoint inside picked's Y range AND
            // X-range overlaps picked's X range (n's tip lands ON
            // the picked wire's body).
            let yTipIn =
                (nyLo >= pyLo && nyLo <= pyHi)
                || (nyHi >= pyLo && nyHi <= pyHi)
            let xOverlap = nxHi >= pxLo && pxHi >= nxLo
            yTipIn && xOverlap
        | Vertical ->
            let xTipIn =
                (nxLo >= pxLo && nxLo <= pxHi)
                || (nxHi >= pxLo && nxHi <= pxHi)
            let yOverlap = nyHi >= pyLo && pyHi >= nyLo
            xTipIn && yOverlap

/// Top-cell RectEls in `cellName` that are cross-wire neighbours
/// of `r`: same layer, perpendicular axis, and at least one of
/// the candidate's long-axis endpoints lands inside `r`'s body
/// (endpoint touch). Excludes indices in `excludeIndices`
/// (typically the picked wire's own group + extras).
///
/// Pre-fix this was a bare `bboxesTouch` test that pulled in
/// every rect overlapping `r`'s bbox — chip-boundary rails,
/// foreign-net cell rects, unrelated wires. The downstream drag
/// commit re-stamped those rects with the picked wire's WireId,
/// silently corrupting tens of cell elements per drag. Restrict
/// here to actual cross-wire endpoint touches on the same layer.
let touchingNeighbors
        (cellName : string)
        (excludeIndices : Set<int>)
        (r : Rectangle)
        (doc : Document) : (int * Rectangle) list =
    let cellOpt = doc.Cells |> List.tryFind (fun c -> c.Name = cellName)
    match cellOpt with
    | None -> []
    | Some c ->
        [ for idx, el in List.indexed c.Elements do
              if not (Set.contains idx excludeIndices) then
                  match el with
                  | RectEl r' when
                          r'.Layer = r.Layer
                          && endpointTouches r r' ->
                      yield (idx, r')
                  | _ -> () ]

/// Axis-aligned bbox of the union of `rects`. Used to materialise
/// a collinear-abutting group as one virtual segment for drag.
/// Empty list returns a degenerate (0,0,0,0) rect; callers should
/// guard.
let unionBbox (rects : Rectangle list) : int64 * int64 * int64 * int64 =
    if List.isEmpty rects then (0L, 0L, 0L, 0L)
    else
        let mutable xLo = System.Int64.MaxValue
        let mutable yLo = System.Int64.MaxValue
        let mutable xHi = System.Int64.MinValue
        let mutable yHi = System.Int64.MinValue
        for r in rects do
            let rxLo = min r.X1 r.X2
            let ryLo = min r.Y1 r.Y2
            let rxHi = max r.X1 r.X2
            let ryHi = max r.Y1 r.Y2
            if rxLo < xLo then xLo <- rxLo
            if ryLo < yLo then yLo <- ryLo
            if rxHi > xHi then xHi <- rxHi
            if ryHi > yHi then yHi <- ryHi
        (xLo, yLo, xHi, yHi)

/// Strip WireId from rectangles that share a WireId but aren't
/// spatially connected as one logical wire. A legitimate wire's
/// rectangles all touch each other (via corner overlaps, pad
/// stacks, or contiguous segments); disjoint spatial clusters
/// under one WireId are corruption from earlier drag commits that
/// re-stamped unrelated rects.
///
/// For each (cell, WireId) group: compute connected components
/// using bbox-touching. If more than ONE component, the WireId
/// is suspect — strip it from all rects in that group. The
/// geometry is preserved; only the wire-grouping metadata is
/// removed. Subsequent selections fall back to
/// `collinearGroupOf`, which behaves correctly.
///
/// Returns `(scrubbed-doc, strip-count)` where strip-count is
/// the number of distinct WireIds that were removed.
let scrubDispersedWireIds (doc : Document) : Document * int =
    let mutable stripped = 0
    let cells' =
        doc.Cells
        |> List.map (fun c ->
            // Per-WireId, gather indices into the cell.
            let byWireId =
                c.Elements
                |> List.indexed
                |> List.choose (fun (i, el) ->
                    match el with
                    | RectEl r ->
                        match getWireId r with
                        | Some wid -> Some (wid, (i, r))
                        | None -> None
                    | _ -> None)
                |> List.groupBy fst
                |> List.map (fun (wid, xs) ->
                    wid, xs |> List.map snd)
            // For each group, compute connected components via
            // bbox-touching. If >1 component, mark indices for
            // WireId strip.
            let toStrip = System.Collections.Generic.HashSet<int>()
            for (wid, members) in byWireId do
                if List.length members < 2 then () else
                // Union-find over members by bbox-touching.
                let n = List.length members
                let memArr = members |> List.toArray
                let parent = Array.init n id
                let rec find x =
                    if parent.[x] = x then x
                    else
                        let r = find parent.[x]
                        parent.[x] <- r
                        r
                let union x y =
                    let rx = find x
                    let ry = find y
                    if rx <> ry then parent.[rx] <- ry
                for i in 0 .. n - 1 do
                    for j in i + 1 .. n - 1 do
                        let (_, ri) = memArr.[i]
                        let (_, rj) = memArr.[j]
                        if bboxesTouch ri rj then union i j
                let roots =
                    [| for i in 0 .. n - 1 -> find i |]
                    |> Array.distinct
                if roots.Length > 1 then
                    stripped <- stripped + 1
                    for (idx, _) in members do
                        toStrip.Add idx |> ignore
                    ignore wid
            if toStrip.Count = 0 then c
            else
                let elems' =
                    c.Elements
                    |> List.mapi (fun i el ->
                        if toStrip.Contains i then
                            match el with
                            | RectEl r ->
                                let props' =
                                    r.Props
                                    |> List.filter (fun p -> p.Key <> wireIdKey)
                                RectEl { r with Props = props' }
                            | other -> other
                        else el)
                { c with Elements = elems' })
    { doc with Cells = cells' }, stripped

/// Same-wire segments in the cell touching `r` at its endpoints.
/// "Touching" = sharing a bbox edge along the wire's long axis,
/// i.e., the next segment in the polyline. Returns up to two
/// entries (start-end, end-end); could be 0, 1, or 2 depending
/// on whether `r` is a terminus or middle of the wire.
///
/// Uses bbox proximity rather than the wire's polyline order
/// because the on-disk schema has no notion of polyline ordering —
/// the wire is just a bag of RectEls that share a WireId. The
/// renderer also doesn't care; only segment-drag needs this.
let neighborsOf
        (wireId : int)
        (cellName : string)
        (selfIdx : int)
        (r : Rectangle)
        (doc : Document) : (int * Rectangle) list =
    let xLo = min r.X1 r.X2
    let xHi = max r.X1 r.X2
    let yLo = min r.Y1 r.Y2
    let yHi = max r.Y1 r.Y2
    [ for c in doc.Cells do
          if c.Name = cellName then
              for idx, el in List.indexed c.Elements do
                  if idx <> selfIdx then
                      match el with
                      | RectEl r' when getWireId r' = Some wireId ->
                          let xLo' = min r'.X1 r'.X2
                          let xHi' = max r'.X1 r'.X2
                          let yLo' = min r'.Y1 r'.Y2
                          let yHi' = max r'.Y1 r'.Y2
                          // Overlap-or-touch in both axes — a
                          // neighbour shares an edge OR overlaps
                          // (corner pieces between two legs of an
                          // L share bbox in both axes).
                          let xTouch = xHi' >= xLo && xLo' <= xHi
                          let yTouch = yHi' >= yLo && yLo' <= yHi
                          if xTouch && yTouch then yield (idx, r')
                      | _ -> () ]
