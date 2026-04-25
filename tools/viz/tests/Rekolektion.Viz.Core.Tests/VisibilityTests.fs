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
let ``highlightNet sets HighlightNet and dims others`` () =
    let s = Visibility.empty |> Visibility.highlightNet (Some "BL_3")
    s.HighlightNet |> should equal (Some "BL_3")
    Visibility.isNetDimmed s "VPWR" |> should equal true
    Visibility.isNetDimmed s "BL_3" |> should equal false

[<Fact>]
let ``isolateBlock hides all other blocks`` () =
    let s = Visibility.empty |> Visibility.isolateBlock (Some "sram_array")
    Visibility.isBlockVisible s "sram_array" |> should equal true
    Visibility.isBlockVisible s "row_decoder" |> should equal false
