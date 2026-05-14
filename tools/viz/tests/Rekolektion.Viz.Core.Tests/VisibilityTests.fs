module Rekolektion.Viz.Core.Tests.VisibilityTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core

[<Fact>]
let ``empty ToggleState shows everything`` () =
    let s = Visibility.empty
    Visibility.isLayerVisible s (68, 20) |> should equal true
    Visibility.isNetVisible s "BL" |> should equal true
    Visibility.isBlockVisible s "sram_array" |> should equal true

[<Fact>]
let ``toggling layer off hides it`` () =
    let s = Visibility.empty |> Visibility.toggleLayer (68, 20) false
    Visibility.isLayerVisible s (68, 20) |> should equal false
    Visibility.isLayerVisible s (69, 20) |> should equal true

[<Fact>]
let ``toggleNetHighlight adds then removes net from highlighted set`` () =
    let s1 = Visibility.empty |> Visibility.toggleNetHighlight "BL_3"
    s1.HighlightedNets.Contains "BL_3" |> should equal true
    Visibility.isNetHighlighted s1 "BL_3" |> should equal true
    Visibility.isNetDimmed s1 "VPWR" |> should equal true
    Visibility.isNetDimmed s1 "BL_3" |> should equal false
    let s2 = s1 |> Visibility.toggleNetHighlight "BL_3"
    s2.HighlightedNets |> should equal (Set.empty : Set<string>)
    // Empty set = no dimming.
    Visibility.isNetDimmed s2 "VPWR" |> should equal false

[<Fact>]
let ``multi-net highlight: any highlighted net renders, others dim`` () =
    let s =
        Visibility.empty
        |> Visibility.toggleNetHighlight "BL_3"
        |> Visibility.toggleNetHighlight "WL_5"
    s.HighlightedNets |> should equal (Set.ofList ["BL_3"; "WL_5"])
    Visibility.isNetDimmed s "BL_3" |> should equal false
    Visibility.isNetDimmed s "WL_5" |> should equal false
    Visibility.isNetDimmed s "VPWR" |> should equal true

[<Fact>]
let ``setHighlightedNets replaces the set wholesale`` () =
    let s =
        Visibility.empty
        |> Visibility.toggleNetHighlight "old"
        |> Visibility.setHighlightedNets (Set.ofList ["BL_0"; "BL_1"])
    s.HighlightedNets |> should equal (Set.ofList ["BL_0"; "BL_1"])

[<Fact>]
let ``toggleNetRatline / setVisibleRatlines manage ratline set independently`` () =
    let s1 = Visibility.empty |> Visibility.toggleNetRatline "CLK"
    Visibility.isRatlineVisible s1 "CLK" |> should equal true
    // Ratline state must NOT affect highlight state.
    Visibility.isNetHighlighted s1 "CLK" |> should equal false
    s1.HighlightedNets |> should equal (Set.empty : Set<string>)
    let s2 = s1 |> Visibility.setVisibleRatlines (Set.ofList ["A"; "B"; "C"])
    s2.VisibleRatlines |> should equal (Set.ofList ["A"; "B"; "C"])
    // CLK was dropped by the wholesale replace.
    Visibility.isRatlineVisible s2 "CLK" |> should equal false

[<Fact>]
let ``highlight and ratline sets are independent`` () =
    let s =
        Visibility.empty
        |> Visibility.toggleNetHighlight "BL"
        |> Visibility.toggleNetRatline "WL"
    s.HighlightedNets |> should equal (Set.singleton "BL")
    s.VisibleRatlines |> should equal (Set.singleton "WL")
    // Cross-check: toggling one doesn't touch the other.
    let s2 = s |> Visibility.toggleNetHighlight "BL"
    s2.HighlightedNets |> should equal (Set.empty : Set<string>)
    s2.VisibleRatlines |> should equal (Set.singleton "WL")

[<Fact>]
let ``isolateBlock hides all other blocks`` () =
    let s = Visibility.empty |> Visibility.isolateBlock (Some "sram_array")
    Visibility.isBlockVisible s "sram_array" |> should equal true
    Visibility.isBlockVisible s "row_decoder" |> should equal false
