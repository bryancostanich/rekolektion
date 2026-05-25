module Rekolektion.Viz.Core.Drc.Check

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Drc.Geometry

/// µm per DBU, derived from the document's `Units.DbuNm` (nm/DBU).
/// 1 µm = 1000 nm, so 1 nm/DBU = 0.001 µm/DBU.
let private umPerDbuOf (units: Units) : float =
    float units.DbuNm * 1.0e-3

/// One DRC violation. `Rule` is "<layer>.<rule>", e.g. "met1.spacing".
/// `BboxA` / `BboxB` are world-DBU axis-aligned bboxes — for a
/// width violation `BboxB` is None; for a spacing violation it's
/// the second polygon. `MeasuredDbu` is the actual width or gap
/// measured from the polygons; the renderer reports it next to
/// the geometry so the user sees both the rule limit and how
/// much margin is missing.
type Violation = {
    Rule        : string
    LayerNumber : int
    LayerType   : int
    LimitDbu    : int64
    MeasuredDbu : int64
    BboxA       : int64 * int64 * int64 * int64
    BboxB       : (int64 * int64 * int64 * int64) option
}

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

/// Orthogonal facing-edge gap, with the gap REGION as a bbox:
/// returns `Some (d, gapBbox)` when the two input bboxes have a
/// facing edge, and `None` otherwise. The gap bbox covers the
/// rectangular strip between the two facing edges — gap-wide on
/// the perpendicular axis, common-projection-long on the
/// parallel axis. Clustering uses this small bbox so violations
/// at unrelated gaps don't merge by virtue of their polygon
/// pair's union encompassing the whole layer.
let private bboxOrthoGapAndRegion
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : (int64 * (int64 * int64 * int64 * int64)) option =
    let xOverlap = (min ax2 bx2) > (max ax1 bx1)
    let yOverlap = (min ay2 by2) > (max ay1 by1)
    if xOverlap && yOverlap then
        // Bbox overlap — not a clean spacing case; emit zero
        // gap with a tiny gap region at the intersection.
        let gx1 = max ax1 bx1
        let gy1 = max ay1 by1
        let gx2 = min ax2 bx2
        let gy2 = min ay2 by2
        Some (0L, (gx1, gy1, gx2, gy2))
    elif xOverlap then
        // Vertical gap (one above, one below). Gap bbox = X
        // common projection × Y between the two.
        let g, gy1, gy2 =
            if ay2 <= by1 then by1 - ay2, ay2, by1
            elif by2 <= ay1 then ay1 - by2, by2, ay1
            else 0L, 0L, 0L
        let gx1 = max ax1 bx1
        let gx2 = min ax2 bx2
        Some (g, (gx1, gy1, gx2, gy2))
    elif yOverlap then
        // Horizontal gap. Gap bbox = X between × Y common.
        let g, gx1, gx2 =
            if ax2 <= bx1 then bx1 - ax2, ax2, bx1
            elif bx2 <= ax1 then ax1 - bx2, bx2, ax1
            else 0L, 0L, 0L
        let gy1 = max ay1 by1
        let gy2 = min ay2 by2
        Some (g, (gx1, gy1, gx2, gy2))
    else
        None

/// Orthogonal facing-edge gap: returns `Some d` when the two
/// bboxes have a facing edge (one axis's projections overlap,
/// the other axis's don't — so there's a clean perpendicular
/// distance between them) and `None` for diagonal pairs (no
/// projection overlap on either axis — only the corners face
/// each other, governed by separate corner rules, not by per-
/// layer spacing). Magic's spacing rules behave the same way.
///
/// Returns 0 when the projections overlap on BOTH axes (bboxes
/// intersect) — caller decides whether to treat that as an
/// overlap or as a zero-gap touch.
let private bboxOrthoGap
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : int64 option =
    let xOverlap = (min ax2 bx2) > (max ax1 bx1)
    let yOverlap = (min ay2 by2) > (max ay1 by1)
    if xOverlap && yOverlap then
        Some 0L
    elif xOverlap then
        // X projections overlap: shapes are one-above-the-other;
        // perpendicular gap is along Y.
        let g =
            if ay2 <= by1 then by1 - ay2
            elif by2 <= ay1 then ay1 - by2
            else 0L
        Some g
    elif yOverlap then
        let g =
            if ax2 <= bx1 then bx1 - ax2
            elif bx2 <= ax1 then ax1 - bx2
            else 0L
        Some g
    else
        // Diagonal pair — no facing edge, no spacing rule fires.
        None

let private bboxGap
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : int64 =
    let xGap =
        if ax2 < bx1 then bx1 - ax2
        elif bx2 < ax1 then ax1 - bx2
        else 0L
    let yGap =
        if ay2 < by1 then by1 - ay2
        elif by2 < ay1 then ay1 - by2
        else 0L
    if xGap = 0L && yGap = 0L then 0L
    elif xGap = 0L then yGap
    elif yGap = 0L then xGap
    else
        // Diagonal — use Euclidean distance, rounded to integer
        // DBU. Sub-DBU diagonal-corner gaps round up to 1 so we
        // don't false-trigger spacing.
        let dx = float xGap
        let dy = float yGap
        let d = sqrt (dx * dx + dy * dy)
        max 1L (int64 (System.Math.Round d))

let private umToDbu (umPerDbu: float) (um: float) : int64 =
    if umPerDbu <= 0.0 then 0L
    else max 0L (int64 (System.Math.Round (um / umPerDbu)))

/// Bbox containment with margin: does `outer` fully contain
/// `inner` with at least `marginDbu` slack on every side? Returns
/// the smallest of the four edge margins (the violating-edge
/// distance). A negative-or-zero return means the inner pokes
/// out (or has zero margin somewhere) and the rule fires.
let private bboxContainsMargin
        ((ix1, iy1, ix2, iy2): int64 * int64 * int64 * int64)
        ((ox1, oy1, ox2, oy2): int64 * int64 * int64 * int64)
        : int64 =
    let leftM   = ix1 - ox1
    let bottomM = iy1 - oy1
    let rightM  = ox2 - ix2
    let topM    = oy2 - iy2
    min (min leftM bottomM) (min rightM topM)

/// Per-axis enclosure margins. Returns the smaller of the two
/// per-axis pairs: `xMargin` = min(left, right), `yMargin` =
/// min(bottom, top). Asymmetric enclosure compares these against
/// per-axis thresholds (one for the long-axis pair, one for the
/// short).
let private bboxContainsMarginAxis
        ((ix1, iy1, ix2, iy2): int64 * int64 * int64 * int64)
        ((ox1, oy1, ox2, oy2): int64 * int64 * int64 * int64)
        : int64 * int64 =
    let xMargin = min (ix1 - ox1) (ox2 - ix2)
    let yMargin = min (iy1 - oy1) (oy2 - iy2)
    xMargin, yMargin

/// Bbox overlap (any shared interior, not just touching). True
/// when the two rects share area > 0.
let private bboxOverlaps
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : bool =
    ax1 < bx2 && bx1 < ax2 && ay1 < by2 && by1 < ay2

/// Index polygons by (Layer, DataType) once so each rule lookup
/// is O(1) instead of re-scanning the whole flat array. Each
/// entry pairs the polygon with its bbox AND its position in the
/// original `flat` array, so the caller can look up implant tags
/// (which are indexed parallel to `flat`).
let private indexByLayer
        (flat: FlatPolygon array)
        : System.Collections.Generic.Dictionary<int * int,
            (FlatPolygon * (int64 * int64 * int64 * int64) * int) array> =
    let dict =
        System.Collections.Generic.Dictionary<int * int,
            (FlatPolygon * (int64 * int64 * int64 * int64) * int) array>()
    flat
    |> Array.mapi (fun i p -> i, p)
    |> Array.groupBy (fun (_, p) -> p.Layer, p.DataType)
    |> Array.iter (fun (key, items) ->
        let withBboxes =
            items |> Array.map (fun (i, p) -> p, bboxOf p, i)
        dict.[key] <- withBboxes)
    dict

let private layerKey (l: Rules.LayerKey) = l.Number, l.DataType

let private polysOnLayer
        (idx: System.Collections.Generic.Dictionary<int * int,
                (FlatPolygon * (int64 * int64 * int64 * int64) * int) array>)
        (key: Rules.LayerKey) =
    match idx.TryGetValue (layerKey key) with
    | true, arr -> arr
    | _ -> [||]

/// Test an `InnerCondition` against the implant tags of a single
/// polygon. Used by Enclosure to skip inner polygons that don't
/// match the rule's type filter (e.g. licon.5a only checks
/// diff-contact licons).
let private condMatches
        (cond: Rules.InnerCondition)
        (tags: Implant.ImplantTags)
        : bool =
    match cond with
    | Rules.Always -> true
    | Rules.OverlapsDiff -> tags.OverlapsDiff
    | Rules.OverlapsPoly -> tags.OverlapsPoly
    | Rules.PsdmOverlaps -> tags.PsdmOverlaps
    | Rules.NsdmOverlaps -> tags.NsdmOverlaps
    | Rules.NsdmNotInNwell -> tags.NsdmOverlaps && not tags.OverlapsNwell

