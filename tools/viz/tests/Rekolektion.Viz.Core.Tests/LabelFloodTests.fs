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

[<Fact>]
let ``top-level li1 RectEl containing a drn_R pin label is claimed by drn_R`` () =
    // Reproduces the bug from blc_trim_dac: a long top-level li1
    // rect spanning (3948..12930, 7273..7443) physically connects
    // two drn_R pin labels at (4033, 7358) and (12845, 7374).
    // After the user committed this wire, a subsequent walkaround
    // saw it as a foreign obstacle and returned noPath. Expect
    // LabelFlood to claim the rect for drn_R via the seed lookup.
    let li1     = Unknown (67, 20)
    let li1Pin  = Unknown (67, 5)
    let drnRRect =
        RectEl {
            Layer = li1
            X1 = 3948L; Y1 = 7273L; X2 = 12930L; Y2 = 7443L
            Net = None; Props = []; Comments = []
        }
    let drnRLabelLeft =
        LabelEl {
            Layer = li1Pin
            Text = "drn_R"
            Origin = { X = 4033L; Y = 7358L }
            Class = None; Props = []; Comments = []
            IsInternal = false; Kind = NetName
        }
    let drnRLabelRight =
        LabelEl {
            Layer = li1Pin
            Text = "drn_R"
            Origin = { X = 12845L; Y = 7374L }
            Class = None; Props = []; Comments = []
            IsInternal = false; Kind = NetName
        }
    let doc = docWith [ drnRRect; drnRLabelLeft; drnRLabelRight ]
    let nets = LabelFlood.derive doc
    nets.ContainsKey "drn_R" |> should equal true
    nets.["drn_R"].Polygons |> List.length |> should be (greaterThanOrEqualTo 1)
