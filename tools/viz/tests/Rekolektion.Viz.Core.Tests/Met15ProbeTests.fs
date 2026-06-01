module Rekolektion.Viz.Core.Tests.Met15ProbeTests

// PROBE — find out exactly what's at the b1_5_stage1 viz-only
// met1.5 violation bbox (30,-1440,200,-1270). Read mcon and met1
// shapes, dump their bboxes and how much enclosure margin each
// covering met1 provides. Should reveal whether the fire is a
// per-polygon-vs-merged-feature problem (multiple met1 polys
// touching) or a true layout issue.

open System
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Core.Drc.Geometry
open Rekolektion.Viz.Core.Rkt.Types

let private bboxOf (p: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon)
        : int64 * int64 * int64 * int64 =
    let mutable x1 = System.Int64.MaxValue
    let mutable y1 = System.Int64.MaxValue
    let mutable x2 = System.Int64.MinValue
    let mutable y2 = System.Int64.MinValue
    for q in p.Points do
        if q.X < x1 then x1 <- q.X
        if q.X > x2 then x2 <- q.X
        if q.Y < y1 then y1 <- q.Y
        if q.Y > y2 then y2 <- q.Y
    x1, y1, x2, y2

type Met15Probe(out: ITestOutputHelper) =

    [<Fact>]
    member _.``find mcon + met1 at b1_5_stage1 viz-only met1.5 bbox`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "column_readout_chain", "b1_5_stage1.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: missing %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        // mcon = 67/44, met1 = 68/20.
        let mcon =
            flat |> Array.filter (fun p -> p.Layer = 67 && p.DataType = 44)
        let met1 =
            flat |> Array.filter (fun p -> p.Layer = 68 && p.DataType = 20)
        out.WriteLine (sprintf
            "total mcon polys=%d, met1 polys=%d" mcon.Length met1.Length)
        // Find the mcon whose bbox is (30,-1440,200,-1270).
        let target = 30L, -1440L, 200L, -1270L
        let matches =
            mcon |> Array.filter (fun p -> bboxOf p = target)
        out.WriteLine (sprintf
            "mcons matching exact target bbox: %d" matches.Length)
        // If none exact, fall back to any mcon whose center is near (115,-1355).
        let nearTarget =
            let cx, cy = 115L, -1355L
            mcon
            |> Array.filter (fun p ->
                let (x1, y1, x2, y2) = bboxOf p
                let mx, my = (x1+x2)/2L, (y1+y2)/2L
                abs (mx - cx) < 200L && abs (my - cy) < 200L)
        out.WriteLine (sprintf
            "mcons near target center: %d" nearTarget.Length)
        for p in nearTarget do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  mcon bbox=(%d,%d,%d,%d) src=%s" x1 y1 x2 y2 p.SourceStructure)
        // For each near-target mcon, find all met1 that overlap it and
        // report per-axis enclosure margin.
        for mp in nearTarget do
            let (mx1, my1, mx2, my2) = bboxOf mp
            out.WriteLine (sprintf
                "--- enclosing met1 for mcon (%d,%d,%d,%d) ---"
                mx1 my1 mx2 my2)
            for m1 in met1 do
                let (e1, f1, e2, f2) = bboxOf m1
                let overlap =
                    mx1 <= e2 && e1 <= mx2 && my1 <= f2 && f1 <= my2
                if overlap then
                    let leftM = mx1 - e1
                    let rightM = e2 - mx2
                    let botM = my1 - f1
                    let topM = f2 - my2
                    let xM = min leftM rightM
                    let yM = min botM topM
                    out.WriteLine (sprintf
                        "  met1 bbox=(%d,%d,%d,%d) src=%s  margins L=%d R=%d B=%d T=%d  xM=%d yM=%d"
                        e1 f1 e2 f2 m1.SourceStructure
                        leftM rightM botM topM xM yM)
