module Rekolektion.Viz.Core.Drc.Geometry.Region

/// Slab-decomposed Region — the canonical "set of axis-aligned
/// polygons on one layer" representation that DRC operates on.
///
/// A Region is stored as an ordered sequence of horizontal
/// **slabs**. Each slab covers a Y interval `[Y, Y+Height)` and
/// holds a sorted list of non-overlapping X-intervals that are
/// "inside" the region at that Y. Two adjacent slabs always
/// differ in their interval set — runs of identical bands are
/// merged into one taller slab on construction. Empty intervals
/// are never stored.
///
/// **Closed under boolean operations** — union, intersect, and
/// subtract on slab-decomposed Regions produce another slab-
/// decomposed Region with the canonical (merged-runs) form. The
/// boolean ops live in `Drc.Geometry.Boolean`; this module owns
/// only construction, conversion, and basic queries.
///
/// **Why slabs vs. corner-stitched tiles** — slabs are pure
/// immutable F#: each slab is a record, each interval list is a
/// sorted array. Corner-stitched tiles need mutable linked
/// pointers and N/S/E/W neighbor invariants that F# doesn't
/// model gracefully. Slab decomposition is asymptotically
/// equivalent for our DRC workload (Lauther 1981 sweep on
/// slab-merged intervals is O((N+M) log N) per boolean op).
///
/// All coordinates are signed `int64` DBU — same unit FlatPolygon
/// uses. No floating point inside the geometry layer; rules that
/// take µm thresholds convert to DBU at the rule-eval boundary.

open Rekolektion.Viz.Core.Layout.Flatten

/// A single X-interval `[X1, X2]` inside a slab. Stored as a
/// closed-closed pair so a 0-width interval `{X1=10; X2=10}`
/// represents an isolated edge — useful for spacing checks that
/// emit zero-width violation strips. Construction normalizes
/// `X1 <= X2`; downstream code can rely on that invariant.
type Interval = {
    X1 : int64
    X2 : int64
}

/// A horizontal slab covering `[Y, Y + Height]`. `Intervals` is
/// sorted by `X1` ascending and contains no overlapping pairs.
/// An empty `Intervals` is allowed during construction but
/// removed during canonicalization (no point storing a slab
/// with nothing inside).
type Slab = {
    Y        : int64
    Height   : int64
    Intervals : Interval array
}

/// A Region is an ordered array of slabs. Invariants enforced by
/// every constructor and operator:
///   * Slabs are sorted by `Y` ascending.
///   * No two slabs overlap in Y (their Y intervals are disjoint).
///   * Adjacent slabs differ in their `Intervals` set — runs of
///     identical bands are merged into one taller slab.
///   * Empty slabs are dropped.
///   * Within each slab, intervals are sorted by `X1` and
///     pairwise disjoint (no overlap, no adjacency — touching
///     intervals are merged).
///   * `X1 <= X2` for every interval.
///
/// Any function returning a Region must produce one in canonical
/// form. Downstream consumers (boolean ops, DRC rules) rely on
/// the invariants — don't construct a Region directly, use the
/// builders.
type Region = {
    Slabs : Slab array
}

/// The empty region. Idempotent under any boolean op with itself.
let empty : Region = { Slabs = [||] }

/// True iff the region contains no geometry.
let isEmpty (r: Region) : bool = r.Slabs.Length = 0

// --- Interval-list helpers --------------------------------------------
//
// Each slab's `Intervals` is a sorted, non-overlapping,
// non-adjacent set. These helpers maintain that invariant.
// Used inside `ofPolygons` and shared with the boolean module.

/// Merge a sorted list of (possibly overlapping or adjacent)
/// intervals into the canonical form: sorted, non-overlapping,
/// non-adjacent. O(N) given sorted input.
let mergeSortedIntervals (sorted: Interval array) : Interval array =
    if sorted.Length <= 1 then sorted
    else
        let result = ResizeArray<Interval>(sorted.Length)
        let mutable cur = sorted.[0]
        for i in 1 .. sorted.Length - 1 do
            let next = sorted.[i]
            // Touching intervals (X2 + 1 = X1') ARE merged here
            // because the canonical form requires non-adjacent
            // intervals. DRC consumers treat the merged span as
            // one continuous strip — which is what they need
            // for width/spacing measurements across a contiguous
            // edge.
            if next.X1 <= cur.X2 + 1L then
                cur <- { cur with X2 = max cur.X2 next.X2 }
            else
                result.Add cur
                cur <- next
        result.Add cur
        result.ToArray()

