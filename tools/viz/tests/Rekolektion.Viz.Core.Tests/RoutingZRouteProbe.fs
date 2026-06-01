module Rekolektion.Viz.Core.Tests.RoutingZRouteProbe

// PROBE: reproduces the "Z route" complaint from opamp_lowv_buffer
// 2026-05-30. Walkaround returned a 4-node path (anchor + 2 Steiner
// corners + cursor) producing a Z-shape commit, even though the
// direct VFirst L is clear of every obstacle.
//
// Obstacle list captured from the live viz log (walkaround event
// at 2026-05-31T05:07:22.575): 9 foreign li1 bars + 2 same-net
// bboxes (excluded — search treats them as ours). Anchor + cursor
// also from that log.
//
// Hypothesis: the steinerDiscount tiebreaker (VisibilityGraph.fs
// ~line 372) is making the Steiner-rich detour cheaper than the
// equal-Manhattan direct edge. With the discount, the 3-edge
// Steiner path costs (manhattan - 3); the 1-edge direct path
// costs manhattan. Direct wins on ties only without the discount.
//
// Expected pre-fix: search returns 4-node Steiner detour.
// Expected post-fix (drop discount): search returns 2-node direct
// edge, withBends adds 1 bend, final path 3 nodes (clean L).

open Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Routing.VisibilityGraph

let private p (x : int64) (y : int64) : Pt = { X = x; Y = y }

let private rect (x0 : int64) (y0 : int64) (x1 : int64) (y1 : int64) : FlatPolygon =
    { Layer = 67; DataType = 20
      Points =
        [| { X = x0; Y = y0 }; { X = x1; Y = y0 }
           { X = x1; Y = y1 }; { X = x0; Y = y1 }
           { X = x0; Y = y0 } |]
      SourceStructure = "probe"; SourceIndex = 0
      TopInstanceIndex = None }

[<Fact>]
let ``PROBE: opamp_lowv_buffer Z route reproduces`` () =
    // 9 foreign obstacles from the log (already expanded by clearance
    // when reported; pass clearance=0 to reuse as-is).
    let obstacles =
        [| rect 1465L -14699L 1635L -9659L
           rect 3765L -14699L 3935L -9659L
           rect 4621L -11419L 14661L -11249L
           rect 4621L -12209L 14661L -12039L
           rect 4621L -12999L 14661L -12829L
           rect 4621L -13789L 14661L -13619L
           rect 4621L -14579L 14661L -14409L
           rect 4965L -16404L 7125L -16054L
           rect 0L    -16565L 4000L -16145L |]
    let g = build 0L obstacles
    let anchor = p 2560L -12179L
    let cursor = p 5398L -15461L
    // NoPreference — neither axis dominates by 2x in this scenario.
    let path =
        shortestPath
            System.Threading.CancellationToken.None
            NoPreference g anchor cursor
    // Path SHOULD be 3 nodes (anchor + bend + cursor — clean
    // single L). With the steinerDiscount bug, it returns 4 nodes
    // (anchor + 2 Steiner corners + cursor — Z shape).
    Assert.True(path.IsSome, "expected path to exist")
    let nodes = path.Value
    // Dump path nodes so probe output diagnoses pass vs fail.
    let dump =
        nodes
        |> List.map (fun pt -> sprintf "(%d,%d)" pt.X pt.Y)
        |> String.concat " → "
    // Regression guard (post-fix): the direct-edge short-circuit
    // in shortestPath returns the 3-node clean L. Pre-fix this
    // returned a 4-node Steiner-Z because the steinerDiscount
    // tiebreaker preferred the detour even when direct was clear.
    Assert.Equal(3, nodes.Length)
    Assert.Equal(anchor, List.head nodes)
    Assert.Equal(cursor, List.last nodes)
    // The bend must be at one of the two valid L corners.
    let bend = nodes.[1]
    let vFirstBend = { X = anchor.X; Y = cursor.Y }
    let hFirstBend = { X = cursor.X; Y = anchor.Y }
    Assert.True(
        bend = vFirstBend || bend = hFirstBend,
        sprintf "bend at (%d,%d) is neither VFirst (%d,%d) nor HFirst (%d,%d). Path: %s"
            bend.X bend.Y vFirstBend.X vFirstBend.Y hFirstBend.X hFirstBend.Y dump)