/// Run all rules in `Rules.allRules` against every polygon in
/// `flat`, filtered by `disabledRules` (Magic-compatible rule
/// names from `Rules.nameOf` — any rule whose name appears in the
/// set is skipped).
///
/// Per-rule complexity:
///   Width / MinArea     — O(N) over polys on the layer
///   Spacing             — O(N²) within a layer
///   CrossSpacing        — O(N×M) across two layers
///   Enclosure / Endcap  — O(N×M) across two layers
///
/// At edit time the canvas restricts `flat` to the active macro's
/// top cell + flattened instances; production-scale macros may
/// want neighborhood-restriction further if any single layer
/// crosses ~10k polys.
let checkWithToggles
        (view: Rules.RulesetView)
        (units: Units)
        (flat: FlatPolygon array)
        (tags: Implant.ImplantTags array)
        (disabledRules: Set<string>)
        : Violation array =
    let umPerDbu = umPerDbuOf units
    let raw = System.Collections.Generic.List<Violation>()
    let result = raw
    let idx = indexByLayer flat
    // Collect COREID core areas once. Empty when there are no
    // areaid_core polygons in the flat — no waivers fire and the
    // post-pass filter is a no-op.
    let coreAreas = Waiver.collectCoreAreas flat
    // Foundry-primitive cell footprints. Any violation fully
    // inside one of these is waived (Magic's per-cell scope:
    // foundry cells are atomic and their internal sub-spec
    // geometry is pre-validated). Cross-cell violations stay
    // in the report.
    let foundryFootprints = Waiver.collectFoundryFootprints flat

    let checkRule (rule: Rules.Rule) =
        let ruleName = Rules.nameOf rule
        if disabledRules.Contains ruleName then () else
        match rule with
        | Rules.Width (name, layer, minUm) ->
            // Per-polygon bbox width, but a narrow polygon is
            // waived when an overlapping same-layer polygon
            // extends its narrow axis to ≥ limit across the
            // perpendicular range — Magic merges connected
            // metals into one region, and the merged feature
            // is wide enough at this polygon's location, so
            // there's no width violation. Classic case: a
            // small rect entirely contained in a wider rect
            // (the wider rect's bbox covers the smaller's
            // perpendicular range and extends past on the
            // narrow axis).
            //
            // Morphological opening on the Region was tried
            // and rejected — slab fragmentation produces
            // thousands of bogus tile-fragment violations on
            // clean licon arrays, and integer-DBU shrink/grow
            // can't represent "width ≥ limit passes, < limit
            // fails" cleanly (the half-grid problem). Per-
            // polygon with the merge-coverage waiver is what
            // matches Magic on the cases we've seen.
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let polys = polysOnLayer idx layer
                for i in 0 .. polys.Length - 1 do
                    let (_, (ax1, ay1, ax2, ay2), _) = polys.[i]
                    let w = ax2 - ax1
                    let h = ay2 - ay1
                    let m = min w h
                    if m < limit then
                        let narrowIsX = w < h
                        let mutable covered = false
                        let mutable j = 0
                        while not covered && j < polys.Length do
                            if j <> i then
                                let (_, (bx1, by1, bx2, by2), _) = polys.[j]
                                // Touch-or-overlap (closed-interval).
                                // Magic merges touching polys on
                                // the same layer into one feature
                                // for width checks. The classic
                                // case: a 5 nm-thick sliver of
                                // poly inside a foundry resistor
                                // abuts the resistor body exactly
                                // at one edge — strict overlap
                                // wouldn't see the join, and the
                                // sliver fires a bogus poly.1a.
                                let touches =
                                    ax1 <= bx2 && bx1 <= ax2 &&
                                    ay1 <= by2 && by1 <= ay2
                                if touches then
                                    if narrowIsX then
                                        if by1 <= ay1 && ay2 <= by2 then
                                            let ux = (max ax2 bx2) - (min ax1 bx1)
                                            if ux >= limit then covered <- true
                                    else
                                        if bx1 <= ax1 && ax2 <= bx2 then
                                            let uy = (max ay2 by2) - (min ay1 by1)
                                            if uy >= limit then covered <- true
                            j <- j + 1
                        if not covered then
                            result.Add {
                                Rule = name
                                LayerNumber = layer.Number
                                LayerType   = layer.DataType
                                LimitDbu    = limit
                                MeasuredDbu = m
                                BboxA = (ax1, ay1, ax2, ay2)
                                BboxB = None }
        | Rules.Spacing (name, layer, minUm) ->
            // Per-pair facing-edge spacing, but pairs that belong
            // to the same connected component of the layer are
            // skipped — they're physically one feature so Magic
            // doesn't fire spacing between their parts.
            //
            // Connectivity: bbox overlap OR touching (touch =
            // connected for SKY130 metal-like layers). Build DSU
            // over polys by sweeping bboxes once. A bridged
            // L-shape (A and C, connected via overlapping B) is
            // detected: A and C end up in the same component, so
            // the A↔C narrow gap doesn't fire even though their
            // bboxes don't directly overlap.
            //
            // Morphology was ruled out: rectilinear closing fills
            // any cavity narrower than 2·limit anywhere on its
            // perimeter, even if no edge-pair is < limit apart.
            // That's closing semantics, not spacing semantics.
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let polys = polysOnLayer idx layer
                let n = polys.Length
                // DSU over poly indices. Two polys are in the
                // same component when their bboxes touch or
                // overlap (right/left edges share a coordinate
                // OR perpendicular projections overlap with
                // bbox-intersect on the other axis).
                let parent = Array.init n id
                let rec find x =
                    if parent.[x] = x then x
                    else
                        let r = find parent.[x]
                        parent.[x] <- r
                        r
                let union a b =
                    let ra = find a
                    let rb = find b
                    if ra <> rb then parent.[ra] <- rb
                // Pairwise component build (O(N²) for clarity;
                // matches the spacing loop's cost so no asymptotic
                // change).
                for i in 0 .. n - 1 do
                    let (_, bbA, _) = polys.[i]
                    let (ax1, ay1, ax2, ay2) = bbA
                    for j in i + 1 .. n - 1 do
                        let (_, bbB, _) = polys.[j]
                        let (bx1, by1, bx2, by2) = bbB
                        // Touch-or-overlap: bboxes share any
                        // point. Using closed-interval test
                        // (≤/≥) so abutting metals count as
                        // connected.
                        let touches =
                            ax1 <= bx2 && bx1 <= ax2 &&
                            ay1 <= by2 && by1 <= ay2
                        if touches then union i j
                // Spacing per pair, skipping same-component pairs.
                for i in 0 .. n - 1 do
                    let (_, bbA, _) = polys.[i]
                    for j in i + 1 .. n - 1 do
                        let (_, bbB, _) = polys.[j]
                        if find i <> find j then
                            match bboxOrthoGapAndRegion bbA bbB with
                            | Some (g, gapBb) when g > 0L && g < limit ->
                                result.Add {
                                    Rule = name
                                    LayerNumber = layer.Number
                                    LayerType   = layer.DataType
                                    LimitDbu    = limit
                                    MeasuredDbu = g
                                    BboxA = gapBb
                                    BboxB = None }
                            | _ -> ()
        | Rules.CrossSpacing (name, layerA, layerB, minUm, condA, condB) ->
            // Same orthogonal-only rule as same-layer Spacing.
            // Overlap = same net at this layer pair (e.g. poly
            // contact on diff is legal); skip to avoid false-
            // firing on intentional crossings.
            //
            // `condA`/`condB` filter each side to typed subsets
            // via the implant tags pre-pass. Examples:
            //   diff/tap.9: NsdmNotInNwell on the source side
            //               (n-diff outside well).
            //   rpm.3-6-nsd.5a: NsdmNotInNwell on the *target*
            //               diff (precision resistor only
            //               cares about distance to n-diff).
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let polysA = polysOnLayer idx layerA
                let polysB = polysOnLayer idx layerB
                for (_, bbA, aIdx) in polysA do
                    let aTags = Implant.tagOf tags aIdx
                    if condMatches condA aTags then
                        for (_, bbB, bIdx) in polysB do
                            let bTags = Implant.tagOf tags bIdx
                            if condMatches condB bTags
                               && not (bboxOverlaps bbA bbB) then
                                match bboxOrthoGapAndRegion bbA bbB with
                                | Some (g, gapBb) when g > 0L && g < limit ->
                                    // Gap-region bbox (small)
                                    // for correct clustering —
                                    // see Spacing rule above.
                                    result.Add {
                                        Rule = name
                                        LayerNumber = layerA.Number
                                        LayerType   = layerA.DataType
                                        LimitDbu    = limit
                                        MeasuredDbu = g
                                        BboxA = gapBb
                                        BboxB = None }
                                | _ -> ()
        | Rules.Enclosure (name, outer, inner, minUm, cond) ->
            // Magic-fidelity enclosure via Region morphology:
            //   violations = inner \ shrink(outer, N)
            // shrink(outer, N) is the "core" of outer that has
            // ≥ N margin on every side. Anything in inner that
            // isn't in that core is too close to outer's edge
            // (or completely outside outer) — a violation.
            //
            // Implant condition filters which inner polygons
            // contribute. licon.5a only applies to diff-contact
            // licons (OverlapsDiff); inners that don't match
            // the condition are excluded from the violation
            // calculation.
            //
            // Per-cluster reporting falls out of the post-pass.
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let outers = polysOnLayer idx outer
                let inners = polysOnLayer idx inner
                let outerPolys = outers |> Array.map (fun (p, _, _) -> p)
                let innerPolysFiltered =
                    inners
                    |> Array.filter (fun (_, _, iIdx) ->
                        condMatches cond (Implant.tagOf tags iIdx))
                    |> Array.map (fun (p, _, _) -> p)
                if innerPolysFiltered.Length > 0 then
                    let outerR = Region.ofPolygons outerPolys
                    let innerR = Region.ofPolygons innerPolysFiltered
                    let outerCore = Size.shrink limit outerR
                    let violations = Boolean.subtract innerR outerCore
                    for slab in violations.Slabs do
                        for iv in slab.Intervals do
                            let m =
                                min (iv.X2 - iv.X1) slab.Height
                            result.Add {
                                Rule = name
                                LayerNumber = inner.Number
                                LayerType   = inner.DataType
                                LimitDbu    = limit
                                MeasuredDbu = m
                                BboxA = (iv.X1, slab.Y, iv.X2, slab.Y + slab.Height)
                                BboxB = None }
        | Rules.Endcap (name, source, reference, minUm) ->
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let sources = polysOnLayer idx source
                let refs    = polysOnLayer idx reference
                // For each source polygon that crosses (overlaps)
                // a reference polygon, measure how far the source
                // extends past the reference's bbox edges. The
                // extension axis is the one along which the
                // source is longer than the reference (gate-axis
                // for poly past diff; channel-axis for diff past
                // poly).
                for (_, sBb, _) in sources do
                    let (sx1, sy1, sx2, sy2) = sBb
                    let sW = sx2 - sx1
                    let sH = sy2 - sy1
                    for (_, rBb, _) in refs do
                        if bboxOverlaps sBb rBb then
                            let (rx1, ry1, rx2, ry2) = rBb
                            let rW = rx2 - rx1
                            let rH = ry2 - ry1
                            let extX = (rx1 - sx1) + (sx2 - rx2)
                            let extY = (ry1 - sy1) + (sy2 - ry2)
                            let useX = sW >= rW && extX > 0L
                            let useY = sH >= rH && extY > 0L
                            let axisX =
                                if useX && useY then extX >= extY
                                else useX
                            if axisX then
                                let extLeft  = rx1 - sx1
                                let extRight = sx2 - rx2
                                let m = min extLeft extRight
                                if m < limit then
                                    result.Add {
                                        Rule = name
                                        LayerNumber = source.Number
                                        LayerType   = source.DataType
                                        LimitDbu    = limit
                                        MeasuredDbu = max 0L m
                                        BboxA = sBb
                                        BboxB = Some rBb }
                            elif useY then
                                let extBottom = ry1 - sy1
                                let extTop    = sy2 - ry2
                                let m = min extBottom extTop
                                if m < limit then
                                    result.Add {
                                        Rule = name
                                        LayerNumber = source.Number
                                        LayerType   = source.DataType
                                        LimitDbu    = limit
                                        MeasuredDbu = max 0L m
                                        BboxA = sBb
                                        BboxB = Some rBb }
        | Rules.AsymEnclosure (name, outer, inner, oneDirUm, otherDirUm, cond) ->
            // Two thresholds, one per axis. The rule passes when
            // EITHER (xMargin ≥ one AND yMargin ≥ other) OR
            // (xMargin ≥ other AND yMargin ≥ one) — i.e. the
            // larger threshold is satisfied on one axis and the
            // smaller on the other.
            let oneLim   = umToDbu umPerDbu oneDirUm
            let otherLim = umToDbu umPerDbu otherDirUm
            if oneLim > 0L || otherLim > 0L then
                let outers = polysOnLayer idx outer
                let inners = polysOnLayer idx inner
                for (_, ibb, iIdx) in inners do
                    let iTags = Implant.tagOf tags iIdx
                    if condMatches cond iTags then
                        // Find the covering outer with the best
                        // (largest min-axis) per-axis margin.
                        let mutable bestPair :
                                (int64 * int64 *
                                 (int64 * int64 * int64 * int64)) voption =
                            ValueNone
                        for (_, obb, _) in outers do
                            if bboxOverlaps obb ibb then
                                let xM, yM = bboxContainsMarginAxis ibb obb
                                // Score = (min axis satisfied,
                                // max axis satisfied). We keep
                                // the pair that maximises the
                                // smaller-axis margin (i.e.
                                // closest to passing).
                                let score = min xM yM
                                match bestPair with
                                | ValueNone ->
                                    bestPair <- ValueSome (xM, yM, obb)
                                | ValueSome (bx, by, _) when score > (min bx by) ->
                                    bestPair <- ValueSome (xM, yM, obb)
                                | _ -> ()
                        match bestPair with
                        | ValueNone ->
                            result.Add {
                                Rule = name
                                LayerNumber = inner.Number
                                LayerType   = inner.DataType
                                LimitDbu    = max oneLim otherLim
                                MeasuredDbu = 0L
                                BboxA = ibb
                                BboxB = None }
                        | ValueSome (xM, yM, obb) ->
                            // Try both axis assignments. Pass if
                            // either works.
                            let assignA =
                                xM >= oneLim   && yM >= otherLim
                            let assignB =
                                xM >= otherLim && yM >= oneLim
                            if not (assignA || assignB) then
                                // Report the smaller actual margin
                                // as the measured value — the
                                // narrowest place the rule fails.
                                let measured = min xM yM
                                result.Add {
                                    Rule = name
                                    LayerNumber = inner.Number
                                    LayerType   = inner.DataType
                                    LimitDbu    = min oneLim otherLim
                                    MeasuredDbu = max 0L measured
                                    BboxA = ibb
                                    BboxB = Some obb }
        | Rules.BoundaryCrossing (name, source, destination, minUm) ->
            // For each source poly:
            //   * any destination FULLY contains it → skip
            //     (legal case, e.g. NSDM inside nwell = n-tap).
            //   * any destination PARTIALLY overlaps it (bbox
            //     overlap but not containment) → fire at gap=0
            //     (the source crosses the destination edge; the
            //     outside-part touches the edge).
            //   * all destinations fully separate (no overlap):
            //     fire when nearest-edge gap < minUm.
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let sources = polysOnLayer idx source
                let destinations = polysOnLayer idx destination
                for (_, sBb, _) in sources do
                    let (sx1, sy1, sx2, sy2) = sBb
                    let mutable fullyInside = false
                    let mutable crossingDest : (int64 * int64 * int64 * int64) option = None
                    let mutable minSepGap = System.Int64.MaxValue
                    let mutable nearestDest = sBb
                    for (_, dBb, _) in destinations do
                        let (dx1, dy1, dx2, dy2) = dBb
                        if sx1 >= dx1 && sy1 >= dy1 && sx2 <= dx2 && sy2 <= dy2 then
                            fullyInside <- true
                        elif bboxOverlaps sBb dBb then
                            crossingDest <- Some dBb
                        else
                            match bboxOrthoGap sBb dBb with
                            | Some g when g < minSepGap ->
                                minSepGap <- g
                                nearestDest <- dBb
                            | _ -> ()
                    if fullyInside then ()
                    elif crossingDest.IsSome then
                        result.Add {
                            Rule = name
                            LayerNumber = source.Number
                            LayerType   = source.DataType
                            LimitDbu    = limit
                            MeasuredDbu = 0L
                            BboxA = sBb
                            BboxB = crossingDest }
                    elif minSepGap > 0L && minSepGap < limit then
                        result.Add {
                            Rule = name
                            LayerNumber = source.Number
                            LayerType   = source.DataType
                            LimitDbu    = limit
                            MeasuredDbu = minSepGap
                            BboxA = sBb
                            BboxB = Some nearestDest }
        | Rules.ImplantOutsideWellSpacing (name, implant, active, well, minUm) ->
            // Build the actual diffusion-outside-well region
            // via polygon booleans, then check distance to
            // well. The full Region pipeline:
            //   nDiff       = implant ∩ active
            //   nDiffOut    = nDiff \ well
            //   if nDiffOut is empty → no possible violation
            //   wellGrown   = grow well by limit
            //   violations  = nDiffOut ∩ wellGrown
            // Where nDiffOut and wellGrown overlap, an n-diff
            // region is within `limit` of well — a violation.
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let implantPolys =
                    polysOnLayer idx implant
                    |> Array.map (fun (p, _, _) -> p)
                let activePolys =
                    polysOnLayer idx active
                    |> Array.map (fun (p, _, _) -> p)
                let wellPolys =
                    polysOnLayer idx well
                    |> Array.map (fun (p, _, _) -> p)
                if implantPolys.Length > 0
                   && activePolys.Length > 0
                   && wellPolys.Length > 0 then
                    let implantR = Region.ofPolygons implantPolys
                    let activeR  = Region.ofPolygons activePolys
                    let wellR    = Region.ofPolygons wellPolys
                    let nDiff = Boolean.intersect implantR activeR
                    let nDiffOut = Boolean.subtract nDiff wellR
                    if not (Region.isEmpty nDiffOut) then
                        let wellGrown = Size.grow limit wellR
                        let violations = Boolean.intersect nDiffOut wellGrown
                        for slab in violations.Slabs do
                            for iv in slab.Intervals do
                                let m = min (iv.X2 - iv.X1) slab.Height
                                result.Add {
                                    Rule = name
                                    LayerNumber = active.Number
                                    LayerType   = active.DataType
                                    LimitDbu    = limit
                                    MeasuredDbu = m
                                    BboxA = (iv.X1, slab.Y, iv.X2, slab.Y + slab.Height)
                                    BboxB = None }
        | Rules.MinArea (name, layer, minUm2) ->
            // Magic-fidelity min-area: build a Region from the
            // input polygons, find connected components, check
            // each component's ACTUAL area (sum of tile areas,
            // not bbox area — an L-shape's bbox overstates its
            // true area).
            //
            // Per-rectangle min-area would wrongly fire on
            // narrow fragments of a larger connected feature
            // (e.g. a 200×100 strip made of two 100×100 abutting
            // rects would fail twice at threshold > 10000 even
            // though the actual connected feature has area
            // 20000). Components fix that.
            //
            // Limit converted to DBU² up front; comparison is
            // integer to avoid round-off near the threshold.
            let scale = umPerDbu * umPerDbu
            if scale > 0.0 then
                let limit = max 0L (int64 (System.Math.Round (minUm2 / scale)))
                if limit > 0L then
                    let polys =
                        polysOnLayer idx layer
                        |> Array.map (fun (p, _, _) -> p)
                    let r = Region.ofPolygons polys
                    for ((x1, y1, x2, y2), area) in
                            Components.componentBboxesAndAreas r do
                        if area < limit then
                            result.Add {
                                Rule = name
                                LayerNumber = layer.Number
                                LayerType   = layer.DataType
                                LimitDbu    = limit
                                MeasuredDbu = area
                                BboxA = (x1, y1, x2, y2)
                                BboxB = None }

    for rule in view.Rules do
        checkRule rule

    // Post-pass: drop COREID-waived violations. The waiver test
    // unions BboxA and BboxB (when present) into a single test
    // bbox — a spacing violation is waived only if BOTH polygons
    // fall inside a COREID area, since spacing across a COREID
    // boundary is a real bug.
    let unionBbox
            ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
            (b: (int64 * int64 * int64 * int64) option)
            : int64 * int64 * int64 * int64 =
        match b with
        | None -> (ax1, ay1, ax2, ay2)
        | Some (bx1, by1, bx2, by2) ->
            (min ax1 bx1, min ay1 by1, max ax2 bx2, max ay2 by2)
    // For the foundry-waiver check, we need to identify which
    // input polygons CONTRIBUTE to each violation. Polys whose
    // bbox overlaps the violation bbox are candidates. The
    // dual-test waiver then asks whether ALL such contributors
    // are sourced from foundry cells — distinguishing genuine
    // foundry-internal DRC from a user-cell polygon that
    // happens to fall inside a foundry footprint by coincidence.
    let contributingPolys (v: Violation) =
        let (vx1, vy1, vx2, vy2) = unionBbox v.BboxA v.BboxB
        flat
        |> Array.filter (fun p ->
            // Same-layer filter: only polys on the rule's
            // layer can actually contribute. Without this,
            // any nearby user poly (on any layer) blocks the
            // foundry waiver even when the real contributors
            // are foundry-internal.
            if p.Layer <> v.LayerNumber || p.DataType <> v.LayerType then false
            else
                let mutable xMin = System.Int64.MaxValue
                let mutable yMin = System.Int64.MaxValue
                let mutable xMax = System.Int64.MinValue
                let mutable yMax = System.Int64.MinValue
                for pt in p.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
                xMin <= vx2 && vx1 <= xMax && yMin <= vy2 && vy1 <= yMax)
    let waivedViolations =
        result
        |> Seq.filter (fun v ->
            let bb = unionBbox v.BboxA v.BboxB
            // Drop if EITHER waiver applies:
            //   * COREID + rule in waiver list (SRAM bitcell-
            //     class relaxations).
            //   * Violation fully inside any foundry-primitive
            //     cell footprint AND every contributing polygon
            //     (same layer, bbox-overlapping) comes from a
            //     foundry cell — Magic per-cell scope.
            not (Waiver.isWaived coreAreas v.Rule bb
                 || Waiver.isFoundryWaived foundryFootprints v.Rule bb (contributingPolys v)))
        |> Array.ofSeq

    // Final post-pass: cluster per-rule adjacent violations
    // into single per-region violations. Per-pair rules (Spacing,
    // CrossSpacing) emit one violation per polygon-pair; Magic
    // emits one violation per connected gap region. Group by
    // (Rule, Layer, DataType), build a Region from the
    // violation bboxes (each as a rectangle), find connected
    // components, emit one consolidated violation per component
    // with the worst (smallest) MeasuredDbu in the cluster.
    //
    // Rules that already emit per-component (MinArea) cluster
    // trivially — each input component IS already a singleton
    // cluster, no change.
    let mkPolyForBbox
            (layer: int) (dt: int)
            ((x1, y1, x2, y2): int64 * int64 * int64 * int64)
            (idx: int)
            : FlatPolygon =
        let pts : Rekolektion.Viz.Core.Rkt.Types.Point array =
            [| { X = x1; Y = y1 }
               { X = x2; Y = y1 }
               { X = x2; Y = y2 }
               { X = x1; Y = y2 } |]
        { Layer = layer; DataType = dt
          Points = pts
          SourceStructure = "drc-cluster"
          SourceIndex = idx
          TopInstanceIndex = None }
    waivedViolations
    |> Array.groupBy (fun v -> v.Rule, v.LayerNumber, v.LayerType)
    |> Array.collect (fun ((ruleName, ln, lt), vs) ->
        if vs.Length <= 1 then vs
        else
            // Cluster via connected components. Spacing/Cross-
            // Spacing use gap-region bboxes (small strips
            // between facing polygons); MinArea uses already-
            // clustered component bboxes; Width/Enclosure use
            // per-polygon bboxes. Adjacent or overlapping
            // bboxes merge into one cluster regardless of which
            // source rule produced them.
            //
            // One reported violation per spatial cluster. Magic
            // emits per-tile within each cluster (so Magic's
            // count is higher than viz's per-cluster count), but
            // for the canvas overlay one marker per cluster is
            // a better UX than N markers in one spot.
            let bboxes =
                vs |> Array.map (fun v -> unionBbox v.BboxA v.BboxB)
            let polys =
                bboxes
                |> Array.mapi (fun i bb -> mkPolyForBbox ln lt bb i)
            let r = Region.ofPolygons polys
            let clusters = Components.componentBboxes r
            let measuredPerCluster = Array.create clusters.Length System.Int64.MaxValue
            let limitPerCluster = Array.create clusters.Length 0L
            let clusterIdOf ((vx1, vy1, vx2, vy2): int64 * int64 * int64 * int64) =
                clusters
                |> Array.tryFindIndex (fun (cx1, cy1, cx2, cy2) ->
                    vx1 >= cx1 && vy1 >= cy1 && vx2 <= cx2 && vy2 <= cy2)
            for i in 0 .. vs.Length - 1 do
                let v = vs.[i]
                match clusterIdOf bboxes.[i] with
                | Some ci ->
                    if v.MeasuredDbu < measuredPerCluster.[ci] then
                        measuredPerCluster.[ci] <- v.MeasuredDbu
                    limitPerCluster.[ci] <- v.LimitDbu
                | None -> ()
            clusters
            |> Array.mapi (fun ci (cx1, cy1, cx2, cy2) ->
                { Rule = ruleName
                  LayerNumber = ln
                  LayerType   = lt
                  LimitDbu    = limitPerCluster.[ci]
                  MeasuredDbu = measuredPerCluster.[ci]
                  BboxA = (cx1, cy1, cx2, cy2)
                  BboxB = None }))

