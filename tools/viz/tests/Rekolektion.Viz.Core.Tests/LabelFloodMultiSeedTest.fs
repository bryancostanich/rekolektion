module Rekolektion.Viz.Core.Tests.LabelFloodMultiSeedTest

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types

let private li1 : Layer = Named ("sky130", "li1")

let private mkRect x1 y1 x2 y2 : Rectangle = {
    Layer = li1
    X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
    Net = None
    Props = []
    Comments = []
    SubFormComments = Map.empty
}

let private mkLabel text x y : Label = {
    Layer = li1
    Text = text
    Origin = { X = x; Y = y }
    Class = None
    Props = []
    Comments = []
    SubFormComments = Map.empty
    IsInternal = false
    Kind = NetName
}

[<Fact>]
let ``multi-seed: label inside two overlapping same-layer polys claims both`` () =
    let small = mkRect 0L 0L 100L 100L         // small box at origin
    let wide  = mkRect 0L 0L 1000L 100L        // wide box covering small + more
    let lbl   = mkLabel "NET_X" 50L 50L         // label inside both
    let cell : Cell = {
        Name = "top"
        Meta = None
        Elements = [ RectEl small; RectEl wide; LabelEl lbl ]
        Comments = []
        SubFormComments = Map.empty
    }
    let doc =
        { emptyDocument with
            Cells = [ cell ]
            TopCell = Some "top" }
    let nets = Net.LabelFlood.derive doc
    let entry =
        match Map.tryFind "NET_X" nets with
        | Some e -> e
        | None -> failwith "NET_X not derived"
    // Both seed polys should be in Polygons.
    entry.Polygons.Length |> should equal 2
    // AND both should be in SeedPolygons (multi-seed marker).
    entry.SeedPolygons.Length |> should equal 2
