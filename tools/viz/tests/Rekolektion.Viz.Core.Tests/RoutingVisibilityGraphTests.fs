module Rekolektion.Viz.Core.Tests.RoutingVisibilityGraphTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Routing.VisibilityGraph

// ---- Helpers ----------------------------------------------------

let private p (x : int) (y : int) : Pt = { X = int64 x; Y = int64 y }

/// Rectangle as a FlatPolygon on li1 — layer doesn't matter for the
/// visibility graph; the obstacle pipeline already filtered by layer.
let private rect (x0 : int) (y0 : int) (x1 : int) (y1 : int) : FlatPolygon =
    { Layer = 67
      DataType = 20
      Points =
        [| { X = int64 x0; Y = int64 y0 }
           { X = int64 x1; Y = int64 y0 }
           { X = int64 x1; Y = int64 y1 }
           { X = int64 x0; Y = int64 y1 }
           { X = int64 x0; Y = int64 y0 } |]
      SourceStructure = "test"
      SourceIndex = 0
      TopInstanceIndex = None
      Net = None }

// ---- build ------------------------------------------------------

[<Fact>]
let ``empty obstacle set builds an empty graph`` () =
    let g = build 0L [||]
    g.Nodes |> should be Empty
    g.Adjacency |> Array.length |> should equal 0

[<Fact>]
let ``one obstacle produces 4 corner nodes (no clearance)`` () =
    let g = build 0L [| rect 100 100 200 200 |]
    g.Nodes |> Array.length |> should equal 4
    // The four corners of the bbox should all be present.
    let corners =
        [ p 100 100; p 200 100; p 100 200; p 200 200 ]
        |> Set.ofList
    g.Nodes |> Set.ofArray |> should equal corners

[<Fact>]
let ``clearance expands obstacle corners outward`` () =
    let g = build 10L [| rect 100 100 200 200 |]
    // Corners should be at the obstacle bbox expanded by 10 on each
    // side: (90,90), (210,90), (90,210), (210,210).
    let expected =
        [ p 90 90; p 210 90; p 90 210; p 210 210 ]
        |> Set.ofList
    g.Nodes |> Set.ofArray |> should equal expected

// ---- shortestPath — trivial cases ------------------------------

[<Fact>]
let ``no obstacles: shortest path is just start and goal`` () =
    let g = build 0L [||]
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g (p 0 0) (p 100 100)
    path.IsSome |> should equal true
    path.Value |> List.head |> should equal (p 0 0)
    path.Value |> List.last |> should equal (p 100 100)

[<Fact>]
let ``start equals goal: path has the single point`` () =
    let g = build 0L [||]
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g (p 50 50) (p 50 50)
    path.IsSome |> should equal true
    // Start and goal are two distinct slots in the augmented graph
    // even when they share coordinates; the path lists both.
    path.Value |> List.head |> should equal (p 50 50)
    path.Value |> List.last |> should equal (p 50 50)

// ---- shortestPath — walk-around --------------------------------

[<Fact>]
let ``single obstacle in the direct path forces a detour`` () =
    // Obstacle blocks the (0,50) → (200,50) horizontal line. Both
    // L-shapes (H-first, V-first) cut through the obstacle's
    // interior. The path must turn at one of the expanded corners.
    let obs = rect 50 0 150 100
    let g = build 0L [| obs |]
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g (p 0 50) (p 200 50)
    path.IsSome |> should equal true
    // Detour: route to a corner of the obstacle and out the other
    // side. With clearance = 0, corners are at the obstacle's own
    // bbox: (50,0), (150,0), (50,100), (150,100).
    // Either (0,50) → corner → (200,50) or via two corners.
    path.Value.Length |> should be (greaterThanOrEqualTo 3)
    path.Value |> List.head |> should equal (p 0 50)
    path.Value |> List.last |> should equal (p 200 50)
    // No node in the path should sit strictly inside the obstacle.
    for pt in path.Value do
        let insideX = pt.X > 50L && pt.X < 150L
        let insideY = pt.Y > 0L  && pt.Y < 100L
        (insideX && insideY) |> should equal false

[<Fact>]
let ``clearance pushes the detour outside the obstacle by the margin`` () =
    let obs = rect 50 0 150 100
    let g = build 5L [| obs |]
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g (p 0 50) (p 200 50)
    path.IsSome |> should equal true
    // No corner-node on the path should sit inside the EXPANDED
    // bbox (45..155, -5..105). The endpoints (0,50) and (200,50)
    // are outside it; intermediate corners are on its boundary.
    for pt in path.Value do
        let insideX = pt.X > 45L && pt.X < 155L
        let insideY = pt.Y > -5L && pt.Y < 105L
        (insideX && insideY) |> should equal false

