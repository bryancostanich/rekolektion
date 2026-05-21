module Rekolektion.Viz.Core.Tests.RoutingSnapTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Routing

let private rect (x1, y1, x2, y2) (layer, dt) idx : FlatPolygon =
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
      SourceIndex = idx
      TopInstanceIndex = None }

let private label layer textType (x, y) text kind : FlatLabel =
    { Layer = layer
      TextType = textType
      Origin = { X = int64 x; Y = int64 y }
      Text = text
      Kind = kind }

// --- buildTargets -------------------------------------------------------

[<Fact>]
let ``buildTargets emits one target per labeled pin polygon`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20) 0     // met1 pin patch #0
        rect (500L, 0L, 700L, 200L) (68, 20) 1   // met1 pin patch #1
    |]
    let labels = [|
        // Label sits inside polygon 0 → snap target at its centroid (100, 100).
        label 68 5 (100L, 100L) "BL_3" NetName
        // Label sits inside polygon 1 → centroid (600, 100).
        label 68 5 (600L, 100L) "BL_4" NetName
    |]
    let targets = Snap.buildTargets labels polys
    targets.Length |> should equal 2
    targets.[0].X |> should equal 100L
    targets.[0].Y |> should equal 100L
    targets.[0].Net |> should equal "BL_3"
    targets.[1].X |> should equal 600L
    targets.[1].Y |> should equal 100L

[<Fact>]
let ``buildTargets returns the polygon center even when the label is at the polygon edge`` () =
    // Generated labels are usually centered, but Magic-extracted
    // labels can land at a polygon corner. Snap target should still
    // be the centroid so wires connect at the geometric middle.
    let polys = [| rect (0L, 0L, 1000L, 1000L) (68, 20) 0 |]
    let labels = [| label 68 5 (0L, 0L) "VPWR" NetName |]   // origin at corner
    let targets = Snap.buildTargets labels polys
    targets.[0].X |> should equal 500L
    targets.[0].Y |> should equal 500L

[<Fact>]
let ``buildTargets ignores DeviceTerminal labels (not user nets)`` () =
    let polys = [| rect (0L, 0L, 200L, 200L) (68, 20) 0 |]
    let labels = [|
        label 68 5 (100L, 100L) "G" DeviceTerminal   // FET gate annotation
    |]
    Snap.buildTargets labels polys |> should be Empty

[<Fact>]
let ``buildTargets ignores labels whose origin falls in no same-layer polygon`` () =
    let polys = [| rect (0L, 0L, 200L, 200L) (68, 20) 0 |]
    // Label is on met1 but origin (500,500) misses the only met1 poly.
    let labels = [| label 68 5 (500L, 500L) "BL" NetName |]
    Snap.buildTargets labels polys |> should be Empty

[<Fact>]
let ``buildTargets does NOT match labels to polygons on a different layer`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20) 0     // met1 at origin
    |]
    // Label is on met2 (layer 69) but origin sits over the met1 poly.
    let labels = [| label 69 5 (100L, 100L) "BL" NetName |]
    Snap.buildTargets labels polys |> should be Empty

// --- nearest -----------------------------------------------------------

let private mkTarget x y net : Snap.SnapTarget = {
    X = int64 x; Y = int64 y
    Net = net; Layer = 68; DataType = 20
    Source = "test", 0
}

[<Fact>]
let ``nearest returns None when no target is within radius`` () =
    let targets = [| mkTarget 0 0 "A"; mkTarget 1000 1000 "B" |]
    Snap.nearest targets (5000L, 5000L) 100L
    |> should equal (None : Snap.SnapTarget option)

[<Fact>]
let ``nearest picks the closest target inside the radius`` () =
    let targets = [|
        mkTarget 0 0 "A"
        mkTarget 100 100 "B"
        mkTarget 200 200 "C"
    |]
    // Cursor at (120,120). Distances: A=√28800, B=√800, C=√12800.
    let pick = Snap.nearest targets (120L, 120L) 1000L
    pick |> Option.map (fun t -> t.Net) |> should equal (Some "B")

[<Fact>]
let ``nearest with empty target list returns None`` () =
    Snap.nearest [||] (0L, 0L) 1000L
    |> should equal (None : Snap.SnapTarget option)

[<Fact>]
let ``nearest is inclusive at the radius boundary`` () =
    let targets = [| mkTarget 1000 0 "edge" |]
    // Cursor at (0,0), target at (1000,0) → distance exactly 1000.
    Snap.nearest targets (0L, 0L) 1000L
    |> Option.isSome |> should equal true
