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
    /// Clearance the obstacle bboxes were expanded by at build
    /// time. `shortestPath` uses this to test "inside the
    /// ORIGINAL polygon" (= shrunk expanded bbox) for the
    /// endpoint exemption — so a pin doesn't accidentally exempt
    /// every neighbour within its clearance margin.
    Clearance  : int64
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
    { Nodes = nodes; Obstacles = bboxes; Adjacency = adjacency
      Clearance = clearance }

/// Shortest-path query: splice `start` and `goal` into the prebuilt
/// graph, run Dijkstra, return the manhattan node sequence from
/// start to goal. Returns `None` when no path exists.
///
/// Obstacles whose INTERIOR contains `start` or `goal` are dropped
/// from the visibility tests for the duration of this query — the
/// wire has to begin and end somewhere, and a snap-target pin can
/// easily land inside the clearance-expanded bbox of an adjacent
/// foreign feature. Without this exemption, every wire from a
/// tight pin would return `noPath` and the user would see a dumb
/// straight L. Edges between non-start/goal corner nodes still use
/// the FULL obstacle set, so the search can't sneak through a
/// foreign feature mid-route.
let shortestPath
    (graph : Prebuilt)
    (start : Pt)
    (goal  : Pt) : Pt list option =
    let inside (pt : Pt) (b : Bbox) =
        pt.X > b.XMin && pt.X < b.XMax
        && pt.Y > b.YMin && pt.Y < b.YMax
    // Per-edge-type exemption, against the EXPANDED bbox. Looser
    // than the strict-original-bbox variant — lets the wire escape
    // a tight pin AND reach a cursor that mid-drag passes near a
    // foreign feature. Trade-off: the corner↔goal edge may briefly
    // cross a foreign clearance zone if the cursor sits in it,
    // producing a path the live-DRC overlay flags. Pin landings
    // (snap targets) sit at polygon centroids well clear of
    // foreign expanded bboxes, so committed wires are DRC-clean.
    //   • start↔corner edges: skip obstacles whose EXPANDED bbox
    //     contains start.
    //   • corner↔goal edges: skip obstacles whose EXPANDED bbox
    //     contains goal.
    //   • corner↔corner edges: FULL obstacle set (prebuilt).
    //   • direct start↔goal: FULL obstacle set, no exemption —
    //     prevents trivialStraight shortcuts.
    let startObstacles =
        graph.Obstacles |> Array.filter (fun b -> not (inside start b))
    let goalObstacles =
        graph.Obstacles |> Array.filter (fun b -> not (inside goal b))
    // Steiner points on the start/goal X columns aligned with each
    // obstacle's expanded Y boundaries. Without these the only
    // graph nodes are obstacle bbox corners, so the shortest path
    // tends to use a corner at start's Y or goal's Y — the wire
    // then renders as "horizontal stub + vertical" instead of
    // "vertical, jog OUT, vertical, jog BACK, vertical." Steiner
    // points give the search exit/return points on the wire's
    // intended column. Tested only for obstacles whose Y range
    // overlaps the corridor between start and goal, to keep the
    // node count bounded.
    let yLo = min start.Y goal.Y
    let yHi = max start.Y goal.Y
    let xLo = min start.X goal.X
    let xHi = max start.X goal.X
    let steiners : Pt array =
        let acc = System.Collections.Generic.List<Pt>()
        for b in graph.Obstacles do
            // Only consider obstacles in the corridor — their
            // expanded bbox must overlap the start/goal rectangle.
            let overlaps =
                b.XMin < xHi && b.XMax > xLo
                && b.YMin < yHi && b.YMax > yLo
            if overlaps then
                if start.X <> goal.X then
                    acc.Add { X = start.X; Y = b.YMin - 1L }
                    acc.Add { X = start.X; Y = b.YMax + 1L }
                    acc.Add { X = goal.X;  Y = b.YMin - 1L }
                    acc.Add { X = goal.X;  Y = b.YMax + 1L }
                else
                    // Single-column route: only one set needed.
                    acc.Add { X = start.X; Y = b.YMin - 1L }
                    acc.Add { X = start.X; Y = b.YMax + 1L }
        acc.ToArray()
    let n = graph.Nodes.Length
    let s = steiners.Length
    // Index layout: [0..n-1] obstacle corners, [n..n+s-1] Steiner
    // points, n+s = startIdx, n+s+1 = goalIdx.
    let steinerBase = n
    let startIdx = n + s
    let goalIdx  = n + s + 1
    let nodeOf idx =
        if idx < n then graph.Nodes.[idx]
        elif idx < startIdx then steiners.[idx - steinerBase]
        elif idx = startIdx then start
        else goal
    // Tiebreaker discount: edges landing on a Steiner point shave
    // 1 DBU off the manhattan cost. Two paths with identical raw
    // manhattan distances — one via Steiners, one direct — would
    // otherwise tie and Dijkstra picks arbitrarily. The discount
    // breaks the tie toward Steiner-rich paths, which keep the wire
    // on the start.X / goal.X columns and produce V-first / V-last
    // movements. Magnitude is small enough that it never overrides
    // an actually-shorter non-Steiner path.
    let steinerDiscount (v : int) (cost : int64) : int64 =
        if v >= steinerBase && v < startIdx then max 0L (cost - 1L)
        else cost
    let neighbours (i : int) : (int * int64) seq =
        seq {
            if i < n then
                // Corner node. Prebuilt corner↔corner adjacency
                // (full obstacle set).
                for (j, c) in graph.Adjacency.[i] do
                    yield (j, c)
                for k in 0 .. (n - 1) do
                    if k < i then
                        for (j, c) in graph.Adjacency.[k] do
                            if j = i then yield (k, c)
                let a = graph.Nodes.[i]
                // Edges to Steiner points — full obstacle set.
                for sIdx in 0 .. (s - 1) do
                    let sp = steiners.[sIdx]
                    if manhattanVisible graph.Obstacles a sp then
                        let v = steinerBase + sIdx
                        yield (v, steinerDiscount v (manhattanCost a sp))
                // Augment edges to start / goal — exempt obstacles
                // containing the respective endpoint.
                if manhattanVisible startObstacles a start then
                    yield (startIdx, manhattanCost a start)
                if manhattanVisible goalObstacles a goal then
                    yield (goalIdx, manhattanCost a goal)
            elif i < startIdx then
                // Steiner node. Edges to corner nodes (full
                // obstacle set), other Steiner nodes (full), and
                // start/goal (the column-aligned start↔Steiner or
                // goal↔Steiner edge uses the respective endpoint
                // exemption so the Steiner just outside a foreign
                // bbox can still be reached from a start that
                // shares its column).
                let here = steiners.[i - steinerBase]
                for k in 0 .. (n - 1) do
                    let nk = graph.Nodes.[k]
                    if manhattanVisible graph.Obstacles here nk then
                        yield (k, manhattanCost here nk)
                for sk in 0 .. (s - 1) do
                    if sk <> (i - steinerBase) then
                        let sp = steiners.[sk]
                        if manhattanVisible graph.Obstacles here sp then
                            let v = steinerBase + sk
                            yield (v, steinerDiscount v (manhattanCost here sp))
                if manhattanVisible startObstacles here start then
                    yield (startIdx, manhattanCost here start)
                if manhattanVisible goalObstacles here goal then
                    yield (goalIdx, manhattanCost here goal)
            else
                // start or goal endpoint.
                let here = nodeOf i
                let augmentObstacles =
                    if i = startIdx then startObstacles else goalObstacles
                for k in 0 .. (n - 1) do
                    let nk = graph.Nodes.[k]
                    if manhattanVisible augmentObstacles here nk then
                        yield (k, manhattanCost here nk)
                for sk in 0 .. (s - 1) do
                    let sp = steiners.[sk]
                    if manhattanVisible augmentObstacles here sp then
                        let v = steinerBase + sk
                        yield (v, steinerDiscount v (manhattanCost here sp))
                // Direct start↔goal edge: FULL obstacle set.
                if i = startIdx then
                    if manhattanVisible graph.Obstacles here goal then
                        yield (goalIdx, manhattanCost here goal)
                else
                    if manhattanVisible graph.Obstacles here start then
                        yield (startIdx, manhattanCost here start)
        }
    // Dijkstra with a System.Collections.Generic.PriorityQueue.
    let total = n + s + 2
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
        let raw = walk [] goalIdx
        // Post-process to emit AXIS-ALIGNED segments. Between any two
        // consecutive path nodes that differ on both axes, insert an
        // explicit bend point so the renderer doesn't have to guess
        // a posture — manhattanVisible accepts EITHER L-shape, but
        // only ONE may actually be clear of obstacles. The bend
        // point is at (b.X, a.Y) when H-first is clear, otherwise
        // (a.X, b.Y) (V-first).
        let obstaclesFor (a : Pt) (b : Pt) : Bbox array =
            // For augment edges involving start/goal, use the
            // respective endpoint-filtered set so the renderer
            // matches what the search accepted. For pure
            // corner-to-corner or Steiner-to-corner, use full.
            let isStart (p : Pt) = p.X = start.X && p.Y = start.Y
            let isGoal (p : Pt) = p.X = goal.X && p.Y = goal.Y
            if isStart a || isStart b then startObstacles
            elif isGoal a || isGoal b then goalObstacles
            else graph.Obstacles
        let withBends (pts : Pt list) : Pt list =
            let rec loop acc (xs : Pt list) =
                match xs with
                | [] | [_] -> List.rev (List.append xs acc)
                | a :: (b :: _ as tail) ->
                    if a.X = b.X || a.Y = b.Y then
                        loop (a :: acc) tail
                    else
                        let obs = obstaclesFor a b
                        let hFirstClear = lClear obs true a b
                        let vFirstClear = lClear obs false a b
                        // Match `lShape`'s posture rule: dy > dx
                        // prefers V-first, dx > dy prefers H-first.
                        // When both Ls are clear, pick the posture
                        // the renderer would pick so the path that
                        // gets drawn matches the path that was
                        // verified clear.
                        let dx = abs (b.X - a.X)
                        let dy = abs (b.Y - a.Y)
                        let preferVFirst = dy > dx
                        let bend =
                            if preferVFirst && vFirstClear then
                                { X = a.X; Y = b.Y }
                            elif hFirstClear then
                                { X = b.X; Y = a.Y }
                            elif vFirstClear then
                                { X = a.X; Y = b.Y }
                            else
                                // Neither clear (rare; the search
                                // accepted the edge because EITHER L
                                // is clear, so this shouldn't fire).
                                // Fall back to H-first.
                                { X = b.X; Y = a.Y }
                        loop (bend :: a :: acc) tail
            loop [] pts
        Some (withBends raw)
