module Rekolektion.Viz.Core.Tests.OpampDiffTap3ProbeTests

// PROBE for opamp_buffer_r2r diff/tap.3 magic-only deltas.
// Magic reports diff spacing < 54 (270 nm) violations at:
//   bbox=(3940,290,12210,350)
//   bbox=(12210,290,12480,350)
//   bbox=(-265,-17435,9735,-17375)
// All 60 nm tall, very wide.  viz's diff/tap.3 (Spacing("diff/tap.3",
// diff, 0.27)) doesn't fire here even after the П-bridge containment
// fix. This probe dumps diff polygons near those gaps so we can see
// what's actually there.

open System
open System.IO
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout

let private testDataPath (rel : string) =
    let asmDir =
        System.Reflection.Assembly.GetExecutingAssembly().Location
        |> Path.GetDirectoryName
    Path.Combine(asmDir, rel)

let private polyBbox (p : Flatten.FlatPolygon)
        : int64 * int64 * int64 * int64 =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    for q in p.Points do
        if q.X < xMin then xMin <- q.X
        if q.X > xMax then xMax <- q.X
        if q.Y < yMin then yMin <- q.Y
        if q.Y > yMax then yMax <- q.Y
    xMin, yMin, xMax, yMax

let private bboxesOverlap
        ((ax1, ay1, ax2, ay2) : int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2) : int64 * int64 * int64 * int64) : bool =
    ax1 <= bx2 && ax2 >= bx1 && ay1 <= by2 && ay2 >= by1

type OpampDiffTap3Probe(out : ITestOutputHelper) =

    [<Fact>]
    member _.``probe diff polygons near magic diff/tap.3 tile (3940,290,12210,350)`` () =
        let path =
            testDataPath "testdata/cell_designs/dac/opamp_buffer_r2r/opamp_buffer_r2r.rkt"
        if not (File.Exists path) then
            out.WriteLine (sprintf "SKIP: missing fixture %s" path)
        else

        let doc, _w = LayoutLoader.load path
        let flat = Flatten.flatten doc

        // Expand probe window by 500 nm on each side to catch
        // bridging polygons.
        let win : int64 * int64 * int64 * int64 =
            (3440L, -210L, 12710L, 850L)
        let (px1, py1, px2, py2) = win
        out.WriteLine (sprintf
            "probe window x=[%d,%d] y=[%d,%d]" px1 px2 py1 py2)

        // diff layer = 65/20
        let diff =
            flat
            |> Array.filter (fun p ->
                p.Layer = 65 && p.DataType = 20
                && bboxesOverlap (polyBbox p) win)
            |> Array.sortBy (fun p ->
                let (_, y, _, _) = polyBbox p in y)
        out.WriteLine (sprintf "diff (65/20) polys in window: %d" diff.Length)
        for p in diff do
            let (x1, y1, x2, y2) = polyBbox p
            out.WriteLine (sprintf
                "  diff bbox=(%d,%d,%d,%d) size=%dx%d cell=%s"
                x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)

        // tap layer = 65/44 — taps are part of "diff/tap" for the
        // SKY130 difftap.3 rule (Magic's "diff/tap.3" treats diff
        // and tap together as one regional space).
        let tap =
            flat
            |> Array.filter (fun p ->
                p.Layer = 65 && p.DataType = 44
                && bboxesOverlap (polyBbox p) win)
            |> Array.sortBy (fun p ->
                let (_, y, _, _) = polyBbox p in y)
        out.WriteLine (sprintf "tap (65/44) polys in window: %d" tap.Length)
        for p in tap do
            let (x1, y1, x2, y2) = polyBbox p
            out.WriteLine (sprintf
                "  tap bbox=(%d,%d,%d,%d) size=%dx%d cell=%s"
                x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)
