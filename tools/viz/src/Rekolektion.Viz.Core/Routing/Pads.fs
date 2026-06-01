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

/// Per-rule enclosure thresholds for a (metal layer → via layer)
/// pair, in DBU. Returns (strictDbu, relaxedDbu) where strictDbu
/// ≥ relaxedDbu.
///
/// * Symmetric `Enclosure(metal, via, m)`: strict = relaxed = m.
///   Foreign poly must provide ≥m on every side of the via cut.
/// * `AsymEnclosure(metal, via, a, b)`: strict = max(a, b),
///   relaxed = min(a, b). Foreign poly must provide ≥strict on
///   one axis AND ≥relaxed on the other (either orientation —
///   the strict-axis can be X or Y).
///
/// When multiple matching rules exist (rare but possible after
/// override merging), take the per-axis maximum to stay
/// conservative.
let private enclosureRequirementsDbu
        (view : RulesetView)
        (units : Units)
        (metalLayer : int * int)
        (viaLayer : int * int) : (int64 * int64) option =
    let isMetal (l : LayerKey) =
        l.Number = fst metalLayer && l.DataType = snd metalLayer
    let isVia (l : LayerKey) =
        l.Number = fst viaLayer && l.DataType = snd viaLayer
    let pairs =
        view.Rules
        |> List.choose (fun r ->
            match r with
            | Enclosure (_, outer, inner, m, _)
                when isMetal outer && isVia inner -> Some (m, m)
            | AsymEnclosure (_, outer, inner, a, b, _)
                when isMetal outer && isVia inner ->
                Some (max a b, min a b)
            | _ -> None)
    if pairs.IsEmpty then None
    else
        let strict  = pairs |> List.map fst |> List.max
        let relaxed = pairs |> List.map snd |> List.max
        let umPerDbu = float units.DbuNm * 1.0e-3
        let toDbu um = int64 (System.Math.Ceiling (um / umPerDbu))
        Some (toDbu strict, toDbu relaxed)