/// Backward-compatible entry point: same signature as before, no
/// toggles, no implant tags. Computes implant tags internally.
/// Tests + callers without a tag pipeline call this; the canvas
/// uses `checkWithToggles` directly so the tag computation is
/// shared with other consumers.
let check
        (view: Rules.RulesetView)
        (units: Units)
        (flat: FlatPolygon array)
        : Violation array =
    let tags = Implant.tagAll flat
    checkWithToggles view units flat tags Set.empty

/// ADR-0003 — precompute the cross-net overlap violations within
/// the cell itself (no draft involved). O(N²) over `cellFlat` so
/// callers should compute this only on cell-geometry changes
/// (cached at the canvas level), not on every mouse move.
let cellCrossNetOverlaps
        (cellFlat: FlatPolygon array)
        (nets: Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>)
        : Violation array =
    let polyToNet =
        let acc = System.Collections.Generic.Dictionary<string * int, string>()
        for kv in nets do
            for pr in kv.Value.Polygons do
                acc.[(pr.Structure, pr.Index)] <- kv.Key
        acc
    let netOf (p: FlatPolygon) : string option =
        match polyToNet.TryGetValue((p.SourceStructure, p.SourceIndex)) with
        | true, n -> Some n
        | _ -> None
    let layerName (num: int) (dt: int) : string =
        match Rekolektion.Viz.Core.Layout.Layer.bySky130Number num dt with
        | Some l -> l.Name
        | None   -> sprintf "layer%d_%d" num dt
    let mkOverlap (a: FlatPolygon) (b: FlatPolygon) : Violation option =
        let (ax1, ay1, ax2, ay2) = bboxOf a
        let (bx1, by1, bx2, by2) = bboxOf b
        let ix1 = max ax1 bx1
        let iy1 = max ay1 by1
        let ix2 = min ax2 bx2
        let iy2 = min ay2 by2
        if ix1 < ix2 && iy1 < iy2 then
            Some {
                Rule        =
                    sprintf "%s.overlap" (layerName a.Layer a.DataType)
                LayerNumber = a.Layer
                LayerType   = a.DataType
                LimitDbu    = 0L
                MeasuredDbu = (ix2 - ix1) * (iy2 - iy1)
                BboxA       = (ix1, iy1, ix2, iy2)
                BboxB       = None
            }
        else None
    let acc = System.Collections.Generic.List<Violation>()
    // Flag an overlap whenever the two polys could be on different
    // electrical nets. Conditions:
    //   • named A + named B, names differ → real short
    //   • named + unclaimed → unclaimed isn't proven same-net; flag
    //     conservatively. Without this, a wire on a labeled net that
    //     overlaps a top-cell li1 rect with no label sits in DRC's
    //     blind spot (Spacing rule treats overlap as merged; this
    //     post-pass used to require both nets known). User-reported
    //     gap: wire on drn_R overlapped an unlabeled foreign li1
    //     strip, DRC said nothing.
    //   • unclaimed + unclaimed → can't tell; skip (avoid spam on
    //     legitimately-merged unlabeled geometry like power rails).
    for i in 0 .. cellFlat.Length - 1 do
        let a = cellFlat.[i]
        for j in i + 1 .. cellFlat.Length - 1 do
            let b = cellFlat.[j]
            if a.Layer = b.Layer && a.DataType = b.DataType then
                let flag =
                    match netOf a, netOf b with
                    | Some na, Some nb -> na <> nb
                    | Some _, None
                    | None, Some _    -> true
                    | None, None      -> false
                if flag then
                    match mkOverlap a b with
                    | Some v -> acc.Add v
                    | None -> ()
    acc.ToArray()

