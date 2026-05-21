module Rekolektion.Viz.Render.Tests.DrcOverlayTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Render.Skia

let private mkViolation rule : Check.Violation = {
    Rule        = rule
    LayerNumber = 68
    LayerType   = 20
    LimitDbu    = 140L
    MeasuredDbu = 139L
    BboxA       = (0L, 0L, 100L, 100L)
    BboxB       = None
}

[<Fact>]
let ``formatLabel includes provenance basename when the map has the rule`` () =
    let v = mkViolation "met2.2"
    let prov = Map.ofList [ "met2.2", "drc/overrides/v1_tapeout.yaml" ]
    let label = DrcOverlay.formatLabel prov 1.0e-3 v
    label |> should equal "met2.2 (v1_tapeout.yaml): 0.139<0.140 um"

[<Fact>]
let ``formatLabel omits parenthetical when no provenance entry exists`` () =
    let v = mkViolation "met1.2"
    let label = DrcOverlay.formatLabel Map.empty 1.0e-3 v
    label |> should equal "met1.2: 0.139<0.140 um"

[<Fact>]
let ``formatLabel ignores an empty-string provenance value`` () =
    let v = mkViolation "met1.2"
    let prov = Map.ofList [ "met1.2", "" ]
    let label = DrcOverlay.formatLabel prov 1.0e-3 v
    label |> should equal "met1.2: 0.139<0.140 um"

[<Fact>]
let ``formatLabel uses basename, not full path`` () =
    let v = mkViolation "poly.4"
    let prov =
        Map.ofList [ "poly.4", "/Users/bryan/project/drc/base/sky130.yaml" ]
    let label = DrcOverlay.formatLabel prov 1.0e-3 v
    label |> should equal "poly.4 (sky130.yaml): 0.139<0.140 um"

[<Fact>]
let ``formatLabel respects the umPerDbu scale`` () =
    // 5 nm/DBU → MeasuredDbu = 139 × 5 / 1000 = 0.695 µm.
    let v = mkViolation "met1.2"
    let label = DrcOverlay.formatLabel Map.empty 5.0e-3 v
    label |> should equal "met1.2: 0.695<0.700 um"
