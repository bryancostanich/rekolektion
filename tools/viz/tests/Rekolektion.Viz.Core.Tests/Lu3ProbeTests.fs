module Rekolektion.Viz.Core.Tests.Lu3ProbeTests

// PROBE — confirm the LU.3 layout model for opamp_buffer_r2r.
// LU.3: every point of any P-diffusion must be within 15 µm
// Euclidean of some N-tap. Magic fires 18 LU.3 tiles on opamp;
// this probe dumps the layout so we can pick the right algorithm.
//
// Inputs:
//   diff = 65/20, tap = 65/44
//   psdm = 94/20, nsdm = 93/44, nwell = 64/20
// P-diff = diff ∩ psdm (outside nwell)
// N-tap  = tap  ∩ nsdm (inside  nwell)

open System
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Drc.Geometry

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

let private overlapsAny
        (b: int64 * int64 * int64 * int64)
        (others: (int64 * int64 * int64 * int64) array) : bool =
    let (x1, y1, x2, y2) = b
    others
    |> Array.exists (fun (a1, b1, a2, b2) ->
        x1 < a2 && a1 < x2 && y1 < b2 && b1 < y2)

type Lu3Probe(out: ITestOutputHelper) =

    [<Fact>]
    member _.``opamp polygons overlapping rpm magic-only bbox`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "dac", "opamp_buffer_r2r",
                            "opamp_buffer_r2r.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let target = 5610L, -8725L, 6820L, -8125L
        let (tx1,ty1,tx2,ty2) = target
        let bboxOverlaps (b: int64*int64*int64*int64) =
            let (x1,y1,x2,y2) = b
            tx1 < x2 && x1 < tx2 && ty1 < y2 && y1 < ty2
        let hits =
            flat
            |> Array.filter (fun p -> bboxOverlaps (bboxOf p))
        out.WriteLine (sprintf
            "polygons overlapping rpm magic-only bbox %A: %d" target hits.Length)
        for p in hits do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  layer=%d/%d bbox=(%d,%d,%d,%d) %dx%d src=%s"
                p.Layer p.DataType x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)

    [<Fact>]
    member _.``opamp URPM (79/20) polygons`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "dac", "opamp_buffer_r2r",
                            "opamp_buffer_r2r.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let urpm =
            flat |> Array.filter (fun p -> p.Layer = 79 && p.DataType = 20)
        out.WriteLine (sprintf "URPM (79/20) polys: %d" urpm.Length)
        for p in urpm do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  urpm bbox=(%d,%d,%d,%d) %dx%d" x1 y1 x2 y2 (x2-x1) (y2-y1))
        let region = Region.ofPolygons urpm
        let comps = Components.componentBboxes region
        out.WriteLine (sprintf "URPM merged components: %d" comps.Length)
        for (x1, y1, x2, y2) in comps do
            let w = x2 - x1
            let h = y2 - y1
            out.WriteLine (sprintf
                "  comp bbox=(%d,%d,%d,%d) %dx%d shorter=%d"
                x1 y1 x2 y2 w h (min w h))

    [<Fact>]
    member _.``opamp polyres polygons and merged components`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "dac", "opamp_buffer_r2r",
                            "opamp_buffer_r2r.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let polyres =
            flat |> Array.filter (fun p -> p.Layer = 66 && p.DataType = 13)
        out.WriteLine (sprintf "polyres polys: %d" polyres.Length)
        for p in polyres do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  polyres bbox=(%d,%d,%d,%d) %dx%d" x1 y1 x2 y2 (x2-x1) (y2-y1))
        let region = Region.ofPolygons polyres
        let comps = Components.componentBboxes region
        out.WriteLine (sprintf "merged components: %d" comps.Length)
        for (x1, y1, x2, y2) in comps do
            let w = x2 - x1
            let h = y2 - y1
            out.WriteLine (sprintf
                "  comp bbox=(%d,%d,%d,%d) %dx%d shorter=%d" x1 y1 x2 y2 w h (min w h))

    [<Fact>]
    member _.``check whether opamp n-tap has licon on it`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "dac", "opamp_buffer_r2r",
                            "opamp_buffer_r2r.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let byKey k =
            flat |> Array.filter (fun p -> p.Layer = fst k && p.DataType = snd k)
        let taps  = byKey (65, 44) |> Array.map bboxOf
        let licon = byKey (66, 44) |> Array.map bboxOf
        let nsdm  = byKey (93, 44) |> Array.map bboxOf
        let nwell = byKey (64, 20) |> Array.map bboxOf
        out.WriteLine (sprintf
            "tap=%d licon=%d nsdm=%d" taps.Length licon.Length nsdm.Length)
        for t in taps do
            let (tx1,ty1,tx2,ty2) = t
            let liconsOn =
                licon
                |> Array.filter (fun (lx1,ly1,lx2,ly2) ->
                    tx1 <= lx2 && lx1 <= tx2 && ty1 <= ly2 && ly1 <= ty2)
            let inNwell =
                nwell |> Array.exists (fun (wx1,wy1,wx2,wy2) ->
                    tx1 < wx2 && wx1 < tx2 && ty1 < wy2 && wy1 < ty2)
            let hasNsdm =
                nsdm |> Array.exists (fun (nx1,ny1,nx2,ny2) ->
                    tx1 < nx2 && nx1 < tx2 && ty1 < ny2 && ny1 < ty2)
            out.WriteLine (sprintf
                "  tap=(%d,%d,%d,%d) licons=%d inNwell=%b hasNsdm=%b"
                tx1 ty1 tx2 ty2 liconsOn.Length inNwell hasNsdm)

    member _.dumpFixture (path: string) =
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let byKey k =
            flat |> Array.filter (fun p -> p.Layer = fst k && p.DataType = snd k)
        let diffs = byKey (65, 20) |> Array.map bboxOf
        let taps  = byKey (65, 44) |> Array.map bboxOf
        let licon = byKey (66, 44) |> Array.map bboxOf
        let psdm  = byKey (94, 20) |> Array.map bboxOf
        let nsdm  = byKey (93, 44) |> Array.map bboxOf
        let nwell = byKey (64, 20) |> Array.map bboxOf
        let pdiffPolys =
            diffs
            |> Array.filter (fun b ->
                overlapsAny b psdm && overlapsAny b nwell)
        // Valid n-tap: tap ∩ nsdm ∩ nwell, AND has at least one
        // licon contacting it.
        let ntapPolys =
            taps
            |> Array.filter (fun b ->
                overlapsAny b nsdm && overlapsAny b nwell
                && overlapsAny b licon)
        out.WriteLine (sprintf
            "=== %s ===\n  diff=%d tap=%d psdm=%d nsdm=%d nwell=%d  p-diff=%d n-tap=%d"
            (System.IO.Path.GetFileName path)
            diffs.Length taps.Length psdm.Length nsdm.Length nwell.Length
            pdiffPolys.Length ntapPolys.Length)
        // For each nwell, count contained p-diffs and tap-overlap.
        let bboxContains (w: int64*int64*int64*int64) (t: int64*int64*int64*int64) =
            let (wx1, wy1, wx2, wy2) = w
            let (tx1, ty1, tx2, ty2) = t
            wx1 <= tx1 && tx2 <= wx2 && wy1 <= ty1 && ty2 <= wy2
        out.WriteLine "  per-nwell:"
        for w in nwell do
            let pdInW =
                pdiffPolys |> Array.filter (fun d ->
                    let (wx1,wy1,wx2,wy2) = w
                    let (dx1,dy1,dx2,dy2) = d
                    dx1 < wx2 && wx1 < dx2 && dy1 < wy2 && wy1 < dy2)
            // n-tap counted as belonging to W if it OVERLAPS W
            // (not necessarily fully inside) — the LICON inside W
            // is what biases the well.
            let ntFullInW =
                ntapPolys |> Array.filter (fun t ->
                    let (tx1,ty1,tx2,ty2) = t
                    let (wx1,wy1,wx2,wy2) = w
                    tx1 < wx2 && wx1 < tx2 && ty1 < wy2 && wy1 < ty2
                    // Specifically, at least one licon-on-this-tap
                    // must lie INSIDE W.
                    && (licon |> Array.exists (fun (lx1,ly1,lx2,ly2) ->
                        lx1 >= wx1 && lx2 <= wx2 && ly1 >= wy1 && ly2 <= wy2
                        && lx1 < tx2 && tx1 < lx2 && ly1 < ty2 && ty1 < ly2)))
            let (wx1,wy1,wx2,wy2) = w
            if pdInW.Length > 0 then
                out.WriteLine (sprintf
                    "    nwell=(%d,%d,%d,%d) pdiff=%d ntapFullInside=%d -> %s"
                    wx1 wy1 wx2 wy2 pdInW.Length ntFullInW.Length
                    (if ntFullInW.Length = 0 then "WOULD FIRE LU.3" else "ok"))

    [<Fact>]
    member this.``probe all three fixtures`` () =
        let baseDir =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
        let opamp =
            System.IO.Path.Combine(baseDir, "testdata", "cell_designs",
                                   "dac", "opamp_buffer_r2r",
                                   "opamp_buffer_r2r.rkt")
        let bias =
            System.IO.Path.Combine(baseDir, "testdata", "cell_designs",
                                   "precision_ref", "bias_gen.rkt")
        let b15 =
            System.IO.Path.Combine(baseDir, "testdata", "cell_designs",
                                   "column_readout_chain", "b1_5_stage1.rkt")
        this.dumpFixture opamp
        this.dumpFixture bias
        this.dumpFixture b15

    [<Fact>]
    member _.``opamp_buffer_r2r p-diff and n-tap layout`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "dac", "opamp_buffer_r2r",
                            "opamp_buffer_r2r.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: missing %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let byKey k =
            flat |> Array.filter (fun p -> p.Layer = fst k && p.DataType = snd k)
        let diffs = byKey (65, 20) |> Array.map bboxOf
        let taps  = byKey (65, 44) |> Array.map bboxOf
        let psdm  = byKey (94, 20) |> Array.map bboxOf
        let nsdm  = byKey (93, 44) |> Array.map bboxOf
        let nwell = byKey (64, 20) |> Array.map bboxOf
        out.WriteLine (sprintf
            "counts: diff=%d tap=%d psdm=%d nsdm=%d nwell=%d"
            diffs.Length taps.Length psdm.Length nsdm.Length nwell.Length)
        out.WriteLine "diff bboxes:"
        for (x1,y1,x2,y2) in diffs do
            out.WriteLine (sprintf "  diff (%d,%d,%d,%d)" x1 y1 x2 y2)
        out.WriteLine "psdm bboxes:"
        for (x1,y1,x2,y2) in psdm do
            out.WriteLine (sprintf "  psdm (%d,%d,%d,%d)" x1 y1 x2 y2)
        out.WriteLine "nwell bboxes:"
        for (x1,y1,x2,y2) in nwell do
            out.WriteLine (sprintf "  nwell (%d,%d,%d,%d)" x1 y1 x2 y2)
        out.WriteLine "tap bboxes:"
        for (x1,y1,x2,y2) in taps do
            out.WriteLine (sprintf "  tap (%d,%d,%d,%d)" x1 y1 x2 y2)
        // P-diff = PMOS source/drain. diff ∩ psdm INSIDE nwell.
        // (Inside an nwell, with psdm implant, means PMOS active.)
        let pdiffs =
            diffs
            |> Array.filter (fun b ->
                overlapsAny b psdm && overlapsAny b nwell)
        // N-tap = nwell contact. tap ∩ nsdm INSIDE nwell.
        let ntaps =
            taps
            |> Array.filter (fun b ->
                overlapsAny b nsdm && overlapsAny b nwell)
        out.WriteLine (sprintf
            "derived: p-diff=%d n-tap=%d" pdiffs.Length ntaps.Length)
        for b in ntaps do
            let (x1, y1, x2, y2) = b
            out.WriteLine (sprintf "  n-tap bbox=(%d,%d,%d,%d)" x1 y1 x2 y2)
        // Dump p-diff bboxes ordered top-down for cross-reference
        // with the magic LU.3 violation bbox list.
        let sorted =
            pdiffs |> Array.sortBy (fun (_, y1, _, _) -> -y1)
        out.WriteLine "p-diff bboxes (top-down):"
        for b in sorted do
            let (x1, y1, x2, y2) = b
            out.WriteLine (sprintf
                "  pdiff bbox=(%d,%d,%d,%d) %dx%d"
                x1 y1 x2 y2 (x2-x1) (y2-y1))
        // For each p-diff, compute nearest-corner-to-nearest-n-tap-corner
        // distance, and check against 15 µm.
        let limit = 15000L
        let dist2 (b1: int64*int64*int64*int64) (b2: int64*int64*int64*int64) =
            let (x1,y1,x2,y2) = b1
            let (a1,b1',a2,b2') = b2
            let dx =
                if x2 < a1 then a1 - x2
                elif a2 < x1 then x1 - a2
                else 0L
            let dy =
                if y2 < b1' then b1' - y2
                elif b2' < y1 then y1 - b2'
                else 0L
            dx*dx + dy*dy
        let lim2 = limit * limit
        out.WriteLine "p-diff min-edge-dist to nearest n-tap:"
        for b in sorted do
            let (x1, y1, x2, y2) = b
            let best =
                if ntaps.Length = 0 then None
                else
                    ntaps
                    |> Array.map (fun nb -> dist2 b nb, nb)
                    |> Array.minBy fst
                    |> Some
            match best with
            | None ->
                out.WriteLine (sprintf
                    "  pdiff (%d,%d,%d,%d): NO n-tap exists → fires" x1 y1 x2 y2)
            | Some (d2, nb) ->
                let d = int64 (System.Math.Sqrt (float d2))
                let mark = if d2 > lim2 then "FIRES" else "ok"
                out.WriteLine (sprintf
                    "  pdiff (%d,%d,%d,%d) min-dist=%d %s (vs n-tap %A)"
                    x1 y1 x2 y2 d mark nb)
