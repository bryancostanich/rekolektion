module Rekolektion.Viz.Core.Tests.BiasGenLi3ProbeTests

// Headless probe — OBSERVE step of the debug protocol applied to the
// MagicVsViz parity failure on bias_gen.rkt.
//
// Magic reports 28 li.3 (LI spacing) tiles in two vertical columns
// near x = 4925 nm and x = 4755 nm, y in -20150 to -21505 nm, and
// 12 met1.2 (Metal1 spacing) tiles near x = 4615 / 4475 nm in the
// same y range. viz misses every one of these.
//
// This probe loads bias_gen.rkt the same way `Drc.Check.check`
// would, then asks: what polygons does viz actually see on li1
// (67/20) and met1 (68/20) in those channels? If we see two
// polygons with a sub-min gap, the bug is in viz's spacing
// algorithm. If we don't — only one merged polygon, or the
// geometry is on a different layer — the bug is upstream
// (flattening, layer mapping, import resolution).
//
// The test is structured as a Fact (so it runs headlessly with the
// rest of the suite), but it logs to `ITestOutputHelper` rather
// than asserting — its job is to print the truth so we can read it.

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

let private rktPath () =
    testDataPath "testdata/cell_designs/precision_ref/bias_gen.rkt"

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

type BiasGenLi3Probe(out : ITestOutputHelper) =

    [<Fact>]
    member _.``probe li1 polygons in the magic li.3 channel`` () =
        let path = rktPath ()
        if not (File.Exists path) then
            out.WriteLine (sprintf "SKIP: fixture missing at %s" path)
        else

        let doc, _warnings = LayoutLoader.load path
        let flat = Flatten.flatten doc
        out.WriteLine (sprintf "total polygons after flatten: %d" flat.Length)

        // Rough X-Y window covering BOTH the x=4925 and x=4755
        // li.3 columns Magic reports, plus a margin on either
        // side. Y-range matches Magic's stack of tiles.
        let probeBbox : int64 * int64 * int64 * int64 =
            (4500L, -22000L, 5100L, -19500L)
        let (px1, py1, px2, py2) = probeBbox
        out.WriteLine (sprintf
            "probe window: x=[%d,%d] y=[%d,%d] (DBU=nm)"
            px1 px2 py1 py2)

        let interesting (layerN : int) (layerDt : int) =
            flat
            |> Array.filter (fun p ->
                p.Layer = layerN
                && p.DataType = layerDt
                && bboxesOverlap (polyBbox p) probeBbox)

        // li1 = 67/20 — the layer Magic reports li.3 on.
        let li1 = interesting 67 20
        out.WriteLine (sprintf "li1 (67/20) polygons in window: %d" li1.Length)
        for p in li1 do
            let (x1, y1, x2, y2) = polyBbox p
            out.WriteLine (sprintf
                "  li1 bbox=(%d,%d,%d,%d)  size=%dx%d  cell=%s"
                x1 y1 x2 y2 (x2 - x1) (y2 - y1) p.SourceStructure)

        // met1 = 68/20 — channel x=4615/4475
        let met1Probe : int64 * int64 * int64 * int64 =
            (4400L, -22000L, 4700L, -19500L)
        let (mx1, my1, mx2, my2) = met1Probe
        out.WriteLine (sprintf
            "met1 probe window: x=[%d,%d] y=[%d,%d]" mx1 mx2 my1 my2)
        let met1 =
            flat
            |> Array.filter (fun p ->
                p.Layer = 68
                && p.DataType = 20
                && bboxesOverlap (polyBbox p) met1Probe)
        out.WriteLine (sprintf "met1 (68/20) polygons in window: %d" met1.Length)
        for p in met1 do
            let (x1, y1, x2, y2) = polyBbox p
            out.WriteLine (sprintf
                "  met1 bbox=(%d,%d,%d,%d)  size=%dx%d  cell=%s"
                x1 y1 x2 y2 (x2 - x1) (y2 - y1) p.SourceStructure)

        // Widen the probe to see polygons whose EDGE is in this
        // region — the spacing partner could sit outside the bbox.
        let widerProbe : int64 * int64 * int64 * int64 =
            (4500L, -22000L, 6000L, -19000L)
        let widerLi1 =
            flat
            |> Array.filter (fun p ->
                p.Layer = 67
                && p.DataType = 20
                && bboxesOverlap (polyBbox p) widerProbe)
            |> Array.sortBy (fun p -> let (x1,_,_,_) = polyBbox p in x1)
        out.WriteLine (sprintf
            "WIDER li1 polygons (x=[4500,6000]): %d" widerLi1.Length)
        for p in widerLi1 do
            let (x1, y1, x2, y2) = polyBbox p
            out.WriteLine (sprintf
                "  li1 bbox=(%d,%d,%d,%d)  size=%dx%d  cell=%s"
                x1 y1 x2 y2 (x2 - x1) (y2 - y1) p.SourceStructure)

        // Run viz DRC on the full layout and report what li.3 /
        // met1.2 fires viz actually finds inside the probe window.
        let units = doc.Units
        let allViolations =
            Drc.Check.check Drc.Rules.defaultView units flat
        let inWindow (v : Drc.Check.Violation) =
            bboxesOverlap v.BboxA probeBbox
            || (match v.BboxB with
                | Some b -> bboxesOverlap b probeBbox
                | None -> false)

        let li3 =
            allViolations
            |> Array.filter (fun v -> v.Rule = "li.3" && inWindow v)
        let met12 =
            allViolations
            |> Array.filter (fun v -> v.Rule = "met1.2" && inWindow v)
        out.WriteLine (sprintf
            "viz violations in probe window: li.3=%d  met1.2=%d"
            li3.Length met12.Length)

        // Direct probe: bbox geometry between polygons (1) and (3)
        // as described in the OBSERVE step. (1) = pfet primitive
        // vertical strip at x=[4923,5093]; (3) = block vertical
        // strip at x=[4594,4764]. The gap between (3)'s right edge
        // and (1)'s left edge = 4923 - 4764 = 159 nm; li.3 rule
        // = 170 nm. The pair has y-overlap [-21335, -19980], so
        // there IS a facing edge — Magic flags it.
        //
        // viz's Spacing algorithm builds a DSU over connected
        // (bbox-overlapping) polygons and SKIPS pairs in the same
        // component. Polygon (2) is the H-bar at y=[-20150,-19980]
        // that bridges (1) and (3) via bbox-overlap with both — so
        // viz puts (1), (2), (3) in one component and never tests
        // the (1)↔(3) gap below the bridge. Confirm by spelling
        // out the connectivity check.
        let p1Bb : int64 * int64 * int64 * int64 = (4923L, -21335L, 5093L, -18795L)
        let p2Bb : int64 * int64 * int64 * int64 = (4594L, -20150L, 5093L, -19980L)
        let p3Bb : int64 * int64 * int64 * int64 = (4594L, -21685L, 4764L, -19980L)
        let touches
                ((ax1, ay1, ax2, ay2) : int64 * int64 * int64 * int64)
                ((bx1, by1, bx2, by2) : int64 * int64 * int64 * int64) =
            let xStrict = ax1 < bx2 && bx1 < ax2
            let yStrict = ay1 < by2 && by1 < ay2
            let xClosed = ax1 <= bx2 && bx1 <= ax2
            let yClosed = ay1 <= by2 && by1 <= ay2
            (xStrict && yClosed) || (yStrict && xClosed)
        out.WriteLine (sprintf
            "connectivity:  (1)↔(2)=%b  (2)↔(3)=%b  (1)↔(3)=%b"
            (touches p1Bb p2Bb) (touches p2Bb p3Bb) (touches p1Bb p3Bb))
        // Gap (1)↔(3): (1) left edge x=4923, (3) right edge x=4764.
        out.WriteLine (sprintf
            "horizontal gap (3.right=4764) -> (1.left=4923) = %d nm (li.3 limit = 170 nm)"
            (4923L - 4764L))

        // PROBE the proposed fix: gap bbox between (1) and (3).
        // For each OTHER li1 polygon in the layout, does its bbox
        // FULLY CONTAIN the gap region? If any does, the gap is
        // bridged across its full y-span and Magic would not fire.
        // If none does, the gap is genuinely open along some part
        // of its length — fire.
        let gapBbox : int64 * int64 * int64 * int64 =
            (4764L, -21335L, 4923L, -19980L)
        let (gx1, gy1, gx2, gy2) = gapBbox
        let allLi1 =
            flat
            |> Array.filter (fun p -> p.Layer = 67 && p.DataType = 20)
        out.WriteLine (sprintf
            "ALL li1 polygons in layout: %d  gap=(%d,%d,%d,%d)"
            allLi1.Length gx1 gy1 gx2 gy2)
        let containers =
            allLi1
            |> Array.choose (fun p ->
                let (x1, y1, x2, y2) = polyBbox p
                if x1 <= gx1 && y1 <= gy1 && x2 >= gx2 && y2 >= gy2
                   && not (x1 = 4923L && x2 = 5093L)   // exclude (1)
                   && not (x1 = 4594L && x2 = 4764L) then  // exclude (3)
                    Some (x1, y1, x2, y2)
                else None)
        out.WriteLine (sprintf
            "other li1 polygons FULLY CONTAINING the gap region: %d"
            containers.Length)
        for (x1, y1, x2, y2) in containers do
            out.WriteLine (sprintf
                "  container li1 bbox=(%d,%d,%d,%d)" x1 y1 x2 y2)

        // Probe one of the new viz-only fires from the post-fix
        // bias_gen run: li.3 at bbox=(23542,-8755,23649,-8584),
        // measured=107 nm. Check whether MULTIPLE other li1
        // polygons together cover the gap region (which would mean
        // my single-poly containment is too strict).
        let li3Gap : int64 * int64 * int64 * int64 =
            (23542L, -8755L, 23649L, -8584L)
        let (lgx1, lgy1, lgx2, lgy2) = li3Gap
        // Find any li1 polygon whose bbox INTERSECTS the gap region
        // (excluding extreme outliers).
        let intersectors =
            allLi1
            |> Array.choose (fun p ->
                let (x1, y1, x2, y2) = polyBbox p
                if x1 <= lgx2 && x2 >= lgx1 && y1 <= lgy2 && y2 >= lgy1
                then Some (x1, y1, x2, y2)
                else None)
        out.WriteLine (sprintf
            "li1 polygons intersecting the viz-only li.3 gap (%d,%d,%d,%d): %d"
            lgx1 lgy1 lgx2 lgy2 intersectors.Length)
        for (x1, y1, x2, y2) in intersectors do
            out.WriteLine (sprintf
                "  li1 bbox=(%d,%d,%d,%d)" x1 y1 x2 y2)
