module Rekolektion.Viz.Core.Tests.DrcFilterTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Drc

/// Convenience for building a synthetic Violation. Layer fields
/// match the rule's primary layer per Check's emission convention.
let private mkViolation
        (rule: string)
        (layer: Visibility.LayerKey)
        : Check.Violation =
    let n, dt = layer
    {
        Rule        = rule
        LayerNumber = n
        LayerType   = dt
        LimitDbu    = 140L
        MeasuredDbu = 100L
        BboxA       = (0L, 0L, 100L, 100L)
        BboxB       = None
    }

// --- layersOfRule -------------------------------------------------------

[<Fact>]
let ``layersOfRule on a Width rule returns its single layer`` () =
    let r = Rules.Width ("met1.1", Rules.met1, 0.14)
    Filter.layersOfRule r
    |> should equal (Set.singleton (68, 20))

[<Fact>]
let ``layersOfRule on a Spacing rule returns its single layer`` () =
    let r = Rules.Spacing ("met2.2", Rules.met2, 0.14)
    Filter.layersOfRule r
    |> should equal (Set.singleton (69, 20))

[<Fact>]
let ``layersOfRule on a MinArea rule returns its single layer`` () =
    let r = Rules.MinArea ("met1.6", Rules.met1, 0.083)
    Filter.layersOfRule r
    |> should equal (Set.singleton (68, 20))

[<Fact>]
let ``layersOfRule on a CrossSpacing rule returns both layers`` () =
    let r =
        Rules.CrossSpacing (
            "poly.4", Rules.poly, Rules.diff, 0.075,
            Rules.Always, Rules.Always)
    Filter.layersOfRule r
    |> should equal (Set.ofList [(66, 20); (65, 20)])

[<Fact>]
let ``layersOfRule on an Enclosure rule returns outer and inner`` () =
    let r =
        Rules.Enclosure (
            "difftap.8a", Rules.nwell, Rules.diff, 0.18,
            Rules.PsdmOverlaps)
    Filter.layersOfRule r
    |> should equal (Set.ofList [(64, 20); (65, 20)])

[<Fact>]
let ``layersOfRule on an Endcap rule returns source and reference`` () =
    let r = Rules.Endcap ("poly.7", Rules.diff, Rules.poly, 0.25)
    Filter.layersOfRule r
    |> should equal (Set.ofList [(65, 20); (66, 20)])

[<Fact>]
let ``layersOfRule on an AsymEnclosure returns outer and inner`` () =
    let r =
        Rules.AsymEnclosure (
            "via.5a", Rules.met1, Rules.via, 0.06, 0.03, Rules.Always)
    Filter.layersOfRule r
    |> should equal (Set.ofList [(68, 20); (68, 44)])

[<Fact>]
let ``layersOfRule on an ImplantOutsideWellSpacing returns all three`` () =
    let r =
        Rules.ImplantOutsideWellSpacing (
            "difftap.9", Rules.nsdm, Rules.diff, Rules.nwell, 0.34)
    Filter.layersOfRule r
    |> should equal (Set.ofList [(93, 44); (65, 20); (64, 20)])

// --- layersOfViolation --------------------------------------------------

[<Fact>]
let ``layersOfViolation resolves a known single-layer rule via lookup`` () =
    let v = mkViolation "met1.1" (68, 20)
    Filter.layersOfViolation v
    |> should equal (Set.singleton (68, 20))

[<Fact>]
let ``layersOfViolation recovers BOTH layers for a two-layer rule`` () =
    // met1.5 in the live rule table is an AsymEnclosure on met1 + via1.
    // The violation only carries one of those in its LayerNumber/Type;
    // the second one comes from the rule definition lookup.
    let r =
        Rules.AsymEnclosure (
            "met1.5_test", Rules.met1, Rules.via, 0.06, 0.03, Rules.Always)
    // We can't inject into Rules.allRules in a test, so we cross-check
    // an actual entry instead: poly.7 (Endcap of diff + poly).
    let v = mkViolation "poly.7" (66, 20)
    let layers = Filter.layersOfViolation v
    Set.contains (66, 20) layers |> should equal true  // poly
    Set.contains (65, 20) layers |> should equal true  // diff
    Set.count layers |> should equal 2

[<Fact>]
let ``layersOfViolation falls back to primary layer for unknown rule`` () =
    let v = mkViolation "unknown.bogus" (88, 99)
    Filter.layersOfViolation v
    |> should equal (Set.singleton (88, 99))

