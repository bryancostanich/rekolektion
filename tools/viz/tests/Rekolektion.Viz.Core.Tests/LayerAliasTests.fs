module Rekolektion.Viz.Core.Tests.LayerAliasTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Layout

[<Fact>]
let ``known sky130_fd_pr_reram aliases translate to standard SKY130 keys`` () =
    LayerAlias.translate 6 0    |> should equal (65, 20)
    LayerAlias.translate 6 251  |> should equal (65, 20)
    LayerAlias.translate 7 0    |> should equal (66, 44)
    LayerAlias.translate 8 0    |> should equal (68, 20)
    LayerAlias.translate 8 251  |> should equal (68, 20)
    LayerAlias.translate 40 0   |> should equal (201, 20)

[<Fact>]
let ``standard SKY130 keys pass through unchanged`` () =
    LayerAlias.translate 65 20  |> should equal (65, 20)
    LayerAlias.translate 68 20  |> should equal (68, 20)
    LayerAlias.translate 201 20 |> should equal (201, 20)
    LayerAlias.translate 89 44  |> should equal (89, 44)

[<Fact>]
let ``normalize rewrites Boundary, Path, and Text layers`` () =
    let lib : Library = {
        Name = "x"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1e-9
        Structures = [
            { Name = "top"
              Elements = [
                Boundary { Layer = 6; DataType = 0; Points = [{X=0L;Y=0L}] }
                Path     { Layer = 8; DataType = 0; Width = 100; Points = [{X=0L;Y=0L}] }
                Text     { Layer = 6; TextType = 251; Origin = {X=0L;Y=0L}; Text = "VDD" }
              ] }
        ]
    }
    let normalized = LayerAlias.normalize lib
    let elems = normalized.Structures.Head.Elements
    match elems with
    | [Boundary b; Path p; Text t] ->
        b.Layer    |> should equal 65
        b.DataType |> should equal 20
        p.Layer    |> should equal 68
        p.DataType |> should equal 20
        t.Layer    |> should equal 65
        t.TextType |> should equal 20
    | _ -> failwith "expected three rewritten elements"

[<Fact>]
let ``normalize is identity when no aliased layers are used`` () =
    let lib : Library = {
        Name = "x"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1e-9
        Structures = [
            { Name = "top"
              Elements = [
                Boundary { Layer = 65; DataType = 20; Points = [{X=0L;Y=0L}] }
                Boundary { Layer = 68; DataType = 20; Points = [{X=0L;Y=0L}] }
              ] }
        ]
    }
    let normalized = LayerAlias.normalize lib
    let elems = normalized.Structures.Head.Elements
    elems.Length |> should equal 2
    match elems.[0] with
    | Boundary b -> b.Layer |> should equal 65
    | _ -> failwith "expected Boundary"
