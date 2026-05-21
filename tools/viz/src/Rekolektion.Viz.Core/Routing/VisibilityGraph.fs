/// Visibility graph for the walk-around router (ADR-0006).
///
/// Given an obstacle set (rectangular bboxes derived from
/// `Routing.Obstacles`), a start point, an end point, and a wire
/// clearance, produce a graph whose nodes are:
///
///   - the start and end points (added per-query)
///   - corner candidates from every obstacle, expanded outward by
///     the clearance so a wire of full width can route around them
///
/// An edge connects two nodes if a manhattan L-path between them
/// (horizontal-then-vertical OR vertical-then-horizontal) does NOT
/// pass through any obstacle's expanded bbox. Edge cost is the L's
/// total length.
///
/// The graph (corner nodes + their pairwise adjacency) is built
/// once per (obstacle set, clearance) and reused for every cursor
/// move. Each query splices start + end into the graph and runs a
/// Dijkstra shortest-path.
module Rekolektion.Viz.Core.Routing.VisibilityGraph

open Rekolektion.Viz.Core.Layout.Flatten

/// A 2D point in DBU. Kept distinct from the (Rkt/Gds) Point types
/// so the search internals don't drag those modules into every
/// caller — VisibilityGraph operates on raw int64 coordinates.
[<Struct>]
type Pt = { X : int64; Y : int64 }

/// Axis-aligned bbox, inclusive on both sides. Obstacles get
/// expanded by `clearance` BEFORE entering the graph so visibility
/// tests don't have to add the margin per edge.
[<Struct>]
type Bbox = { XMin : int64; YMin : int64; XMax : int64; YMax : int64 }

let private bboxOf (fp : FlatPolygon) : Bbox =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    for pt in fp.Points do
        if pt.X < xMin then xMin <- pt.X
        if pt.X > xMax then xMax <- pt.X
        if pt.Y < yMin then yMin <- pt.Y
        if pt.Y > yMax then yMax <- pt.Y
    { XMin = xMin; YMin = yMin; XMax = xMax; YMax = yMax }

let private expand (clearance : int64) (b : Bbox) : Bbox =
    { XMin = b.XMin - clearance
      YMin = b.YMin - clearance
      XMax = b.XMax + clearance
      YMax = b.YMax + clearance }

/// The pre-built portion of the graph: obstacle-corner nodes and
/// pairwise visibility. Cached across mouse moves until obstacles
/// change.
type Prebuilt = {
    Nodes      : Pt array
    Obstacles  : Bbox array
    /// Adjacency[i] = array of (j, cost) where j > i; pairs are
    /// stored once to keep the structure compact, and the search
    /// walks both directions.
    Adjacency  : (int * int64) array array
}

/// True if the closed segment from `(x1,y) → (x2,y)` (inclusive)
/// intersects the bbox interior. Endpoints exactly on the boundary
/// do NOT count as intersection — corner nodes sit on the expanded
/// bbox boundary and must remain connectable.
let private hSegHitsBbox (y : int64) (x1 : int64) (x2 : int64) (b : Bbox) : bool =
    let xa = min x1 x2
    let xb = max x1 x2
    // Segment must straddle the bbox in X and lie strictly inside in Y.
    y > b.YMin && y < b.YMax && xa < b.XMax && xb > b.XMin

let private vSegHitsBbox (x : int64) (y1 : int64) (y2 : int64) (b : Bbox) : bool =
    let ya = min y1 y2
    let yb = max y1 y2
    x > b.XMin && x < b.XMax && ya < b.YMax && yb > b.YMin

/// True if the L-path (a → corner → b) is clear of every obstacle.
///   H-first: a → (b.X, a.Y) → b. H-seg at y = a.Y from a.X to b.X;
///            V-seg at x = b.X from a.Y to b.Y.
///   V-first: a → (a.X, b.Y) → b. V-seg at x = a.X from a.Y to b.Y;
///            H-seg at y = b.Y from a.X to b.X.
let private lClear
    (obstacles : Bbox array)
    (horizontalFirst : bool)
    (a : Pt) (b : Pt) : bool =
    let hSegY = if horizontalFirst then a.Y else b.Y
    let vSegX = if horizontalFirst then b.X else a.X
    let mutable clear = true
    let mutable i = 0
    while clear && i < obstacles.Length do
        let o = obstacles.[i]
        let hitH = hSegHitsBbox hSegY a.X b.X o
        let hitV = vSegHitsBbox vSegX a.Y b.Y o
        if hitH || hitV then clear <- false
        i <- i + 1
    clear

/// Manhattan-visible: an L-path in EITHER posture clears every
/// obstacle. Either posture is acceptable for graph adjacency —
/// the search picks the cheaper one per edge.
let private manhattanVisible (obstacles : Bbox array) (a : Pt) (b : Pt) : bool =
    lClear obstacles true a b
    || lClear obstacles false a b