let private intervalsEqual (a: Interval array) (b: Interval array) : bool =
    if a.Length <> b.Length then false
    else
        let mutable i = 0
        let mutable eq = true
        while eq && i < a.Length do
            if a.[i].X1 <> b.[i].X1 || a.[i].X2 <> b.[i].X2 then
                eq <- false
            i <- i + 1
        eq

// --- Slab list canonicalization ---------------------------------------

/// Take a Y-sorted list of (Y, Intervals) and produce the
/// canonical Region: drop empty slabs, merge adjacent slabs
/// with identical interval sets, set each slab's Height to the
/// distance to the next slab's Y. The LAST slab's Height is
/// the user-supplied terminator (caller passes the Y where the
/// region ends).
///
/// Internal helper used by `ofPolygons` and the boolean ops.
let private fromSlabBuilders
        (buildersY: int64 array)
        (buildersIntervals: Interval array array)
        : Region =
    // Walk the Y-sorted builders, computing each slab's Height
    // from the gap to the next builder. A slab with empty
    // intervals "closes" the previous run; we drop empty slabs
    // from the output but use them to terminate the prior
    // non-empty slab's height.
    if buildersY.Length = 0 then empty
    else
        let result = ResizeArray<Slab>()
        let mutable i = 0
        while i < buildersY.Length do
            let y = buildersY.[i]
            let intervals = buildersIntervals.[i]
            if intervals.Length = 0 then
                i <- i + 1
            else
                // Find the next builder where the intervals
                // differ (or the end). The current slab spans
                // [y, next_y).
                let mutable j = i + 1
                while j < buildersY.Length
                      && intervalsEqual buildersIntervals.[j] intervals do
                    j <- j + 1
                if j < buildersY.Length then
                    let nextY = buildersY.[j]
                    let height = nextY - y
                    if height > 0L then
                        result.Add { Y = y; Height = height; Intervals = intervals }
                    i <- j
                else
                    // No "next builder" to close this slab —
                    // means the polygon set genuinely ends at
                    // some Y above. The caller (ofPolygons)
                    // ensures the last builder is an empty
                    // entry that terminates. If we somehow
                    // reach here without a terminator, skip
                    // the trailing slab (would have infinite
                    // height otherwise).
                    i <- buildersY.Length
        { Slabs = result.ToArray() }

// --- Construction from FlatPolygon -----------------------------------

/// Per-polygon entry used inside `ofPolygons`. Cached bbox plus
/// a derived sorted edge list ready for sweep.
let private bboxOf (poly: FlatPolygon) : int64 * int64 * int64 * int64 =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    for p in poly.Points do
        if p.X < xMin then xMin <- p.X
        if p.X > xMax then xMax <- p.X
        if p.Y < yMin then yMin <- p.Y
        if p.Y > yMax then yMax <- p.Y
    xMin, yMin, xMax, yMax

/// Build a Region from a `FlatPolygon` array. Each polygon
/// contributes one rectangle (its bbox) — this is exact for
/// SKY130's axis-aligned-rect layout convention. For
/// non-rectangular polygons the bbox is a conservative
/// over-approximation (would over-flag rules); a future pass
/// can decompose general orthogonal polygons into rectangles
/// via the standard "scanline → rectangle decomposition"
/// algorithm if needed.
///
/// Algorithm: collect Y events from every polygon's top + bottom
/// edges, sort, sweep. At each unique Y, the "active set" is the
/// X-intervals contributed by polygons whose Y range straddles
/// that band. Adjacent bands with identical active sets merge
/// (the canonicalization in `fromSlabBuilders`).
let ofPolygons (polys: FlatPolygon array) : Region =
    if polys.Length = 0 then empty
    else
        // Per polygon: (yMin, yMax, [X1; X2] interval).
        let boxes =
            polys
            |> Array.map (fun p ->
                let xMin, yMin, xMax, yMax = bboxOf p
                yMin, yMax, { X1 = xMin; X2 = xMax })
        // Collect all unique Y values where the active set can
        // change. A polygon's yMin "opens" its interval; yMax
        // "closes" it. Both are event points.
        let yEvents =
            let s = System.Collections.Generic.SortedSet<int64>()
            for (yMin, yMax, _) in boxes do
                s.Add yMin |> ignore
                s.Add yMax |> ignore
            s |> Seq.toArray
        // For each event Y, the active intervals are those whose
        // polygon range covers [Y, Y+ε). The "+ε" matters at the
        // boundary: at yMax, the polygon's interval is no longer
        // active (the polygon ends exactly there). At yMin, it
        // just becomes active.
        let buildersIntervals =
            yEvents
            |> Array.map (fun y ->
                // Active = polygons whose [yMin, yMax) includes y.
                let active =
                    boxes
                    |> Array.choose (fun (yMin, yMax, iv) ->
                        if yMin <= y && y < yMax then Some iv else None)
                if active.Length = 0 then [||]
                else
                    active
                    |> Array.sortBy (fun iv -> iv.X1)
                    |> mergeSortedIntervals)
        fromSlabBuilders yEvents buildersIntervals

