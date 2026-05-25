module Rekolektion.Viz.Core.Tests.RoutingWalkAroundTests

// End-to-end through Obstacles + VisibilityGraph + WalkAround:
// builds a small synthetic FET-wall scene, drops a wire on li1,
// and confirms the path lands on the right side of every foreign-
// net feature.

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.Core.Routing
open Rekolektion.Viz.Core.Routing.VisibilityGraph

// ---- Helpers ----------------------------------------------------

let private p (x : int) (y : int) : Pt = { X = int64 x; Y = int64 y }

let private rect (cell : string) (layer : int) (dt : int) (idx : int)
                 (x0 : int) (y0 : int) (x1 : int) (y1 : int) : FlatPolygon =
    { Layer = layer
      DataType = dt
      Points =
        [| { X = int64 x0; Y = int64 y0 }
           { X = int64 x1; Y = int64 y0 }
           { X = int64 x1; Y = int64 y1 }
           { X = int64 x0; Y = int64 y1 }
           { X = int64 x0; Y = int64 y0 } |]
      SourceStructure = cell
      SourceIndex = idx
      TopInstanceIndex = None }

let private li1Layer  : Obstacles.LayerKey = { Number = 67; DataType = 20 }
let private met1Layer : Obstacles.LayerKey = { Number = 68; DataType = 20 }

let private pref (cell : string) (layer : int) (dt : int) (idx : int) : PolygonRef =
    { Structure = cell; Layer = layer; DataType = dt; Index = idx
      TopInstanceIndex = None }

let private mkEntry (name : string) (cls : NetClass)
                    (polys : PolygonRef list) : NetEntry =
    // Tests use synthetic obstacle sets without contact-flood
    // ambiguity, so the polygon list IS the seed list.
    { Name = name; Class = cls; Polygons = polys; SeedPolygons = polys
      DirectLabelPolys = polys }

let private buildKey layer startNet clearance flat nets : WalkAround.BuildKey =
    { Layer = layer
      StartNet = startNet
      Clearance = clearance
      FlatPolyRef = flat
      NetMapRef = nets }

/// Whole-cell graph build, the old `WalkAround.buildGraph` shape
/// for tests. Wraps `buildGraphInRegion` with a region that easily
/// covers any synthetic obstacle scene used by the test suite.
let private buildGraphAll (key : WalkAround.BuildKey) : VisibilityGraph.Prebuilt =
    let region : Obstacles.Region =
        { XMin = -1_000_000L; YMin = -1_000_000L
          XMax =  1_000_000L; YMax =  1_000_000L }
    WalkAround.buildGraphInRegion key region

// ---- Trivial pass-through --------------------------------------

[<Fact>]
let ``empty cell: straight path from start to cursor`` () =
    let key = buildKey li1Layer "VGND" 0L [||] Map.empty
    let g = buildGraphAll key
    let path = WalkAround.route VisibilityGraph.NoPreference g (p 0 0) (p 100 100)
    path.IsSome |> should equal true
    path.Value |> List.head |> should equal (p 0 0)
    path.Value |> List.last |> should equal (p 100 100)

// ---- The FET-wall scene ----------------------------------------

[<Fact>]
let ``li1 wire dodges a foreign-net licon directly in its path`` () =
    // Wire wants to go from (0, 50) → (300, 50) on li1, but a
    // foreign-net licon sits at (100..200, 0..100) blocking the
    // direct route. Walk-around must detour around the licon.
    let foreignLicon = rect "nfet" 66 44 0  100 0 200 100
    let nets =
        Map.ofList [
            "BL", mkEntry "BL" Signal [ pref "nfet" 66 44 0 ]
        ]
    let key = buildKey li1Layer "VGND" 0L [| foreignLicon |] nets
    let g = buildGraphAll key
    let path = WalkAround.route VisibilityGraph.NoPreference g (p 0 50) (p 300 50)
    path.IsSome |> should equal true
    path.Value |> List.head |> should equal (p 0 50)
    path.Value |> List.last |> should equal (p 300 50)
    // No intermediate node inside the licon interior.
    for pt in path.Value do
        let insideX = pt.X > 100L && pt.X < 200L
        let insideY = pt.Y > 0L   && pt.Y < 100L
        (insideX && insideY) |> should equal false

