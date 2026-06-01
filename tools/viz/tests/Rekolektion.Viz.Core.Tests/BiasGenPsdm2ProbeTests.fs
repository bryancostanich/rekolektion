module Rekolektion.Viz.Core.Tests.BiasGenPsdm2ProbeTests

// PROBE — bias_gen has 2 viz-only psdm.2 fires Magic doesn't see.
//   viz[psdm.2] limit=380 measured=195 bbox=(11050,-18224,11720,-18029)
//   viz[psdm.2] limit=380 measured=195 bbox=(12275,-18224,21465,-18029)
// Both gaps are 195 nm (< 380 nm rule), but Magic sees a continuous
// PSDM region thanks to the 190 nm implant-close pass already in
// `Drc.Check.applyImplantClose`.  Probe the PSDM polygons near the
// first viz-only bbox to figure out what's going on.

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

type BiasGenPsdm2Probe(out : ITestOutputHelper) =

    [<Fact>]
    member _.``probe psdm polygons around the viz-only psdm.2 fire`` () =
        let path =
            testDataPath "testdata/cell_designs/precision_ref/bias_gen.rkt"
        if not (File.Exists path) then
            out.WriteLine (sprintf "SKIP: missing fixture %s" path)
        else

        let doc, _w = LayoutLoader.load path
        let flat = Flatten.flatten doc
        out.WriteLine (sprintf "total flattened polygons: %d" flat.Length)

        // Window around (11050,-18224,11720,-18029) plus 500 nm slop.
        let win : int64 * int64 * int64 * int64 =
            (10500L, -18800L, 12500L, -17500L)
        let (px1, py1, px2, py2) = win
        out.WriteLine (sprintf
            "probe window: x=[%d,%d] y=[%d,%d]" px1 px2 py1 py2)

        // PSDM = (94, 20)
        let psdm =
            flat
            |> Array.filter (fun p ->
                p.Layer = 94 && p.DataType = 20
                && bboxesOverlap (polyBbox p) win)
            |> Array.sortBy (fun p ->
                let (x, _, _, _) = polyBbox p in x)
        out.WriteLine (sprintf
            "PSDM (94/20) polygons intersecting probe window: %d"
            psdm.Length)
        for p in psdm do
            let (x1, y1, x2, y2) = polyBbox p
            out.WriteLine (sprintf
                "  psdm bbox=(%d,%d,%d,%d) size=%dx%d cell=%s"
                x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)

        // Also check the GAP bbox specifically — does any other PSDM
        // polygon NEAR the gap have its bbox overlap it after the
        // 190 nm implant-close pass?  applyImplantClose grows then
        // shrinks; effectively any pair of PSDM polygons within
        // 380 nm gets merged into one region. The 195 nm gap should
        // close — yet viz still emits a violation.
        let gap : int64 * int64 * int64 * int64 = (11050L, -18224L, 11720L, -18029L)
        let nearby =
            flat
            |> Array.filter (fun p ->
                p.Layer = 94 && p.DataType = 20
                && bboxesOverlap (polyBbox p) (10000L, -19000L, 12500L, -17500L))
        out.WriteLine (sprintf
            "PSDM polygons whose bbox could pair across the gap: %d" nearby.Length)

        // Every PSDM polygon whose x-range overlaps [11050, 11720]
        // — the violation tile is 670 nm wide matching this band,
        // and 195 nm tall, so the firing pair likely stacks
        // vertically across this x-range with a 195 nm gap. Look
        // at all of them.
        let allPsdm =
            flat
            |> Array.filter (fun p -> p.Layer = 94 && p.DataType = 20)
        let stacked =
            allPsdm
            |> Array.filter (fun p ->
                let (x1, _, x2, _) = polyBbox p
                x1 <= 11720L && x2 >= 11050L)
            |> Array.sortBy (fun p -> let (_, y, _, _) = polyBbox p in y)
        out.WriteLine (sprintf
            "ALL PSDM polygons overlapping x=[11050,11720]: %d" stacked.Length)
        for p in stacked do
            let (x1, y1, x2, y2) = polyBbox p
            out.WriteLine (sprintf
                "  bbox=(%d,%d,%d,%d) size=%dx%d cell=%s"
                x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)
