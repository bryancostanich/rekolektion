module Rekolektion.Viz.Core.Tests.LabelFloodTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Net

let private rect (x: int64) (y: int64) (w: int64) (h: int64) : Point list =
    [ { X = x;     Y = y     }
      { X = x + w; Y = y     }
      { X = x + w; Y = y + h }
      { X = x;     Y = y + h }
      { X = x;     Y = y     } ]

let private poly (pts: Point list) : Element =
    PolyEl {
        Layer = Named ("sky130", "met1")
        Points = pts
        Net = None
        Props = []
        Comments = []
    }

let private label (origin: Point) (text: string) : Element =
    LabelEl {
        Layer = Named ("sky130", "met1")
        Text = text
        Origin = origin
        Class = None
        Props = []
        Comments = []
        IsInternal = false
        Kind = NetName
    }

let private docWith (elements: Element list) : Document =
    { emptyDocument with
        Cells = [
            { Name = "top"; Meta = None; Elements = elements; Comments = [] }
        ] }

[<Fact>]
let ``label on a polygon names that polygon's net`` () =
    let doc =
        docWith [
            poly (rect 0L 0L 100L 50L)
            label { X = 50L; Y = 25L } "BL"
        ]
    let nets = LabelFlood.derive doc
    nets.ContainsKey "BL" |> should equal true
    nets.["BL"].Polygons |> List.length |> should equal 1

[<Fact>]
let ``label on overlapping polys connects both`` () =
    let doc =
        docWith [
            poly (rect 0L  0L 100L 50L)   // labeled
            poly (rect 80L 0L 100L 50L)   // overlaps first
            label { X = 10L; Y = 25L } "WL"
        ]
    let nets = LabelFlood.derive doc
    nets.["WL"].Polygons |> List.length |> should equal 2
