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

    let checkRule (rule: Rules.Rule) =
        let ruleName = Rules.nameOf rule
        if disabledRules.Contains ruleName then () else
        match rule with
        | Rules.Width (name, layer, minUm) ->
            // Width is per-rectangle in Magic semantics: each
            // input polygon's bbox dimensions are checked
            // against the limit. Tried doing this via
            // morphological opening on the Region (r \ opened);
            // it works mathematically but the slab-tile
            // decomposition of the Region splits each polygon
            // into many narrow tiles based on neighbor Y events,
            // and each tile fragment reports a violation —
            // bogus 4000+ violations on a clean licon array.
            // Magic's tiles are MAXIMAL strips, not slab tiles,
            // so the morphology approach doesn't translate to
            // viz's reporting model. Per-rectangle is correct.
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                for (_, (x1, y1, x2, y2), _) in polysOnLayer idx layer do
                    let m = min (x2 - x1) (y2 - y1)
                    if m < limit then
                        result.Add {
                            Rule = name
                            LayerNumber = layer.Number
                            LayerType   = layer.DataType
                            LimitDbu    = limit
                            MeasuredDbu = m
                            BboxA = (x1, y1, x2, y2)
                            BboxB = None }
        | Rules.Spacing (name, layer, minUm) ->
            // Spacing tried via Region morphology (shrink(grow,
            // s/2), s/2) \ r) but the slab-tile decomposition
            // fragments each gap region into many narrow tiles
            // based on global Y events from neighbors. Each
            // fragment reports a violation with a misleading
            // MeasuredDbu (the slab height, not the actual
            // gap). To match Magic's per-gap-region counting,
            // need connected-components on the violation
            // Region — group adjacent tiles into one component,
            // report one violation per component. That's the
            // next step. For now: per-pair with orthogonal-only
            // facing-edge filter (matches Magic semantics, just
            // not Magic counts).
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let polys = polysOnLayer idx layer
                for i in 0 .. polys.Length - 1 do
                    let (_, bbA, _) = polys.[i]
                    for j in i + 1 .. polys.Length - 1 do
                        let (_, bbB, _) = polys.[j]
                        match bboxOrthoGap bbA bbB with
                        | Some g when g > 0L && g < limit ->
                            result.Add {
                                Rule = name
                                LayerNumber = layer.Number
                                LayerType   = layer.DataType
                                LimitDbu    = limit
                                MeasuredDbu = g
                                BboxA = bbA
                                BboxB = Some bbB }
                        | _ -> ()
        | Rules.CrossSpacing (name, layerA, layerB, minUm, condA) ->
            // Same orthogonal-only rule as same-layer Spacing.
            // Overlap = same net at this layer pair (e.g. poly
            // contact on diff is legal); skip to avoid false-
            // firing on intentional crossings.
            //
            // `condA` filters the source layer to typed subsets
            // (e.g. diff/tap.9 only fires on n-diff outside
            // nwell — NsdmNotInNwell tag).
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let polysA = polysOnLayer idx layerA
                let polysB = polysOnLayer idx layerB
                for (_, bbA, aIdx) in polysA do
                    let aTags = Implant.tagOf tags aIdx
                    if condMatches condA aTags then
                        for (_, bbB, _) in polysB do
                            if not (bboxOverlaps bbA bbB) then
                                match bboxOrthoGap bbA bbB with
                                | Some g when g > 0L && g < limit ->
                                    result.Add {
                                        Rule = name
                                        LayerNumber = layerA.Number
                                        LayerType   = layerA.DataType
                                        LimitDbu    = limit
                                        MeasuredDbu = g
                                        BboxA = bbA
                                        BboxB = Some bbB }
                                | _ -> ()
        | Rules.Enclosure (name, outer, inner, minUm, cond) ->
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let outers = polysOnLayer idx outer
                let inners = polysOnLayer idx inner
                for (_, ibb, iIdx) in inners do
                    // Skip inner polygons whose implant tags
                    // don't match the rule's condition (e.g.
                    // licon.5a only applies to diff-contact
                    // licons, which have OverlapsDiff = true).
                    let iTags = Implant.tagOf tags iIdx
                    if condMatches cond iTags then
                        // The innermost-margin outer is the one
                        // whose bbox contains the inner. If
                        // multiple outers cover (rare), the
                        // largest margin wins — a generously-
                        // enclosing outer doesn't fail because
                        // another smaller outer also covers and
                        // trims tight.
                        let mutable bestMargin : int64 voption = ValueNone
                        let mutable bestOuter = ibb
                        for (_, obb, _) in outers do
                            if bboxOverlaps obb ibb then
                                let m = bboxContainsMargin ibb obb
                                match bestMargin with
                                | ValueNone ->
                                    bestMargin <- ValueSome m
                                    bestOuter <- obb
                                | ValueSome cur when m > cur ->
                                    bestMargin <- ValueSome m
                                    bestOuter <- obb
                                | _ -> ()
                        match bestMargin with
                        | ValueNone ->
                            // No outer covers this inner at all.
                            // Report as zero-margin enclosure
                            // failure — the inner is fully
                            // outside any outer.
                            result.Add {
                                Rule = name
                                LayerNumber = inner.Number
                                LayerType   = inner.DataType
                                LimitDbu    = limit
                                MeasuredDbu = 0L
                                BboxA = ibb
                                BboxB = None }
                        | ValueSome m when m < limit ->
                            result.Add {
                                Rule = name
                                LayerNumber = inner.Number
                                LayerType   = inner.DataType
                                LimitDbu    = limit
                                MeasuredDbu = m
                                BboxA = ibb
                                BboxB = Some bestOuter }
                        | _ -> ()
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
        | Rules.MinArea (name, layer, minUm2) ->
            // Compare areas in DBU² to avoid round-off near the
            // limit. (umPerDbu)² scales the µm² threshold up to
            // DBU² for one comparison per polygon.
            let scale = umPerDbu * umPerDbu
            if scale > 0.0 then
                let limit = max 0L (int64 (System.Math.Round (minUm2 / scale)))
                if limit > 0L then
                    for (_, (x1, y1, x2, y2), _) in polysOnLayer idx layer do
                        let a = (x2 - x1) * (y2 - y1)
                        if a < limit then
                            result.Add {
                                Rule = name
                                LayerNumber = layer.Number
                                LayerType   = layer.DataType
                                LimitDbu    = limit
                                MeasuredDbu = a
                                BboxA = (x1, y1, x2, y2)
                                BboxB = None }

    for rule in Rules.allRules do
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
    result
    |> Seq.filter (fun v ->
        not (Waiver.isWaived coreAreas v.Rule (unionBbox v.BboxA v.BboxB)))
    |> Array.ofSeq

