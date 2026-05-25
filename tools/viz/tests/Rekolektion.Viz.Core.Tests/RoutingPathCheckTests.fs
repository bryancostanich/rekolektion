module Rekolektion.Viz.Core.Tests.RoutingPathCheckTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Routing.VisibilityGraph
open Rekolektion.Viz.Core.Routing.PathCheck

// ---- Helpers ----------------------------------------------------

let private p (x : int) (y : int) : Pt = { X = int64 x; Y = int64 y }

let private bbox (xMin : int) (yMin : int) (xMax : int) (yMax : int) : Bbox =
    { XMin = int64 xMin; YMin = int64 yMin
      XMax = int64 xMax; YMax = int64 yMax }

// ---- crossings: clean path -------------------------------------

[<Fact>]
let ``empty path: no crossings`` () =
    crossings [] [| bbox 0 0 100 100 |] |> should be Empty

[<Fact>]
let ``single point: no crossings`` () =
    crossings [ p 50 50 ] [| bbox 0 0 100 100 |] |> should be Empty

[<Fact>]
let ``no obstacles: no crossings even for a long path`` () =
    crossings [ p 0 0; p 100 0; p 100 100 ] [||] |> should be Empty

[<Fact>]
let ``H segment passing above an obstacle: no crossings`` () =
    // Obstacle at y 0..50, segment at y 100. Strict interior test:
    // 100 > 0 ✓ but 100 < 50 ✗ → no crossing.
    let path = [ p 0 100; p 200 100 ]
    crossings path [| bbox 50 0 150 50 |] |> should be Empty

[<Fact>]
let ``H segment exactly on an obstacle's edge: no crossing (strict)`` () =
    // Segment y = YMax of obstacle. Strict inequality means edge
    // contact is NOT a crossing — the wire is exactly at clearance,
    // which is legal.
    let path = [ p 0 50; p 200 50 ]
    crossings path [| bbox 50 0 150 50 |] |> should be Empty

// ---- crossings: violations -------------------------------------

[<Fact>]
let ``H segment piercing an obstacle: one crossing reported`` () =
    let path = [ p 0 25; p 200 25 ]
    let obs = [| bbox 50 0 150 50 |]
    let result = crossings path obs
    result |> List.length |> should equal 1
    let c = List.head result
    c.ObstacleIndex |> should equal 0
    c.Segment |> should equal (p 0 25, p 200 25)

[<Fact>]
let ``V segment piercing an obstacle: one crossing reported`` () =
    let path = [ p 100 0; p 100 200 ]
    crossings path [| bbox 50 50 150 150 |]
    |> List.length |> should equal 1

[<Fact>]
let ``two-segment path with bend through obstacle: H seg flagged`` () =
    // Obstacle squarely in the H-leg's path; V-leg is clear.
    let path = [ p 0 25; p 100 25; p 100 200 ]
    let result = crossings path [| bbox 25 0 75 50 |]
    result |> List.length |> should equal 1
    let c = List.head result
    c.Segment |> should equal (p 0 25, p 100 25)

[<Fact>]
let ``path passing through TWO separate obstacles reports both`` () =
    let path = [ p 0 25; p 400 25 ]
    let obs =
        [| bbox 50 0 150 50
           bbox 200 0 300 50 |]
    let result = crossings path obs
    result |> List.length |> should equal 2
    result |> List.map (fun c -> c.ObstacleIndex)
        |> should equal [ 0; 1 ]

[<Fact>]
let ``crossingCount mirrors crossings length`` () =
    let path = [ p 0 25; p 400 25 ]
    let obs =
        [| bbox 50 0 150 50
           bbox 200 0 300 50 |]
    crossingCount path obs |> should equal 2
    crossingCount [ p 0 100; p 400 100 ] obs |> should equal 0
