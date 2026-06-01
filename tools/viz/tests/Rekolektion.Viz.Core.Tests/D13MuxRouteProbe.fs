module Rekolektion.Viz.Core.Tests.D13MuxRouteProbe

open System.IO
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Routing

type D13MuxRouteProbe(out : ITestOutputHelper) =
    let macroPath =
        "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/column_readout_chain/d13_mux.rkt"
    let hasMacro () = File.Exists macroPath

    let clearCaches () =
        Routing.Obstacles.ClearCaches()
        Routing.WalkAround.ClearCaches()

    let routeAndDump
        (label     : string)
        (ct        : System.Threading.CancellationToken)
        (preferred : VisibilityGraph.PreferredPosture)
        (key       : WalkAround.BuildKey)
        (startPt   : VisibilityGraph.Pt)
        (cursorPt  : VisibilityGraph.Pt)
        (initialMargin : int64)
        (macroBounds   : WalkAround.MacroBounds) =

        let result =
            WalkAround.routeAdaptive
                ct preferred key startPt cursorPt initialMargin macroBounds 3

        out.WriteLine(sprintf "=== %s ===" label)
        out.WriteLine(sprintf "  path: %s"
            (match result.Path with
             | Some nodes ->
                 nodes |> List.map (fun n -> sprintf "(%d,%d)" n.X n.Y)
                       |> String.concat " → "
             | None -> "None"))
        out.WriteLine(sprintf "  expansions: %d" result.Expansions)
        out.WriteLine(sprintf "  final region: (%d,%d,%d,%d)"
            result.FinalRegion.XMin result.FinalRegion.YMin
            result.FinalRegion.XMax result.FinalRegion.YMax)
        result

    [<Fact>]
    member _.``PROBE: exact user route on d13_mux VDD`` () =
        clearCaches ()
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else
        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc
        let layer : Obstacles.LayerKey = { Number = 68; DataType = 20 }
        // Clearance matching the app at met1: half of 200 + spacing 170
        let clearance = 100L + 170L
        let startNet = "VDD"

        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }

        let startPt : VisibilityGraph.Pt = { X = 28615L; Y = 4510L }
        let cursorPt : VisibilityGraph.Pt = { X = 40185L; Y = 4510L }

        let macroBounds =
            match WalkAround.macroBoundsOf flat with
            | Some b -> b
            | None ->
                { XMin = 0L; YMin = 0L; XMax = 95000L; YMax = 5000L }

        let dxAbs = abs (cursorPt.X - startPt.X)
        let dyAbs = abs (cursorPt.Y - startPt.Y)
        let initialMargin = max (dxAbs + dyAbs) (clearance * 4L)

        // Warm the cache
        let _ = routeAndDump
                    "WARMUP (NoPreference)"
                    System.Threading.CancellationToken.None
                    VisibilityGraph.NoPreference
                    key startPt cursorPt initialMargin macroBounds

        // Real routing with NoPreference (as the app does)
        let _ = routeAndDump
                    "ROUTE (NoPreference)"
                    System.Threading.CancellationToken.None
                    VisibilityGraph.NoPreference
                    key startPt cursorPt initialMargin macroBounds

        // Compare with explicit VFirst
        let _ = routeAndDump
                    "ROUTE (PreferVFirst)"
                    System.Threading.CancellationToken.None
                    VisibilityGraph.PreferVFirst
                    key startPt cursorPt initialMargin macroBounds

        // Compare with explicit HFirst
        let _ = routeAndDump
                    "ROUTE (PreferHFirst)"
                    System.Threading.CancellationToken.None
                    VisibilityGraph.PreferHFirst
                    key startPt cursorPt initialMargin macroBounds

        // Also dump ALL obstacles in the region so we can
        // understand why the walkaround dodges
        let region : Obstacles.Region =
            { XMin = startPt.X - initialMargin
              YMin = startPt.Y - initialMargin
              XMax = cursorPt.X + initialMargin
              YMax = cursorPt.Y + initialMargin }
        let set = Obstacles.obstacleSet layer startNet (Obstacles.buildNetIndex nets) flat
        let obs = Obstacles.polygonsOf set
        out.WriteLine(sprintf "=== OBSTACLES NEAR ROUTE ===")
        // Only those overlapping the route bbox
        let routeBbox xMin yMin xMax yMax =
            obs |> Array.iteri (fun i fp ->
                let mutable fxMin = System.Int64.MaxValue
                let mutable fyMin = System.Int64.MaxValue
                let mutable fxMax = System.Int64.MinValue
                let mutable fyMax = System.Int64.MinValue
                for pt in fp.Points do
                    if pt.X < fxMin then fxMin <- pt.X
                    if pt.X > fxMax then fxMax <- pt.X
                    if pt.Y < fyMin then fyMin <- pt.Y
                    if pt.Y > fyMax then fyMax <- pt.Y
                if fxMax >= xMin && fxMin <= xMax
                   && fyMax >= yMin && fyMin <= yMax then
                    out.WriteLine(sprintf "  obs[%d] bbox=(%d,%d,%d,%d)"
                        i fxMin fyMin fxMax fyMax))
        routeBbox (startPt.X - 1000L) (startPt.Y - 1000L)
                  (cursorPt.X + 1000L) (cursorPt.Y + 1000L)
        out.WriteLine(sprintf "  total obstacles: %d" obs.Length)

        // Now also trace the raw A* path by calling shortestPath directly
        // on the warm graph
        let dummyRegion : Obstacles.Region =
            { XMin = 0L; YMin = 0L; XMax = 0L; YMax = 0L }
        let graph = WalkAround.buildGraphInRegion key dummyRegion
        out.WriteLine(sprintf "=== GRAPH ===")
        out.WriteLine(sprintf "  nodes: %d" graph.Nodes.Length)
        out.WriteLine(sprintf "  obstacles: %d" graph.Obstacles.Length)
