module Rekolektion.Viz.Core.Drc.Implant

open Rekolektion.Viz.Core.Layout.Flatten

/// Per-polygon implant / context tags derived from a bbox-overlap
/// pre-pass over the implant marker layers. SKY130 implants are
/// axis-aligned rectangles in practice, so bbox-AND faithfully
/// models the real polygon-AND we'd get from a boolean engine.
///
/// Most rules don't need every field — `licon.5a` only consults
/// `OverlapsDiff` on the licon, `difftap.8a` only consults
/// `PsdmOverlaps` on the diff. Fields are computed for every
/// polygon regardless; the cost is paid once per file load.
type ImplantTags = {
    /// True iff a PSDM (94/20) polygon overlaps this polygon's
    /// bbox. For diff polygons this marks p-diff. PSDM also
    /// marks p-taps.
    PsdmOverlaps : bool
    /// True iff an NSDM (93/44) polygon overlaps this polygon's
    /// bbox. For diff polygons this marks n-diff. NSDM also
    /// marks n-taps.
    NsdmOverlaps : bool
    /// True iff a DIFF (65/20) polygon overlaps this polygon's
    /// bbox. Used to classify licon1 contacts: a licon over diff
    /// is a "diff-contact" (subject to licon.5a/c enclosure
    /// rules); over poly is a "poly-contact" (licon.8/9).
    OverlapsDiff : bool
    /// True iff a POLY (66/20) polygon overlaps this polygon's
    /// bbox. Mirrors `OverlapsDiff` for poly-contact licons.
    OverlapsPoly : bool
    /// True iff an NWELL (64/20) polygon overlaps this polygon's
    /// bbox. Used by p-diff / n-diff vs nwell interaction rules
    /// (difftap.8a — p-diff must be in nwell; difftap.9 — n-diff
    /// must not be in nwell).
    OverlapsNwell : bool
}

/// Empty tags — every field false. Returned for polygons outside
/// the tagged-array bounds, and the safe default when implant
/// awareness isn't required for a particular rule.
let emptyTags : ImplantTags = {
    PsdmOverlaps  = false
    NsdmOverlaps  = false
    OverlapsDiff  = false
    OverlapsPoly  = false
    OverlapsNwell = false
}

// SKY130 marker-layer keys. Matches Rules.fs / Layout.Layer
// numbering exactly.
let private psdmKey  = 94, 20
let private nsdmKey  = 93, 44
let private diffKey  = 65, 20
let private polyKey  = 66, 20
let private nwellKey = 64, 20

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

let private bboxOverlaps
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : bool =
    ax1 < bx2 && bx1 < ax2 && ay1 < by2 && by1 < ay2

/// Compute tags for every polygon in `flat`. Returns an array
/// indexed the same way as `flat` — `tags.[i]` describes
/// `flat.[i]`. Cost: O(N) per marker layer over the polygons
/// being tagged. For production-scale macros with thousands of
/// diff polygons + thousands of implant rects this is the
/// expensive step; future optimization could replace the inner
/// loops with a sweepline.
let tagAll (flat: FlatPolygon array) : ImplantTags array =
    if flat.Length = 0 then [||]
    else
    // Precompute bbox lists for each marker layer.
    let mutable psdmBbs  = ResizeArray<int64 * int64 * int64 * int64>()
    let mutable nsdmBbs  = ResizeArray<int64 * int64 * int64 * int64>()
    let mutable diffBbs  = ResizeArray<int64 * int64 * int64 * int64>()
    let mutable polyBbs  = ResizeArray<int64 * int64 * int64 * int64>()
    let mutable nwellBbs = ResizeArray<int64 * int64 * int64 * int64>()
    let bboxes = Array.zeroCreate<int64 * int64 * int64 * int64> flat.Length
    for i in 0 .. flat.Length - 1 do
        let p = flat.[i]
        let bb = bboxOf p
        bboxes.[i] <- bb
        let key = p.Layer, p.DataType
        if   key = psdmKey  then psdmBbs.Add  bb
        elif key = nsdmKey  then nsdmBbs.Add  bb
        elif key = diffKey  then diffBbs.Add  bb
        elif key = polyKey  then polyBbs.Add  bb
        elif key = nwellKey then nwellBbs.Add bb
    let anyOverlaps (src: int64 * int64 * int64 * int64)
                    (others: ResizeArray<int64 * int64 * int64 * int64>) =
        let mutable hit = false
        let mutable i = 0
        while not hit && i < others.Count do
            if bboxOverlaps src others.[i] then hit <- true
            i <- i + 1
        hit
    Array.init flat.Length (fun i ->
        let bb = bboxes.[i]
        // Skip self-overlap: a diff polygon overlapping itself
        // shouldn't count as "OverlapsDiff" — but since DIFF is
        // not a contact layer we don't query OverlapsDiff for
        // diff polygons in practice. Same logic for the others.
        // Cheaper to compute and let the caller filter than to
        // special-case here.
        { PsdmOverlaps  = anyOverlaps bb psdmBbs
          NsdmOverlaps  = anyOverlaps bb nsdmBbs
          OverlapsDiff  = anyOverlaps bb diffBbs
          OverlapsPoly  = anyOverlaps bb polyBbs
          OverlapsNwell = anyOverlaps bb nwellBbs })

