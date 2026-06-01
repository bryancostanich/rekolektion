module Rekolektion.Viz.Core.Tests.D13MuxPerfProbe

// PERF PROBE: d13_mux walkaround buildMs = 2–4 minutes per cursor
// frame (observed live 2026-05-30). Macro has 5622 obstacles /
// 17242 graph nodes. Per-frame routing is unusable.
//
// This probe loads d13_mux.rkt headlessly, runs the same walkaround
// dispatch path the canvas runs, and times each layer
// (buildNetIndex, obstacleSet, VisibilityGraph.build, shortestPath)
// so we can identify where the seconds go before touching code.
//
// Cell lives in khalkulo — guarded with hasMacro so CI without that
// repo skips cleanly.

open System.IO
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Routing

type D13MuxPerfProbe(out : ITestOutputHelper) =
    let macroPath =
        "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/column_readout_chain/d13_mux.rkt"
    let hasMacro () = File.Exists macroPath

    let clearCaches () =
        Routing.Obstacles.ClearCaches()
        Routing.WalkAround.ClearCaches()

    [<Fact>]
    member _.``PROBE: time each walkaround layer on d13_mux`` () =
        clearCaches()
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else

        let swLoad = System.Diagnostics.Stopwatch.StartNew()
        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        swLoad.Stop()
        out.WriteLine(sprintf "load+flatten: %d ms (flat polys = %d)"
                        swLoad.ElapsedMilliseconds flat.Length)

        let swNets = System.Diagnostics.Stopwatch.StartNew()
        let nets = Net.LabelFlood.derive doc
        swNets.Stop()
        out.WriteLine(sprintf "LabelFlood.derive: %d ms (nets = %d)"
                        swNets.ElapsedMilliseconds nets.Count)

        // First met1 net we find — anchor + cursor anywhere on it.
        // d13_mux is heavy on met1 routing (VDD/VSS rails).
        let layer : Obstacles.LayerKey = { Number = 68; DataType = 20 }
        let startNet =
            nets
            |> Map.toSeq
            |> Seq.filter (fun (_, e) -> e.Polygons.Length > 0)
            |> Seq.tryHead
            |> Option.map fst
            |> Option.defaultValue ""
        out.WriteLine(sprintf "using startNet = %s" startNet)

        // Layer 1: buildNetIndex (cached by Map ref).
        let swIdx1 = System.Diagnostics.Stopwatch.StartNew()
        let idx1 = Obstacles.buildNetIndex nets
        swIdx1.Stop()
        let swIdx2 = System.Diagnostics.Stopwatch.StartNew()
        let idx2 = Obstacles.buildNetIndex nets
        swIdx2.Stop()
        out.WriteLine(sprintf "buildNetIndex 1st: %d ms ; 2nd (should be cache hit): %d ms ; same ref = %b"
                        swIdx1.ElapsedMilliseconds swIdx2.ElapsedMilliseconds (obj.ReferenceEquals(idx1, idx2)))

        // Layer 2: obstacleSet (cached on (layer, net, flatRef, idxRef)).
        let swObs1 = System.Diagnostics.Stopwatch.StartNew()
        let set1 = Obstacles.obstacleSet layer startNet idx1 flat
        swObs1.Stop()
        let swObs2 = System.Diagnostics.Stopwatch.StartNew()
        let set2 = Obstacles.obstacleSet layer startNet idx1 flat
        swObs2.Stop()
        out.WriteLine(sprintf "obstacleSet 1st: %d ms ; 2nd (should be cache hit): %d ms ; same ref = %b"
                        swObs1.ElapsedMilliseconds swObs2.ElapsedMilliseconds (obj.ReferenceEquals(set1, set2)))

        let obstacleCount = (Obstacles.polygonsOf set1).Length
        out.WriteLine(sprintf "obstacle count on layer %d/%d for net %s = %d"
                        layer.Number layer.DataType startNet obstacleCount)

        // Layer 3: VisibilityGraph.build (cached on (set ref, clearance)).
        let clearance = 95L + 140L   // met1 typical
        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }
        // Dummy region — buildGraphInRegion no longer uses it.
        let region : Obstacles.Region =
            { XMin = 0L; YMin = 0L; XMax = 0L; YMax = 0L }

        let swG1 = System.Diagnostics.Stopwatch.StartNew()
        let g1 = WalkAround.buildGraphInRegion key region
        swG1.Stop()
        let swG2 = System.Diagnostics.Stopwatch.StartNew()
        let g2 = WalkAround.buildGraphInRegion key region
        swG2.Stop()
        out.WriteLine(sprintf "buildGraphInRegion 1st: %d ms ; 2nd (should be cache hit): %d ms ; same ref = %b"
                        swG1.ElapsedMilliseconds swG2.ElapsedMilliseconds (obj.ReferenceEquals(g1, g2)))
        out.WriteLine(sprintf "graph nodes = %d ; expanded obstacles = %d"
                        g1.Nodes.Length g1.Obstacles.Length)

        // Adjacency density — most pairs blocked or most pairs visible?
        let totalEdges =
            g1.Adjacency
            |> Array.sumBy (fun a -> a.Length)
        let possiblePairs = int64 g1.Nodes.Length * int64 (g1.Nodes.Length - 1)
        let avgDegree =
            if g1.Nodes.Length = 0 then 0.0
            else float totalEdges / float g1.Nodes.Length
        let maxDegree =
            if g1.Nodes.Length = 0 then 0
            else g1.Adjacency |> Array.map (fun a -> a.Length) |> Array.max
        out.WriteLine(sprintf "adjacency: total edges = %d (bidir), avg degree = %.1f, max degree = %d, possible pairs (bidir) = %d"
                        totalEdges avgDegree maxDegree possiblePairs)

        // Layer 4: shortestPath. Pick start/cursor inside the macro
        // bbox at non-trivial separation.
        let macroBbox =
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for fp in flat do
                for pt in fp.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
            xMin, yMin, xMax, yMax
        let xMin, yMin, xMax, yMax = macroBbox
        let cx = (xMin + xMax) / 2L
        let cy = (yMin + yMax) / 2L
        let startPt : VisibilityGraph.Pt = { X = cx - 1000L; Y = cy }
        let cursorPt : VisibilityGraph.Pt = { X = cx + 1000L; Y = cy + 500L }
        out.WriteLine(sprintf "macro bbox = (%d,%d,%d,%d) ; start = (%d,%d) ; cursor = (%d,%d)"
                        xMin yMin xMax yMax startPt.X startPt.Y cursorPt.X cursorPt.Y)

        let swS = System.Diagnostics.Stopwatch.StartNew()
        let path =
            VisibilityGraph.shortestPath
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference g1 startPt cursorPt
        swS.Stop()
        out.WriteLine(sprintf "shortestPath (None-case, search exhausts): %d ms ; path nodes = %s"
                        swS.ElapsedMilliseconds
                        (match path with
                         | Some p -> string p.Length
                         | None -> "None"))

        // Pick start/cursor INSIDE the macro but outside any
        // obstacle, near a known same-net label (VDD = startNet)
        // so the search returns Some path quickly. This is the
        // realistic live-routing per-frame cost.
        let realStart : VisibilityGraph.Pt = { X = 1000L; Y = -3500L }
        let realCursor : VisibilityGraph.Pt = { X = 1500L; Y = -3000L }
        let swS2 = System.Diagnostics.Stopwatch.StartNew()
        let path2 =
            VisibilityGraph.shortestPath
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference g1 realStart realCursor
        swS2.Stop()
        out.WriteLine(sprintf "shortestPath (realistic Some-path): %d ms ; nodes = %s"
                        swS2.ElapsedMilliseconds
                        (match path2 with
                         | Some p -> string p.Length
                         | None -> "None"))

        // Same scenario, second call → should hit cache where any
        // memoisation exists (the search itself doesn't cache, but
        // start/goal Steiner setup might benefit from hot caches).
        let swS3 = System.Diagnostics.Stopwatch.StartNew()
        let _ =
            VisibilityGraph.shortestPath
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference g1 realStart realCursor
        swS3.Stop()
        out.WriteLine(sprintf "shortestPath (same scenario, 2nd call, hot caches): %d ms"
                        swS3.ElapsedMilliseconds)

        // Full pipeline timing — what the canvas hits per cursor frame.
        let swFull = System.Diagnostics.Stopwatch.StartNew()
        let macroBounds : WalkAround.MacroBounds =
            { XMin = xMin; YMin = yMin; XMax = xMax; YMax = yMax }
        let dxAbs = abs (cursorPt.X - startPt.X)
        let dyAbs = abs (cursorPt.Y - startPt.Y)
        let initialMargin = max (dxAbs + dyAbs) (clearance * 4L)
        let result =
            WalkAround.routeAdaptive
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference
                key startPt cursorPt initialMargin macroBounds 3
        swFull.Stop()
        out.WriteLine(sprintf "routeAdaptive (cached): %d ms ; outcome = %s"
                        swFull.ElapsedMilliseconds
                        (match result.Path with
                         | Some p -> sprintf "Some path (%d nodes)" p.Length
                         | None -> "None"))

        // Force a cold run by passing a FRESH netMap so caches miss
        // — simulates what happens when NetMap reference flips per
        // frame in the canvas.
        let netsCloned =
            nets |> Map.toSeq |> Map.ofSeq   // new Map instance, same content
        let keyCloned : WalkAround.BuildKey =
            { key with NetMapRef = netsCloned }
        let swCold = System.Diagnostics.Stopwatch.StartNew()
        let resultCold =
            WalkAround.routeAdaptive
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference
                keyCloned startPt cursorPt initialMargin macroBounds 3
        swCold.Stop()
        out.WriteLine(sprintf "routeAdaptive (cloned NetMap = cache miss): %d ms ; outcome = %s"
                        swCold.ElapsedMilliseconds
                        (match resultCold.Path with
                         | Some p -> sprintf "Some path (%d nodes)" p.Length
                         | None -> "None"))

        // SIMULATE LIVE CURSOR MOVES — same key reused across many
        // routeAdaptive calls, as the canvas would do per cursor
        // frame. If the cache is invalidating per frame, every call
        // pays the cold-build cost; if it's holding, calls 2..N are
        // all fast.
        let frameStart : VisibilityGraph.Pt = { X = 1000L; Y = -3500L }
        let frameKeyStable : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }
        let frameTimes = ResizeArray<int64>()
        for i in 0 .. 19 do
            let cur : VisibilityGraph.Pt =
                { X = frameStart.X + int64 (i * 100)
                  Y = frameStart.Y + int64 (i * 50) }
            // Force GC before each frame to isolate from cumulative
            // GC pressure across frames.
            System.GC.Collect()
            System.GC.WaitForPendingFinalizers()
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let _ =
                WalkAround.routeAdaptive
                    System.Threading.CancellationToken.None
                    VisibilityGraph.NoPreference
                    frameKeyStable frameStart cur
                    initialMargin macroBounds 3
            sw.Stop()
            frameTimes.Add sw.ElapsedMilliseconds
        out.WriteLine(sprintf "20 live-simulated frames (stable key): %s ms"
                        (frameTimes |> Seq.map string |> String.concat ", "))
        let max20 = frameTimes |> Seq.max
        let median20 =
            let sorted = frameTimes |> Seq.sort |> Seq.toArray
            sorted.[sorted.Length / 2]
        out.WriteLine(sprintf "frame max = %d ms, median = %d ms"
                        max20 median20)

        // INFOPRINT: no assertions in the long probe — timing is
        // inherently noisy when other test classes run concurrently
        // and share the global routing caches. The concurrent
        // single-flight and cold-concurrent probes below are the
        // regression guards.

    [<Fact>]
    member _.``PROBE: concurrent routeAdaptive calls single-flight the build`` () =
        clearCaches()
        // REGRESSION GUARD for the d13_mux "16-second per-frame
        // buildMs" bug 2026-05-30. Canvas dispatch fires a new
        // routeAdaptive task per cursor frame; without single-flight
        // caching, 5 concurrent builds × ~700 ms isolated → ~16 s
        // each in live as they fight for parallel threads. The
        // Lazy-valued cache makes the first caller build, the rest
        // block on the same task.
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else

        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc
        let layer : Obstacles.LayerKey = { Number = 68; DataType = 20 }
        let clearance = 95L + 140L
        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = "VDD"
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }
        let startPt : VisibilityGraph.Pt = { X = 1000L; Y = -3500L }
        let cursorPt : VisibilityGraph.Pt = { X = 1500L; Y = -3000L }
        let macroBounds : WalkAround.MacroBounds =
            { XMin = -1000L; YMin = -5000L; XMax = 95000L; YMax = 5000L }
        let dxAbs = abs (cursorPt.X - startPt.X)
        let dyAbs = abs (cursorPt.Y - startPt.Y)
        let initialMargin = max (dxAbs + dyAbs) (clearance * 4L)

        // Warm-up: priming call to ensure NetMap / obstacleSet
        // caches are populated before we time the concurrent test.
        // (The concurrent build phase below is what we're testing.)
        let _ =
            WalkAround.routeAdaptive
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference
                key startPt cursorPt initialMargin macroBounds 3
        out.WriteLine "warm-up done; cache should hold the Lazy entry now"

        // Fire 5 concurrent routeAdaptive tasks with the SAME key.
        // Pre-fix: 5 builds run in parallel, each ~5x slower than
        // isolated. Post-fix (Lazy single-flight): cache hits on
        // each call after the first.
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let tasks =
            [| for i in 0 .. 4 ->
                System.Threading.Tasks.Task.Run(fun () ->
                    let cur : VisibilityGraph.Pt =
                        { X = cursorPt.X + int64 (i * 10)
                          Y = cursorPt.Y + int64 (i * 10) }
                    let s = System.Diagnostics.Stopwatch.StartNew()
                    let _ =
                        WalkAround.routeAdaptive
                            System.Threading.CancellationToken.None
                            VisibilityGraph.NoPreference
                            key startPt cur initialMargin macroBounds 3
                    s.Stop()
                    s.ElapsedMilliseconds) |]
        System.Threading.Tasks.Task.WaitAll(tasks |> Array.map (fun t -> t :> System.Threading.Tasks.Task))
        sw.Stop()
        let times = tasks |> Array.map (fun t -> t.Result)
        out.WriteLine(sprintf "5 concurrent same-key tasks: each = %s ms ; wall = %d ms"
                        (times |> Array.map string |> String.concat ", ")
                        sw.ElapsedMilliseconds)
        let maxTime = times |> Array.max
        Assert.True(
            maxTime < 200L,
            sprintf "concurrent task max regressed: %d ms (gate 200; cache miss / no single-flight?)"
                maxTime)
        Assert.True(
            sw.ElapsedMilliseconds < 300L,
            sprintf "concurrent wall time regressed: %d ms (gate 300; builds racing?)"
                sw.ElapsedMilliseconds)

    [<Fact>]
    member _.``PROBE: concurrent COLD routeAdaptive calls only build once`` () =
        clearCaches()
        // The actual live scenario: canvas dispatches 5 cursor-frame
        // tasks back-to-back BEFORE the first build completes. All
        // 5 should piggy-back on a single build (~700 ms), not race
        // 5 separate ~700 ms builds (would saturate cores and each
        // take 5×).
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else

        // Fresh load → fresh caches at every layer (Obstacles,
        // NetIndex, ObstacleSet, graphCache).
        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc
        let layer : Obstacles.LayerKey = { Number = 68; DataType = 20 }
        let clearance = 95L + 140L
        // Use a DIFFERENT startNet so the cache from the warm probe
        // (if shared via static cache) doesn't pollute. VSS is also
        // a substantial net on d13_mux.
        let startNet =
            nets
            |> Map.toSeq
            |> Seq.filter (fun (n, _) -> n <> "VDD")
            |> Seq.tryHead
            |> Option.map fst
            |> Option.defaultValue "VSS"
        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }
        let startPt : VisibilityGraph.Pt = { X = 2000L; Y = -3500L }
        let cursorPt : VisibilityGraph.Pt = { X = 2500L; Y = -3000L }
        let macroBounds : WalkAround.MacroBounds =
            { XMin = -1000L; YMin = -5000L; XMax = 95000L; YMax = 5000L }
        let dxAbs = abs (cursorPt.X - startPt.X)
        let dyAbs = abs (cursorPt.Y - startPt.Y)
        let initialMargin = max (dxAbs + dyAbs) (clearance * 4L)
        out.WriteLine(sprintf "cold concurrent on net %s" startNet)

        let sw = System.Diagnostics.Stopwatch.StartNew()
        let tasks =
            [| for i in 0 .. 4 ->
                System.Threading.Tasks.Task.Run(fun () ->
                    let cur : VisibilityGraph.Pt =
                        { X = cursorPt.X + int64 (i * 10)
                          Y = cursorPt.Y + int64 (i * 10) }
                    let s = System.Diagnostics.Stopwatch.StartNew()
                    let _ =
                        WalkAround.routeAdaptive
                            System.Threading.CancellationToken.None
                            VisibilityGraph.NoPreference
                            key startPt cur initialMargin macroBounds 3
                    s.Stop()
                    s.ElapsedMilliseconds) |]
        System.Threading.Tasks.Task.WaitAll(tasks |> Array.map (fun t -> t :> System.Threading.Tasks.Task))
        sw.Stop()
        let times = tasks |> Array.map (fun t -> t.Result)
        out.WriteLine(sprintf "5 COLD concurrent tasks: each = %s ms ; wall = %d ms"
                        (times |> Array.map string |> String.concat ", ")
                        sw.ElapsedMilliseconds)
        // Pre-fix wall would be 5×build = 5×~700ms = ~3500ms minimum
        // even if perfectly parallel, or more realistically ~15s as
        // they saturate. With single-flight: ~1 build + 4 instant
        // cache hits = ~700ms wall, all task times bunch around that.
        Assert.True(
            sw.ElapsedMilliseconds < 2500L,
            sprintf "cold concurrent wall regressed: %d ms (gate 2500; expect ~700-1500 with single-flight)"
                sw.ElapsedMilliseconds)