[<Fact>]
let ``li1 wire passes through same-net licons unaffected`` () =
    // Same scene, but the licon belongs to VGND (our net) — it's
    // pass-through, not an obstacle. The path should be the
    // straight two-point line.
    let ourLicon = rect "nfet" 66 44 0  100 0 200 100
    let nets =
        Map.ofList [
            "VGND", mkEntry "VGND" Ground [ pref "nfet" 66 44 0 ]
        ]
    let key = buildKey li1Layer "VGND" 0L [| ourLicon |] nets
    let g = buildGraphAll key
    let path = WalkAround.route VisibilityGraph.NoPreference g (p 0 50) (p 300 50)
    path.IsSome |> should equal true
    path.Value.Length |> should equal 2

[<Fact>]
let ``FET-wall escape: route threads the gap between adjacent foreign licons`` () =
    // Three licons in a row at y=[0..100], spaced with 50-unit
    // gaps. The wire starts at (75, 50) — the centre of the first
    // gap — and wants to go to (75, 300), straight up out of the
    // wall. The path should run straight up the gap.
    let l0 = rect "nfet" 66 44 0    0 0  50 100   // far left
    let l1 = rect "nfet" 66 44 1  100 0 150 100   // middle
    let l2 = rect "nfet" 66 44 2  200 0 250 100   // far right
    let nets =
        Map.ofList [
            "BL",  mkEntry "BL" Signal [ pref "nfet" 66 44 0; pref "nfet" 66 44 2 ]
            "WL",  mkEntry "WL" Signal [ pref "nfet" 66 44 1 ]
        ]
    let key = buildKey li1Layer "VGND" 0L [| l0; l1; l2 |] nets
    let g = buildGraphAll key
    let path = WalkAround.route VisibilityGraph.NoPreference g (p 75 50) (p 75 300)
    path.IsSome |> should equal true
    // Path is straight up the gap — no horizontal jog needed.
    for pt in path.Value do
        // Gap between l0 (x≤50) and l1 (x≥100) is x ∈ (50, 100).
        pt.X |> should be (greaterThanOrEqualTo 50L)
        pt.X |> should be (lessThanOrEqualTo 100L)

[<Fact>]
let ``met1 wire dodges a foreign-net via1 in its path`` () =
    // Same shape as the li1 test, just on met1 with a via1 (68/44)
    // foreign-net obstacle. Confirms the bridge-layer logic
    // generalises.
    let foreignVia = rect "x" 68 44 0  100 0 200 100
    let nets =
        Map.ofList [
            "Foreign", mkEntry "Foreign" Signal [ pref "x" 68 44 0 ]
        ]
    let key = buildKey met1Layer "Ours" 0L [| foreignVia |] nets
    let g = buildGraphAll key
    let path = WalkAround.route VisibilityGraph.NoPreference g (p 0 50) (p 300 50)
    path.IsSome |> should equal true
    for pt in path.Value do
        let insideX = pt.X > 100L && pt.X < 200L
        let insideY = pt.Y > 0L   && pt.Y < 100L
        (insideX && insideY) |> should equal false

// ---- Failure mode ----------------------------------------------

[<Fact>]
let ``goal sitting inside a foreign-net polygon returns no path`` () =
    let foreign = rect "x" 67 20 0   50 50 150 150
    let nets =
        Map.ofList [
            "F", mkEntry "F" Signal [ pref "x" 67 20 0 ]
        ]
    let key = buildKey li1Layer "Ours" 0L [| foreign |] nets
    let g = buildGraphAll key
    let path = WalkAround.route VisibilityGraph.NoPreference g (p 0 0) (p 100 100)
    path |> should equal (None : Pt list option)