/// ADR-0003 live DRC entry point. Runs only `Rules.liveRules`
/// (clearance, width, enclosure, endcap — locally decidable rules)
/// against the union of `cellFlat` and `draftFlat`. Commit-only
/// rules (MinArea, BoundaryCrossing, ImplantOutsideWellSpacing)
/// are deferred to the full `check` pass at RouteFinish time.
///
/// The non-live rules are folded into `disabledRules` so the
/// existing engine simply skips them; that keeps a single rule-
/// evaluation code path and avoids drift between live and full.
///
/// `disabledRules` from the caller (e.g., user-silenced rule
/// names) is honored on top — both layers compose by union.
///
/// **Cross-net overlap post-pass.** The standard Spacing rule
/// treats touching/overlapping same-layer polys as one merged net
/// and skips the gap check — correct when the overlap IS intended
/// (the wires really do connect on one net). When the overlap is
/// between polys on DIFFERENT named nets, that's a short, and
/// this pass emits a synthetic `<layer>.overlap` violation for it.
/// Draft segments have no net yet, so any overlap of the draft
/// against a labeled cell poly is flagged conservatively.
///
/// `nets` is the document's net-membership map (typically derived
/// once per cell by `Net.LabelFlood.derive`). Empty map disables
/// the cross-net pass entirely — unlabeled designs skip it.
/// `runLive` variant that consumes a pre-built spatial index over
/// `cellFlat`. The canvas caches the index across mouse moves so the
/// per-frame region query is O(local-density) instead of O(cell-size).
/// Index indices must be aligned 1:1 with `cellFlat` positions —
/// `UniformGrid.build` over each polygon's bbox does that naturally.
/// Phase-level timing of a single `runLiveWithIndex` call. Captured
/// by the caller so the log can show where any slow recomputes are
/// burning ms. All timings are wall-clock milliseconds for that
/// phase only — they sum to roughly the function's total time.
type LivePhaseTimings = {
    mutable RegionFilterMs : int64
    mutable TagAllMs       : int64
    mutable StandardMs     : int64
    mutable NetIndexMs     : int64
    mutable OverlapMs      : int64
    mutable RegionFilterCount : int
    mutable CombinedCount     : int
}

