module Rekolektion.Viz.Core.Drc.Geometry.Size

open Rekolektion.Viz.Core.Drc.Geometry.Region

/// Region sizing — Minkowski sum/difference with an axis-aligned
/// square of half-width N.
///
/// `grow N` — every point of the region "leaks" by N DBU in each
/// of the four cardinal directions, then the result is unioned
/// back into a canonical Region. Equivalent to: take every
/// rectangle in the input, expand its bbox by N on all sides,
/// then union the expanded rects.
///
/// `shrink N` — every point that's within N DBU of the region's
/// boundary is removed. Equivalent to the morphological erosion
/// with a 2N-side square structuring element. Computed via the
/// standard "complement of grown complement" trick, bounded to
/// a universe N larger than the input bbox so the complement
/// is finite.
///
/// Both operators preserve the half-open interval convention
/// of Region. Both return canonical-form Regions (slab merging
/// done by Region.ofPolygons / Boolean.subtract).

// --- grow --------------------------------------------------------------

/// Grow the region outward by `n` DBU in all four directions.
/// For each rectangle (slab × interval) in the input, emit an
/// expanded rectangle and re-union via Region.ofPolygons (which
/// handles overlapping/touching expanded rects correctly by
/// design — that's what the slab-sweep construction does).
///
/// `n <= 0`: returns the input unchanged.
let grow (n: int64) (r: Region) : Region =
    if n <= 0L then r
    elif isEmpty r then empty
    else
        // Decompose into (slab × interval) rectangles, expand
        // each, hand back to ofPolygons for canonicalization.
        let rects = ResizeArray<Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon>()
        let mutable seq = 0
        for slab in r.Slabs do
            let y0 = slab.Y - n
            let y1 = slab.Y + slab.Height + n
            for iv in slab.Intervals do
                let x0 = iv.X1 - n
                let x1 = iv.X2 + n
                let pts : Rekolektion.Viz.Core.Rkt.Types.Point array =
                    [| { X = x0; Y = y0 }
                       { X = x1; Y = y0 }
                       { X = x1; Y = y1 }
                       { X = x0; Y = y1 } |]
                rects.Add {
                    Layer = 0
                    DataType = 0
                    Points = pts
                    SourceStructure = "grow"
                    SourceIndex = seq
                    TopInstanceIndex = None }
                seq <- seq + 1
        ofPolygons (rects.ToArray())

// --- shrink ------------------------------------------------------------

/// Shrink (erode) the region inward by `n` DBU. Standard
/// morphology identity: `shrink(r, n) = U \ grow(U \ r, n)`
/// where U is a universe Region large enough to contain
/// everything we touch.
///
/// We use the input's bbox expanded by 2n as the universe — that
/// way the universe contains both the input AND any expansion
/// of its complement. Any region that disappears entirely under
/// erosion (smallest dimension < 2n) returns empty.
///
/// `n <= 0`: returns the input unchanged.
let shrink (n: int64) (r: Region) : Region =
    if n <= 0L then r
    elif isEmpty r then empty
    else
        match bbox r with
        | None -> empty
        | Some (xMin, yMin, xMax, yMax) ->
            // Universe: bbox of r expanded by 2n on each side.
            // grow(complement) can reach up to n into the
            // universe's edge; the 2n margin guarantees it
            // never touches the edge so the complement-of-
            // grown-complement returns the actual shrunk r,
            // not an artifact at the boundary.
            let pad = 2L * n
            let universe =
                ofRect (xMin - pad) (yMin - pad) (xMax + pad) (yMax + pad)
            let complement = Boolean.subtract universe r
            let complementGrown = grow n complement
            Boolean.subtract universe complementGrown
