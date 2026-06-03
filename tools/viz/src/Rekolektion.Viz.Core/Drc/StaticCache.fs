/// Static-layout DRC cache used by `GdsCanvasControl` to skip the
/// `Check.checkWithToggles` pass when none of its inputs changed
/// across renders.  Extracted from the canvas so the cache invariant
/// — `flat identity ∧ disabled set ∧ compat ∧ view identity` — can
/// be unit-tested without spinning up Avalonia.  Track 02 follow-up
/// added compat + view to the validity tuple; the earlier
/// `flat + disabled`-only version returned stale violations after a
/// Mode-menu DRC-compat toggle.
module Rekolektion.Viz.Core.Drc.StaticCache

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten

type T = {
    mutable Flat       : FlatPolygon array
    mutable Disabled   : Set<string>
    mutable Compat     : Compat.Compat
    mutable View       : Rules.RulesetView
    mutable Violations : Check.Violation array
}

let empty () : T = {
    Flat       = [||]
    Disabled   = Set.empty
    Compat     = Compat.Klayout
    View       = Rules.defaultView
    Violations = [||]
}

/// Look up cached violations for the given inputs.  Refreshes when
/// any of (flat identity, disabled set, compat, view identity)
/// differs from the previous call.  `view` is compared by reference
/// because `RulesYaml.loadEffectiveOrDefault` always returns a fresh
/// instance on compat change, so identity mismatch fires without
/// walking the rule list.
let get
        (cache: T)
        (compat: Compat.Compat)
        (view: Rules.RulesetView)
        (units: Units)
        (flat: FlatPolygon array)
        (disabled: Set<string>)
        : Check.Violation array =
    let valid =
        obj.ReferenceEquals(cache.Flat, flat)
        && cache.Disabled = disabled
        && cache.Compat = compat
        && obj.ReferenceEquals(cache.View, view)
    if valid then
        cache.Violations
    else
        let tags = Implant.tagAll flat
        let vs =
            Check.checkWithToggles
                compat true view units flat tags disabled
        cache.Flat       <- flat
        cache.Disabled   <- disabled
        cache.Compat     <- compat
        cache.View       <- view
        cache.Violations <- vs
        vs
