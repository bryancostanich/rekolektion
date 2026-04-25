module Rekolektion.Viz.Core.Tests.LabelFloodTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Net

let private rect (x: int64) (y: int64) (w: int64) (h: int64) : Point list =
    [ { X = x;     Y = y     }
      { X = x + w; Y = y     }
      { X = x + w; Y = y + h }
      { X = x;     Y = y + h }
      { X = x;     Y = y     } ]

[<Fact>]
let ``label on a polygon names that polygon's net`` () =
    let lib = {
        Name = "x"; UserUnitsPerDbUnit = 0.001; DbUnitsInMeters = 1e-9
        Structures = [{
            Name = "top"
            Elements = [
                Boundary { Layer = 68; DataType = 20; Points = rect 0L 0L 100L 50L }
                Text     { Layer = 68; TextType = 5; Origin = { X = 50L; Y = 25L }; Text = "BL" }
            ]
        }]
    }
    let nets = LabelFlood.derive lib
    nets.ContainsKey "BL" |> should equal true
    nets.["BL"].Polygons |> List.length |> should equal 1

[<Fact>]
let ``label on overlapping polys connects both`` () =
    let lib = {
        Name = "x"; UserUnitsPerDbUnit = 0.001; DbUnitsInMeters = 1e-9
        Structures = [{
            Name = "top"
            Elements = [
                Boundary { Layer = 68; DataType = 20; Points = rect 0L  0L 100L 50L }  // labeled
                Boundary { Layer = 68; DataType = 20; Points = rect 80L 0L 100L 50L }  // overlaps first
                Text     { Layer = 68; TextType = 5; Origin = { X = 10L; Y = 25L }; Text = "WL" }
            ]
        }]
    }
    let nets = LabelFlood.derive lib
    nets.["WL"].Polygons |> List.length |> should equal 2
