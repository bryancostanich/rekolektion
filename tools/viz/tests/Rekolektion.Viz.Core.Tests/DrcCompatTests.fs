module Rekolektion.Viz.Core.Tests.DrcCompatTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Drc

// --- Compat type ----------------------------------------------------------

[<Fact>]
let ``Compat.defaultCompat is Klayout`` () =
    Compat.defaultCompat |> should equal Compat.Klayout

[<Fact>]
let ``Compat.toString round-trips through parse`` () =
    let cases = [ Compat.Klayout; Compat.Magic ]
    for c in cases do
        let parsed = Compat.parse (Compat.toString c)
        parsed |> should equal (Some c)

[<Fact>]
let ``Compat.parse is case-insensitive`` () =
    Compat.parse "klayout" |> should equal (Some Compat.Klayout)
    Compat.parse "Klayout" |> should equal (Some Compat.Klayout)
    Compat.parse "KLAYOUT" |> should equal (Some Compat.Klayout)
    Compat.parse "magic"   |> should equal (Some Compat.Magic)
    Compat.parse "Magic"   |> should equal (Some Compat.Magic)

[<Fact>]
let ``Compat.parse returns None on unknown`` () =
    Compat.parse "calibre"        |> should equal (None: Compat.Compat option)
    Compat.parse ""               |> should equal (None: Compat.Compat option)
    Compat.parse null             |> should equal (None: Compat.Compat option)

// --- Magic alias submodule ------------------------------------------------

[<Fact>]
let ``Rules.Magic.allRules is the same list as Rules.allRules`` () =
    Rules.Magic.allRules |> should equal Rules.allRules

[<Fact>]
let ``Rules.Magic.defaultView is the same view as Rules.defaultView`` () =
    Rules.Magic.defaultView |> should equal Rules.defaultView

[<Fact>]
let ``Rules.Magic has the populated Magic-flavored ruleset`` () =
    // Magic is frozen at whatever this number is at the time of the
    // refactor; the value matters less than "non-empty + non-trivial."
    // If a future PR intentionally trims the Magic ruleset, update
    // this lower bound.
    (List.length Rules.Magic.allRules) |> should be (greaterThan 20)

// --- Klayout populated submodule ------------------------------------------
//
// As Phase 4 populates Rules.Klayout.allRules rule-by-rule, these
// assertions tighten.  Today's bar: the three seed met1 rules
// (m1.1 / m1.2 / m1.6) are present, proven green on the corpus.

[<Fact>]
let ``Rules.Klayout.allRules has the proven met1 rules`` () =
    let names = Rules.Klayout.allRules |> List.map Rules.nameOf |> Set.ofList
    names |> should contain "m1.1"
    names |> should contain "m1.2"
    names |> should contain "m1.6"

[<Fact>]
let ``Rules.Klayout derived helpers reflect the populated rule list`` () =
    // Live-eligible = Width / Spacing / CrossSpacing / Enclosure
    // / Endcap / AsymEnclosure. MinArea / BoundaryCrossing /
    // ImplantOutsideWellSpacing / EnclosureOfIntersection are
    // commit-only. As Phase 4 populates rules, both counts grow
    // — keep the assertion structural rather than fixing exact
    // numbers (which would force a test update every commit).
    Rules.Klayout.liveRules |> should not' (be Empty)
    Rules.Klayout.liveEligibleNames |> should contain "m1.1"
    Rules.Klayout.liveEligibleNames |> should contain "m1.2"
    // MinArea is not live-eligible.
    Rules.Klayout.liveEligibleNames |> should not' (contain "m1.6")
    // liveRules should be a SUBSET of allRules (sanity).
    (List.length Rules.Klayout.liveRules)
        |> should be (lessThanOrEqualTo (List.length Rules.Klayout.allRules))

[<Fact>]
let ``Rules.Klayout.defaultView mirrors the rule list, empty provenance`` () =
    Rules.Klayout.defaultView.Rules |> should equal Rules.Klayout.allRules
    Rules.Klayout.defaultView.Provenance |> should equal Map.empty<string, string>

[<Fact>]
let ``Rules.Klayout.tryFind resolves implemented layers`` () =
    // met1 (68/20) — the canonical proven layer; assertion stays
    // fixed because the values are foundry-spec.
    let m1 = Rules.Klayout.tryFind 68 20
    m1.IsSome |> should equal true
    m1.Value.MinWidthUm |> should equal 0.14
    m1.Value.MinSpacingUm |> should equal 0.14
    // Other implemented layers should also resolve as Phase 4
    // populates them. Don't assert specific values here — those
    // change as more rules land; the Phase 4 corpus harness +
    // status doc are the canonical record of which rules are
    // implemented and what their numbers are.
    Rules.Klayout.tryFind 67 20 |> Option.isSome |> should equal true  // li1
    Rules.Klayout.tryFind 69 20 |> Option.isSome |> should equal true  // met2
    Rules.Klayout.tryFind 70 20 |> Option.isSome |> should equal true  // met3
    // Layers truly without any width/spacing rule (e.g. text-only
    // marker layers) should still return None.
    Rules.Klayout.tryFind 999 999 |> should equal (None: Rules.LayerRule option)

// --- viewFor dispatcher ---------------------------------------------------

[<Fact>]
let ``viewFor Magic returns the Magic-flavored view`` () =
    Rules.viewFor Compat.Magic |> should equal Rules.Magic.defaultView

[<Fact>]
let ``viewFor Klayout returns the KLayout-flavored view`` () =
    Rules.viewFor Compat.Klayout |> should equal Rules.Klayout.defaultView

[<Fact>]
let ``viewFor distinguishes Magic from Klayout`` () =
    let magic = Rules.viewFor Compat.Magic
    let klayout = Rules.viewFor Compat.Klayout
    // Both lists are non-empty as Phase 4 populates KLayout.  They
    // stay DISTINCT because they reference different ruleset
    // bodies — same rules under different deck names, plus rules
    // that exist in one engine but not the other.  The size delta
    // (Magic has the full Magic-tuned list; Klayout grows
    // incrementally) is the load-bearing signal.
    magic.Rules   |> should not' (be Empty)
    klayout.Rules |> should not' (be Empty)
    (List.length magic.Rules) |> should be (greaterThan (List.length klayout.Rules))
