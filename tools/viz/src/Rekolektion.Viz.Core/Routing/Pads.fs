module Rekolektion.Viz.Core.Routing.Pads

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Drc.Rules

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
let endpointPadSide
        (view: RulesetView)
        (units: Units)
        ((layerNum, layerDt): int * int)
        : int64 option =
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
