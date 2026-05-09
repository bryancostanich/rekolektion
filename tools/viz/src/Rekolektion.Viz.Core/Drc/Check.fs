module Rekolektion.Viz.Core.Drc.Check

open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Layout.Flatten

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

/// Run min-width + min-spacing checks against every polygon in
/// `lib.flat`. Quadratic per layer in the polygon count, so the
/// caller is responsible for restricting the input to the edited
/// neighborhood when working at production-scale macros.
let check (lib: Library) (flat: FlatPolygon array) : Violation array =
    let umPerDbu = lib.UserUnitsPerDbUnit
    let result = System.Collections.Generic.List<Violation>()

    // Group polygons by (layer, datatype) so per-layer rules only
    // see their own polys.
    let byLayer =
        flat
        |> Array.groupBy (fun p -> p.Layer, p.DataType)

    for ((layer, dt), polys) in byLayer do
        match Rules.tryFind layer dt with
        | None -> ()
        | Some rule ->
            let widthLimit = umToDbu umPerDbu rule.MinWidthUm
            let spacingLimit = umToDbu umPerDbu rule.MinSpacingUm

            let bboxes =
                polys |> Array.map (fun p -> p, bboxOf p)

            // Min width: for an axis-aligned bbox, both x-extent
            // and y-extent must clear `widthLimit`. Polygons with
            // notches will under-report, but we don't decompose to
            // edges yet — bbox-width is a conservative first cut
            // that catches the common case (whole shapes too narrow).
            for (poly, (x1, y1, x2, y2)) in bboxes do
                let w = x2 - x1
                let h = y2 - y1
                let m = min w h
                if widthLimit > 0L && m < widthLimit then
                    result.Add {
                        Rule = sprintf "%s.width" rule.Layer
                        LayerNumber = layer
                        LayerType = dt
                        LimitDbu = widthLimit
                        MeasuredDbu = m
                        BboxA = (x1, y1, x2, y2)
                        BboxB = None }

            // Min spacing: pairwise nearest-edge distance per
            // layer. Skip overlapping bboxes — those are the
            // same shape touching itself in the source data, or a
            // genuine overlap which is an extraction problem, not
            // spacing. Each unordered pair reports once.
            if spacingLimit > 0L then
                for i in 0 .. bboxes.Length - 1 do
                    let (_, bbA) = bboxes.[i]
                    for j in i + 1 .. bboxes.Length - 1 do
                        let (_, bbB) = bboxes.[j]
                        let g = bboxGap bbA bbB
                        if g > 0L && g < spacingLimit then
                            result.Add {
                                Rule = sprintf "%s.spacing" rule.Layer
                                LayerNumber = layer
                                LayerType = dt
                                LimitDbu = spacingLimit
                                MeasuredDbu = g
                                BboxA = bbA
                                BboxB = Some bbB }

    result.ToArray()

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
        (lib: Library)
        (selectedPolys: FlatPolygon array)
        (otherPolys:    FlatPolygon array)
        (dirX: int)
        (dirY: int)
        : int64 option =
    let umPerDbu = lib.UserUnitsPerDbUnit
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
        (lib: Library)
        (instancePolys: Map<int, FlatPolygon array>)
        : Violation array =
    let umPerDbu = lib.UserUnitsPerDbUnit
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