/// Safe tag lookup — returns `emptyTags` for indices outside the
/// tagged-array bounds (e.g. callers that synthesized polygons
/// after tagging).
let tagOf (tags: ImplantTags array) (i: int) : ImplantTags =
    if i >= 0 && i < tags.Length then tags.[i] else emptyTags

/// SKY130 sign-off pre-processing for implant layers.
///
/// Magic's `sky130B.tech` (lines 838–871) treats NSDM/PSDM as
/// CIF-processed layers, not silicon-truth inputs:
///
/// ```
/// templayer extendNSDM  baseNSDM
///     bridge  380 380             ← merges NSDM rects ≤380 nm apart
///     and-not basePSDM
/// layer NSDM baseNSDM,extendNSDM
///     grow    185
///     shrink  185
///     close   265000
/// ```
///
/// Spacing rules apply to the post-processed silicon — the input
/// rects are intentionally drawn with small gaps that get bridged.
/// Running a raw `nsdm.2` spacing check on input rects produces
/// false positives at every legitimate sub-380 nm gap (e.g. two
/// abutting nfet primitives whose own nsdm rects sit 20 nm apart,
/// j_az_col.rkt 2026-05-31).
///
/// This step models the magic pipeline at the boolean-region
/// level: morphological close at radius (bridgeDistance / 2)
/// bridges any gap ≤ bridgeDistance. The 185-nm grow/shrink halo
/// and 265 µm pinch-close are deferred — those affect outer-edge
/// position by ≤185 nm, far below the violation threshold for the
/// spacing rules they gate, so we leave them out until a regression
/// test demands them.
///
/// Returns the rebuilt `FlatPolygon array` with implant layers
/// replaced by their post-close polygons. Other layers pass
/// through unchanged.
///
/// Track 02 follow-up #2 — moved here from Check.fs so
/// `checkWithToggles` can call it directly.  Was previously only
/// applied by the higher-level `Check.check` entry point; the
/// canvas + live-DRC paths called `checkWithToggles` directly and
/// silently skipped the close, producing extra `nsdm.1` / `psdm.1`
/// fires on layouts with abutting primitive rects under
/// Magic-compat.  Under Klayout-compat the function is a no-op
/// (the KLayout deck spacing rules fire on the literal gap).
let applyImplantClose
        (compat: Compat.Compat)
        (flat: FlatPolygon array) : FlatPolygon array =
    if compat = Compat.Klayout then flat else
    let nsdmKey = (93, 44)
    let psdmKey = (94, 20)
    let bridgeRadius = 190L   // close at 190 nm bridges any gap ≤380 nm
    let closeLayer ((num, dt): int * int) (polys: FlatPolygon array)
            : FlatPolygon array =
        if polys.Length = 0 then polys
        else
            let region = Geometry.Region.ofPolygons polys
            let closed =
                region
                |> Geometry.Size.grow bridgeRadius
                |> Geometry.Size.shrink bridgeRadius
            // Re-emit as FlatPolygons on the original layer. Source
            // tracking is lost in the boolean-region round-trip, so
            // we tag the result with a "drc-closed" structure name
            // for diagnostics.
            Geometry.Region.toPolygons num dt closed
            |> Array.mapi (fun i p ->
                { p with
                    SourceStructure = "drc-implant-closed"
                    SourceIndex = i
                    TopInstanceIndex = None })
    let groupByKey (key: int * int) =
        flat |> Array.filter (fun p ->
            p.Layer = fst key && p.DataType = snd key)
    let nsdmIn = groupByKey nsdmKey
    let psdmIn = groupByKey psdmKey
    let nsdmOut = closeLayer nsdmKey nsdmIn
    let psdmOut = closeLayer psdmKey psdmIn
    let other =
        flat
        |> Array.filter (fun p ->
            not (p.Layer = fst nsdmKey && p.DataType = snd nsdmKey)
            && not (p.Layer = fst psdmKey && p.DataType = snd psdmKey))
    Array.concat [ other; nsdmOut; psdmOut ]
