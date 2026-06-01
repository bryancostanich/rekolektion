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
    member _.``bias_gen_output_legs region check on the tap`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "precision_ref", "bias_gen_output_legs.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let nwellPolys =
            flat |> Array.filter (fun p -> p.Layer = 64 && p.DataType = 20)
        out.WriteLine (sprintf "nwell polys (flattened): %d" nwellPolys.Length)
        for p in nwellPolys do
            out.WriteLine (sprintf "  nwell points=%d" p.Points.Length)
            for q in p.Points do
                out.WriteLine (sprintf "    (%d,%d)" q.X q.Y)
        let nwellRegion = Region.ofPolygons nwellPolys
        out.WriteLine (sprintf "nwell region slabs: %d" nwellRegion.Slabs.Length)
        // Check the tap region intersection.
        let tapRegion = Region.ofRect 4862L -13152L 11011L -12732L
        let intersect = Boolean.intersect tapRegion nwellRegion
        out.WriteLine (sprintf
            "tap region: 1 slab; nwell intersection: empty=%b slabs=%d"
            (Region.isEmpty intersect) intersect.Slabs.Length)
        for slab in intersect.Slabs do
            out.WriteLine (sprintf
                "  slab y=[%d,%d) intervals=%d" slab.Y (slab.Y + slab.Height) slab.Intervals.Length)
            for iv in slab.Intervals do
                out.WriteLine (sprintf "    x=[%d,%d)" iv.X1 iv.X2)
        // Check the n-tap at top.
        let ntapRegion = Region.ofRect -154L -1026L 20196L -606L
        let ntapInt = Boolean.intersect ntapRegion nwellRegion
        out.WriteLine (sprintf
            "n-tap intersection: empty=%b" (Region.isEmpty ntapInt))

    [<Fact>]
    member _.``bias_gen_output_legs p-tap detection`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "precision_ref", "bias_gen_output_legs.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let byKey k =
            flat |> Array.filter (fun p -> p.Layer = fst k && p.DataType = snd k)
        let taps  = byKey (65, 44) |> Array.map bboxOf
        let licon = byKey (66, 44) |> Array.map bboxOf
        let psdm  = byKey (94, 20) |> Array.map bboxOf
        let nsdm  = byKey (93, 44) |> Array.map bboxOf
        let nwell = byKey (64, 20) |> Array.map bboxOf
        let diffs = byKey (65, 20) |> Array.map bboxOf
        out.WriteLine (sprintf
            "diff=%d tap=%d licon=%d psdm=%d nsdm=%d nwell=%d"
            diffs.Length taps.Length licon.Length psdm.Length nsdm.Length nwell.Length)
        // Show all taps with their psdm/nwell/licon classifications.
        for t in taps do
            let (tx1,ty1,tx2,ty2) = t
            let hasPsdm = psdm |> Array.exists (fun p -> tx1 < (let (_,_,a,_)=p in a) && (let (a,_,_,_)=p in a) < tx2 && ty1 < (let (_,_,_,b)=p in b) && (let (_,b,_,_)=p in b) < ty2)
            let hasNsdm = nsdm |> Array.exists (fun p -> tx1 < (let (_,_,a,_)=p in a) && (let (a,_,_,_)=p in a) < tx2 && ty1 < (let (_,_,_,b)=p in b) && (let (_,b,_,_)=p in b) < ty2)
            let inNwell = nwell |> Array.exists (fun w -> tx1 < (let (_,_,a,_)=w in a) && (let (a,_,_,_)=w in a) < tx2 && ty1 < (let (_,_,_,b)=w in b) && (let (_,b,_,_)=w in b) < ty2)
            let liconsOn =
                licon |> Array.filter (fun l ->
                    tx1 < (let (_,_,a,_)=l in a) && (let (a,_,_,_)=l in a) < tx2
                    && ty1 < (let (_,_,_,b)=l in b) && (let (_,b,_,_)=l in b) < ty2)
            let kind =
                if hasPsdm && not inNwell && liconsOn.Length > 0 then "VALID-P-TAP"
                elif hasNsdm && inNwell && liconsOn.Length > 0 then "VALID-N-TAP"
                else "uncontacted/ambiguous"
            out.WriteLine (sprintf
                "  tap=(%d,%d,%d,%d) psdm=%b nsdm=%b inNwell=%b licons=%d -> %s"
                tx1 ty1 tx2 ty2 hasPsdm hasNsdm inNwell liconsOn.Length kind)
            // Which nwell is this tap supposedly inside?
            let containingNwells =
                nwell |> Array.filter (fun (w1,x1,w2,x2) ->
                    tx1 < w2 && w1 < tx2 && ty1 < x2 && x1 < ty2)
            for (w1,x1,w2,x2) in containingNwells do
                out.WriteLine (sprintf
                    "    overlapping nwell=(%d,%d,%d,%d)" w1 x1 w2 x2)
        // Also show the n-diff bboxes my viz fires on (LU.2 viz-only):
        let viz1 = 8931L, -12297L, 10931L, -10717L
        let viz2 = 4132L, -12296L, 8132L, -10716L
        out.WriteLine "viz-only LU.2 bboxes:"
        for (vx1,vy1,vx2,vy2) as v in [|viz1; viz2|] do
            out.WriteLine (sprintf "  viz=(%d,%d,%d,%d)" vx1 vy1 vx2 vy2)
            // What is the underlying diff?
            let cover =
                diffs |> Array.filter (fun (a1,b1,a2,b2) ->
                    a1 < vx2 && vx1 < a2 && b1 < vy2 && vy1 < b2)
            for (a1,b1,a2,b2) as d in cover do
                let hasNsdm = nsdm |> Array.exists (fun (p1,q1,p2,q2) ->
                    a1 < p2 && p1 < a2 && b1 < q2 && q1 < b2)
                let inNwell = nwell |> Array.exists (fun (w1,x1,w2,x2) ->
                    a1 < w2 && w1 < a2 && b1 < x2 && x1 < b2)
                out.WriteLine (sprintf
                    "    underlying diff=(%d,%d,%d,%d) nsdm=%b inNwell=%b"
                    a1 b1 a2 b2 hasNsdm inNwell)

    [<Fact>]
    member _.``b1_5_stage1 waiver decision for mcon.2 gap bbox`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "column_readout_chain", "b1_5_stage1.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path); ()
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let footprints = Rekolektion.Viz.Core.Drc.Waiver.collectFoundryFootprints flat
        // The viz-side rule would emit BboxA = gap region between
        // mcons (1470,-1440,1640,-1270) and (1740,-1386,1910,-1216),
        // which is (1640, -1386, 1740, -1270) (X gap, Y overlap).
        let gapBb = 1640L, -1386L, 1740L, -1270L
        let waived =
            Rekolektion.Viz.Core.Drc.Waiver.isFoundryWaived
                footprints "mcon.2" gapBb [||]
        let (cx, cy) = (1640L+1740L)/2L, (-1386L + -1270L)/2L
        out.WriteLine (sprintf
            "gap bbox=(1640,-1386,1740,-1270), center=(%d,%d)" cx cy)
        out.WriteLine (sprintf "isFoundryWaived(mcon.2)=%b" waived)
        // Dump nearby foundry footprints (anything within 1500 nm of
        // the center) to see what's bounding the waiver decision.
        out.WriteLine "footprints within 1500 nm of center:"
        for (fx0, fy0, fx1, fy1) in footprints do
            let dx =
                if cx < fx0 then fx0 - cx
                elif cx > fx1 then cx - fx1
                else 0L
            let dy =
                if cy < fy0 then fy0 - cy
                elif cy > fy1 then cy - fy1
                else 0L
            if dx*dx + dy*dy < 1500L*1500L then
                out.WriteLine (sprintf
                    "  footprint=(%d,%d,%d,%d) dx=%d dy=%d"
                    fx0 fy0 fx1 fy1 dx dy)

    [<Fact>]
    member _.``b1_5_stage1 viz Drc.Check raw fires by rule`` () =
        // What does Drc.Check actually return BEFORE the waiver
        // post-pass on b1_5_stage1? If mcon.2 doesn't appear here,
        // the rule itself is not firing; if it does, the waiver is
        // hiding it. Distinguishes "rule never fired" from "fired
        // then waived".
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "column_readout_chain", "b1_5_stage1.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let view = Rekolektion.Viz.Core.Drc.RulesYaml.loadEffectiveOrDefault "sky130" None
        let violations = Rekolektion.Viz.Core.Drc.Check.check view doc.Units flat
        // Count by rule.
        let byRule =
            violations
            |> Array.groupBy (fun v -> v.Rule)
            |> Array.map (fun (rule, vs) -> rule, vs.Length)
        out.WriteLine (sprintf "post-waiver viz fires: %d" violations.Length)
        for (rule, count) in byRule do
            out.WriteLine (sprintf "  viz[%s] = %d" rule count)
        // Now also list any mcon.2 violations regardless of source
        // (in case they came back with different rule names).
        let mcon2s =
            violations
            |> Array.filter (fun v -> v.Rule = "mcon.2")
        out.WriteLine (sprintf "mcon.2 post-waiver: %d" mcon2s.Length)
        for v in mcon2s |> Array.truncate 10 do
            let (x1, y1, x2, y2) = v.BboxA
            out.WriteLine (sprintf
                "  mcon.2 bbox=(%d,%d,%d,%d) measured=%d limit=%d"
                x1 y1 x2 y2 v.MeasuredDbu v.LimitDbu)

    [<Fact>]
    member _.``b1_5_stage1 mcon2 fire at (1550,-1440)`` () =
        // PROBE: Magic fires mcon.2 (mcon spacing < 38 nm) at
        // bbox (1550,-1440,1640,-1270) and (1550,-1440,1640,-1385).
        // viz fires no mcon.2. Find the actual mcons near this
        // location and measure the gap.
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "column_readout_chain", "b1_5_stage1.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        // Look at mcons in a WIDER window around the fire bbox to
        // catch any third mcon that might bridge the gap.
        let target = 500L, -2200L, 2500L, -500L
        let (tx1, ty1, tx2, ty2) = target
        let overlaps (b: int64*int64*int64*int64) =
            let (x1, y1, x2, y2) = b
            tx1 < x2 && x1 < tx2 && ty1 < y2 && y1 < ty2
        let mcons =
            flat
            |> Array.filter (fun p ->
                p.Layer = 67 && p.DataType = 44
                && overlaps (bboxOf p))
            |> Array.sortBy bboxOf
        out.WriteLine (sprintf
            "mcons (67/44) near fire bbox: %d" mcons.Length)
        for p in mcons do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  bbox=(%d,%d,%d,%d) %dx%d src=%s"
                x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)

    [<Fact>]
    member _.``b1_5_stage1 difftap.9 fire at (30,-80,1370,125)`` () =
        // PROBE: Magic fires diff/tap.9 (n-diff to nwell spacing
        // < 0.34 µm) at this bbox. viz's F# rule
        //   ImplantOutsideWellSpacing("diff/tap.9", nsdm, diff,
        //                              nwell, 0.34)
        // doesn't fire. Find the actual nsdm / diff / nwell
        // polygons that bound the gap.
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "column_readout_chain", "b1_5_stage1.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        // Widen the target bbox by 500 nm on every side (>> limit 340)
        // to catch any contributing polygon.
        let target = -470L, -580L, 1870L, 625L
        let (tx1, ty1, tx2, ty2) = target
        let overlaps (b: int64*int64*int64*int64) =
            let (x1, y1, x2, y2) = b
            tx1 < x2 && x1 < tx2 && ty1 < y2 && y1 < ty2
        let dump label num dt =
            let polys =
                flat
                |> Array.filter (fun p ->
                    p.Layer = num && p.DataType = dt
                    && overlaps (bboxOf p))
                |> Array.sortBy bboxOf
            out.WriteLine (sprintf "%s (%d/%d): %d" label num dt polys.Length)
            for p in polys do
                let (x1, y1, x2, y2) = bboxOf p
                out.WriteLine (sprintf
                    "  bbox=(%d,%d,%d,%d) %dx%d src=%s"
                    x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)
        dump "diff"  65 20
        dump "tap"   65 44
        dump "nsdm"  93 44
        dump "psdm"  94 20
        dump "nwell" 64 20

    [<Fact>]
    member _.``tap_mux_row difftap3 fire at (11420,-365)`` () =
        // PROBE: 175 nm gap between diff and tap. viz fires
        // difftap.3 (CrossSpacing diff↔tap, limit 0.27 µm).
        // Magic doesn't. Find the actual diff and tap polygons
        // that bound the gap.
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "wl_tap_mux", "tap_mux_row.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        // Widen the target bbox by 600 nm (>> limit 270) to catch
        // any contributing diff/tap polygon.
        let target = 10800L, -1000L, 12200L, 500L
        let (tx1, ty1, tx2, ty2) = target
        let overlaps (b: int64*int64*int64*int64) =
            let (x1, y1, x2, y2) = b
            tx1 < x2 && x1 < tx2 && ty1 < y2 && y1 < ty2
        let dump label num dt =
            let polys =
                flat
                |> Array.filter (fun p ->
                    p.Layer = num && p.DataType = dt
                    && overlaps (bboxOf p))
                |> Array.sortBy bboxOf
            out.WriteLine (sprintf "%s (%d/%d): %d" label num dt polys.Length)
            for p in polys do
                let (x1, y1, x2, y2) = bboxOf p
                out.WriteLine (sprintf
                    "  bbox=(%d,%d,%d,%d) %dx%d src=%s"
                    x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)
        dump "diff" 65 20
        dump "tap"  65 44
        dump "nsdm" 93 44
        dump "psdm" 94 20
        dump "nwell" 64 20

    [<Fact>]
    member _.``tap_mux_row psdm vs diff vs nwell at nwell5 fire`` () =
        // PROBE for the F# nwell.5 over-fire on tap_mux_row.
        // Hypothesis: the Enclosure rule checks bbox(psdm) vs
        // nwell, but Magic checks *pdiff = diff ∩ psdm. The pfet
        // primitive's psdm has a 125 nm halo on every side past
        // the diff. The halo eats into the nwell-enclosure
        // margin (180 → 55 by halo) while the actual p-diff is
        // enclosed by exactly 180 (Magic clean).
        //
        // Confirm by reading the flattened polygons and showing:
        //   * psdm bbox at the fire location
        //   * diff bbox at the same X range
        //   * parent nwell bbox
        // Predict: psdm.top = 1835, diff.top = 1710 (= 1835 - 125
        // halo), nwell.top = 1890. So nwell - diff.top = 180
        // (clean), nwell - psdm.top = 55 (fires by 125).
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(
                            d, "testdata", "cell_designs",
                            "wl_tap_mux", "tap_mux_row.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let target = 1565L, 1710L, 2545L, 1835L
        let (tx1, ty1, tx2, ty2) = target
        let bboxOverlaps (b: int64*int64*int64*int64) =
            let (x1, y1, x2, y2) = b
            tx1 < x2 && x1 < tx2 && ty1 < y2 && y1 < ty2
        let dump (label: string) (layer: int) (dt: int) =
            let polys =
                flat
                |> Array.filter (fun p ->
                    p.Layer = layer && p.DataType = dt
                    && bboxOverlaps (bboxOf p))
                |> Array.sortBy bboxOf
            out.WriteLine (sprintf
                "%s (%d/%d) overlapping fire bbox: %d" label layer dt polys.Length)
            for p in polys do
                let (x1, y1, x2, y2) = bboxOf p
                out.WriteLine (sprintf
                    "  bbox=(%d,%d,%d,%d) %dx%d src=%s"
                    x1 y1 x2 y2 (x2-x1) (y2-y1) p.SourceStructure)
        // 65/20 = diff, 65/44 = tap, 94/20 = psdm, 64/20 = nwell.
        dump "psdm" 94 20
        dump "diff" 65 20
        dump "tap"  65 44
        dump "nwell" 64 20
        // Also list ALL psdm/diff at the FULL X column (1565-2545),
        // ignoring the Y filter, so we can see the FULL extent of
        // the psdm and diff in that column.
        let columnOverlaps (b: int64*int64*int64*int64) =
            let (x1, _, x2, _) = b
            tx1 < x2 && x1 < tx2
        let dumpColumn (label: string) (layer: int) (dt: int) =
            let polys =
                flat
                |> Array.filter (fun p ->
                    p.Layer = layer && p.DataType = dt
                    && columnOverlaps (bboxOf p))
                |> Array.sortBy bboxOf
            out.WriteLine (sprintf
                "FULL COLUMN %s (%d/%d): %d" label layer dt polys.Length)
            for p in polys do
                let (x1, y1, x2, y2) = bboxOf p
                out.WriteLine (sprintf
                    "  bbox=(%d,%d,%d,%d) src=%s" x1 y1 x2 y2 p.SourceStructure)
        dumpColumn "psdm" 94 20
        dumpColumn "diff" 65 20
        dumpColumn "nwell" 64 20

    [<Fact>]
    member _.``opamp mcons near magic-only mcon2 bbox`` () =
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
        let mcons =
            flat |> Array.filter (fun p -> p.Layer = 67 && p.DataType = 44)
        // Magic-only bbox = (9410, 665, 9520, 775), 110x110.
        // Spacing limit = 38. Find mcons in a 1 µm radius.
        let nearby =
            mcons
            |> Array.filter (fun p ->
                let (x1, y1, x2, y2) = bboxOf p
                x1 < 10500L && 8500L < x2 && y1 < 1700L && -300L < y2)
        out.WriteLine (sprintf
            "mcons near bbox (9410,665,9520,775): %d" nearby.Length)
        let sorted =
            nearby |> Array.sortBy (fun p ->
                let (x1, y1, _, _) = bboxOf p
                x1, y1)
        for p in sorted do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  mcon bbox=(%d,%d,%d,%d) src=%s" x1 y1 x2 y2 p.SourceStructure)

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