// --- keepViolation: AND semantics ---------------------------------------

[<Fact>]
let ``keepViolation keeps everything when ToggleState is empty (default ON)`` () =
    let s = Visibility.empty
    let v = mkViolation "met1.1" (68, 20)
    Filter.keepViolation s v |> should equal true

[<Fact>]
let ``keepViolation hides a single-layer rule when its only layer is OFF`` () =
    let s =
        Visibility.empty
        |> Visibility.setDrcVisibleLayer (68, 20) false
    let v = mkViolation "met1.1" (68, 20)
    Filter.keepViolation s v |> should equal false

[<Fact>]
let ``keepViolation hides MinArea when its layer is OFF`` () =
    let s =
        Visibility.empty
        |> Visibility.setDrcVisibleLayer (68, 20) false
    let v = mkViolation "met1.6" (68, 20)
    Filter.keepViolation s v |> should equal false

[<Fact>]
let ``keepViolation keeps a two-layer rule when ANY layer is ON`` () =
    // poly.7 is an Endcap on diff + poly. Turn off poly but leave diff
    // on — rule stays visible (AND semantics require BOTH off to hide).
    let s =
        Visibility.empty
        |> Visibility.setDrcVisibleLayer (66, 20) false  // poly off
    let v = mkViolation "poly.7" (66, 20)
    Filter.keepViolation s v |> should equal true

[<Fact>]
let ``keepViolation hides a two-layer rule only when BOTH layers are OFF`` () =
    let s =
        Visibility.empty
        |> Visibility.setDrcVisibleLayer (66, 20) false  // poly off
        |> Visibility.setDrcVisibleLayer (65, 20) false  // diff off
    let v = mkViolation "poly.7" (66, 20)
    Filter.keepViolation s v |> should equal false

[<Fact>]
let ``keepViolation hides a three-layer rule only when all three are OFF`` () =
    let s =
        Visibility.empty
        |> Visibility.setDrcVisibleLayer (93, 44) false  // nsdm off
        |> Visibility.setDrcVisibleLayer (65, 20) false  // diff off
    // Implant rule needs nsdm + diff + nwell all off to hide.
    // With nwell still on, it stays visible.
    // Note: rule name has a slash — `diff/tap.9`, not `difftap.9`.
    let v = mkViolation "diff/tap.9" (65, 20)
    Filter.keepViolation s v |> should equal true
    let s2 = s |> Visibility.setDrcVisibleLayer (64, 20) false  // nwell off
    Filter.keepViolation s2 v |> should equal false

[<Fact>]
let ``keepViolation falls back to primary-layer gating for unknown rules`` () =
    let s =
        Visibility.empty
        |> Visibility.setDrcVisibleLayer (88, 99) false
    let v = mkViolation "custom.from.yaml" (88, 99)
    Filter.keepViolation s v |> should equal false
    // Other layer's OFF doesn't affect this unknown-rule violation.
    let s2 =
        Visibility.empty
        |> Visibility.setDrcVisibleLayer (68, 20) false
    Filter.keepViolation s2 v |> should equal true

// --- filterArray --------------------------------------------------------

[<Fact>]
let ``filterArray returns the input array verbatim when all layers are ON`` () =
    let s = Visibility.empty
    let arr =
        [|
            mkViolation "met1.1" (68, 20)
            mkViolation "met2.2" (69, 20)
            mkViolation "poly.7" (66, 20)
        |]
    Filter.filterArray s arr |> should equal arr

[<Fact>]
let ``filterArray drops only the violations whose layers are all OFF`` () =
    let s =
        Visibility.empty
        |> Visibility.setDrcVisibleLayer (68, 20) false  // met1 off
    let m1 = mkViolation "met1.1" (68, 20)   // hidden (only met1)
    let m2 = mkViolation "met2.2" (69, 20)   // kept (met2 still on)
    let p7 = mkViolation "poly.7" (66, 20)   // kept (poly + diff both on)
    let arr = [| m1; m2; p7 |]
    let kept = Filter.filterArray s arr
    kept |> should equal [| m2; p7 |]

[<Fact>]
let ``filterArray on an empty array returns empty`` () =
    let s = Visibility.empty
    Filter.filterArray s [||] |> Array.isEmpty |> should equal true