// ---- short-circuit bend safety ---------------------------------
//
// REGRESSION GUARD for the met1 "wire through obstacles" bug
// 2026-05-30: the first short-circuit implementation picked the
// bend by `preferred` posture alone, without verifying THAT L was
// clear. When the caller had a locked posture and only the OPPOSITE
// L was clear, the bend landed on the blocked posture → wire ran
// straight through obstacles, zero collision avoidance.
//
// The fix verifies hFirstClear / vFirstClear separately and falls
// back to the clear posture even when `preferred` says otherwise.

[<Fact>]
let ``PROBE: only-VFirst-clear forces VFirst bend even when PreferHFirst`` () =
    // Obstacle on the HFirst L's horizontal leg at Y=0 (X 20..50).
    // HFirst would bend at (100, 0) and run through the obstacle.
    // VFirst bends at (0, 100) and is clear. Caller passes
    // PreferHFirst — pre-fix the short-circuit honoured this and
    // returned the blocked HFirst bend.
    let obs = rect 20L -5L 50L 5L
    let g = build 0L [| obs |]
    let anchor = p 0L 0L
    let cursor = p 100L 100L
    let path =
        shortestPath
            System.Threading.CancellationToken.None
            PreferHFirst g anchor cursor
    Assert.True(path.IsSome, "expected path to exist")
    let nodes = path.Value
    let dump =
        nodes
        |> List.map (fun pt -> sprintf "(%d,%d)" pt.X pt.Y)
        |> String.concat " → "
    Assert.Equal(3, nodes.Length)
    let bend = nodes.[1]
    let vFirstBend = { X = anchor.X; Y = cursor.Y }   // (0, 100)
    let hFirstBend = { X = cursor.X; Y = anchor.Y }   // (100, 0)
    Assert.True(
        (bend = vFirstBend),
        sprintf "bend at (%d,%d) ≠ VFirst (%d,%d). HFirst (%d,%d) is blocked. Path: %s"
            bend.X bend.Y vFirstBend.X vFirstBend.Y hFirstBend.X hFirstBend.Y dump)

[<Fact>]
let ``PROBE: only-HFirst-clear forces HFirst bend even when PreferVFirst`` () =
    // Mirror case: obstacle on VFirst's vertical leg at X=0
    // (Y 20..50). VFirst would bend at (0, 100) and run through
    // the obstacle. HFirst bends at (100, 0) and is clear.
    let obs = rect -5L 20L 5L 50L
    let g = build 0L [| obs |]
    let anchor = p 0L 0L
    let cursor = p 100L 100L
    let path =
        shortestPath
            System.Threading.CancellationToken.None
            PreferVFirst g anchor cursor
    Assert.True(path.IsSome, "expected path to exist")
    let nodes = path.Value
    let dump =
        nodes
        |> List.map (fun pt -> sprintf "(%d,%d)" pt.X pt.Y)
        |> String.concat " → "
    Assert.Equal(3, nodes.Length)
    let bend = nodes.[1]
    let vFirstBend = { X = anchor.X; Y = cursor.Y }
    let hFirstBend = { X = cursor.X; Y = anchor.Y }
    Assert.True(
        (bend = hFirstBend),
        sprintf "bend at (%d,%d) ≠ HFirst (%d,%d). VFirst (%d,%d) is blocked. Path: %s"
            bend.X bend.Y hFirstBend.X hFirstBend.Y vFirstBend.X vFirstBend.Y dump)

[<Fact>]
let ``PROBE: neither-L-clear falls through to A for Steiner detour`` () =
    // Single small obstacle that blocks BOTH HFirst's vertical leg
    // (at X=100) AND VFirst's horizontal leg (at Y=100). With both
    // L's blocked, the short-circuit must decline and A* must run,
    // returning a Steiner detour path with ≥4 nodes (anchor + ≥2
    // corners + cursor).
    let obsBlockH = rect 95L 20L 105L 50L   // blocks HFirst vertical at X=100
    let obsBlockV = rect 20L 95L 50L 105L   // blocks VFirst horizontal at Y=100
    let g = build 0L [| obsBlockH; obsBlockV |]
    let anchor = p 0L 0L
    let cursor = p 100L 100L
    let path =
        shortestPath
            System.Threading.CancellationToken.None
            NoPreference g anchor cursor
    Assert.True(path.IsSome, "expected A* to find a detour")
    let nodes = path.Value
    let dump =
        nodes
        |> List.map (fun pt -> sprintf "(%d,%d)" pt.X pt.Y)
        |> String.concat " → "
    Assert.True(
        nodes.Length >= 4,
        sprintf "expected ≥4-node Steiner detour, got %d-node path: %s" nodes.Length dump)
