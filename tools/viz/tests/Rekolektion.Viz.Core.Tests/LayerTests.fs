module Rekolektion.Viz.Core.Tests.LayerTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Layout

[<Fact>]
let ``Layer.bySky130Number returns met2 for layer 69`` () =
    match Layer.bySky130Number 69 20 with
    | Some l -> l.Name |> should equal "met2"
    | None -> failwith "expected met2"

[<Fact>]
let ``Layer.bySky130Number returns li1 for layer 67 dt 20`` () =
    match Layer.bySky130Number 67 20 with
    | Some l -> l.Name |> should equal "li1"
    | None -> failwith "expected li1"

[<Fact>]
let ``Layer.allDrawing returns at least 8 entries`` () =
    Layer.allDrawing |> List.length |> should be (greaterThanOrEqualTo 8)

[<Fact>]
let ``Layer stack Z increases monotonically for met layers`` () =
    // Filter to just met1..met5 to validate the metal stack ordering.
    let names = ["met1"; "met2"; "met3"; "met4"; "met5"]
    let mets =
        Layer.allDrawing
        |> List.filter (fun l -> List.contains l.Name names)
        |> List.sortBy (fun l -> List.findIndex ((=) l.Name) names)
    let zs = mets |> List.map (fun l -> l.StackZ)
    zs |> should equal (List.sort zs)
