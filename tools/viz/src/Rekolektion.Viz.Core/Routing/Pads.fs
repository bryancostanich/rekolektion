module Rekolektion.Viz.Core.Routing.Pads

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Drc.Rules
open Rekolektion.Viz.Core.Layout.Flatten

/// Compute the side length (in DBU) of a square endpoint pad for
/// the given routing layer, driven by the view's DRC rules.
///
/// Scans every Enclosure / AsymEnclosure rule where the routing
/// layer is the OUTER and the inner is anything (typically the
/// via going up). Each contributes
///   inner_width + 2 × max(enclosure thresholds)
/// where `inner_width` is the inner layer's `Width` rule and the
/// max-threshold takes the LARGER of the two AsymEnclosure values
/// (a square pad satisfies both axes when sized to the bigger).
///
/// Floored by the layer's `MinArea` square so a pad never violates
/// min-area on its own.
///
/// Returns `None` when no enclosure data for this layer is in the
/// view — callers (RouteFinish) skip pad emission and leave the
/// wire endpoint bare. Add the layer's enclosure rule to expand
/// coverage.
/// Layers explicitly excluded from endpoint-pad emission. Pin
/// patches on these layers are managed by primitive generators
/// (the `pin_patch` helper in the .rkt workflow), not by the
/// interactive router — a square knuckle here would stack on top
/// of an existing patch and either visually duplicate it or trip
/// `mcon.2` spacing against the primitive's own mcons.
let private noPadLayers : Set<int * int> = Set.ofList [
    (67, 20)   // li1 — pin patches come from gen_*_core primitives
]

let endpointPadSide
        (view: RulesetView)
        (units: Units)
        ((layerNum, layerDt): int * int)
        : int64 option =
    if noPadLayers.Contains (layerNum, layerDt) then None
    else
    let umPerDbu = float units.DbuNm * 1.0e-3
    // (number, datatype) → width-in-µm lookup from Width rules.
    let widthByLayer =
        view.Rules
        |> List.choose (fun r ->
            match r with
            | Width (_, l, m) -> Some ((l.Number, l.DataType), m)
            | _ -> None)
        |> Map.ofList
    // Enclosure pad candidates for THIS layer as the outer.
    let isThisLayer (l: LayerKey) =
        l.Number = layerNum && l.DataType = layerDt
    let enclosureCandidates =
        view.Rules
        |> List.choose (fun r ->
            match r with
            | Enclosure (_, outer, inner, m, _) when isThisLayer outer ->
                widthByLayer
                |> Map.tryFind (inner.Number, inner.DataType)
                |> Option.map (fun w -> w + 2.0 * m)
            | AsymEnclosure (_, outer, inner, a, b, _) when isThisLayer outer ->
                widthByLayer
                |> Map.tryFind (inner.Number, inner.DataType)
                |> Option.map (fun w -> w + 2.0 * (max a b))
            | _ -> None)
    // Min-area floor for this layer.
    let minAreaCandidate =
        view.Rules
        |> List.tryPick (fun r ->
            match r with
            | MinArea (_, l, m2) when isThisLayer l -> Some (sqrt m2)
            | _ -> None)
    let candidates =
        enclosureCandidates @ (Option.toList minAreaCandidate)
    if List.isEmpty candidates then None
    else
        let sideUm = List.max candidates
        Some (int64 (sideUm / umPerDbu))

/// Default wire width (DBU) for the routing layer — the layer's
/// `Width` rule min, e.g. 0.14 µm for met1, 0.30 µm for met3. The
/// interactive router uses this when starting a new draft so wires
/// match the PDK's minimum, not an arbitrary global default.
/// Returns None when no Width rule names this layer (no PDK data).
let wireWidthFor
        (view: RulesetView)
        (units: Units)
        ((layerNum, layerDt): int * int)
        : int64 option =
    let umPerDbu = float units.DbuNm * 1.0e-3
    view.Rules
    |> List.tryPick (fun r ->
        match r with
        | Width (_, l, m) when l.Number = layerNum && l.DataType = layerDt ->
            Some (int64 (m / umPerDbu))
        | _ -> None)

