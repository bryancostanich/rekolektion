module Rekolektion.Viz.Core.Tests.DrcCheckTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Drc

let private rect (x1, y1, x2, y2) (layer, dt) : FlatPolygon =
    { Layer = layer
      DataType = dt
      Points = [|
        { X = int64 x1; Y = int64 y1 }
        { X = int64 x2; Y = int64 y1 }
        { X = int64 x2; Y = int64 y2 }
        { X = int64 x1; Y = int64 y2 }
        { X = int64 x1; Y = int64 y1 }
      |]
      SourceStructure = "test"
      SourceIndex = 0 }

// 1 nm/DBU library so DBU = nm and the SKY130 0.14 µm met1 spacing
// = 140 DBU. Synthetic cells use these scales to hit the spacing
// boundary cleanly.
let private lib1nm : Library = {
    Name = "test"
    UserUnitsPerDbUnit = 0.001
    DbUnitsInMeters = 1.0e-9
    Structures = []
}

[<Fact>]
let ``met1 spacing exactly at limit (140 nm) does not violate`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
        rect (340L, 0L, 540L, 200L) (68, 20)   // 140 nm gap
    |]
    let v = Check.check lib1nm polys
    v |> Array.filter (fun x -> x.Rule = "met1.spacing")
      |> Array.length
      |> should equal 0

[<Fact>]
let ``met1 spacing 1 nm under limit triggers a violation`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
        rect (339L, 0L, 539L, 200L) (68, 20)   // 139 nm gap
    |]
    let v = Check.check lib1nm polys
    v |> Array.exists (fun x -> x.Rule = "met1.spacing" && x.MeasuredDbu = 139L)
      |> should equal true

[<Fact>]
let ``met1 width below 140 nm triggers a width violation`` () =
    let polys = [| rect (0L, 0L, 100L, 200L) (68, 20) |]   // 100 nm wide
    let v = Check.check lib1nm polys
    v |> Array.exists (fun x -> x.Rule = "met1.width" && x.MeasuredDbu = 100L)
      |> should equal true

[<Fact>]
let ``unknown layer (datatype 99) produces no violations`` () =
    let polys = [|
        rect (0L, 0L, 100L, 100L) (68, 99)
        rect (105L, 0L, 200L, 100L) (68, 99)   // 5 nm gap
    |]
    let v = Check.check lib1nm polys
    v.Length |> should equal 0

[<Fact>]
let ``poly spacing 0.21 µm = 210 nm enforced`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (66, 20)
        rect (200L + 209L, 0L, 200L + 409L, 200L) (66, 20)
    |]
    let v = Check.check lib1nm polys
    v |> Array.exists (fun x -> x.Rule = "poly.spacing")
      |> should equal true

[<Fact>]
let ``different layers don't trigger same-layer spacing`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20)        // met1
        rect (210L, 0L, 410L, 200L) (69, 20)      // met2 — different layer
    |]
    let v = Check.check lib1nm polys
    v.Length |> should equal 0
