module Rekolektion.Viz.Core.Drc.Geometry.Components

open Rekolektion.Viz.Core.Drc.Geometry.Region

/// Connected-component analysis on a slab-decomposed Region.
///
/// A "cell" is one (slab × interval) pair — the smallest unit of
/// canonical Region storage. Two cells are CONNECTED iff:
///   * Same slab — NOT adjacent (canonical form requires
///     non-adjacent intervals within a slab; no two cells in the
///     same slab can connect).
///   * Different slabs that are vertically adjacent
///     (slabA.Y + slabA.Height == slabB.Y) AND the intervals
///     share X-overlap.
///
/// Standard union-find collapses connected cells into components.
/// Each component's bounding box is the rectangle enclosing all
/// its cells — the natural "this violation covers this area"
/// reporting unit, equivalent to a Magic-style per-feature
/// violation.
///
/// `componentBboxes` is the primary entry point for DRC: hand it
/// a violation Region produced by morphology, get back one bbox
/// per connected violation cluster. That's the per-rule
/// per-violation count consumers want.

type private DSU(n: int) =
    let parent = Array.init n id
    let mutable rank = Array.zeroCreate<int> n
    let rec find (i: int) : int =
        if parent.[i] = i then i
        else
            let root = find parent.[i]
            parent.[i] <- root
            root
    member _.Find i = find i
    member _.Union (i: int) (j: int) =
        let ri = find i
        let rj = find j
        if ri <> rj then
            if rank.[ri] < rank.[rj] then parent.[ri] <- rj
            elif rank.[ri] > rank.[rj] then parent.[rj] <- ri
            else
                parent.[rj] <- ri
                rank.[ri] <- rank.[ri] + 1

/// Enumerate every (slab × interval) cell with its bbox + a
/// flat index. The flat index is the union-find key.
let private enumerateCells
        (r: Region)
        : (int * (int64 * int64 * int64 * int64)) array =
    let result = ResizeArray<int * (int64 * int64 * int64 * int64)>()
    let mutable idx = 0
    for si in 0 .. r.Slabs.Length - 1 do
        let slab = r.Slabs.[si]
        for iv in slab.Intervals do
            let bb = iv.X1, slab.Y, iv.X2, slab.Y + slab.Height
            result.Add (idx, bb)
            idx <- idx + 1
    result.ToArray()

/// Per-slab list of (cell flat-index, X-interval). Lets us look
/// up "every cell in slab i and its X range" without re-walking
/// the slab list.
let private cellsBySlab (r: Region) : (int * Interval) array array =
    let mutable idx = 0
    r.Slabs
    |> Array.map (fun slab ->
        slab.Intervals
        |> Array.map (fun iv ->
            let i = idx
            idx <- idx + 1
            i, iv))