let private manhattanCost (a : Pt) (b : Pt) : int64 =
    abs (b.X - a.X) + abs (b.Y - a.Y)

/// Corner candidates for one obstacle: the four outside corners of
/// its expanded bbox. These are the points the walk-around path
/// turns at.
let private cornersOf (b : Bbox) : Pt array =
    [|
        { X = b.XMin; Y = b.YMin }
        { X = b.XMax; Y = b.YMin }
        { X = b.XMin; Y = b.YMax }
        { X = b.XMax; Y = b.YMax }
    |]

/// Build the prebuilt graph for an obstacle set. O(O² · O) in the
/// worst case (corner-pair visibility test scans all obstacles);
/// for the typical small obstacle sets (single FET wall, ~16
/// obstacles → 64 corner nodes) this is comfortably sub-millisecond.
let build (clearance : int64) (obstacles : FlatPolygon array) : Prebuilt =
    let bboxes =
        obstacles
        |> Array.map (bboxOf >> expand clearance)
    let nodes =
        bboxes
        |> Array.collect cornersOf
        |> Array.distinct
    let adjacency =
        Array.init nodes.Length (fun i ->
            let from = nodes.[i]
            let acc = System.Collections.Generic.List<int * int64>()
            for j in (i + 1) .. (nodes.Length - 1) do
                let toN = nodes.[j]
                if manhattanVisible bboxes from toN then
                    acc.Add((j, manhattanCost from toN))
            acc.ToArray())
    { Nodes = nodes; Obstacles = bboxes; Adjacency = adjacency }

/// Shortest-path query: splice `start` and `goal` into the prebuilt
/// graph, run Dijkstra, return the manhattan node sequence from
/// start to goal. Returns `None` when no path exists.
///
/// "No path" can happen when:
///   - start or goal is inside an obstacle interior (start/goal
///     can't see any node), or
///   - the obstacle set fully encloses one of them.
let shortestPath
    (graph : Prebuilt)
    (start : Pt)
    (goal  : Pt) : Pt list option =
    // Adjacency for the augmented graph: prebuilt corners +
    // {start, goal} appended. Augment edges are computed lazily on
    // demand so we don't allocate a full N+2 adjacency table.
    let n = graph.Nodes.Length
    let startIdx = n
    let goalIdx  = n + 1
    let nodeOf idx =
        if idx < n then graph.Nodes.[idx]
        elif idx = startIdx then start
        else goal
    let neighbours (i : int) : (int * int64) seq =
        seq {
            if i < n then
                // Prebuilt adjacency — already manhattan-visible.
                for (j, c) in graph.Adjacency.[i] do
                    yield (j, c)
                // Reverse edges (other i's adjacency listed us; we
                // need them on this side too for the search).
                for k in 0 .. (n - 1) do
                    if k < i then
                        for (j, c) in graph.Adjacency.[k] do
                            if j = i then yield (k, c)
                // start / goal augment edges.
                let a = graph.Nodes.[i]
                if manhattanVisible graph.Obstacles a start then
                    yield (startIdx, manhattanCost a start)
                if manhattanVisible graph.Obstacles a goal then
                    yield (goalIdx, manhattanCost a goal)
            else
                let here = nodeOf i
                for k in 0 .. (n - 1) do
                    let nk = graph.Nodes.[k]
                    if manhattanVisible graph.Obstacles here nk then
                        yield (k, manhattanCost here nk)
                if i = startIdx then
                    if manhattanVisible graph.Obstacles here goal then
                        yield (goalIdx, manhattanCost here goal)
                else
                    if manhattanVisible graph.Obstacles here start then
                        yield (startIdx, manhattanCost here start)
        }
    // Dijkstra with a System.Collections.Generic.PriorityQueue.
    let total = n + 2
    let dist = Array.create total System.Int64.MaxValue
    let prev = Array.create total -1
    dist.[startIdx] <- 0L
    let pq = System.Collections.Generic.PriorityQueue<int, int64>()
    pq.Enqueue(startIdx, 0L)
    let mutable found = false
    while not found && pq.Count > 0 do
        let u = pq.Dequeue()
        if u = goalIdx then found <- true
        elif dist.[u] <> System.Int64.MaxValue then
            for (v, w) in neighbours u do
                let nd = dist.[u] + w
                if nd < dist.[v] then
                    dist.[v] <- nd
                    prev.[v] <- u
                    pq.Enqueue(v, nd)
    if not found then None
    else
        // Reconstruct path goal → start, then reverse.
        let rec walk acc i =
            if i < 0 then acc
            else walk ((nodeOf i) :: acc) prev.[i]
        Some (walk [] goalIdx)
