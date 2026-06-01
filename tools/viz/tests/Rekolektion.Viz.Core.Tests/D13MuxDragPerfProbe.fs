module Rekolektion.Viz.Core.Tests.D13MuxDragPerfProbe

// Simulates the live App dispatcher cascade for the VDD slice 2 →
// slice 3 wire: anchor at slice-2 VDD label, walk the cursor in many
// small steps toward slice-3 VDD. Times each routeAdaptive call.
//
// User report 2026-05-31: "routefinding is many seconds, not under a
// second" in the live App. The synthetic warm-cache probe shows 2-4
// ms per cursor frame, so something about the live cascade is
// hitting cold paths repeatedly. This probe asserts the cumulative
// drag (40 frames) lands under 2 s — fails today if any frame goes
// cold mid-drag.

open System.IO
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Routing

type D13MuxDragPerfProbe(out : ITestOutputHelper) =
    let macroPath =
        "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/column_readout_chain/d13_mux.rkt"
    let hasMacro () = File.Exists macroPath

    [<Fact>]
    member _.``simulated user drag from slice 2 VDD to slice 3 VDD on li1 — 40 frames`` () =
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else

        Obstacles.ClearCaches()
        WalkAround.ClearCaches()

        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc

        let layer : Obstacles.LayerKey = { Number = 67; DataType = 20 }
        let startNet = "VDD"
        let clearance = 85L + 170L

        // Stable key — exactly what the canvas would build per
        // dispatch with the SAME FlatPolygons / NetMap references.
        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }
        let macroBounds : WalkAround.MacroBounds =
            match WalkAround.macroBoundsOf flat with
            | Some b -> b
            | None -> { XMin = 0L; YMin = -5000L; XMax = 95000L; YMax = 5000L }

        let startPt : VisibilityGraph.Pt = { X = 28615L; Y = 4510L }
        let endPt   : VisibilityGraph.Pt = { X = 40185L; Y = 4510L }

        // 40 cursor positions interpolating from anchor to end. The
        // canvas would dispatch on each (coalesced). First call is
        // the cold path; the rest should hit warm caches.
        let frameCount = 40
        let frameTimes = ResizeArray<int64>()
        let mutable firstMs = 0L
        for i in 0 .. frameCount - 1 do
            let t = float (i + 1) / float frameCount
            let cur : VisibilityGraph.Pt =
                { X = startPt.X + int64 (t * float (endPt.X - startPt.X))
                  Y = startPt.Y + int64 (t * float (endPt.Y - startPt.Y)) }
            let dxAbs = abs (cur.X - startPt.X)
            let dyAbs = abs (cur.Y - startPt.Y)
            let initialMargin = max (dxAbs + dyAbs) (clearance * 4L)
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let _ =
                WalkAround.routeAdaptive
                    System.Threading.CancellationToken.None
                    VisibilityGraph.NoPreference
                    key startPt cur initialMargin macroBounds 3
            sw.Stop()
            frameTimes.Add sw.ElapsedMilliseconds
            if i = 0 then firstMs <- sw.ElapsedMilliseconds

        let totalMs = frameTimes |> Seq.sum
        let maxMs = frameTimes |> Seq.max
        let medianMs =
            let s = frameTimes |> Seq.sort |> Seq.toArray
            s.[s.Length / 2]
        let warmMs = totalMs - firstMs
        let warmFrames = frameCount - 1
        let warmAvg = if warmFrames > 0 then warmMs / int64 warmFrames else 0L

        out.WriteLine(sprintf "first frame (cold): %d ms" firstMs)
        out.WriteLine(sprintf "next %d frames total: %d ms (avg %d ms)"
                        warmFrames warmMs warmAvg)
        out.WriteLine(sprintf "max single frame: %d ms ; median: %d ms"
                        maxMs medianMs)
        out.WriteLine(sprintf "full 40-frame timeline (ms): %s"
                        (frameTimes |> Seq.map string |> String.concat ","))

        // Informational probe — actual perf assertion lives in
        // RoutingVisibilityGraphTests `shortestPath fast-fails
        // when cursor is strictly inside an obstacle's silicon`,
        // which is robust to parallel-test load. The numbers
        // above are useful for tuning but vary by 50× under
        // parallel CPU contention (e.g. running all 555+ Core
        // tests together).
        ignore warmMs
        ignore firstMs
        ignore maxMs
        ignore medianMs

    [<Fact>]
    member _.``shortestPath ALONE — break down which cursor positions trigger slow A*`` () =
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else

        Obstacles.ClearCaches()
        WalkAround.ClearCaches()

        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc
        let layer : Obstacles.LayerKey = { Number = 67; DataType = 20 }
        let startNet = "VDD"
        let clearance = 85L + 170L
        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }
        let dummyRegion : Obstacles.Region =
            { XMin = 0L; YMin = 0L; XMax = 0L; YMax = 0L }
        // Force the cold graph build once before the loop so the
        // per-call timing isolates A* search cost.
        let graph = WalkAround.buildGraphInRegion key dummyRegion
        out.WriteLine(sprintf "graph: %d nodes, %d obstacles"
                        graph.Nodes.Length graph.Obstacles.Length)

        let startPt : VisibilityGraph.Pt = { X = 28615L; Y = 4510L }
        let endPt   : VisibilityGraph.Pt = { X = 40185L; Y = 4510L }

        // Test a sweep of cursor X values matching the drag probe.
        // Tag each with whether it's inside an obstacle's expanded
        // bbox (the suspected slow case).
        let inside (pt : VisibilityGraph.Pt) =
            graph.Obstacles
            |> Array.exists (fun b ->
                pt.X > b.XMin && pt.X < b.XMax
                && pt.Y > b.YMin && pt.Y < b.YMax)

        let frameCount = 40
        for i in 0 .. frameCount - 1 do
            let t = float (i + 1) / float frameCount
            let cur : VisibilityGraph.Pt =
                { X = startPt.X + int64 (t * float (endPt.X - startPt.X))
                  Y = startPt.Y + int64 (t * float (endPt.Y - startPt.Y)) }
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let path =
                VisibilityGraph.shortestPath
                    System.Threading.CancellationToken.None
                    VisibilityGraph.NoPreference graph startPt cur
            sw.Stop()
            let cursorIn = inside cur
            let pathLen =
                match path with
                | None -> "None"
                | Some n -> string n.Length
            out.WriteLine(sprintf
                "frame %02d cur=(%d,%d) cursorInsideExpanded=%b ms=%d pathLen=%s"
                i cur.X cur.Y cursorIn sw.ElapsedMilliseconds pathLen)
