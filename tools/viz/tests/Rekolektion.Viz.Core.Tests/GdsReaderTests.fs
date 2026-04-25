module Rekolektion.Viz.Core.Tests.GdsReaderTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds

let private fixturePath name =
    Path.Combine(System.AppContext.BaseDirectory, "testdata", name)

[<Fact>]
let ``Reader.readGds parses bitcell_lr fixture`` () =
    let lib = Reader.readGds (fixturePath "bitcell_lr.gds")
    lib.Name |> should not' (equal "")
    lib.Structures |> List.isEmpty |> should equal false
    lib.UserUnitsPerDbUnit |> should be (greaterThan 0.0)

[<Fact>]
let ``Reader.readGds bitcell_lr has at least one boundary`` () =
    let lib = Reader.readGds (fixturePath "bitcell_lr.gds")
    let total =
        lib.Structures
        |> List.sumBy (fun s ->
            s.Elements
            |> List.filter (function Types.Boundary _ -> true | _ -> false)
            |> List.length)
    total |> should be (greaterThan 0)
