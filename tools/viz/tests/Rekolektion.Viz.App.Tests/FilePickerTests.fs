module Rekolektion.Viz.App.Tests.FilePickerTests

open FsUnit.Xunit
open Xunit
open Rekolektion.Viz.App.View

[<Fact>]
let ``open picker layout files include rkt`` () =
    let choices = FilePickers.openLayoutFileTypes () |> Seq.toList
    let patterns =
        choices
        |> List.collect (fun ft -> ft.Patterns |> Seq.toList)

    patterns |> should contain "*.rkt"

[<Fact>]
let ``open picker keeps existing layout formats`` () =
    let choices = FilePickers.openLayoutFileTypes () |> Seq.toList
    let patterns =
        choices
        |> List.collect (fun ft -> ft.Patterns |> Seq.toList)

    patterns |> should contain "*.gds"
    patterns |> should contain "*.gds2"
    patterns |> should contain "*.mag"
