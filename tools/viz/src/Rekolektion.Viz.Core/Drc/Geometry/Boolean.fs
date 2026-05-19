module Rekolektion.Viz.Core.Drc.Geometry.Boolean

open Rekolektion.Viz.Core.Drc.Geometry.Region

/// Boolean operations on slab-decomposed Regions.
///
/// All three primitives use the same skeleton (Lauther 1981
/// applied to slabs): collect Y events from both inputs, slice
/// the Y axis into bands where both regions are constant,
/// combine the interval sets on each band with the operator's
/// per-band combiner, feed the resulting (Y, intervals) list to
/// `Region.fromSlabBuilders` for canonicalization.
///
/// O((N+M) log (N+M)) for sorted Y-event collection, O((N+M)·K)
/// for per-band combining where K is the average intervals-per-
/// slab. SKY130-scale macros (a few thousand polygons per layer)
/// run sub-millisecond per op.
///
/// The interval-set combiners (intersect, subtract, union) are
/// the same primitives the X axis would need on a per-band
/// basis; they're shared internal helpers below.

// --- Per-band interval-set combiners -----------------------------------

/// Sorted union of two sorted, non-overlapping interval arrays.
/// Result is canonical (sorted, non-overlapping, non-adjacent).
/// Used by union; also the way Subtract / Intersect produce
/// their per-band results when both inputs touch.
let private unionIntervals
        (a: Interval array)
        (b: Interval array)
        : Interval array =
    if a.Length = 0 then b
    elif b.Length = 0 then a
    else
        // Merge the two sorted lists, then collapse overlaps /
        // adjacencies via the shared merge helper.
        let combined = Array.zeroCreate<Interval> (a.Length + b.Length)
        let mutable i = 0
        let mutable j = 0
        let mutable k = 0
        while i < a.Length && j < b.Length do
            if a.[i].X1 <= b.[j].X1 then
                combined.[k] <- a.[i]
                i <- i + 1
            else
                combined.[k] <- b.[j]
                j <- j + 1
            k <- k + 1
        while i < a.Length do
            combined.[k] <- a.[i]; i <- i + 1; k <- k + 1
        while j < b.Length do
            combined.[k] <- b.[j]; j <- j + 1; k <- k + 1
        mergeSortedIntervals combined

/// Per-band intersection of two sorted interval arrays. Result
/// holds every X range that's "inside" both inputs. Already
/// canonical when produced (no adjacency collapse needed —
/// intersection can't create touching pairs that weren't there
/// in both inputs).
let private intersectIntervals
        (a: Interval array)
        (b: Interval array)
        : Interval array =
    if a.Length = 0 || b.Length = 0 then [||]
    else
        let result = ResizeArray<Interval>()
        let mutable i = 0
        let mutable j = 0
        while i < a.Length && j < b.Length do
            let ax1, ax2 = a.[i].X1, a.[i].X2
            let bx1, bx2 = b.[j].X1, b.[j].X2
            let lo = max ax1 bx1
            let hi = min ax2 bx2
            // Half-open: emit non-empty intersection only when
            // lo < hi (lo == hi means touching, no overlap).
            if lo < hi then
                result.Add { X1 = lo; X2 = hi }
            // Advance whichever ends first; the other might
            // still overlap with the next from this side.
            if ax2 < bx2 then i <- i + 1
            elif bx2 < ax2 then j <- j + 1
            else
                i <- i + 1
                j <- j + 1
        result.ToArray()

