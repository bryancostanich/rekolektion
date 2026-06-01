module Rekolektion.Viz.Core.Tests.RegionComponentsTests

// Tests for `Drc.Geometry.Components.componentBboxes`. The function
// implements DSU-over-slab-intervals and returns one bbox per
// connected component. Foundation for the region-based Width and
// Spacing rule rewrites (`docs/superpowers/plans/2026-05-31-
// region-based-drc-rules.md`).

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Drc.Geometry

let private sortByX1 (boxes: (int64 * int64 * int64 * int64) array) =
    boxes
    |> Array.sortBy (fun (x1, _, _, _) -> x1)

[<Fact>]
let ``componentBboxes of a single 100x500 rectangle returns one bbox`` () =
    let r = Region.ofRect 0L 0L 100L 500L
    let parts = Components.componentBboxes r
    parts.Length |> should equal 1
    parts.[0] |> should equal (0L, 0L, 100L, 500L)

[<Fact>]
let ``componentBboxes of two disjoint rectangles returns two bboxes`` () =
    let r =
        Boolean.union
            (Region.ofRect 0L 0L 100L 50L)
            (Region.ofRect 200L 0L 300L 50L)
    let parts = sortByX1 (Components.componentBboxes r)
    parts.Length |> should equal 2
    parts.[0] |> should equal (0L, 0L, 100L, 50L)
    parts.[1] |> should equal (200L, 0L, 300L, 50L)

[<Fact>]
let ``componentBboxes of an L-shape returns one bbox`` () =
    // Bottom horizontal arm 200x100; vertical arm 100x500 sharing
    // the same x=[0,100] band.
    let r =
        Boolean.union
            (Region.ofRect 0L 0L 200L 100L)
            (Region.ofRect 0L 0L 100L 500L)
    let parts = Components.componentBboxes r
    parts.Length |> should equal 1
    // The L's bbox is the AABB of both arms.
    parts.[0] |> should equal (0L, 0L, 200L, 500L)

[<Fact>]
let ``componentBboxes of П-bridged shape returns one bbox`` () =
    // Two vertical arms joined at the top by a horizontal bar.
    let r =
        Region.empty
        |> Boolean.union (Region.ofRect 0L 0L 50L 500L)         // left arm
        |> Boolean.union (Region.ofRect 150L 0L 200L 500L)      // right arm
        |> Boolean.union (Region.ofRect 0L 450L 200L 500L)      // top bar
    let parts = Components.componentBboxes r
    parts.Length |> should equal 1
    parts.[0] |> should equal (0L, 0L, 200L, 500L)

[<Fact>]
let ``componentBboxes of corner-touching rectangles returns two bboxes`` () =
    // 4-connectivity: pure corner touch (shared single point) leaves
    // the rectangles as separate components. Magic-compatible.
    let r =
        Boolean.union
            (Region.ofRect 0L 0L 100L 100L)
            (Region.ofRect 100L 100L 200L 200L)
    let parts = Components.componentBboxes r
    parts.Length |> should equal 2

[<Fact>]
let ``componentBboxes on bias_gen post-close PSDM has far fewer components than slab polygons`` () =
    // Round-trip validation: the post-close PSDM region for bias_gen
    // currently decomposes into ~130 slab polygons via
    // `Region.toPolygons`, causing Width-rule false fires. Connected-
    // component analysis must collapse them back to the small number
    // of actual implant "blobs" (single-digit to low double-digit).
    let path =
        System.Reflection.Assembly.GetExecutingAssembly().Location
        |> System.IO.Path.GetDirectoryName
        |> fun d -> System.IO.Path.Combine(
                        d, "testdata", "cell_designs",
                        "precision_ref", "bias_gen.rkt")
    if System.IO.File.Exists path then
        let doc, _w = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let psdm =
            flat
            |> Array.filter (fun p -> p.Layer = 94 && p.DataType = 20)
        let region = Region.ofPolygons psdm
        let closed =
            region
            |> Size.grow 190L
            |> Size.shrink 190L
        let parts = Components.componentBboxes closed
        let polygonCount = (Region.toPolygons 94 20 closed).Length
        // Real-world expectation: 130-ish slab polygons collapse to a
        // dozen or two components. Concretely require both an
        // absolute and a relative shrink so a regression jumps out.
        parts.Length |> should be (lessThan 30)
        parts.Length |> should be (lessThan (polygonCount / 4))
