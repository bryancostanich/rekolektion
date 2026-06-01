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
/// bucket it touches.
///
/// Two buckets:
///   * Per-layer — controlled by `s.DrcVisibleLayers`. Applies to
///     every layer the violation touches that is ALSO in
///     `panelLayers` (the set of layer keys the Layers panel
///     surfaces as rows).
///   * Other — controlled by `s.DrcVisibleOther`. Applies when the
///     violation touches at least one layer NOT in `panelLayers`
///     (e.g. label-only or custom-YAML layers the panel doesn't
///     show).
///
/// `keepViolation` shows the tile when EITHER bucket it touches
/// is on. So:
///   * Violation entirely on panel layers — shown iff at least one
///     of those panel layers has DRC on. (OR across panel toggles.)
///   * Violation entirely on non-panel layers — shown iff
///     DrcVisibleOther is on.
///   * Violation touching both — shown iff at least one panel
///     layer is on OR DrcVisibleOther is on.
///   * Violation with empty layer set (defensive — shouldn't
///     happen with the current `layersOfViolation` fallback) —
///     always shown.
let keepViolation
        (panelLayers: Set<Visibility.LayerKey>)
        (s: Visibility.ToggleState)
        (v: Check.Violation) : bool =
    let vLayers = layersOfViolation v
    if Set.isEmpty vLayers then true
    else
        let onPanel    = Set.intersect vLayers panelLayers
        let offPanel   = Set.difference vLayers panelLayers
        let panelOn    =
            (not (Set.isEmpty onPanel))
            && (onPanel |> Set.exists (Visibility.isDrcVisibleForLayer s))
        let otherOn    =
            (not (Set.isEmpty offPanel))
            && Visibility.isDrcVisibleOther s
        panelOn || otherOn

/// True when the violation lands in the "Other" bucket — every
/// layer it touches is OUTSIDE `panelLayers`. Used by the call
/// site and the master tri-state indicator to know how many
/// tiles a user-facing "Other" toggle is gating.
let isOtherBucket
        (panelLayers: Set<Visibility.LayerKey>)
        (v: Check.Violation) : bool =
    let vLayers = layersOfViolation v
    not (Set.isEmpty vLayers)
    && Set.isEmpty (Set.intersect vLayers panelLayers)

/// Convenience wrapper for the call site. Filters a violations
/// array in-place-equivalent fashion (returns a new array) — keeps
/// the shape `DrcOverlay.render` already consumes.
let filterArray
        (panelLayers: Set<Visibility.LayerKey>)
        (s: Visibility.ToggleState)
        (violations: Check.Violation array)
        : Check.Violation array =
    violations |> Array.filter (keepViolation panelLayers s)