let newPhaseTimings () : LivePhaseTimings = {
    RegionFilterMs = 0L; TagAllMs = 0L; StandardMs = 0L
    NetIndexMs = 0L; OverlapMs = 0L
    RegionFilterCount = 0; CombinedCount = 0
}

let runLiveWithIndexTimed
        (view: Rules.RulesetView)
        (units: Units)
        (cellFlat: FlatPolygon array)
        (cellIndex: Rekolektion.Viz.Core.Spatial.UniformGrid.Index)
        (draftFlat: FlatPolygon array)
        (nets: Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>)
        (draftStartNet: string option)
        (disabledRules: Set<string>)
        (timings: LivePhaseTimings)
        : Violation array =
    let phaseSw = System.Diagnostics.Stopwatch()
    // Region-filter the cell to a bbox of (draft + margin) before
    // running the standard rule pass. Keeps per-frame cost bounded
    // by the route's neighborhood, not the whole macro. 5 µm margin
    // is comfortably wider than any single-layer SKY130 spacing rule.
    //
    // Bug history: the previous formula
    //   `5000.0 * (1.0 / umPerDbuOf units)`
    // computed 5 µm × (1000 DBU/µm) = 5,000,000 DBU = **5 mm**, an
    // off-by-1000 that expanded the bbox to cover the entire macro
    // and effectively disabled the region filter. The correct
    // formula is just `5.0 µm / (µm/DBU)` = 5000 DBU on a 1nm grid.
    phaseSw.Restart()
    let regionMarginDbu = int64 (5.0 / umPerDbuOf units)
    let regionFiltered =
        if draftFlat.Length = 0 then cellFlat
        else
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for d in draftFlat do
                let (ax1, ay1, ax2, ay2) = bboxOf d
                xMin <- min xMin ax1
                yMin <- min yMin ay1
                xMax <- max xMax ax2
                yMax <- max yMax ay2
            let rx1 = xMin - regionMarginDbu
            let ry1 = yMin - regionMarginDbu
            let rx2 = xMax + regionMarginDbu
            let ry2 = yMax + regionMarginDbu
            // Spatial-index lookup: returns only poly indices whose
            // bbox cells overlap the query window. Replaces the
            // previous O(N) Array.filter scan over every cell poly.
            let hits =
                Rekolektion.Viz.Core.Spatial.UniformGrid.queryBboxArray
                    cellIndex (rx1, ry1, rx2, ry2)
            hits |> Array.map (fun i -> cellFlat.[i])
    let combined = Array.append regionFiltered draftFlat
    phaseSw.Stop()
    timings.RegionFilterMs <- phaseSw.ElapsedMilliseconds
    timings.RegionFilterCount <- regionFiltered.Length
    timings.CombinedCount <- combined.Length

    phaseSw.Restart()
    let tags = Implant.tagAll combined
    phaseSw.Stop()
    timings.TagAllMs <- phaseSw.ElapsedMilliseconds

    phaseSw.Restart()
    let nonLiveDisabled =
        view.Rules
        |> List.filter (Rules.isLiveEligible >> not)
        |> List.map Rules.nameOf
        |> Set.ofList
    let disabled' = Set.union disabledRules nonLiveDisabled
    let standardViolations = checkWithToggles view units combined tags disabled'
    phaseSw.Stop()
    timings.StandardMs <- phaseSw.ElapsedMilliseconds

    // (Structure, Index) → net name. A polygon not in the map
    // belongs to no labeled net (or to a draft) and is treated as
    // `None` in the cross-net check.
    phaseSw.Restart()
    let polyToNet =
        let acc = System.Collections.Generic.Dictionary<string * int, string>()
        for kv in nets do
            for pr in kv.Value.Polygons do
                acc.[(pr.Structure, pr.Index)] <- kv.Key
        acc
    let netOf (p: FlatPolygon) : string option =
        if p.SourceStructure = "<draft-route>" then
            // Draft segments belong to the route's StartNet (the
            // pin the user clicked to begin the route). Without
            // this, the live-DRC cross-net check flags the draft
            // overlapping its own start polygon as a short.
            draftStartNet
        else
            match polyToNet.TryGetValue((p.SourceStructure, p.SourceIndex)) with
            | true, n -> Some n
            | _ -> None

    let layerName (num: int) (dt: int) : string =
        match Rekolektion.Viz.Core.Layout.Layer.bySky130Number num dt with
        | Some l -> l.Name
        | None   -> sprintf "layer%d_%d" num dt
    let mkOverlap (a: FlatPolygon) (b: FlatPolygon) : Violation option =
        let (ax1, ay1, ax2, ay2) = bboxOf a
        let (bx1, by1, bx2, by2) = bboxOf b
        let ix1 = max ax1 bx1
        let iy1 = max ay1 by1
        let ix2 = min ax2 bx2
        let iy2 = min ay2 by2
        if ix1 < ix2 && iy1 < iy2 then
            Some {
                Rule        =
                    sprintf "%s.overlap" (layerName a.Layer a.DataType)
                LayerNumber = a.Layer
                LayerType   = a.DataType
                LimitDbu    = 0L
                MeasuredDbu = (ix2 - ix1) * (iy2 - iy1)
                BboxA       = (ix1, iy1, ix2, iy2)
                BboxB       = None
            }
        else None
    let isDraft (p: FlatPolygon) = p.SourceStructure = "<draft-route>"
    let isCrossNet (a: FlatPolygon) (b: FlatPolygon) : bool =
        let na = netOf a
        let nb = netOf b
        match na, nb with
        | Some x, Some y -> x <> y
        | _ ->
            // One side is unlabeled. If either side is a draft and
            // we know its StartNet, na/nb hit the Some branch above
            // — so falling here means the cell poly is unlabeled.
            // Flag draft↔unlabeled overlaps as before (we can't
            // prove they're safe); skip cell↔unlabeled overlaps
            // (existing behaviour — labeled-net-only DRC).
            isDraft a || isDraft b

    phaseSw.Stop()
    timings.NetIndexMs <- phaseSw.ElapsedMilliseconds

    // Draft vs cell only — cell-vs-cell overlap is O(N²) and lives
    // in `cellCrossNetOverlaps` (callers cache its result). Iterate
    // over the region-filtered cell so we only touch nearby polys.
    phaseSw.Restart()
    let overlapViolations = System.Collections.Generic.List<Violation>()
    for d in draftFlat do
        for c in regionFiltered do
            if c.Layer = d.Layer && c.DataType = d.DataType
               && isCrossNet d c then
                match mkOverlap d c with
                | Some v -> overlapViolations.Add v
                | None -> ()
    phaseSw.Stop()
    timings.OverlapMs <- phaseSw.ElapsedMilliseconds
    Array.append standardViolations (overlapViolations.ToArray())

