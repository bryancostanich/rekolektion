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

// --- Klayout skeleton submodule -------------------------------------------

[<Fact>]
let ``Rules.Klayout.allRules is empty in Phase 3`` () =
    // Phase 4 populates this list one rule at a time via the corpus
    // harness.  Once that work lands, this test will need to flip
    // to a "≥ N" assertion against the equivalency table.
    Rules.Klayout.allRules |> should equal ([]: Rules.Rule list)

[<Fact>]
let ``Rules.Klayout derived helpers match the empty allRules`` () =
    Rules.Klayout.allCrossSpacings |> should equal ([]: Rules.CrossSpacingRule list)
    Rules.Klayout.liveRules        |> should equal ([]: Rules.Rule list)
    Rules.Klayout.liveEligibleNames |> should equal (Set.empty: Set<string>)

[<Fact>]
let ``Rules.Klayout.defaultView has empty rule list, empty provenance`` () =
    Rules.Klayout.defaultView.Rules |> should equal ([]: Rules.Rule list)
    Rules.Klayout.defaultView.Provenance |> should equal Map.empty<string, string>

[<Fact>]
let ``Rules.Klayout.tryFind returns None for any layer`` () =
    Rules.Klayout.tryFind 68 20 |> should equal (None: Rules.LayerRule option)
    Rules.Klayout.tryFind 69 20 |> should equal (None: Rules.LayerRule option)
    Rules.Klayout.tryFind 67 20 |> should equal (None: Rules.LayerRule option)

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
    // Cannot be the same view as long as Magic has rules and Klayout
    // doesn't.  Phase 4 work that populates Klayout will keep them
    // distinct in their RULE CONTENT.
    magic.Rules |> should not' (be Empty)
    klayout.Rules |> should equal ([]: Rules.Rule list)
