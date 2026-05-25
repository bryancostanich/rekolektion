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
    /// Uniform-grid spatial index over `Obstacles`. Used by the
    /// visibility test fast path: instead of scanning every obstacle
    /// per L-test (O(corners² × obstacles) on dense macros — 56 s
    /// on the 744-obstacle blc_trim_dac), the test queries only
    /// obstacles whose bbox overlaps the L's bounding rectangle.
    /// Drops build time to ~1 s on the same macro.
    Grid       : Rekolektion.Viz.Core.Spatial.UniformGrid.Index
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

/// Grid-accelerated `lClear`. Same semantic — does any obstacle's
/// expanded bbox interior overlap the L's segments — but walks only
/// grid cells the L's bbox covers, testing each obstacle in those
/// cells. Allocation-free hot path: inlined cell-iteration loop and
/// no dedup (testing the same obstacle twice across overlapping
/// cells is cheap and never produces a wrong answer; early-exit on
/// first hit limits the redundant work).
let private lClearGrid
    (obstacles : Bbox array)
    (grid : Rekolektion.Viz.Core.Spatial.UniformGrid.Index)
    (horizontalFirst : bool)
    (a : Pt) (b : Pt) : bool =
    let hSegY = if horizontalFirst then a.Y else b.Y
    let vSegX = if horizontalFirst then b.X else a.X
    let xMin = min a.X b.X
    let xMax = max a.X b.X
    let yMin = min a.Y b.Y
    let yMax = max a.Y b.Y
    let cs = grid.CellSize
    let cxMin = xMin / cs
    let cxMax = xMax / cs
    let cyMin = yMin / cs
    let cyMax = yMax / cs
    let mutable clear = true
    let mutable cx = cxMin
    while clear && cx <= cxMax do
        let mutable cy = cyMin
        while clear && cy <= cyMax do
            match grid.Cells.TryGetValue (struct (cx, cy)) with
            | true, bucket ->
                let mutable k = 0
                while clear && k < bucket.Count do
                    let o = obstacles.[bucket.[k]]
                    let hitH = hSegHitsBbox hSegY a.X b.X o
                    let hitV = vSegHitsBbox vSegX a.Y b.Y o
                    if hitH || hitV then clear <- false
                    k <- k + 1
            | _ -> ()
            cy <- cy + 1L
        cx <- cx + 1L
    clear

/// Manhattan-visible: an L-path in EITHER posture clears every
/// obstacle. Either posture is acceptable for graph adjacency —
/// the search picks the cheaper one per edge.
let private manhattanVisible (obstacles : Bbox array) (a : Pt) (b : Pt) : bool =
    lClear obstacles true a b
    || lClear obstacles false a b

/// Grid-accelerated `manhattanVisible`. Use when the obstacle array
/// matches the `grid` built at `build` time (i.e., `graph.Obstacles`
/// + `graph.Grid`). For modified obstacle arrays (start/goal-augment
/// edges where `shrinkForMargin` rewrites some bboxes), fall back
/// to `manhattanVisible` — the grid wouldn't match.
let private manhattanVisibleGrid
    (obstacles : Bbox array)
    (grid : Rekolektion.Viz.Core.Spatial.UniformGrid.Index)
    (a : Pt) (b : Pt) : bool =
    lClearGrid obstacles grid true a b
    || lClearGrid obstacles grid false a b

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

/// Build the prebuilt graph for an obstacle set. With the spatial
/// grid the hot path (corner-pair visibility) drops from
/// O(corners² × obstacles) to O(corners² × k) where k is the
/// average obstacle count per L-bbox cell-coverage. On the
/// 744-obstacle blc_trim_dac this takes the build from ~56 s to
/// ~1 s; on small sets it's still sub-millisecond (the grid
/// degenerates gracefully).
let build (clearance : int64) (obstacles : FlatPolygon array) : Prebuilt =
    let bboxes =
        obstacles
        |> Array.map (bboxOf >> expand clearance)
    let gridBboxes : (int64 * int64 * int64 * int64) array =
        bboxes
        |> Array.map (fun b -> b.XMin, b.YMin, b.XMax, b.YMax)
    let cellSize =
        Rekolektion.Viz.Core.Spatial.UniformGrid.suggestCellSize gridBboxes
    let grid =
        Rekolektion.Viz.Core.Spatial.UniformGrid.build cellSize gridBboxes
    let nodes =
        bboxes
        |> Array.collect cornersOf
        |> Array.distinct
    // Parallelize the upper-triangle adjacency discovery: each `i`'s
    // candidate list is independent — only `nodes`, `bboxes`, `grid`
    // are read, all immutable. On a multicore machine this drops the
    // build by a factor of ~(physical cores).
    let upperTri =
        Array.Parallel.init nodes.Length (fun i ->
            let from = nodes.[i]
            let acc = System.Collections.Generic.List<int * int64>()
            for j in (i + 1) .. (nodes.Length - 1) do
                let toN = nodes.[j]
                if manhattanVisibleGrid bboxes grid from toN then
                    acc.Add((j, manhattanCost from toN))
            acc.ToArray())
    // Fold the upper triangle into bidirectional adjacency so the
    // search can read `Adjacency.[i]` once to get every neighbour of
    // `i`. Pre-fix the search had to scan every OTHER node's
    // upper-triangle list looking for back-references — an O(N²) per
    // node-visit overhead that dominated Dijkstra at 2700 corners.
    let adjacency : (int * int64) array array =
        let buckets =
            Array.init nodes.Length (fun _ ->
                System.Collections.Generic.List<int * int64>())
        for i in 0 .. nodes.Length - 1 do
            for (j, c) in upperTri.[i] do
                buckets.[i].Add((j, c))
                buckets.[j].Add((i, c))
        buckets |> Array.map (fun b -> b.ToArray())
    { Nodes = nodes; Obstacles = bboxes; Adjacency = adjacency
      Clearance = clearance; Grid = grid }

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
/// Preferred posture for `shortestPath`'s corner placement.
/// `NoPreference` falls back to geometric `dy > dx` ratio — the
/// historical behaviour. `PreferHFirst` / `PreferVFirst` are
/// honoured whenever the chosen L is clear; if blocked, the
/// search uses whichever L IS clear.
type PreferredPosture =
    | NoPreference
    | PreferHFirst
    | PreferVFirst

