module Rekolektion.Viz.Core.Tests.RegionMaxRectProbeTests

// PROBE — confirm the hypothesis that `Region.toPolygons` slab-
// decomposes a merged region into thin horizontal strips, causing
// Width/Spacing rules to fire on what should be one fat polygon.

open System
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Core.Drc.Geometry
open Rekolektion.Viz.Core.Rkt.Types

let private mkPoly (x1: int64) (y1: int64) (x2: int64) (y2: int64) : Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon =
    { Layer = 94
      DataType = 20
      Points =
        [| { X = x1; Y = y1 }
           { X = x2; Y = y1 }
           { X = x2; Y = y2 }
           { X = x1; Y = y2 } |]
      SourceStructure = "probe"
      SourceIndex = 0
      TopInstanceIndex = None
      Net = None }

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

type RegionMaxRectProbe(out : ITestOutputHelper) =

    [<Fact>]
    member _.``Region.toPolygons decomposes one big rect into one polygon`` () =
        // A single 1000x500 input rectangle should round-trip to one
        // polygon (sanity check).
        let inputs = [| mkPoly 0L 0L 1000L 500L |]
        let region = Region.ofPolygons inputs
        let outputs = Region.toPolygons 94 20 region
        out.WriteLine (sprintf "input: 1 polygon (1000x500)")
        out.WriteLine (sprintf "output: %d polygons" outputs.Length)
        for p in outputs do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  bbox=(%d,%d,%d,%d)  size=%dx%d"
                x1 y1 x2 y2 (x2-x1) (y2-y1))

    [<Fact>]
    member _.``Region.toPolygons of an L-shape: how many polygons`` () =
        // L: bottom horizontal arm 200x100 + vertical arm 100x500
        let inputs =
            [| mkPoly 0L 0L 200L 100L     // bottom arm
               mkPoly 0L 0L 100L 500L |]  // vertical arm
        let region = Region.ofPolygons inputs
        let outputs = Region.toPolygons 94 20 region
        out.WriteLine (sprintf "input: L-shape (2 rects, intersecting)")
        out.WriteLine (sprintf "output: %d polygons" outputs.Length)
        for p in outputs do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  bbox=(%d,%d,%d,%d)  size=%dx%d"
                x1 y1 x2 y2 (x2-x1) (y2-y1))

    [<Fact>]
    member _.``Region.toPolygons after grow+shrink merges two close rects`` () =
        // Two same-y-range rects 195 nm apart (close to the bias_gen
        // psdm.2 viz-only measurement).  Grow(190)+shrink(190)
        // should bridge them.
        let inputs =
            [| mkPoly 0L 0L 1000L 670L
               mkPoly 1195L 0L 2000L 670L |]
        let region = Region.ofPolygons inputs
        let closed =
            region
            |> Size.grow 190L
            |> Size.shrink 190L
        let outputs = Region.toPolygons 94 20 closed
        out.WriteLine (sprintf
            "input: 2 rects with 195 nm gap, 670 nm tall")
        out.WriteLine (sprintf
            "output after grow(190)+shrink(190): %d polygons" outputs.Length)
        let mutable narrowFires = 0
        for p in outputs do
            let (x1, y1, x2, y2) = bboxOf p
            let w = x2 - x1
            let h = y2 - y1
            let shorter = min w h
            out.WriteLine (sprintf
                "  bbox=(%d,%d,%d,%d)  size=%dx%d  shorter=%d"
                x1 y1 x2 y2 w h shorter)
            // psdm.1 / nsdm.1 width limit = 380 nm. Would a Width
            // rule fire on this strip?
            if shorter < 380L then narrowFires <- narrowFires + 1
        out.WriteLine (sprintf
            "polygons that would fire a 380 nm width rule: %d"
            narrowFires)

    [<Fact>]
    member _.``run applyImplantClose equivalent on bias_gen PSDM, dump output near viz-only psdm.2`` () =
        let path =
            System.Reflection.Assembly.GetExecutingAssembly().Location
            |> System.IO.Path.GetDirectoryName
            |> fun d -> System.IO.Path.Combine(d, "testdata", "cell_designs",
                                               "precision_ref", "bias_gen.rkt")
        if not (System.IO.File.Exists path) then
            out.WriteLine (sprintf "SKIP: missing %s" path)
        else
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        // PRE-close: filter PSDM polygons
        let psdmIn =
            flat
            |> Array.filter (fun p -> p.Layer = 94 && p.DataType = 20)
        out.WriteLine (sprintf
            "PRE-close PSDM total polygons: %d" psdmIn.Length)
        // POST-close: replicate applyImplantClose locally
        let region = Region.ofPolygons psdmIn
        let closed =
            region
            |> Size.grow 190L
            |> Size.shrink 190L
        let psdmOut = Region.toPolygons 94 20 closed
        out.WriteLine (sprintf
            "POST-close PSDM total polygons: %d" psdmOut.Length)
        // List POST-close PSDM polygons whose bbox covers x=[11050,11720]
        // — the band where viz-only psdm.2 fires.
        let inBand =
            psdmOut
            |> Array.filter (fun p ->
                let (x1, _, x2, _) = bboxOf p
                x1 <= 11720L && x2 >= 11050L)
            |> Array.sortBy (fun p -> let (_, y, _, _) = bboxOf p in y)
        out.WriteLine (sprintf
            "POST-close PSDM polygons covering x=[11050,11720]: %d" inBand.Length)
        for p in inBand do
            let (x1, y1, x2, y2) = bboxOf p
            out.WriteLine (sprintf
                "  bbox=(%d,%d,%d,%d)  size=%dx%d"
                x1 y1 x2 y2 (x2-x1) (y2-y1))