/// Backward-compatible entry point: same signature as before, no
/// toggles, no implant tags. Computes implant tags internally.
/// Tests + callers without a tag pipeline call this; the canvas
/// uses `checkWithToggles` directly so the tag computation is
/// shared with other consumers.
let check (units: Units) (flat: FlatPolygon array) : Violation array =
    let tags = Implant.tagAll flat
    checkWithToggles units flat tags Set.empty

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

        // For every shared layer that has a per-layer DRC rule:
        // find the closest facing poly-pair on the requested
        // direction (oth-poly on dir-side of sel-poly with
        // perpendicular-axis projection overlap). Δ for that
        // layer = (closest facing gap) − (layer min-spacing).
        // The MIN Δ across layers is the binding constraint —
        // tightening by that amount lands the closest facing
        // pair exactly at its rule limit; every other layer ends
        // up at gap ≥ its own limit.
        let layerSlack =
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
                        bestGap |> Option.map (fun gv -> rule.Layer, gv, limit, gv - limit))
            |> Seq.toList

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
    DirX        : int   // -1, 0, 1
    DirY        : int
    /// Stable 1-based slot tied to direction so the user can
    /// memorize "3 = down". Mapping: 1 = right (+X), 2 = left
    /// (-X), 3 = down (-Y), 4 = up (+Y). Renderer + hit-test +
    /// commit all use this slot, NOT array position, so absent
    /// directions leave gaps in the visible numbering instead of
    /// renaming the surviving directions.
    Slot        : int
    LayerName   : string
    LimitDbu    : int64
    GapDbu      : int64
    SlackDbu    : int64
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

        let dirs = [ (1, 0); (-1, 0); (0, 1); (0, -1) ]
        dirs
        |> List.choose (fun (dirX, dirY) ->
            // Walk every shared layer; per layer find the closest
            // facing pair on this direction; track the BINDING
            // (smallest-slack) pair across layers.
            let mutable best : TightenCandidate option = None
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
                                let (sx1, sy1, sx2, sy2) = sBb
                                for oBb in othBbs do
                                    let (ox1, oy1, ox2, oy2) = oBb
                                    let yOverlap = (min sy2 oy2) > (max sy1 oy1)
                                    let xOverlap = (min sx2 ox2) > (max sx1 ox1)
                                    let gOpt =
                                        if dirX = 1 && yOverlap && ox1 >= sx2 then
                                            Some (ox1 - sx2)
                                        elif dirX = -1 && yOverlap && ox2 <= sx1 then
                                            Some (sx1 - ox2)
                                        elif dirY = 1 && xOverlap && oy1 >= sy2 then
                                            Some (oy1 - sy2)
                                        elif dirY = -1 && xOverlap && oy2 <= sy1 then
                                            Some (sy1 - oy2)
                                        else None
                                    match gOpt with
                                    | Some g ->
                                        let slack = g - limit
                                        if slack > 0L then
                                            let cand = {
                                                DirX = dirX
                                                DirY = dirY
                                                Slot = slotOfDir dirX dirY
                                                LayerName = rule.Layer
                                                LimitDbu = limit
                                                GapDbu = g
                                                SlackDbu = slack
                                                SelBb = sBb
                                                OthBb = oBb
                                            }
                                            match best with
                                            | None -> best <- Some cand
                                            | Some cur when slack < cur.SlackDbu ->
                                                best <- Some cand
                                            | _ -> ()
                                    | None -> ()
            best)
        |> List.toArray
        // Order by stable slot (right, left, down, up) so the
        // user can memorize "3 = down" and have it hold across
        // files. Renderer + hit-test key on `Slot`, so the array
        // order here only affects iteration; the visible labels
        // are stable regardless.
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
