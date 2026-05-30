module Rekolektion.Viz.Core.Tests.DrcCheckTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Drc

let private rect (x1, y1, x2, y2) (layer, dt) : FlatPolygon =
    { Layer = layer
      DataType = dt
      Points = [|
        { X = int64 x1; Y = int64 y1 }
        { X = int64 x2; Y = int64 y1 }
        { X = int64 x2; Y = int64 y2 }
        { X = int64 x1; Y = int64 y2 }
        { X = int64 x1; Y = int64 y1 }
      |]
      SourceStructure = "test"
      SourceIndex = 0
      TopInstanceIndex = None }

// 1 nm/DBU so DBU = nm and the SKY130 0.14 µm met1 spacing = 140
// DBU. Synthetic cells use these scales to hit the spacing
// boundary cleanly.
let private units1nm : Units = { DbuNm = 1; UuUm = 1 }

[<Fact>]
let ``met1 spacing exactly at limit (140 nm) does not violate`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
        rect (340L, 0L, 540L, 200L) (68, 20)   // 140 nm gap
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.filter (fun x -> x.Rule = "met1.2")
      |> Array.length
      |> should equal 0

[<Fact>]
let ``met1 spacing 1 nm under limit triggers a violation`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
        rect (339L, 0L, 539L, 200L) (68, 20)   // 139 nm gap
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "met1.2" && x.MeasuredDbu = 139L)
      |> should equal true

[<Fact>]
let ``met1 width below 140 nm triggers a width violation`` () =
    let polys = [| rect (0L, 0L, 100L, 200L) (68, 20) |]   // 100 nm wide
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "met1.1" && x.MeasuredDbu = 100L)
      |> should equal true

[<Fact>]
let ``unknown layer (datatype 99) produces no violations`` () =
    let polys = [|
        rect (0L, 0L, 100L, 100L) (68, 99)
        rect (105L, 0L, 200L, 100L) (68, 99)   // 5 nm gap
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v.Length |> should equal 0

[<Fact>]
let ``poly spacing 0.21 µm = 210 nm enforced`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (66, 20)
        rect (200L + 209L, 0L, 200L + 409L, 200L) (66, 20)
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "poly.2")
      |> should equal true

[<Fact>]
let ``different layers don't trigger same-layer spacing`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20)        // met1
        rect (210L, 0L, 410L, 200L) (69, 20)      // met2 — different layer
    |]
    let v = Check.check Rules.defaultView units1nm polys
    // Filter to spacing rules — min-area (met1.6 / met2.6) fires
    // on both 0.04 µm² rects since they're under the 0.083 µm²
    // threshold, but that's a different rule class. This test is
    // about same-layer spacing NOT being applied across layers.
    v
    |> Array.filter (fun x -> x.Rule.EndsWith ".2" || x.Rule.EndsWith ".spacing")
    |> Array.length
    |> should equal 0

// ----------------------------------------------------------------
// Same-component step detection (nwell.2a thin-step in merged outline)
//
// Existing per-pair `bboxOrthoGapAndRegion` flags spacing between
// distinct connected components.  When two rects merge into one
// component (overlap on both axes) but form an inward outline step
// shorter than the rule limit, Magic flags it as a spacing
// violation; viz used to miss this entirely.  See
// docs/plans/viz_drc_step_detection.md for the algorithm.
//
// nwell.2a = 1.27 µm = 1270 DBU at 1 nm/DBU.
// ----------------------------------------------------------------

[<Fact>]
let ``nwell single rect: no spacing violations`` () =
    let polys = [| rect (0L, 0L, 5000L, 5000L) (64, 20) |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "nwell.2a")
      |> should equal false

[<Fact>]
let ``nwell two side-by-side rects (all edges aligned): no step violation`` () =
    // Two overlapping rects, same component, equal yMin AND yMax —
    // every potential step has dY = 0 / dX = 0 OR the inner edges
    // are covered by the other rect (not on outline).  A "long
    // horizontal block" with no inward outline steps.
    let polys = [|
        rect (0L, 0L, 1000L, 1000L) (64, 20)
        rect (500L, 0L, 2000L, 1000L) (64, 20)
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "nwell.2a")
      |> should equal false

[<Fact>]
let ``nwell nested rect (small inside big): no step violation`` () =
    // Inner rect's edges are all internal — *OnOutline = false for
    // every direction, so no step pair fires.
    let polys = [|
        rect (0L, 0L, 10000L, 10000L) (64, 20)
        rect (3000L, 3000L, 7000L, 7000L) (64, 20)
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "nwell.2a")
      |> should equal false

[<Fact>]
let ``nwell two disjoint rects with 500 nm gap: classical spacing fires`` () =
    let polys = [|
        rect (0L, 0L, 1000L, 1000L) (64, 20)
        rect (1500L, 0L, 2500L, 1000L) (64, 20)
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "nwell.2a" && x.MeasuredDbu = 500L)
      |> should equal true

[<Fact>]
let ``nwell strip + tub (top-top step, 22 nm): detected`` () =
    // Simplified reproducer of cim_reram_drv_phaseA_srcmux geometry.
    // strip: 8 µm wide × 4.235 µm tall.
    // tub:   4 µm wide, sitting "on top" of strip with 22 nm of Y
    //        overlap (i.e. tub.yMax = 4257 = strip.yMax + 22).
    // Both rects share the same connected component (strict overlap
    // on both axes); the existing per-pair gap loop skips them.
    // dY at top = 22 nm < 1270 → top-top step must fire.
    let polys = [|
        rect (0L, 0L, 8000L, 4235L) (64, 20)
        rect (1225L, 11L, 6310L, 4257L) (64, 20)
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "nwell.2a" && x.MeasuredDbu = 22L)
      |> should equal true

[<Fact>]
let ``nwell strip + tub (left-left step, 1225 nm): detected`` () =
    // Same geometry; the LEFT step is 1225 nm < 1270 limit, so the
    // left-left detector should also fire.
    let polys = [|
        rect (0L, 0L, 8000L, 4235L) (64, 20)
        rect (1225L, 11L, 6310L, 4257L) (64, 20)
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "nwell.2a" && x.MeasuredDbu = 1225L)
      |> should equal true

[<Fact>]
let ``nwell strip + tub mirrored Y (bottom-bottom step, 22 nm): detected`` () =
    // Tub poking off the BOTTOM of strip — mirror of top-top case.
    let polys = [|
        rect (0L, 0L, 8000L, 4235L) (64, 20)
        rect (1225L, -22L, 6310L, 4224L) (64, 20)
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "nwell.2a" && x.MeasuredDbu = 22L)
      |> should equal true

[<Fact>]
let ``nwell strip + tub rotated 90 (right-right step, 22 nm): detected`` () =
    // X<->Y swap of the top-top case. Verifies the right-right
    // detector mirrors the top-top detector under axis swap.
    let polys = [|
        rect (0L, 0L, 4235L, 8000L) (64, 20)
        rect (11L, 1225L, 4257L, 6310L) (64, 20)
    |]
    let v = Check.check Rules.defaultView units1nm polys
    v |> Array.exists (fun x -> x.Rule = "nwell.2a" && x.MeasuredDbu = 22L)
      |> should equal true