[<Fact>]
let ``two parallel obstacles force routing through the channel between them`` () =
    // Two horizontal bars at y=[0..40] and y=[60..100]. A wire from
    // (50,50) → (50, 200) wants to go straight up; the channel at
    // y ∈ (40, 60) plus the open region above y=100 should let it
    // through without forcing a wide detour.
    let g = build 0L [| rect 0 0 100 40; rect 0 60 100 100 |]
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g (p 50 50) (p 50 200)
    path.IsSome |> should equal true
    path.Value |> List.head |> should equal (p 50 50)
    path.Value |> List.last |> should equal (p 50 200)

// ---- shortestPath — failure modes ------------------------------

[<Fact>]
let ``goal strictly inside an obstacle interior returns no path`` () =
    // Box covers (50..150, 50..150). Goal at (100, 100) is in the
    // interior — no corner is manhattan-visible to it without
    // crossing the obstacle.
    let obs = rect 50 50 150 150
    let g = build 0L [| obs |]
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g (p 0 0) (p 100 100)
    path |> should equal (None : Pt list option)

[<Fact>]
let ``both endpoints in same obstacle's expanded margin (not interior) finds a path`` () =
    // Foreign obstacle (100..200, 100..200) with clearance=20.
    // Expanded bbox (80..220, 80..220), original (100..200, 100..200).
    // Place start in left margin, goal in right margin — both inside
    // the expanded bbox but outside the original interior. Endpoint
    // exemption should let the search exit the margin and find a
    // path. Reproduces the user's noPath complaint when both start
    // and cursor snap into the clearance margin of the same foreign
    // obstacle.
    let g = build 20L [| rect 100 100 200 200 |]
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g (p 90 150) (p 210 150)
    path.IsSome |> should equal true

[<Fact>]
let ``both endpoints in same thin-strip obstacle margin finds a path`` () =
    // Thin horizontal strip (0..400, 100..120), clearance=50.
    // Expanded bbox (-50..450, 50..170), original (0..400, 100..120).
    // Start (50, 80) and goal (350, 80) sit above the strip but inside
    // the expanded bbox. Both inside expanded, NEITHER inside original
    // interior. Mirrors the real-macro shape where horizontal li1 rails
    // expand vertically into the route's Y band.
    let g = build 50L [| rect 0 100 400 120 |]
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g (p 50 80) (p 350 80)
    path.IsSome |> should equal true

[<Fact>]
let ``shortestPath fast-fails when goal is strictly inside an obstacle's silicon`` () =
    // Perf regression guard. Pre-fix: when the cursor/goal sat
    // strictly inside a foreign obstacle's ORIGINAL silicon, A*
    // had to exhaust the ENTIRE graph before the "endpoint
    // strictly inside" safety check fired. On d13_mux (17K
    // nodes, 5.6K obstacles) that was ~430 ms per call, and
    // `routeAdaptive` retried 3× → 1.2–1.8 s wasted per cursor
    // frame whenever the user dragged through a rail.
    //
    // With the fast-fail moved to the top of shortestPath, the
    // call returns None in microseconds — no graph walk at all.
    //
    // Synthetic graph: 200-obstacle grid produces a few thousand
    // nodes. Pre-fix this test would take ~50 ms; post-fix it
    // takes <2 ms. Budget 20 ms tolerates parallel-test load.
    let obstacles =
        [| for r in 0 .. 9 do
             for c in 0 .. 19 do
                 yield rect (c * 100) (r * 100) (c * 100 + 50) (r * 100 + 50) |]
    let g = build 10L obstacles
    // Goal at (525, 525) sits strictly inside obstacle (500,500)-(550,550)
    // after the original-silicon shrink-back.
    let goalInside = p 525 525
    let start = p 0 0
    // Warm up JIT
    let _ = shortestPath System.Threading.CancellationToken.None NoPreference g start goalInside
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let path = shortestPath System.Threading.CancellationToken.None NoPreference g start goalInside
    sw.Stop()
    path |> should equal (None : Pt list option)
    Assert.True (sw.ElapsedMilliseconds < 20L,
        sprintf "fast-fail for goal-inside-obstacle took %d ms on a %d-node graph (budget 20)"
            sw.ElapsedMilliseconds g.Nodes.Length)
