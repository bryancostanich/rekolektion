module Rekolektion.Viz.Core.Drc.Filter

/// Per-layer DRC-overlay visibility predicate.
///
/// `keepViolation ts v` returns true when violation `v` should be
/// drawn given the per-layer DRC-viz toggles in `ts`. The semantics
/// are AND across participating layers: a violation is hidden iff
/// it carries at least one layer association AND every layer it
/// touches has `Visibility.isDrcVisibleForLayer = false`. A
/// violation with no resolvable layer (e.g. a layerless transistor
/// rule, a rule name not present in `Rules.allRules`) is always
/// shown — the user can't hide what they can't aim at.
///
/// The `Violation` record carries only one (LayerNumber, LayerType)
/// pair, which represents one of the polygons involved. The other
/// layer (for two-layer rules like CrossSpacing, Enclosure, etc.)
/// has to be reconstructed from the rule definition. `layersOf`
/// does that — it walks `Rules.allRules`, finds the entry whose
/// name matches `v.Rule`, and extracts every layer field the rule
/// kind carries. If no match is found (unknown rule, or a custom
/// rule from a user YAML override), the function falls back to the
/// violation's own layer pair so the toggle still gates at least
/// the primary layer.

open Rekolektion.Viz.Core

/// Convert a `Drc.Rules.LayerKey` record to the tuple shape
/// `Visibility.LayerKey` uses everywhere else.
let private toTuple (k: Rules.LayerKey) : Visibility.LayerKey =
    (k.Number, k.DataType)

/// Pull every layer field a rule discriminant carries. Empty set is
/// reserved for "no layers" — currently no Rule case produces this,
/// but we keep the option for future layerless kinds.
let layersOfRule (rule: Rules.Rule) : Set<Visibility.LayerKey> =
    match rule with
    | Rules.Width    (_, l, _) -> Set.singleton (toTuple l)
    | Rules.Spacing  (_, l, _) -> Set.singleton (toTuple l)
    | Rules.MinArea  (_, l, _) -> Set.singleton (toTuple l)
    | Rules.CrossSpacing (_, a, b, _, _, _) ->
        Set.ofList [toTuple a; toTuple b]
    | Rules.Enclosure (_, outer, inner, _, _) ->
        Set.ofList [toTuple outer; toTuple inner]
    | Rules.Endcap (_, src, refLayer, _) ->
        Set.ofList [toTuple src; toTuple refLayer]
    | Rules.BoundaryCrossing (_, src, dst, _) ->
        Set.ofList [toTuple src; toTuple dst]
    | Rules.ImplantOutsideWellSpacing (_, implant, active, well, _) ->
        Set.ofList [toTuple implant; toTuple active; toTuple well]
    | Rules.AsymEnclosure (_, outer, inner, _, _, _) ->
        Set.ofList [toTuple outer; toTuple inner]

/// Lookup table: rule name → set of participating layers. Built once
/// from `Rules.allRules` and used by `layersOfViolation` to resolve a
/// `Check.Violation.Rule` string back to its layer footprint.
let private rulesByName : Map<string, Set<Visibility.LayerKey>> =
    Rules.allRules
    |> List.map (fun r -> Rules.nameOf r, layersOfRule r)
    |> Map.ofList

/// Layer footprint for a single violation.
///
/// Strategy:
///   1. Look up `v.Rule` in `rulesByName`. If found, return that
///      rule's full layer set. This is the only path that recovers
///      the SECOND layer for two-layer rules.
///   2. Fallback: return the singleton set
///      `{(v.LayerNumber, v.LayerType)}` so unknown rules still
///      respond to the toggle on their primary layer.
let layersOfViolation (v: Check.Violation) : Set<Visibility.LayerKey> =
    match Map.tryFind v.Rule rulesByName with
    | Some layers when not (Set.isEmpty layers) -> layers
    | _ -> Set.singleton (v.LayerNumber, v.LayerType)

/// `true` ⇒ render the violation. `false` ⇒ user has hidden every
/// layer it touches.
///
/// Semantics:
///   * Layerless rules (`layersOfViolation` returns empty) — kept.
///     The spec calls these "immune to the toggle". In practice this
///     branch is unreachable today because `layersOfViolation`
///     always falls back to the violation's own primary layer pair,
///     but the predicate is defined for clarity.
///   * Layer-bearing rules — hidden iff EVERY layer the rule
///     touches is OFF in `s.DrcVisibleLayers`. Equivalently: shown
///     when at least one participating layer is ON.
let keepViolation (s: Visibility.ToggleState) (v: Check.Violation) : bool =
    let layers = layersOfViolation v
    if Set.isEmpty layers then true
    else
        layers
        |> Set.exists (Visibility.isDrcVisibleForLayer s)

/// Convenience wrapper for the call site. Filters a violations array
/// in-place-equivalent fashion (returns a new array) — keeps the
/// shape `DrcOverlay.render` already consumes.
let filterArray
        (s: Visibility.ToggleState)
        (violations: Check.Violation array)
        : Check.Violation array =
    violations |> Array.filter (keepViolation s)