/// Drop synthetic metal-pad segments whose role (providing an
/// adjacent via cut with the DRC-required enclosure) is already
/// filled by an existing foreign polygon on the SAME layer.
///
/// **Respects `ViaStack.emitAt`'s "full ladder" invariant.** The
/// auto-stitcher MUST land a metal pad at every intermediate
/// metal layer in a via stack; a partial ladder silently fails
/// magic's via.4b / via.5b. This filter is the ONLY mechanism
/// allowed to remove a pad post-emit, and only when the
/// enclosure-rule margins are explicitly verified. Adding
/// containment-only shortcuts here re-opens the b1_5_stage2
/// silicon-killer (2026-05-31): a primitive met1 strap that
/// barely contained via1 but failed asym via.4b suppressed the
/// synthetic enlargement pad and silicon went out with metal
/// 15 nm shy of the strict-axis enclosure.
///
/// Two cases:
///
///   1. **Pad with a co-centred via cut** (the common case —
///      intermediate metal pads and snap-layer pads from
///      `ViaStack.emitAt`). The foreign poly must satisfy the
///      via's enclosure rule from the active DRC view:
///      * Symmetric Enclosure: ≥m on every side of the via cut.
///      * AsymEnclosure(a, b): ≥strict on one axis AND ≥relaxed
///        on the other.
///      Just CONTAINING the via cut isn't enough — primitive
///      contact pads commonly enclose the cut by ~70 nm while
///      sky130's asym rules demand ≥85 nm strict-axis; dropping
///      the synthetic pad on those primes a via.4b / via.5b
///      DRC failure (b1_5_stage2 tail2 jumper, 2026-05-31).
///
///   2. **Pad with NO co-centred via cut** (wire-endpoint pads
///      that ride a foreign rail end-to-end). The foreign poly
///      must FULLY CONTAIN the pad's bbox — a simpler subset
///      check, since there's no via to enclose, only redundant
///      metal to suppress.
///
/// Via cuts (mcon, via, via2…) are NEVER dropped: they are the
/// physical layer transition, not enclosure. Removing the cut
/// would electrically break the route even if the foreign poly
/// visually covers it.
///
/// Pad ↔ via-cut pairing is by COINCIDENT CENTRE: `emitAt` builds
/// both the pad and the via cut around the same `(cx, cy)`.
///
/// User reports:
///   * tap_mux_input_inv.rkt VSS knuckle (2026-05-31, bbfe649).
///   * b1_5_stage2.rkt tail2 jumper missing met1 pad (2026-05-31,
///     this commit) — primitive met1 contact pad contained via1
///     cut by only 70 nm so via.4b strict-axis ≥85 nm failed in
///     magic. Filter now checks enclosure-rule satisfaction so
///     under-sized primitive pads no longer suppress the synthetic
///     enlargement.
let dropPadsContainedByForeignPolys
        (view : RulesetView)
        (units : Units)
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
    /// Foreign poly on `metalLayer` provides the enclosure-rule
    /// requirement around the via cut's bbox. Caller has already
    /// verified an enclosure rule exists for (metalLayer, viaLayer);
    /// `strictDbu` / `relaxedDbu` come from `enclosureRequirementsDbu`.
    let foreignPolyProvidesEnclosure
            (metalLayer : int * int)
            (strictDbu : int64)
            (relaxedDbu : int64)
            (cutBbox : int64 * int64 * int64 * int64) : bool =
        let (vx1, vy1, vx2, vy2) = cutBbox
        let (sn, sd) = metalLayer
        foreignPolys
        |> Array.exists (fun p ->
            if p.Layer <> sn || p.DataType <> sd then false
            elif p.Points.Length = 0 then false
            else
                let (xMin, yMin, xMax, yMax) = polyBbox p
                let leftEncl   = vx1 - xMin
                let rightEncl  = xMax - vx2
                let bottomEncl = vy1 - yMin
                let topEncl    = yMax - vy2
                // Must at least contain the via cut.
                if leftEncl < 0L || rightEncl < 0L
                   || bottomEncl < 0L || topEncl < 0L then false
                else
                    let xAxisMin = min leftEncl rightEncl
                    let yAxisMin = min bottomEncl topEncl
                    // Asym rule satisfied when one axis ≥strict
                    // AND the other axis ≥relaxed (either
                    // orientation). For symmetric Enclosure
                    // (strict = relaxed) this collapses to the
                    // ≥m-on-every-side check.
                    (xAxisMin >= strictDbu && yAxisMin >= relaxedDbu)
                    || (yAxisMin >= strictDbu && xAxisMin >= relaxedDbu))
    // Index via cuts by their centre so each metal pad can find
    // its paired cuts in O(N) per pad.  Same-centre = paired by
    // construction (emitAt builds every via cut and every metal
    // pad in a stack around the same (cx, cy)).
    let viaCutsByCentre : Map<int64 * int64, Draft.DraftSegment list> =
        segments
        |> List.filter (fun s -> ViaStack.isViaOrContactLayer s.Layer)
        |> List.groupBy centreOf
        |> Map.ofList
    let segDropped (s : Draft.DraftSegment) : bool =
        if ViaStack.isViaOrContactLayer s.Layer then false
        else
            match Map.tryFind (centreOf s) viaCutsByCentre with
            | Some cuts ->
                // A metal pad emitted by emitAt encloses every
                // ADJACENT via cut (the via above and the via
                // below). Non-adjacent cuts at the same centre
                // (e.g. via2 sits at the same (cx, cy) as a met1
                // pad in a met3-to-li1 stack, but is on the wrong
                // floor) are irrelevant — they have NO enclosure
                // rule against this metal layer and so must NOT
                // contribute to the drop decision. Filter by
                // rule existence first.
                let applicableCuts =
                    cuts
                    |> List.choose (fun v ->
                        match enclosureRequirementsDbu
                                view units s.Layer v.Layer with
                        | Some (strict, relaxed) -> Some (v, strict, relaxed)
                        | None -> None)
                if applicableCuts.IsEmpty then
                    // No via at this centre has an enclosure rule
                    // against the pad's layer — pad has no
                    // enclosure role here. Fall back to strict
                    // bbox containment (handles orphan pads that
                    // are pure duplicates of foreign geometry).
                    let sx1 = min s.X1 s.X2
                    let sy1 = min s.Y1 s.Y2
                    let sx2 = max s.X1 s.X2
                    let sy2 = max s.Y1 s.Y2
                    segBboxContained sx1 sy1 sx2 sy2 s.Layer
                else
                    // Drop ONLY when the foreign poly satisfies
                    // every applicable via's enclosure rule. A
                    // foreign strap that handles mcon enclosure
                    // but fails via1 still leaves the pad
                    // needed — so List.forall, not List.exists.
                    applicableCuts
                    |> List.forall (fun (v, strict, relaxed) ->
                        let vx1 = min v.X1 v.X2
                        let vy1 = min v.Y1 v.Y2
                        let vx2 = max v.X1 v.X2
                        let vy2 = max v.Y1 v.Y2
                        foreignPolyProvidesEnclosure
                            s.Layer strict relaxed
                            (vx1, vy1, vx2, vy2))
            | None ->
                // Pad with no co-centred via cut (wire-endpoint
                // pads, parent-paint duplicates): strict bbox
                // containment is the right semantic — the pad
                // adds no metal beyond the foreign coverage.
                let sx1 = min s.X1 s.X2
                let sy1 = min s.Y1 s.Y2
                let sx2 = max s.X1 s.X2
                let sy2 = max s.Y1 s.Y2
                segBboxContained sx1 sy1 sx2 sy2 s.Layer
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