/// Untimed wrapper preserving the original signature so callers
/// that don't want phase breakdown stay simple. Production canvas
/// uses the `Timed` variant + log.
let runLiveWithIndex
        (view: Rules.RulesetView)
        (units: Units)
        (cellFlat: FlatPolygon array)
        (cellIndex: Rekolektion.Viz.Core.Spatial.UniformGrid.Index)
        (draftFlat: FlatPolygon array)
        (nets: Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>)
        (draftStartNet: string option)
        (disabledRules: Set<string>)
        : Violation array =
    runLiveWithIndexTimed
        view units cellFlat cellIndex draftFlat nets draftStartNet
        disabledRules (newPhaseTimings ())

/// `runLive` convenience: builds the spatial index inline from
/// `cellFlat` and delegates to `runLiveWithIndex`. Used by tests
/// and any caller that doesn't have an external cache. Interactive
/// callers (the canvas) should hold a cached index across mouse
/// moves and call `runLiveWithIndex` directly — the index build is
/// O(cell-size) so doing it per-frame defeats the point.
let runLive
        (view: Rules.RulesetView)
        (units: Units)
        (cellFlat: FlatPolygon array)
        (draftFlat: FlatPolygon array)
        (nets: Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>)
        (draftStartNet: string option)
        (disabledRules: Set<string>)
        : Violation array =
    let bboxes = cellFlat |> Array.map bboxOf
    let cellSize = Rekolektion.Viz.Core.Spatial.UniformGrid.suggestCellSize bboxes
    let cellIndex =
        Rekolektion.Viz.Core.Spatial.UniformGrid.build cellSize bboxes
    runLiveWithIndex
        view units cellFlat cellIndex draftFlat nets draftStartNet disabledRules

/// Compute how far the selection (a set of instance polygons in
/// world coords) can move along `dirX, dirY` (one of {(+1,0),
/// (-1,0), (0,+1), (0,-1)}) before its physical bbox collides
/// with non-selected geometry at the worst-case DRC rule limit.
///
/// Uses cell-bbox-to-cell-bbox distance (not pairwise polygon
/// matching) so the calculation is robust to "approximate" cell
/// placement: even when no polygons share an axis projection,
/// Tighten can collapse the gap. The chosen rule limit is the
/// maximum min-spacing across every shared layer between the
/// two cells — a conservative bound that won't violate any
/// per-layer rule.
///
/// Returns the safe Δ in DBU, or None if the selected bbox is
/// not on `(dirX, dirY)`-side of the other bbox (e.g. asking
/// for +X tighten when nothing is to the selected's right).
let maxOrthoSlackDbu
        (units: Units)
        (selectedPolys: FlatPolygon array)
        (otherPolys:    FlatPolygon array)
        (dirX: int)
        (dirY: int)
        : int64 option =
    let umPerDbu = umPerDbuOf units
    let physical (p: FlatPolygon) =
        not (Rekolektion.Viz.Core.Layout.Layer.isNonPhysical p.Layer p.DataType)
    let selPhys = selectedPolys |> Array.filter physical
    let othPhys = otherPolys    |> Array.filter physical
    if selPhys.Length = 0 || othPhys.Length = 0 then None
    else
        // Per-poly bbox keyed by (layer, datatype). Each polygon
        // is its own bbox so a met1 wire poking past the diff
        // doesn't conflate with the diff edge.
        let bboxOf (p: FlatPolygon) =
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for pt in p.Points do
                if pt.X < xMin then xMin <- pt.X
                if pt.X > xMax then xMax <- pt.X
                if pt.Y < yMin then yMin <- pt.Y
                if pt.Y > yMax then yMax <- pt.Y
            xMin, yMin, xMax, yMax
        let groupBy (polys: FlatPolygon array) =
            polys
            |> Array.map (fun p -> (p.Layer, p.DataType), bboxOf p)
            |> Array.groupBy fst
            |> Array.map (fun (k, arr) -> k, arr |> Array.map snd)
            |> Map.ofArray
        let selByLayer = groupBy selPhys
        let othByLayer = groupBy othPhys

        // Closest facing-edge gap between two bbox arrays in the
        // requested direction. Returns None when no pair has a
        // clean facing edge (overlapping pairs return None — the
        // intent is to bound the move so it doesn't drive sel
        // closer to oth, not to react to overlap shapes which
        // can be either DRC violations or intentional
        // connections).
        let closestGap
                (selBbs: (int64*int64*int64*int64) array)
                (othBbs: (int64*int64*int64*int64) array) =
            let mutable bestGap : int64 option = None
            for sBb in selBbs do
                let (sx1, sy1, sx2, sy2) = sBb
                for oBb in othBbs do
                    let (ox1, oy1, ox2, oy2) = oBb
                    let yOverlap = (min sy2 oy2) > (max sy1 oy1)
                    let xOverlap = (min sx2 ox2) > (max sx1 ox1)
                    let g =
                        if dirX = 1 && yOverlap && ox1 >= sx2 then Some (ox1 - sx2)
                        elif dirX = -1 && yOverlap && ox2 <= sx1 then Some (sx1 - ox2)
                        elif dirY = 1 && xOverlap && oy1 >= sy2 then Some (oy1 - sy2)
                        elif dirY = -1 && xOverlap && oy2 <= sy1 then Some (sy1 - oy2)
                        else None
                    match g with
                    | Some gv ->
                        match bestGap with
                        | None -> bestGap <- Some gv
                        | Some cur when gv < cur -> bestGap <- Some gv
                        | _ -> ()
                    | None -> ()
            bestGap

        // Same-layer Spacing constraints.
        let sameLayerSlack =
            selByLayer
            |> Map.toSeq
            |> Seq.choose (fun (key, selBbs) ->
                match Rules.tryFind (fst key) (snd key) with
                | None -> None
                | Some rule ->
                    match Map.tryFind key othByLayer with
                    | None -> None
                    | Some othBbs ->
                        let limit = umToDbu umPerDbu rule.MinSpacingUm
                        closestGap selBbs othBbs
                        |> Option.map (fun gv -> rule.Layer, gv, limit, gv - limit))
            |> Seq.toList

        // Cross-layer Spacing constraints. Mirrors the tighten-
        // candidate path: a cross-spacing pair already at gap <
        // limit forbids the direction (returns None) rather than
        // just dropping the candidate — moving toward an
        // existing DRC violation makes it worse. Same-layer
        // ambiguity (connection vs violation) doesn't apply at
        // cross-layer scope.
        let mutable crossForbidden = false
        let crossLayerSlack =
            Rules.allCrossSpacings
            |> List.choose (fun cs ->
                let keyA = cs.LayerA.Number, cs.LayerA.DataType
                let keyB = cs.LayerB.Number, cs.LayerB.DataType
                let limit = umToDbu umPerDbu cs.MinUm
                let pairs =
                    [ Map.tryFind keyA selByLayer,
                      Map.tryFind keyB othByLayer
                      Map.tryFind keyB selByLayer,
                      Map.tryFind keyA othByLayer ]
                let best =
                    pairs
                    |> List.choose (fun (s, o) ->
                        match s, o with
                        | Some sBbs, Some oBbs -> closestGap sBbs oBbs
                        | _ -> None)
                    |> List.fold (fun acc g ->
                        match acc with
                        | None -> Some g
                        | Some cur when g < cur -> Some g
                        | _ -> acc) None
                match best with
                | Some gv when gv < limit ->
                    crossForbidden <- true
                    None
                | Some gv -> Some ("xspace", gv, limit, gv - limit)
                | None -> None)

        if crossForbidden then None
        else
            let layerSlack = sameLayerSlack @ crossLayerSlack
            match layerSlack with
            | [] -> None
            | _ ->
                let minSlack =
                    layerSlack |> List.map (fun (_, _, _, s) -> s) |> List.min
                if minSlack > 0L then Some minSlack else None