/// Find all connected components in `r`. Returns the bbox of each
/// component (the rectangle enclosing every tile in the
/// component). Components are emitted in arbitrary order; if the
/// caller wants stable ordering, sort by (yMin, xMin).
///
/// Algorithm: union-find on cells. For each pair of vertically
/// adjacent slabs, walk the cross-product of intervals; union
/// any cell pair whose X intervals overlap. After all unions,
/// group cells by root and compute per-group bbox.
///
/// O(N + E) for the union-find where E is the count of
/// adjacent-cell-pair edges (worst case O(N²); typical
/// proportional to N for SKY130-shape inputs).
let componentBboxes
        (r: Region)
        : (int64 * int64 * int64 * int64) array =
    if isEmpty r then [||]
    else
        let cells = enumerateCells r
        let totalCells = cells.Length
        if totalCells = 0 then [||]
        else
            let dsu = DSU(totalCells)
            let byslab = cellsBySlab r
            // Walk every pair of vertically adjacent slabs and
            // union intervals that share X-overlap. Two slabs
            // (i, i+1) are "adjacent" iff
            // slabs.[i].Y + slabs.[i].Height == slabs.[i+1].Y.
            // Non-adjacent slabs (with a Y gap) don't contribute.
            for i in 0 .. r.Slabs.Length - 2 do
                let upper = r.Slabs.[i]
                let lower = r.Slabs.[i + 1]
                if upper.Y + upper.Height = lower.Y then
                    // Cells in upper and lower can connect.
                    let upperCells = byslab.[i]
                    let lowerCells = byslab.[i + 1]
                    for (uIdx, uIv) in upperCells do
                        for (lIdx, lIv) in lowerCells do
                            let lo = max uIv.X1 lIv.X1
                            let hi = min uIv.X2 lIv.X2
                            if lo < hi then
                                dsu.Union uIdx lIdx
            // Group cells by root, accumulating bbox.
            let groups =
                System.Collections.Generic.Dictionary<int,
                    int64 * int64 * int64 * int64>()
            for (idx, bb) in cells do
                let root = dsu.Find idx
                let (bx1, by1, bx2, by2) = bb
                match groups.TryGetValue root with
                | true, (gx1, gy1, gx2, gy2) ->
                    groups.[root] <-
                        (min gx1 bx1, min gy1 by1,
                         max gx2 bx2, max gy2 by2)
                | _ ->
                    groups.[root] <- bb
            groups.Values |> Array.ofSeq

/// Connected components with both bbox AND actual polygon area
/// per component. Used by MinArea rules where a component's
/// ACTUAL area (sum of tile areas) matters, not its bbox area —
/// an L-shape's bbox is larger than the polygon's true area.
///
/// Returns `(bbox, areaDbu²)` per component. The bbox is the
/// same as `componentBboxes` returns; `areaDbu²` sums every
/// tile (slab.Height × interval-width) in the component.
let componentBboxesAndAreas
        (r: Region)
        : ((int64 * int64 * int64 * int64) * int64) array =
    if isEmpty r then [||]
    else
        let cells = enumerateCells r
        let totalCells = cells.Length
        if totalCells = 0 then [||]
        else
            let dsu = DSU(totalCells)
            let byslab = cellsBySlab r
            // Same union logic as componentBboxes.
            for i in 0 .. r.Slabs.Length - 2 do
                let upper = r.Slabs.[i]
                let lower = r.Slabs.[i + 1]
                if upper.Y + upper.Height = lower.Y then
                    let upperCells = byslab.[i]
                    let lowerCells = byslab.[i + 1]
                    for (uIdx, uIv) in upperCells do
                        for (lIdx, lIv) in lowerCells do
                            let lo = max uIv.X1 lIv.X1
                            let hi = min uIv.X2 lIv.X2
                            if lo < hi then
                                dsu.Union uIdx lIdx
            // Cell flat-index → (slab Y range, interval) — used
            // to compute per-tile area.
            let cellTile = Array.zeroCreate<Interval * int64 * int64> totalCells
            let mutable idx = 0
            for si in 0 .. r.Slabs.Length - 1 do
                let slab = r.Slabs.[si]
                for iv in slab.Intervals do
                    cellTile.[idx] <- (iv, slab.Y, slab.Height)
                    idx <- idx + 1
            // Accumulate per-component bbox + area.
            let groups =
                System.Collections.Generic.Dictionary<int,
                    (int64 * int64 * int64 * int64) * int64>()
            for (i, bb) in cells do
                let root = dsu.Find i
                let (iv, _, h) = cellTile.[i]
                let tileArea = (iv.X2 - iv.X1) * h
                let (bx1, by1, bx2, by2) = bb
                match groups.TryGetValue root with
                | true, ((gx1, gy1, gx2, gy2), area) ->
                    let bbox' =
                        (min gx1 bx1, min gy1 by1,
                         max gx2 bx2, max gy2 by2)
                    groups.[root] <- (bbox', area + tileArea)
                | _ ->
                    groups.[root] <- (bb, tileArea)
            groups.Values |> Array.ofSeq