/// Per-band subtraction `a - b`: every X range inside `a` and
/// not inside any of `b`. Result is canonical.
let private subtractIntervals
        (a: Interval array)
        (b: Interval array)
        : Interval array =
    if a.Length = 0 then [||]
    elif b.Length = 0 then a
    else
        let result = ResizeArray<Interval>()
        let mutable bi = 0
        for ai in 0 .. a.Length - 1 do
            // Track the "remaining left edge" of a.[ai] as we
            // chop off overlapping pieces from b.[bi..].
            let mutable left = a.[ai].X1
            let aright = a.[ai].X2
            // Advance bi past any b intervals entirely left of
            // a.[ai] (b.X2 <= left, touching or fully left
            // under half-open). Touching b doesn't subtract
            // anything from a.
            while bi < b.Length && b.[bi].X2 <= left do
                bi <- bi + 1
            // Walk b's that overlap a.[ai]. Overlap means
            // b.X1 < aright (b starts before a ends, under
            // half-open).
            let mutable scanJ = bi
            while scanJ < b.Length && b.[scanJ].X1 < aright do
                let bx1, bx2 = b.[scanJ].X1, b.[scanJ].X2
                if bx2 <= left then
                    // Already past or touching — skip.
                    ()
                else
                    // The piece of a from `left` to `bx1`
                    // (half-open) survives if non-empty.
                    if left < bx1 then
                        result.Add { X1 = left; X2 = bx1 }
                    // After this b, the next surviving piece
                    // starts at max(left, bx2).
                    if bx2 > left then
                        left <- bx2
                scanJ <- scanJ + 1
            // Tail of a.[ai] past every overlapping b.
            if left < aright then
                result.Add { X1 = left; X2 = aright }
        result.ToArray()

// --- Y-event collection + slab lookup ----------------------------------

/// Binary search for the slab whose Y interval contains `y`.
/// Returns the slab's intervals, or `[||]` if no slab covers
/// `y`. O(log S) where S = slab count.
let private intervalsAt (slabs: Slab array) (y: int64) : Interval array =
    let mutable lo = 0
    let mutable hi = slabs.Length - 1
    let mutable result : Interval array = [||]
    let mutable found = false
    while not found && lo <= hi do
        let mid = (lo + hi) / 2
        let slab = slabs.[mid]
        if y < slab.Y then hi <- mid - 1
        elif y >= slab.Y + slab.Height then lo <- mid + 1
        else
            result <- slab.Intervals
            found <- true
    result

/// Y values where either region's slab set changes. Each slab
/// contributes its Y (start) and Y+Height (end). Sorted,
/// deduplicated.
let private yEventsOf (a: Region) (b: Region) : int64 array =
    let s = System.Collections.Generic.SortedSet<int64>()
    for slab in a.Slabs do
        s.Add slab.Y |> ignore
        s.Add (slab.Y + slab.Height) |> ignore
    for slab in b.Slabs do
        s.Add slab.Y |> ignore
        s.Add (slab.Y + slab.Height) |> ignore
    s |> Seq.toArray

/// Generic boolean dispatch. `combine` defines what to do with
/// each Y band's interval pair. Empty-result bands and adjacent-
/// identical bands are collapsed by `Region.fromSlabBuilders`.
let private bop
        (combine: Interval array -> Interval array -> Interval array)
        (a: Region) (b: Region)
        : Region =
    let yEvents = yEventsOf a b
    if yEvents.Length < 2 then empty
    else
        let buildersIntervals =
            yEvents
            |> Array.map (fun y ->
                combine (intervalsAt a.Slabs y) (intervalsAt b.Slabs y))
        fromSlabBuilders yEvents buildersIntervals

// --- Public API --------------------------------------------------------

/// `a ∪ b` — union. Every X range inside either input is in the
/// result.
let union (a: Region) (b: Region) : Region =
    if isEmpty a then b
    elif isEmpty b then a
    else bop unionIntervals a b

/// `a ∩ b` — intersection. Every X range inside both inputs is
/// in the result.
let intersect (a: Region) (b: Region) : Region =
    if isEmpty a || isEmpty b then empty
    else bop intersectIntervals a b

/// `a \ b` — subtraction. Every X range inside `a` and not
/// inside `b` is in the result.
let subtract (a: Region) (b: Region) : Region =
    if isEmpty a then empty
    elif isEmpty b then a
    else bop subtractIntervals a b
