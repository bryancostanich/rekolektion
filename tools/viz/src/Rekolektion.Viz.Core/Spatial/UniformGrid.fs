module Rekolektion.Viz.Core.Spatial.UniformGrid

open System
open System.Collections.Generic

/// Axis-aligned bbox in DBU: (xMin, yMin, xMax, yMax).
type Bbox = int64 * int64 * int64 * int64

/// Uniform-grid spatial index over indexed bboxes. Each bbox can
/// span multiple cells; lookups walk the cells overlapping a query
/// rectangle and merge the per-cell index lists (deduplicating
/// across cells).
///
/// `CellSize` is the cell side length in DBU. Pick via
/// `suggestCellSize` for ~16 polys per cell on average, which
/// gives the best balance between bucket size (smaller = fewer
/// candidates per query) and memory (a poly's bbox spanning many
/// cells lives in every one).
type Index = {
    CellSize : int64
    Cells    : Dictionary<struct (int64 * int64), List<int>>
}

let private cellsCovering (cs: int64) ((xMin, yMin, xMax, yMax): Bbox) =
    seq {
        let cxMin = xMin / cs
        let cxMax = xMax / cs
        let cyMin = yMin / cs
        let cyMax = yMax / cs
        let mutable cx = cxMin
        while cx <= cxMax do
            let mutable cy = cyMin
            while cy <= cyMax do
                yield struct (cx, cy)
                cy <- cy + 1L
            cx <- cx + 1L
    }

/// Pick a cell size so the index averages ~16 entries per cell.
/// Returns a sensible default (1 µm at 1 nm/DBU) when the input
/// is empty. Clamped to [100, 10000] so pathological inputs don't
/// produce a single mega-cell or millions of single-poly cells.
let suggestCellSize (bboxes: Bbox array) : int64 =
    if bboxes.Length = 0 then 1000L
    else
        let mutable xMin = Int64.MaxValue
        let mutable yMin = Int64.MaxValue
        let mutable xMax = Int64.MinValue
        let mutable yMax = Int64.MinValue
        for (x0, y0, x1, y1) in bboxes do
            if x0 < xMin then xMin <- x0
            if y0 < yMin then yMin <- y0
            if x1 > xMax then xMax <- x1
            if y1 > yMax then yMax <- y1
        let dx = max 1L (xMax - xMin)
        let dy = max 1L (yMax - yMin)
        let area = float dx * float dy
        let perCell = 16.0
        let targetCells = max 1.0 (float bboxes.Length / perCell)
        let cellArea = area / targetCells
        let side = sqrt cellArea |> int64
        if side < 100L then 100L
        elif side > 10000L then 10000L
        else side

/// Build the index from an array of bboxes. Index = position in the
/// input array; that's what queries return.
let build (cellSize: int64) (bboxes: Bbox array) : Index =
    let cells = Dictionary<struct (int64 * int64), List<int>>()
    for i in 0 .. bboxes.Length - 1 do
        for key in cellsCovering cellSize bboxes.[i] do
            let bucket =
                match cells.TryGetValue key with
                | true, b -> b
                | _ ->
                    let b = List<int>()
                    cells.[key] <- b
                    b
            bucket.Add i
    { CellSize = cellSize; Cells = cells }

/// Visit every index whose bbox cell overlaps `bbox`. The visit
/// callback fires at most once per index (de-duplicated across
/// overlapping cells via an internal HashSet). Use this when you
/// want side-effect iteration without allocating a result list.
let queryBbox (idx: Index) (bbox: Bbox) (visit: int -> unit) : unit =
    let seen = HashSet<int>()
    for key in cellsCovering idx.CellSize bbox do
        match idx.Cells.TryGetValue key with
        | true, bucket ->
            for i in bucket do
                if seen.Add i then visit i
        | _ -> ()

/// Same as `queryBbox` but returns an array. Use when you need to
/// hold the result (e.g. for batched downstream filtering).
let queryBboxArray (idx: Index) (bbox: Bbox) : int array =
    let acc = List<int>()
    queryBbox idx bbox (fun i -> acc.Add i)
    acc.ToArray()