let shortestPath
    (preferred : PreferredPosture)
    (graph : Prebuilt)
    (start : Pt)
    (goal  : Pt) : Pt list option =
    let inside (pt : Pt) (b : Bbox) =
        pt.X > b.XMin && pt.X < b.XMax
        && pt.Y > b.YMin && pt.Y < b.YMax
    // True when `pt` sits inside the ORIGINAL polygon (the expanded
    // bbox shrunk back by `Clearance`). The same-net pin's own
    // poly stack is classified as "ours" upstream (LabelFlood
    // seeds + Obstacles.isOurs) and never appears in this
    // obstacle set, so a same-net endpoint is never strictly
    // inside an obstacle. Only foreign polys appear here; an
    // endpoint inside one is an electrical short and must
    // return noPath.
    let insideOriginal (pt : Pt) (b : Bbox) =
        let c = graph.Clearance
        pt.X > b.XMin + c && pt.X < b.XMax - c
        && pt.Y > b.YMin + c && pt.Y < b.YMax - c
    // Per-edge-type exemption (ADR-0006 intent):
    //   • start↔corner edges: for any obstacle whose expanded bbox
    //     contains start but whose ORIGINAL bbox does NOT (start is
    //     in the clearance margin), test that obstacle using its
    //     ORIGINAL bbox instead of the expanded one. The wire is
    //     already in the margin — it can stay there to escape — but
    //     it must NOT cross into the obstacle's actual silicon.
    //   • corner↔goal edges: same rule for `goal`.
    //   • corner↔corner edges: FULL obstacle set, expanded (prebuilt).
    //   • direct start↔goal: FULL obstacle set, expanded — prevents
    //     trivialStraight shortcuts.
    // Endpoint strictly inside a foreign obstacle's original
    // interior: NOT in margin → original bbox unchanged → obstacle
    // still blocks → noPath. That's the correct behaviour; a wire
    // whose pin sits inside a foreign poly's silicon is a short.
    //
    // Earlier the rule was "remove the obstacle entirely from the
    // start/goal test set." That let the search approve start-edges
    // that crossed straight through the obstacle's expanded interior
    // (and even the original interior), producing physically-
    // shorted paths. See `PathCheck`-asserted regression test:
    // `REPRO: drn_R(5145,8965) → (7595,8981) ...`.
    let shrinkForMargin (pt : Pt) (b : Bbox) : Bbox =
        if inside pt b && not (insideOriginal pt b) then
            { XMin = b.XMin + graph.Clearance
              YMin = b.YMin + graph.Clearance
              XMax = b.XMax - graph.Clearance
              YMax = b.YMax - graph.Clearance }
        else b
    let startObstacles =
        graph.Obstacles |> Array.map (shrinkForMargin start)
    let goalObstacles =
        graph.Obstacles |> Array.map (shrinkForMargin goal)
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
                // Corner node. Prebuilt adjacency is bidirectional —
                // one read returns every corner-corner neighbour.
                for (j, c) in graph.Adjacency.[i] do
                    yield (j, c)
                let a = graph.Nodes.[i]
                // Edges to Steiner points — full obstacle set.
                for sIdx in 0 .. (s - 1) do
                    let sp = steiners.[sIdx]
                    if manhattanVisibleGrid graph.Obstacles graph.Grid a sp then
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
                    if manhattanVisibleGrid graph.Obstacles graph.Grid here nk then
                        yield (k, manhattanCost here nk)
                for sk in 0 .. (s - 1) do
                    if sk <> (i - steinerBase) then
                        let sp = steiners.[sk]
                        if manhattanVisibleGrid graph.Obstacles graph.Grid here sp then
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
                    if manhattanVisibleGrid graph.Obstacles graph.Grid here goal then
                        yield (goalIdx, manhattanCost here goal)
                else
                    if manhattanVisibleGrid graph.Obstacles graph.Grid here start then
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
                        // Posture priority: caller-supplied
                        // preference > geometric ratio. The user's
                        // first decisive cursor motion locks a
                        // posture in `Draft.setCursor`; passing it
                        // through here means the corner stops
                        // flipping when dy/dx crosses mid-drag.
                        // Fall back to dy>dx only when the caller
                        // has no preference.
                        let dx = abs (b.X - a.X)
                        let dy = abs (b.Y - a.Y)
                        let preferVFirst =
                            match preferred with
                            | PreferVFirst -> true
                            | PreferHFirst -> false
                            | NoPreference -> dy > dx
                        let bend =
                            if preferVFirst && vFirstClear then
                                { X = a.X; Y = b.Y }
                            elif not preferVFirst && hFirstClear then
                                { X = b.X; Y = a.Y }
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