/// Drop synthetic metal-pad segments whose role (enclosing an
/// adjacent via cut for DRC) is already filled by an existing
/// foreign polygon on the SAME layer.
///
/// A synthetic pad (emitted by `ViaStack.emitAt` for the snap-layer
/// or intermediate metal layers) exists ONLY to give a co-located
/// via cut the metal enclosure DRC demands. When the caller is
/// snapping onto an EXISTING foreign polygon that already fully
/// contains the via cut's bbox, the pad is redundant geometry —
/// a "knuckle" stacked on top of the foreign poly.
///
/// The semantic is "foreign poly encloses the via cut," not
/// "foreign poly encloses the pad." The pad itself is sized for
/// the strictest enclosure rule and may stick a few nm past a
/// foreign poly that nonetheless encloses the via cut cleanly
/// (concrete: tap_mux_input_inv.rkt VSS rail is 260 nm tall, the
/// synthetic met1 pad is 290 nm tall — pad sticks 15 nm proud, but
/// the underlying 170 nm mcon fits 45 nm inside the rail on each
/// Y side).
///
/// Via cuts (mcon, via, via2…) are NEVER dropped: they are the
/// physical layer transition, not enclosure. Removing the cut
/// would electrically break the route even if the foreign poly
/// visually covers it.
///
/// Pad ↔ via-cut pairing is by COINCIDENT CENTRE: `emitAt` builds
/// both the pad and the via cut around the same `(cx, cy)`.
///
/// Containment uses the foreign poly's bounding rectangle (not
/// arbitrary-polygon containment). False-negatives keep the pad,
/// which is the safe direction; all current foreign polys that
/// matter (parent-painted rails, primitive pin straps) are
/// rectangular.
///
/// User report: tap_mux_input_inv.rkt bottom VSS route (2026-05-31).
let dropPadsContainedByForeignPolys
        (foreignPolys : FlatPolygon array)
        (segments : Draft.DraftSegment list)
        : Draft.DraftSegment list =
    if Array.isEmpty foreignPolys then segments
    else
    let polyBbox (p : FlatPolygon) : int64 * int64 * int64 * int64 =
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for pt in p.Points do
            if pt.X < xMin then xMin <- pt.X
            if pt.X > xMax then xMax <- pt.X
            if pt.Y < yMin then yMin <- pt.Y
            if pt.Y > yMax then yMax <- pt.Y
        (xMin, yMin, xMax, yMax)
    let centreOf (s : Draft.DraftSegment) : int64 * int64 =
        ((s.X1 + s.X2) / 2L, (s.Y1 + s.Y2) / 2L)
    let segBboxContained (sx1 : int64) (sy1 : int64)
                         (sx2 : int64) (sy2 : int64)
                         (layer : int * int) : bool =
        let (sn, sd) = layer
        foreignPolys
        |> Array.exists (fun p ->
            if p.Layer <> sn || p.DataType <> sd then false
            elif p.Points.Length = 0 then false
            else
                let (xMin, yMin, xMax, yMax) = polyBbox p
                sx1 >= xMin && sx2 <= xMax
                && sy1 >= yMin && sy2 <= yMax)
    // Index via cuts by their centre so each metal pad can find
    // its paired cut in O(N) per pad.  Same-centre = paired by
    // construction (emitAt builds both around (cx, cy)).
    let viaCutsByCentre : Map<int64 * int64, Draft.DraftSegment list> =
        segments
        |> List.filter (fun s -> ViaStack.isViaOrContactLayer s.Layer)
        |> List.groupBy centreOf
        |> Map.ofList
    let segDropped (s : Draft.DraftSegment) : bool =
        if ViaStack.isViaOrContactLayer s.Layer then false
        else
            // Strict bbox-subset case (kept for parent-paint pads
            // and any pad whose own bbox is already a subset of a
            // foreign poly — handles the same-bbox edge case and
            // intermediate-metal pads minted alongside an existing
            // foreign poly without needing a via cut pairing).
            let sx1 = min s.X1 s.X2
            let sy1 = min s.Y1 s.Y2
            let sx2 = max s.X1 s.X2
            let sy2 = max s.Y1 s.Y2
            let padContained = segBboxContained sx1 sy1 sx2 sy2 s.Layer
            if padContained then true
            else
                // Looser case: a foreign poly fully encloses the
                // via cut this pad was sized for.  Pair by
                // coincident centre.
                match Map.tryFind (centreOf s) viaCutsByCentre with
                | None -> false
                | Some cuts ->
                    cuts |> List.exists (fun v ->
                        let vx1 = min v.X1 v.X2
                        let vy1 = min v.Y1 v.Y2
                        let vx2 = max v.X1 v.X2
                        let vy2 = max v.Y1 v.Y2
                        segBboxContained vx1 vy1 vx2 vy2 s.Layer)
    segments |> List.filter (not << segDropped)

/// Min same-layer spacing (DBU) for a routing layer from its
/// `Spacing` rule. The walk-around adds this to half-wire-width to
/// get an obstacle expansion that keeps the wire edge at least
/// `spacing` away from any foreign feature on the same layer.
/// `None` when no `Spacing` rule covers the layer.
let spacingFor
        (view: RulesetView)
        (units: Units)
        ((layerNum, layerDt): int * int)
        : int64 option =
    let umPerDbu = float units.DbuNm * 1.0e-3
    view.Rules
    |> List.tryPick (fun r ->
        match r with
        | Spacing (_, l, m) when l.Number = layerNum && l.DataType = layerDt ->
            Some (int64 (m / umPerDbu))
        | _ -> None)
