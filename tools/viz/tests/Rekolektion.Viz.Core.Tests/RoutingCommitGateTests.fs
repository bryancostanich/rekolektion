module Rekolektion.Viz.Core.Tests.RoutingCommitGateTests

/// Commit-time validation gate: before writing any segment into the
/// document, the walk-around path must clear every expanded obstacle.
/// The silicon-killer bug (2026-05) was that an empty `Auto` list
/// produced a straight-line commit through foreign obstacles because
/// the BG walkaround hadn't completed yet and there was no check at
/// commit time.
///
/// These tests prove that:
///   • a straight-line path with empty Auto DOES cross obstacles,
///   • a proper walkaround path around the same obstacle does NOT,
///   • the validation logic is correct (no false positives).
///
/// IMPORTANT: These tests do NOT call Obstacles.obstacleSet or
/// Obstacles.buildNetIndex, because those populate global caches
/// (obstacleSetCache, indexCache) that, when overflowing, trigger
/// Clear() and evict entries from concurrent tests (notably
/// D13MuxPerfProbe). We use VisibilityGraph.build directly with
/// synthetic FlatPolygon arrays instead — same semantic, no cache
/// pollution.

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Routing
open Rekolektion.Viz.Core.Routing.VisibilityGraph
open Rekolektion.Viz.Core.Routing.PathCheck

let private p (x : int) (y : int) : Pt =
    { X = int64 x; Y = int64 y }

let private bbox (xMin : int) (yMin : int) (xMax : int) (yMax : int) : Bbox =
    { XMin = int64 xMin; YMin = int64 yMin
      XMax = int64 xMax; YMax = int64 yMax }

let private rect (x0 : int) (y0 : int) (x1 : int) (y1 : int) : FlatPolygon =
    { Layer = 66; DataType = 44   // licon layer (foreign to any li1 wire)
      Points =
        [| { X = int64 x0; Y = int64 y0 }
           { X = int64 x1; Y = int64 y0 }
           { X = int64 x1; Y = int64 y1 }
           { X = int64 x0; Y = int64 y1 }
           { X = int64 x0; Y = int64 y0 } |]
      SourceStructure = "nfet"
      SourceIndex = 0
      TopInstanceIndex = None
      Net = None }

/// Compute expanded-obstacle bboxes from a flat-poly array, matching
/// the clearance expansion VisibilityGraph.build applies. Same
/// expansion as the validation gate in commitRouteWith uses.
let private expandedBboxesOf
    (clearance : int64)
    (polys : FlatPolygon array)
    : Bbox array =
    polys
    |> Array.map (fun fp ->
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for pt in fp.Points do
            if pt.X < xMin then xMin <- pt.X
            if pt.X > xMax then xMax <- pt.X
            if pt.Y < yMin then yMin <- pt.Y
            if pt.Y > yMax then yMax <- pt.Y
        { XMin = xMin - clearance
          YMin = yMin - clearance
          XMax = xMax + clearance
          YMax = yMax + clearance })

// ---- Silicon-killer scene -----------------------------------------
// A foreign-net licon (66/44) blocks the direct path from (0, 50) to
// (300, 50) on li1. Empty-Auto finishSegments produces a straight
// line through it; the walkaround should produce a path around it.

[<Fact>]
let ``empty Auto straight line crosses a foreign obstacle`` () =
    let foreignLicon = rect 100 0 200 100
    let clearance = 70L
    let expanded = expandedBboxesOf clearance [| foreignLicon |]
    let path = [ p 0 50; p 300 50 ]
    let result = crossings path expanded
    result |> should not' (be Empty)

[<Fact>]
let ``walkaround path around the same obstacle does not cross`` () =
    let foreignLicon = rect 100 0 200 100
    let clearance = 70L
    let graph = VisibilityGraph.build clearance [| foreignLicon |]
    let pathOpt =
        shortestPath
            System.Threading.CancellationToken.None
            NoPreference graph (p 0 50) (p 300 50)
    pathOpt.IsSome |> should equal true
    let path = pathOpt.Value
    let result = crossings path graph.Obstacles
    result |> should be Empty

[<Fact>]
let ``straight line through clear space has no crossings`` () =
    let path = [ p 0 50; p 300 50 ]
    crossings path [||] |> should be Empty

[<Fact>]
let ``empty Auto path is rejected by PathCheck when obstacles present`` () =
    // Directly proves the silicon-killer scenario: a DraftRoute
    // with Auto = [] produces a finishSegments path that crosses
    // obstacles, and PathCheck.crossings correctly flags it.
    let foreignLicon = rect 100 0 200 100
    // Build the path the same way finishSegments does when Auto = []:
    // Points @ Auto @ [Cursor] with Auto = [] → Points @ [Cursor].
    let points = [ (0L, 50L) ]
    let auto : (int64 * int64) list = []
    let cursor = Some (300L, 50L)
    let pathPoints =
        match cursor with
        | Some c -> points @ auto @ [c]
        | None -> points
    let centerlinePath = pathPoints |> List.map (fun (x, y) -> p (int x) (int y))
    let clearance = 70L
    let expanded = expandedBboxesOf clearance [| foreignLicon |]
    let result = crossings centerlinePath expanded
    result |> should not' (be Empty)

[<Fact>]
let ``walkaround path with Auto set passes PathCheck`` () =
    let foreignLicon = rect 100 0 200 100
    let clearance = 70L
    let graph = VisibilityGraph.build clearance [| foreignLicon |]
    let pathOpt =
        shortestPath
            System.Threading.CancellationToken.None
            NoPreference graph (p 0 50) (p 300 50)
    pathOpt.IsSome |> should equal true
    let walkaroundPath = pathOpt.Value
    // Auto = intermediate corners (excluding start/end).
    let auto =
        walkaroundPath
        |> List.tail
        |> List.rev
        |> List.tail
        |> List.rev
        |> List.map (fun pt -> (pt.X, pt.Y))
    let points = [ (0L, 50L) ]
    let cursor = Some (300L, 50L)
    let pathPoints =
        match cursor with
        | Some c -> points @ auto @ [c]
        | None -> points
    let centerlinePath = pathPoints |> List.map (fun (x, y) -> p (int x) (int y))
    let result = crossings centerlinePath graph.Obstacles
    result |> should be Empty