/// One Tighten-mode candidate: the binding (most-constrained)
/// polygon-pair on a single cardinal direction. Renderer uses
/// `SelBb` and `OthBb` to draw an orthogonal arrow between the
/// facing edges; on commit, the move is `(dx, dy) * SlackDbu`.
type TightenCandidate = {
    /// MOVE direction in DBU axes. For a TIGHTEN candidate this
    /// matches the concern side (1, 0 = move right toward right
    /// neighbour). For a LOOSEN candidate it's the OPPOSITE of
    /// the concern side (e.g. concern = left neighbour
    /// violating, move = right to back off).
    DirX        : int   // -1, 0, 1
    DirY        : int
    /// Stable 1-based slot tied to CONCERN SIDE (not move
    /// direction). Mapping by concern: 1 = right side, 2 = left
    /// side, 3 = down side, 4 = up side. Slot remains stable
    /// across tighten/loosen so the user can memorize "2 = left
    /// side concern" — clicking slot 2 either tightens leftward
    /// (when the left has clearance) or moves rightward to fix
    /// an existing left-side violation.
    Slot        : int
    LayerName   : string
    LimitDbu    : int64
    GapDbu      : int64
    /// Magnitude of the move (always positive). Apply to the
    /// canvas via (DirX * SlackDbu, DirY * SlackDbu).
    SlackDbu    : int64
    /// True when the candidate moves AWAY from the concern side
    /// to fix a pre-existing DRC violation, false for the normal
    /// "snuggle up to the neighbour" tighten.
    IsLoosen    : bool
    SelBb       : int64 * int64 * int64 * int64
    OthBb       : int64 * int64 * int64 * int64
}

/// Map a cardinal (DirX, DirY) to its stable slot number.
/// (1, 0) -> 1, (-1, 0) -> 2, (0, -1) -> 3, (0, 1) -> 4.
let slotOfDir (dirX: int) (dirY: int) : int =
    if   dirX = 1  && dirY = 0  then 1
    elif dirX = -1 && dirY = 0  then 2
    elif dirX = 0  && dirY = -1 then 3
    elif dirX = 0  && dirY = 1  then 4
    else 0  // unreachable for valid candidates