// --- Conversion back to FlatPolygon ----------------------------------

/// Decompose a Region into a flat array of axis-aligned
/// rectangles. Each slab × interval becomes one rectangle.
/// Vertical neighbors with the same interval are already merged
/// (via the canonical form) so this doesn't emit fragmentary
/// stripes for what was originally a single rect.
///
/// `layerN` and `dataType` populate the layer fields of the
/// emitted FlatPolygons. `SourceStructure` / `SourceIndex` are
/// stubbed to `"drc-violation"` / sequential indices — the
/// emitted polys are violation-region descriptors, not citations
/// of any source cell.
let toPolygons
        (layerN: int)
        (dataType: int)
        (r: Region)
        : FlatPolygon array =
    let result = ResizeArray<FlatPolygon>()
    let mutable seq = 0
    for slab in r.Slabs do
        for iv in slab.Intervals do
            let y0 = slab.Y
            let y1 = slab.Y + slab.Height
            let pts : Rekolektion.Viz.Core.Rkt.Types.Point array =
                [| { X = iv.X1; Y = y0 }
                   { X = iv.X2; Y = y0 }
                   { X = iv.X2; Y = y1 }
                   { X = iv.X1; Y = y1 } |]
            result.Add {
                Layer = layerN
                DataType = dataType
                Points = pts
                SourceStructure = "drc-violation"
                SourceIndex = seq
                TopInstanceIndex = None }
            seq <- seq + 1
    result.ToArray()

// --- Basic queries ----------------------------------------------------

/// Bounding box of the region (xMin, yMin, xMax, yMax). Returns
/// `None` for the empty region. Useful for fast reject in DRC
/// rules ("if A.bbox doesn't touch B.bbox, no need to run the
/// full op").
let bbox (r: Region) : (int64 * int64 * int64 * int64) option =
    if r.Slabs.Length = 0 then None
    else
        let mutable xMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let yMin = r.Slabs.[0].Y
        let mutable yMax = System.Int64.MinValue
        for slab in r.Slabs do
            yMax <- max yMax (slab.Y + slab.Height)
            for iv in slab.Intervals do
                if iv.X1 < xMin then xMin <- iv.X1
                if iv.X2 > xMax then xMax <- iv.X2
        Some (xMin, yMin, xMax, yMax)

/// Total area in DBU². Sum of (slab.Height × sum-of-interval-widths).
/// Used for min-area DRC checks once they're rewritten as Region
/// ops.
let area (r: Region) : int64 =
    let mutable total = 0L
    for slab in r.Slabs do
        let mutable rowWidth = 0L
        for iv in slab.Intervals do
            rowWidth <- rowWidth + (iv.X2 - iv.X1)
        total <- total + slab.Height * rowWidth
    total

/// Build a Region from a single rectangle. Used by tests and by
/// the boolean/sizing ops when they need a one-rect Region.
let ofRect (xMin: int64) (yMin: int64) (xMax: int64) (yMax: int64) : Region =
    if xMin > xMax || yMin > yMax then empty
    else
        { Slabs =
            [| { Y = yMin
                 Height = yMax - yMin
                 Intervals = [| { X1 = xMin; X2 = xMax } |] } |] }
