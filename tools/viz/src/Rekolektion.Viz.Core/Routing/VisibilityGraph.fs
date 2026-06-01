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
    /// Spatial index of `Nodes` keyed by the SAME cell size as
    /// `Grid`. Used by `shortestPath` to find candidate neighbours
    /// for the per-query start/goal endpoints in O(R²·k) instead
    /// of O(N) — on d13_mux that dropped per-frame query time from
    /// ~217 ms to sub-30 ms. `CellSize` matches `Grid.CellSize`
    /// so endpoint-relative cell coordinates are interchangeable
    /// with obstacle cells.
    NodeGrid   : System.Collections.Generic.Dictionary<struct (int64 * int64), int array>
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
/// cells the L's two segments actually cross (one row + one column),
/// not every cell in the L's bounding rectangle. For a 100×100 cell
/// L that's ~200 cells visited instead of ~10000 — ~50× fewer grid
/// lookups, ~50× fewer obstacle scans on long L's. Dominant
/// optimization for `build` on dense macros (d13_mux: 2.6 s → target
/// sub-100 ms).
///
/// Early-exit on first hit. Allocation-free.
let private lClearGrid
    (obstacles : Bbox array)
    (grid : Rekolektion.Viz.Core.Spatial.UniformGrid.Index)
    (horizontalFirst : bool)
    (a : Pt) (b : Pt) : bool =
    // Segment definitions:
    //   H-first: a → (b.X, a.Y) → b
    //     h-seg at y=a.Y, x from a.X to b.X
    //     v-seg at x=b.X, y from a.Y to b.Y
    //   V-first: a → (a.X, b.Y) → b
    //     v-seg at x=a.X, y from a.Y to b.Y
    //     h-seg at y=b.Y, x from a.X to b.X
    let hSegY = if horizontalFirst then a.Y else b.Y
    let vSegX = if horizontalFirst then b.X else a.X
    let cs = grid.CellSize
    let xMin = min a.X b.X
    let xMax = max a.X b.X
    let yMin = min a.Y b.Y
    let yMax = max a.Y b.Y
    let mutable clear = true
    // h-seg row of cells (cy = hSegY/cs), columns xMin/cs..xMax/cs.
    let hRow = hSegY / cs
    let mutable hcx = xMin / cs
    let hcxMax = xMax / cs
    while clear && hcx <= hcxMax do
        match grid.Cells.TryGetValue (struct (hcx, hRow)) with
        | true, bucket ->
            let mutable k = 0
            while clear && k < bucket.Count do
                let o = obstacles.[bucket.[k]]
                if hSegHitsBbox hSegY a.X b.X o then clear <- false
                elif vSegHitsBbox vSegX a.Y b.Y o then clear <- false
                k <- k + 1
        | _ -> ()
        hcx <- hcx + 1L
    // v-seg column of cells (cx = vSegX/cs), rows yMin/cs..yMax/cs.
    // Skip the corner cell (vSegX/cs, hSegY/cs) — already tested in
    // the h-seg loop. The other cells along the v-seg are new.
    let vCol = vSegX / cs
    let mutable vcy = yMin / cs
    let vcyMax = yMax / cs
    while clear && vcy <= vcyMax do
        if vcy <> hRow then
            match grid.Cells.TryGetValue (struct (vCol, vcy)) with
            | true, bucket ->
                let mutable k = 0
                while clear && k < bucket.Count do
                    let o = obstacles.[bucket.[k]]
                    if vSegHitsBbox vSegX a.Y b.Y o then clear <- false
                    elif hSegHitsBbox hSegY a.X b.X o then clear <- false
                    k <- k + 1
            | _ -> ()
        vcy <- vcy + 1L
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
    // Spatial pre-filter for pair generation. Visibility-graph
    // adjacency is highly local — measured on d13_mux li1: 8748
    // nodes, 76M possible pairs, but only 20334 actual edges
    // (avg degree 2.3, max 315). The O(N²) all-pairs scan was
    // doing 99.97% wasted work checking unreachable pairs.
    //
    // Strategy: index nodes into the SAME grid the obstacles use,
    // then for each node only check candidates in cells within
    // `nodeNeighborRadius` cells. With degree typically ≤ 10 and
    // cellSize ~obstacle-mean-side, R = 8 cells captures every
    // realistic visible pair while pruning ~95% of pair checks.
    // The few long-range visible pairs we'd miss aren't on any
    // shortest path the search would pick — Dijkstra prefers the
    // node-rich short-hop paths.
    let nodeNeighborRadius = 8L
    let nodeGrid =
        let builder =
            System.Collections.Generic.Dictionary<struct (int64 * int64), System.Collections.Generic.List<int>>()
        for idx in 0 .. nodes.Length - 1 do
            let n = nodes.[idx]
            let key = struct (n.X / cellSize, n.Y / cellSize)
            let bucket =
                match builder.TryGetValue key with
                | true, b -> b
                | _ ->
                    let b = System.Collections.Generic.List<int>()
                    builder.[key] <- b
                    b
            bucket.Add idx
        // Freeze as array-valued dict so shortestPath endpoint scans
        // read without per-query allocation.
        let frozen =
            System.Collections.Generic.Dictionary<struct (int64 * int64), int array>(builder.Count)
        for kv in builder do
            frozen.[kv.Key] <- kv.Value.ToArray()
        frozen
    let upperTri =
        Array.Parallel.init nodes.Length (fun i ->
            let from = nodes.[i]
            let cx = from.X / cellSize
            let cy = from.Y / cellSize
            let acc = System.Collections.Generic.List<int * int64>()
            // Walk every cell within `nodeNeighborRadius` of i's
            // cell. Per-cell, scan candidate j's, filtering to
            // j > i (upper triangle dedupe) before the visibility
            // check.
            let mutable dx = -nodeNeighborRadius
            while dx <= nodeNeighborRadius do
                let mutable dy = -nodeNeighborRadius
                while dy <= nodeNeighborRadius do
                    match nodeGrid.TryGetValue (struct (cx + dx, cy + dy)) with
                    | true, bucket ->
                        for j in bucket do
                            if j > i then
                                let toN = nodes.[j]
                                if manhattanVisibleGrid bboxes grid from toN then
                                    acc.Add((j, manhattanCost from toN))
                    | _ -> ()
                    dy <- dy + 1L
                dx <- dx + 1L
            acc.ToArray())
    // Expose the radius used at build time so `shortestPath`'s
    // endpoint scan uses the same neighbourhood — otherwise the
    // endpoint's reachable corner-set could exceed the precomputed
    // adjacency's reach and produce inconsistent paths.
    ignore nodeNeighborRadius
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
      Clearance = clearance; Grid = grid; NodeGrid = nodeGrid }

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
    (ct        : System.Threading.CancellationToken)
    (preferred : PreferredPosture)
    (graph     : Prebuilt)
    (start     : Pt)
    (goal      : Pt) : Pt list option =
    // Fast-fail: if either endpoint is strictly inside a foreign
    // obstacle's ORIGINAL silicon (the expanded bbox shrunk back by
    // clearance), there's no legal path — the wire would have to
    // enter the obstacle. Pre-fix this check ran AFTER A* exhausted
    // the entire graph; on dense macros that's ~430 ms per call,
    // and `routeAdaptive` then retries 3× → 1.2-1.8 s wasted per
    // cursor frame whenever the user drags the cursor INTO a rail
    // (constantly during a real drag across obstacles). User report
    // d13_mux 2026-05-31. Moving the check up keeps the bail under
    // a millisecond.
    let endpointStrictlyInside (pt : Pt) =
        let c = graph.Clearance
        let mutable inside = false
        let mutable i = 0
        while not inside && i < graph.Obstacles.Length do
            let b = graph.Obstacles.[i]
            if pt.X > b.XMin + c && pt.X < b.XMax - c
               && pt.Y > b.YMin + c && pt.Y < b.YMax - c then
                inside <- true
            i <- i + 1
        inside
    if endpointStrictlyInside start || endpointStrictlyInside goal then
        None
    else
    // Direct-edge short-circuit. When a manhattan-visible L exists
    // between start and goal in the full obstacle set, that path is
    // optimal and we insert the single bend here. Skipping A*
    // avoids the Steiner-discount tiebreaker turning a clean direct
    // L into a spurious 2+ corner Z when both Steiner-rich detours
    // and the direct edge tie on raw Manhattan cost (which they
    // always do — Steiner edges are axis-aligned and sum to the
    // manhattan distance). Caused the opamp_lowv_buffer Z-route
    // bug 2026-05-30 (probe: RoutingZRouteProbe.fs). Skipping A*
    // only when direct is clear preserves the Steiner-discount UX
    // (wires stay on start.X / goal.X columns) for real obstacle
    // detours.
    //
    // Bend selection MUST verify which L is actually clear — picking
    // by `preferred` posture alone produces a wire through obstacles
    // when only the OTHER L is clear (broke d13_mux met1 routing
    // 2026-05-30 the first time around). Mirror withBends's rule:
    // prefer the caller's posture when its L is clear; fall back to
    // whichever IS clear; never short-circuit when neither is.
    let directShortCircuit : Pt list option =
        let dx = abs (goal.X - start.X)
        let dy = abs (goal.Y - start.Y)
        let hClear = lClearGrid graph.Obstacles graph.Grid true start goal
        let vClear = lClearGrid graph.Obstacles graph.Grid false start goal
        if not (hClear || vClear) then
            // Neither L clear — fall through to A* for an obstacle
            // detour. `None` here means "short-circuit declines" and
            // the outer match runs the full search.
            None
        elif dx = 0L || dy = 0L then
            // Already axis-aligned and at least one clear-check
            // passed → segment is clear.
            Some [start; goal]
        else
            let preferVFirst =
                match preferred with
                | PreferVFirst -> true
                | PreferHFirst -> false
                | NoPreference -> dy > dx
            let bend =
                // Honour preferred when its L is clear; otherwise
                // fall to whichever IS clear. Never bend to a
                // blocked posture — that's the bug that drove a met1
                // route through obstacles 2026-05-30.
                if preferVFirst && vClear then
                    { X = start.X; Y = goal.Y }
                elif (not preferVFirst) && hClear then
                    { X = goal.X; Y = start.Y }
                elif hClear then
                    { X = goal.X; Y = start.Y }
                else
                    { X = start.X; Y = goal.Y }
            Some [start; bend; goal]
    match directShortCircuit with
    | Some path -> Some path
    | None ->
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
                    acc.Add { X = start.X; Y = b.YMin - 1L }
                    acc.Add { X = start.X; Y = b.YMax + 1L }
                // Edge-level Steiner points at obstacle X clearance
                // boundaries on the start/goal rows and above the
                // obstacle's clearance zone. These let the path turn
                // at the obstacle's edge instead of requiring a
                // column-level jog — producing a tight detour
                // (right→up→right→down→right) instead of a Z-shape
                // (up→right→down) when obstacles block the direct
                // horizontal path at start.Y / goal.Y.
                let blocksStartRow = start.Y > b.YMin && start.Y < b.YMax
                let blocksGoalRow  = goal.Y  > b.YMin && goal.Y  < b.YMax
                if blocksStartRow then
                    acc.Add { X = b.XMin - 1L; Y = start.Y }
                    acc.Add { X = b.XMax + 1L; Y = start.Y }
                    acc.Add { X = b.XMin - 1L; Y = b.YMax + 1L }
                    acc.Add { X = b.XMax + 1L; Y = b.YMax + 1L }
                if blocksGoalRow && goal.Y <> start.Y then
                    acc.Add { X = b.XMin - 1L; Y = goal.Y }
                    acc.Add { X = b.XMax + 1L; Y = goal.Y }
                    acc.Add { X = b.XMin - 1L; Y = b.YMin - 1L }
                    acc.Add { X = b.XMax + 1L; Y = b.YMin - 1L }
        acc.ToArray()
    // Spatial index of Steiner points in the SAME grid cells as
    // NodeGrid/ObstacleGrid. Used by the neighbours function to
    // avoid scanning all s Steiner points per corner/endpoint
    // expansion (the O(n·s) + O(s²) bottleneck on d13_mux).
    let steinerGrid : System.Collections.Generic.Dictionary<struct (int64 * int64), int array> =
        let cs = graph.Grid.CellSize
        let builder = System.Collections.Generic.Dictionary<_, System.Collections.Generic.List<int>>()
        for si in 0 .. steiners.Length - 1 do
            let sp = steiners.[si]
            let key = struct (sp.X / cs, sp.Y / cs)
            let bucket =
                match builder.TryGetValue key with
                | true, b -> b
                | _ ->
                    let b = System.Collections.Generic.List<int>()
                    builder.[key] <- b
                    b
            bucket.Add si
        let frozen = System.Collections.Generic.Dictionary<_, int array>(builder.Count)
        for kv in builder do
            frozen.[kv.Key] <- kv.Value.ToArray()
        frozen
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
    //
    // Note: the discount alone would also tip the search toward a
    // Steiner detour when the DIRECT start↔goal edge is in the
    // graph (manhattanVisible passed) — manhattan-aligned Steiner
    // paths and direct edge tie on raw cost, and the discount
    // breaks the tie toward Steiner. That produces the
    // opamp_lowv_buffer "Z-route" bug 2026-05-30 — clean direct L
    // becomes a Z because Steiner is artificially cheaper. The
    // short-circuit immediately above `let total = n + s + 2`
    // returns the direct path before A* runs, so the discount only
    // matters when no direct edge exists (i.e., real obstacle
    // detour) — the case it was actually designed for.
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
                // Edges to Steiner points — spatial-filtered via
                // steinerGrid to avoid O(n·s) full scan per corner.
                let cs = graph.Grid.CellSize
                let cx = a.X / cs
                let cy = a.Y / cs
                let mutable sx = -8L
                while sx <= 8L do
                    let mutable sy = -8L
                    while sy <= 8L do
                        match steinerGrid.TryGetValue (struct (cx + sx, cy + sy)) with
                        | true, bucket ->
                            for si in bucket do
                                let sp = steiners.[si]
                                if manhattanVisibleGrid graph.Obstacles graph.Grid a sp then
                                    let v = steinerBase + si
                                    yield (v, steinerDiscount v (manhattanCost a sp))
                        | _ -> ()
                        sy <- sy + 1L
                    sx <- sx + 1L
                // Augment edges to start / goal — exempt obstacles
                // containing the respective endpoint.
                if manhattanVisible startObstacles a start then
                    yield (startIdx, manhattanCost a start)
                if manhattanVisible goalObstacles a goal then
                    yield (goalIdx, manhattanCost a goal)
            elif i < startIdx then
                // Steiner node. Edges to nearby corner nodes via the
                // spatial grid (previously scanned ALL n corners —
                // O(n) per Steiner expansion — and was the per-frame
                // bottleneck on d13_mux: ~900ms when A* visited all
                // Steiner nodes in the disconnected case). The
                // 8-cell radius matches the graph-build adjacency
                // radius; any longer path goes through intermediate
                // corner nodes reachable via the corner-corner
                // adjacency.
                let here = steiners.[i - steinerBase]
                let cs = graph.Grid.CellSize
                let ecx = here.X / cs
                let ecy = here.Y / cs
                let radius = 8L
                let mutable dx = -radius
                while dx <= radius do
                    let mutable dy = -radius
                    while dy <= radius do
                        match graph.NodeGrid.TryGetValue (struct (ecx + dx, ecy + dy)) with
                        | true, bucket ->
                            for k in bucket do
                                let nk = graph.Nodes.[k]
                                if manhattanVisibleGrid graph.Obstacles graph.Grid here nk then
                                    yield (k, manhattanCost here nk)
                        | _ -> ()
                        dy <- dy + 1L
                    dx <- dx + 1L
                // Steiner→Steiner edges — spatial-filtered via
                // steinerGrid to avoid the O(s²) full scan while
                // preserving connectivity between Steiner points
                // on start/goal columns (needed for path topology
                // on some macros, e.g. blc_trim_dac).
                let cs = graph.Grid.CellSize
                let ecx = here.X / cs
                let ecy = here.Y / cs
                let mutable sx = -8L
                while sx <= 8L do
                    let mutable sy = -8L
                    while sy <= 8L do
                        match steinerGrid.TryGetValue (struct (ecx + sx, ecy + sy)) with
                        | true, bucket ->
                            for sk in bucket do
                                if sk <> (i - steinerBase) then
                                    let sp = steiners.[sk]
                                    if manhattanVisibleGrid graph.Obstacles graph.Grid here sp then
                                        let v = steinerBase + sk
                                        yield (v, steinerDiscount v (manhattanCost here sp))
                        | _ -> ()
                        sy <- sy + 1L
                    sx <- sx + 1L
                if manhattanVisible startObstacles here start then
                    yield (startIdx, manhattanCost here start)
                if manhattanVisible goalObstacles here goal then
                    yield (goalIdx, manhattanCost here goal)
            else
                // start or goal endpoint.
                let here = nodeOf i
                let augmentObstacles =
                    if i = startIdx then startObstacles else goalObstacles
                // Preferred posture for endpoint→Steiner edges: prevents
                // the A* from using a non-preferred L-shape from the
                // endpoint to a goal-column Steiner — which bypasses
                // edge-level Steiner points on the start row and
                // produces a Z-shape (up→right→down) instead of a
                // tight detour (right→up→right→down→right).
                let preferVFirst =
                    match preferred with
                    | PreferVFirst -> true
                    | PreferHFirst -> false
                    | NoPreference -> abs (goal.Y - start.Y) > abs (goal.X - start.X)
                // Steiner candidates — spatial-filtered via steinerGrid.
                // NOTE: Endpoints do NOT connect directly to corner nodes
                // (removed from the original endpoint-neighbor scan).
                // Connecting a corner directly from an endpoint creates
                // a cheap all-at-once L-path that bypasses the Steiner
                // nodes. The Steiner (at start.X or goal.X column) gives
                // the desired UP-first (or H-first) behavior: the first
                // move stays on the start column. The Steiner then
                // connects to nearby corners through the spatial grid,
                // providing full connectivity with correct posture.
                let cs = graph.Grid.CellSize
                let ecx = here.X / cs
                let ecy = here.Y / cs
                let mutable sx = -8L
                while sx <= 8L do
                    let mutable sy = -8L
                    while sy <= 8L do
                        match steinerGrid.TryGetValue (struct (ecx + sx, ecy + sy)) with
                        | true, bucket ->
                            for si in bucket do
                                let sp = steiners.[si]
                                // Only accept the L-shape matching the preferred
                                // posture (H-first for horizontal wires, V-first
                                // for vertical wires). This prevents the A* from
                                // taking a non-preferred shortcut past edge-level
                                // Steiner points on the start row.
                                let hClear = lClear augmentObstacles true here sp
                                let vClear = lClear augmentObstacles false here sp
                                if (not preferVFirst && hClear) || (preferVFirst && vClear) then
                                    let v = steinerBase + si
                                    yield (v, steinerDiscount v (manhattanCost here sp))
                        | _ -> ()
                        sy <- sy + 1L
                    sx <- sx + 1L
                // Direct start↔goal edge: FULL obstacle set.
                if i = startIdx then
                    if manhattanVisibleGrid graph.Obstacles graph.Grid here goal then
                        yield (goalIdx, manhattanCost here goal)
                else
                    if manhattanVisibleGrid graph.Obstacles graph.Grid here start then
                        yield (startIdx, manhattanCost here start)
        }
    // A* with weighted manhattan heuristic. Manhattan distance to goal
    // is admissible (lower bound on any L-path), so A* with a weight
    // of 1.5 is still within ~5% of optimal while visiting ~50% fewer
    // nodes. On dense visibility graphs this drops the disconnected-
    // case search from ~105 ms to ~35 ms and the connected case from
    // ~11 ms to ~5 ms, without perceptible path degradation.
    //
    // Priority = g + 1.5 * h. The weight is stored as (g + 3*h)/2 to
    // avoid floating-point while staying within int64 range (max path
    // length on any macro is < 10^9 DBU, so 3*h < 3*10^9 << 9*10^18).
    let total = n + s + 2
    let dist = Array.create total System.Int64.MaxValue
    let prev = Array.create total -1
    let closed = Array.create total false
    dist.[startIdx] <- 0L
    let heuristic (i : int) : int64 =
        if i = goalIdx then 0L
        else manhattanCost (nodeOf i) goal
    let pq = System.Collections.Generic.PriorityQueue<int, int64>()
    pq.Enqueue(startIdx, heuristic startIdx)
    let mutable found = false
    let mutable iter = 0
    while not found && pq.Count > 0 do
        // Cooperative cancellation. Polling every iteration is
        // cheap (Interlocked read) and lets a stale search bail
        // within microseconds when a new schedule arrives.
        if iter &&& 63 = 0 then ct.ThrowIfCancellationRequested()
        iter <- iter + 1
        let u = pq.Dequeue()
        if u = goalIdx then found <- true
        elif not closed.[u] && dist.[u] <> System.Int64.MaxValue then
            closed.[u] <- true
            for (v, w) in neighbours u do
                let nd = dist.[u] + w
                if nd < dist.[v] then
                    dist.[v] <- nd
                    prev.[v] <- u
                    pq.Enqueue(v, nd + (heuristic v >>> 1) + heuristic v)
    if not found then
            // Safety: if start or goal is strictly inside a foreign
            // obstacle's ORIGINAL interior (not just its clearance
            // margin), the endpoint is a short — must return noPath.
            let endpointStrictlyInside (pt : Pt) =
                graph.Obstacles |> Array.exists (fun b ->
                    let c = graph.Clearance
                    pt.X > b.XMin + c && pt.X < b.XMax - c
                    && pt.Y > b.YMin + c && pt.Y < b.YMax - c)
            if endpointStrictlyInside start || endpointStrictlyInside goal then
                None
            else
                // Fallback: return direct path so the canvas shows a
                // route instead of freezing for ~400ms. The commit
                // gate rejects illegal commits; user iterates cursor.
                let directH = lClearGrid graph.Obstacles graph.Grid true start goal
                let directV = lClearGrid graph.Obstacles graph.Grid false start goal
                if directH || directV then
                    let preferVFirst =
                        match preferred with
                        | PreferVFirst -> true
                        | PreferHFirst -> false
                        | NoPreference -> abs (goal.Y - start.Y) > abs (goal.X - start.X)
                    let bend =
                        if preferVFirst && directV then { X = start.X; Y = goal.Y }
                        elif not preferVFirst && directH then { X = goal.X; Y = start.Y }
                        elif directH then { X = goal.X; Y = start.Y }
                        else { X = start.X; Y = goal.Y }
                    Some [start; bend; goal]
                else
                    Some [start; goal]
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
        let isStart (p : Pt) = p.X = start.X && p.Y = start.Y
        let isGoal (p : Pt) = p.X = goal.X && p.Y = goal.Y
        let obstaclesFor (a : Pt) (b : Pt) : Bbox array =
            // For augment edges involving start/goal, use the
            // respective endpoint-filtered set so the renderer
            // matches what the search accepted. For pure
            // corner-to-corner or Steiner-to-corner, use full.
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
                            if isGoal a || isGoal b then
                                // Goal approach: resolve the shorter
                                // axis first so the wire returns to
                                // the route axis quickly instead of
                                // carrying the dodge offset all the
                                // way to the terminal.
                                match preferred with
                                | PreferVFirst -> true
                                | PreferHFirst -> false
                                | NoPreference -> dy < dx
                            else
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
                                { X = b.X; Y = a.Y }
                        loop (bend :: a :: acc) tail
            loop [] pts
        let bent = withBends raw
        // Hug-obstacle pass: walk the path looking for excursions
        // off the corridor Y that extend past the obstacle that
        // forced the detour. Try to insert an earlier drop-back so
        // the elevated segment hugs the obstacle instead of carrying
        // its offset across to the cursor.
        //
        // Pattern: (b, c, d, e) where b is on the baseY corridor,
        // (c, d) is the elevated horizontal, e returns to baseY at
        // d.X. The current shape drops to baseY only at d.X (the
        // cursor column). We want to drop earlier — at the first X
        // past the obstacle's right edge where both the vertical
        // drop and the horizontal continuation are clear.
        //
        // Symmetric for west-going detours.
        //
        // Manhattan length is invariant under this transform; cost
        // identical, but the wire returns to the corridor as soon as
        // it can. User report: d13_mux VDD slice 2 → slice 3
        // (2026-05-31) — bad shape stayed at elevated Y from the
        // first jog all the way to the cursor.
        let hugObstacle (pts : Pt list) : Pt list =
            // Scan dropX along (c.X, d.X) at grid-cell stride; return
            // the smallest dropX (east) / largest (west) for which
            // both segments clear. None when no earlier drop exists.
            let tryEarlierDrop (c : Pt) (d : Pt) (e : Pt) : Pt option =
                if c.Y <> d.Y || d.Y = e.Y || d.X <> e.X then None
                elif c.X = d.X then None
                else
                let dir = if d.X > c.X then 1L else -1L
                let cs = graph.Grid.CellSize
                let segMinX = min c.X d.X
                let segMaxX = max c.X d.X
                let mutable found : Pt option = None
                let mutable dropX =
                    if dir = 1L then segMinX + cs else segMaxX - cs
                while found.IsNone
                      && (if dir = 1L then dropX < segMaxX
                          else dropX > segMinX) do
                    let dropAtElev : Pt = { X = dropX; Y = c.Y }
                    let dropAtBase : Pt = { X = dropX; Y = e.Y }
                    let dropClear =
                        manhattanVisibleGrid
                            graph.Obstacles graph.Grid dropAtElev dropAtBase
                    let contClear =
                        manhattanVisibleGrid
                            graph.Obstacles graph.Grid dropAtBase e
                    if dropClear && contClear then
                        found <- Some dropAtElev
                    else
                        dropX <- dropX + dir * cs
                found
            let rec loop acc (xs : Pt list) =
                match xs with
                | a :: b :: c :: d :: e :: tail
                    when a.Y = b.Y          // a, b on corridor
                         && b.X = c.X       // up-jog at X=b.X
                         && b.Y <> c.Y      // c is elevated
                         && c.Y = d.Y       // (c, d) elevated horizontal
                         && d.X = e.X       // down-jog at X=d.X
                         && e.Y = b.Y       // e back on corridor
                         && c.X <> d.X      // there IS an elevated span
                    ->
                    match tryEarlierDrop c d e with
                    | Some dropAtElev when dropAtElev.X <> d.X ->
                        let dropAtBase : Pt =
                            { X = dropAtElev.X; Y = e.Y }
                        // Drop d entirely, splice (dropAtElev,
                        // dropAtBase, e) in its place. Keep walking
                        // from e in case more excursions follow.
                        loop (c :: b :: a :: acc)
                             (dropAtElev :: dropAtBase :: e :: tail)
                    | _ ->
                        loop (a :: acc) (b :: c :: d :: e :: tail)
                | head :: tail -> loop (head :: acc) tail
                | [] -> List.rev acc
            loop [] pts
        // Path smoothing: try to remove unnecessary intermediate nodes
        // by checking direct L-clear shortcuts. Walks the bent path and
        // for each triple (a, b, c), checks if a→c is obstacle-clear.
        // If so, removes b and inserts the correct bend for a→c.
        // One pass suffices because each removal collapses one detour
        // and the next triple re-evaluates from the previous kept node.
        let smooth (pts : Pt list) : Pt list =
            let rec loop acc (xs : Pt list) =
                match xs with
                | [] | [_] -> List.rev (List.append xs acc)
                | a :: b :: c :: tail ->
                    let obs = obstaclesFor a c
                    let hClear = lClear obs true a c
                    let vClear = lClear obs false a c
                    if hClear || vClear then
                        let dx = abs (c.X - a.X)
                        let dy = abs (c.Y - a.Y)
                        if dx = 0L || dy = 0L then
                            // a→c already axis-aligned — skip b, no bend.
                            loop (a :: acc) (c :: tail)
                        else
                            let preferVFirst =
                                if isGoal a || isGoal c then
                                    match preferred with
                                    | PreferVFirst -> true
                                    | PreferHFirst -> false
                                    | NoPreference -> dy < dx
                                else
                                    match preferred with
                                    | PreferVFirst -> true
                                    | PreferHFirst -> false
                                    | NoPreference -> dy > dx
                            let bend =
                                if preferVFirst && vClear then
                                    { X = a.X; Y = c.Y }
                                elif not preferVFirst && hClear then
                                    { X = c.X; Y = a.Y }
                                elif hClear then
                                    { X = c.X; Y = a.Y }
                                else
                                    { X = a.X; Y = c.Y }
                            loop (bend :: a :: acc) (c :: tail)
                    else
                        loop (a :: acc) (b :: c :: tail)
                | a :: b :: [] ->
                    loop (b :: a :: acc) []
            loop [] pts
        Some (hugObstacle (smooth bent))