/// Compute the binding-pair Tighten candidate for each cardinal
/// direction. Returns at most 4 entries (one per side that has
/// any same-layer orthogonally-facing neighbor with positive
/// slack). Each candidate's `SlackDbu = GapDbu - LimitDbu`; the
/// candidate with the smallest SlackDbu is the most binding.
///
/// `selectedPolys` and `otherPolys` are world-DBU flat polys;
/// the caller has already filtered selection vs. neighbor.
let tightenCandidates
        (units: Units)
        (selectedPolys: FlatPolygon array)
        (otherPolys:    FlatPolygon array)
        : TightenCandidate array =
    let umPerDbu = umPerDbuOf units
    let physical (p: FlatPolygon) =
        not (Rekolektion.Viz.Core.Layout.Layer.isNonPhysical p.Layer p.DataType)
    let selPhys = selectedPolys |> Array.filter physical
    let othPhys = otherPolys    |> Array.filter physical
    if selPhys.Length = 0 || othPhys.Length = 0 then [||]
    else
        let bboxOf (p: FlatPolygon) =
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for pt in p.Points do
                if pt.X < xMin then xMin <- pt.X
                if pt.X > xMax then xMax <- pt.X
                if pt.Y < yMin then yMin <- pt.Y
                if pt.Y > yMax then yMax <- pt.Y
            xMin, yMin, xMax, yMax
        let groupBy (polys: FlatPolygon array) =
            polys
            |> Array.map (fun p -> (p.Layer, p.DataType), bboxOf p)
            |> Array.groupBy fst
            |> Array.map (fun (k, arr) -> k, arr |> Array.map snd)
            |> Map.ofArray
        let selByLayer = groupBy selPhys
        let othByLayer = groupBy othPhys

        // Sel-edge-to-oth-edge distance for a pair on the side
        // (sx, sy). Includes bbox-overlap pairs (returns a
        // negative number = overlap depth), since loosen needs
        // to see them. "On this side" = oth has *any* extent
        // past sel's bbox in the (sx, sy) direction, with
        // perpendicular projection overlap. Tighten filters out
        // the negative-gap pairs via the s > 0L test; loosen
        // uses them to size the corrective move.
        let facingGap (sx, sy) (sBb: int64*int64*int64*int64) (oBb: int64*int64*int64*int64) =
            let (sx1, sy1, sx2, sy2) = sBb
            let (ox1, oy1, ox2, oy2) = oBb
            let yOver = (min sy2 oy2) > (max sy1 oy1)
            let xOver = (min sx2 ox2) > (max sx1 ox1)
            if sx = 1 && yOver && ox2 > sx2 then Some (ox1 - sx2)
            elif sx = -1 && yOver && ox1 < sx1 then Some (sx1 - ox2)
            elif sy = 1 && xOver && oy2 > sy2 then Some (oy1 - sy2)
            elif sy = -1 && xOver && oy1 < sy1 then Some (sy1 - oy2)
            else None

        // Collect every (selBb, othBb, limit, gap, layerName)
        // pair on the requested side from BOTH same-layer
        // Spacing rules and cross-layer CrossSpacing rules.
        let pairsOnSide (sx, sy) =
            let acc = ResizeArray<_>()
            // Same-layer Spacing.
            for KeyValue (key, selBbs) in selByLayer do
                match Rules.tryFind (fst key) (snd key) with
                | None -> ()
                | Some rule ->
                    match Map.tryFind key othByLayer with
                    | None -> ()
                    | Some othBbs ->
                        let limit = umToDbu umPerDbu rule.MinSpacingUm
                        if limit > 0L then
                            for sBb in selBbs do
                                for oBb in othBbs do
                                    match facingGap (sx, sy) sBb oBb with
                                    | Some g -> acc.Add (sBb, oBb, limit, g, rule.Layer)
                                    | None -> ()
            // Cross-layer Spacing (both sel-on-A/oth-on-B and
            // sel-on-B/oth-on-A orderings).
            for cs in Rules.allCrossSpacings do
                let keyA = cs.LayerA.Number, cs.LayerA.DataType
                let keyB = cs.LayerB.Number, cs.LayerB.DataType
                let limit = umToDbu umPerDbu cs.MinUm
                let layerName = sprintf "%dx%d" cs.LayerA.Number cs.LayerB.Number
                if limit > 0L then
                    let pump selBbs othBbs =
                        for sBb in selBbs do
                            for oBb in othBbs do
                                match facingGap (sx, sy) sBb oBb with
                                | Some g -> acc.Add (sBb, oBb, limit, g, layerName)
                                | None -> ()
                    match Map.tryFind keyA selByLayer, Map.tryFind keyB othByLayer with
                    | Some s, Some o -> pump s o
                    | _ -> ()
                    match Map.tryFind keyB selByLayer, Map.tryFind keyA othByLayer with
                    | Some s, Some o -> pump s o
                    | _ -> ()
            acc.ToArray()

        // Per CONCERN SIDE: decide tighten vs loosen.
        //   Loosen if any pair on this side has gap < limit
        //     (pre-existing DRC violation we should back off
        //     from). Move = AWAY from the concern side. Move
        //     amount = max(limit - gap) over the violating
        //     pairs (just enough to land the worst violator at
        //     exactly limit), capped by the opposite-side min
        //     positive slack so the back-off doesn't create a
        //     new violation on the other side.
        //   Tighten if no violation on this side. Move = TOWARD
        //     the concern side. Amount = smallest positive
        //     slack (standard "snuggle up" semantics).
        // Slot number reflects the CONCERN SIDE (stable across
        // tighten/loosen so the user's muscle memory holds).
        let sides = [ (1, 0); (-1, 0); (0, 1); (0, -1) ]
        sides
        |> List.choose (fun (cx, cy) ->
            let here = pairsOnSide (cx, cy)
            let opposite = pairsOnSide (-cx, -cy)

            // Loosen need: max (limit - gap) over violating pairs.
            let mutable loosenNeed = 0L
            let mutable loosenPair = None
            for (sBb, oBb, limit, gap, layerName) in here do
                if gap < limit then
                    let need = limit - gap
                    if need > loosenNeed then
                        loosenNeed <- need
                        loosenPair <- Some (sBb, oBb, limit, gap, layerName)

            // Tighten slack: smallest positive (gap - limit) on this side.
            let mutable tightenSlack : int64 option = None
            let mutable tightenPair = None
            for (sBb, oBb, limit, gap, layerName) in here do
                let s = gap - limit
                if s > 0L then
                    match tightenSlack with
                    | None ->
                        tightenSlack <- Some s
                        tightenPair <- Some (sBb, oBb, limit, gap, layerName)
                    | Some cur when s < cur ->
                        tightenSlack <- Some s
                        tightenPair <- Some (sBb, oBb, limit, gap, layerName)
                    | _ -> ()

            // No opposite-side cap. Earlier iteration capped the
            // loosen move at the far-side slack to avoid
            // CREATING a new violation, but the user's mental
            // model is "one click should fix the worst violation
            // on this side completely" — capping silently
            // shrinks the move and produces the multi-click
            // incremental behaviour you don't want. Any new
            // violation on the opposite side just shows up as a
            // fresh loosen candidate the next time T is pressed.

            if loosenNeed > 0L then
                let (sBb, oBb, limit, gap, layerName) = loosenPair.Value
                Some { DirX = -cx
                       DirY = -cy
                       Slot = slotOfDir cx cy
                       LayerName = layerName
                       LimitDbu = limit
                       GapDbu = gap
                       SlackDbu = loosenNeed
                       IsLoosen = true
                       SelBb = sBb
                       OthBb = oBb }
            else
                match tightenSlack with
                | Some slack ->
                    let (sBb, oBb, limit, gap, layerName) = tightenPair.Value
                    Some { DirX = cx
                           DirY = cy
                           Slot = slotOfDir cx cy
                           LayerName = layerName
                           LimitDbu = limit
                           GapDbu = gap
                           SlackDbu = slack
                           IsLoosen = false
                           SelBb = sBb
                           OthBb = oBb }
                | None -> None)
        |> List.toArray
        |> Array.sortBy (fun c -> c.Slot)

// Side classification reused by `checkInterInstance`. Returns Some
// for an orthogonally-facing pair (perpendicular projections
// overlap, parallel projections disjoint), None for a diagonal
// pair (skipped — orthogonal-only spacing dims, mirroring the
// dimension overlay).
type private Side = | Right | Left | Top | Bottom

let private classifySide
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : Side option =
    let yOverlap = (min ay2 by2) > (max ay1 by1)
    let xOverlap = (min ax2 bx2) > (max ax1 bx1)
    if yOverlap && bx1 >= ax2 then Some Right
    elif yOverlap && bx2 <= ax1 then Some Left
    elif xOverlap && by1 >= ay2 then Some Top
    elif xOverlap && by2 <= ay1 then Some Bottom
    else None

/// DRC restricted to *inter-instance* spacing — width violations
/// and intra-instance spacings are dropped because the editor
/// can't fix them anyway (SRef instances are atomic from this
/// tool's perspective; you'd have to edit the source cell). Only
/// orthogonally-facing polygon pairs are checked, matching the
/// dimension overlay's "no diagonal pairs" rule.
///
/// `instancePolys` maps top-instance index → flattened polygons
/// in world coords; the caller produces it via
/// `Layout.Flatten.flattenInstance`.
let checkInterInstance
        (units: Units)
        (instancePolys: Map<int, FlatPolygon array>)
        : Violation array =
    let umPerDbu = umPerDbuOf units
    let result = System.Collections.Generic.List<Violation>()

    // Precompute per-instance per-(layer, datatype) bbox arrays —
    // same shape Instances.layerPolyBboxesOf uses. Pairwise scan
    // across instances on each shared layer.
    let instLayerBboxes : Map<int, Map<int * int, (int64 * int64 * int64 * int64) array>> =
        instancePolys
        |> Map.map (fun _ polys ->
            let acc =
                System.Collections.Generic.Dictionary<int * int,
                    System.Collections.Generic.List<int64 * int64 * int64 * int64>>()
            for p in polys do
                if p.Points.Length > 0 then
                    let mutable xMin = System.Int64.MaxValue
                    let mutable yMin = System.Int64.MaxValue
                    let mutable xMax = System.Int64.MinValue
                    let mutable yMax = System.Int64.MinValue
                    for pt in p.Points do
                        if pt.X < xMin then xMin <- pt.X
                        if pt.X > xMax then xMax <- pt.X
                        if pt.Y < yMin then yMin <- pt.Y
                        if pt.Y > yMax then yMax <- pt.Y
                    let key = (p.Layer, p.DataType)
                    let bb = (xMin, yMin, xMax, yMax)
                    match acc.TryGetValue key with
                    | true, lst -> lst.Add bb
                    | _ ->
                        let lst = System.Collections.Generic.List<_>()
                        lst.Add bb
                        acc.[key] <- lst
            acc
            |> Seq.map (fun kv -> kv.Key, kv.Value.ToArray())
            |> Map.ofSeq)

    let instanceIds = instancePolys |> Map.toList |> List.map fst
    let pairs =
        [ for i in 0 .. instanceIds.Length - 1 do
            for j in i + 1 .. instanceIds.Length - 1 do
                yield instanceIds.[i], instanceIds.[j] ]

    for (idA, idB) in pairs do
        let layersA = Map.find idA instLayerBboxes
        let layersB = Map.find idB instLayerBboxes
        for layerKv in layersA do
            let key = layerKv.Key
            match Map.tryFind key layersB, Rules.tryFind (fst key) (snd key) with
            | Some arrB, Some rule ->
                let arrA = layerKv.Value
                let spacingLimit = umToDbu umPerDbu rule.MinSpacingUm
                if spacingLimit > 0L then
                    for bbA in arrA do
                        for bbB in arrB do
                            // Orthogonal-only: skip diagonal pairs
                            // so the canvas doesn't fill with arrows
                            // for shapes that don't share an axis.
                            match classifySide bbA bbB with
                            | None -> ()
                            | Some _ ->
                                let g = bboxGap bbA bbB
                                if g > 0L && g < spacingLimit then
                                    result.Add {
                                        Rule = sprintf "%s.spacing" rule.Layer
                                        LayerNumber = fst key
                                        LayerType = snd key
                                        LimitDbu = spacingLimit
                                        MeasuredDbu = g
                                        BboxA = bbA
                                        BboxB = Some bbB }
            | _ -> ()
    result.ToArray()
