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
