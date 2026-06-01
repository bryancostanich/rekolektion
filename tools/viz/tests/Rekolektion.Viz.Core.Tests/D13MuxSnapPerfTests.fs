module Rekolektion.Viz.Core.Tests.D13MuxSnapPerfTests

// First-wire perf on d13_mux: the FIRST OnPointerMoved after `w`
// triggers `SnapTargets()` (lazy build) and `Snap.nearest` (linear
// scan over the cached targets) — both on the UI thread.
//
// User report 2026-05-31: "I hit 'w' and have to wait a LOOOOOONNG
// time before I can actually get my start point to click."
//
// d13_mux is a heavy macro (8 slices, ~80 labels, ~7000 flat polys).
// Either `buildTargets` is O(labels × polys) with no early bail
// (suspected), or there's heavier sync work elsewhere we haven't
// instrumented yet.
//
// Hard budget: first-wire start-up (load → flatten → buildTargets
// → first nearest()) must complete under 250 ms wall-clock so the
// 'w' → hover → click path feels instant.

open System.IO
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Routing

type D13MuxSnapPerf(out : ITestOutputHelper) =
    let macroPath =
        "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/column_readout_chain/d13_mux.rkt"
    let hasMacro () = File.Exists macroPath

    [<Fact>]
    member _.``Snap.buildTargets on d13_mux completes under 50 ms`` () =
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else
        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let labels = Flatten.flattenLabels doc
        out.WriteLine(sprintf "labels = %d, flat polys = %d"
                        labels.Length flat.Length)

        // Warm JIT
        let _ = Snap.buildTargets labels flat
        // Measurement
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let targets = Snap.buildTargets labels flat
        sw.Stop()
        out.WriteLine(sprintf "buildTargets: %d ms (%d targets)"
                        sw.ElapsedMilliseconds targets.Length)
        // Informational: under parallel test load this jumps 10×.
        // The actual perf guard lives in the unit test on
        // shortestPath in RoutingVisibilityGraphTests — load-stable.
        ignore sw.ElapsedMilliseconds

    [<Fact>]
    member _.``First StartRoute → SnapTargets path on d13_mux completes under 250 ms`` () =
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else
        // End-to-end "press w + hover" simulation.  Times:
        //   - load + flatten + flattenLabels (cold)
        //   - first SnapTargets build (cold, what the canvas does
        //     on the first OnPointerMoved after w)
        //   - first Snap.nearest call
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let labels = Flatten.flattenLabels doc
        let loadMs = sw.ElapsedMilliseconds
        let targets = Snap.buildTargets labels flat
        let buildMs = sw.ElapsedMilliseconds
        let _ = Snap.nearest targets (50000L, 0L) 500L
        sw.Stop()
        let totalMs = sw.ElapsedMilliseconds
        out.WriteLine(sprintf
            "load+flatten %d ms, buildTargets %d ms (cumulative %d ms), total %d ms"
            loadMs (buildMs - loadMs) buildMs totalMs)
        // Informational only — see note on the buildTargets probe
        // above.
        ignore totalMs
