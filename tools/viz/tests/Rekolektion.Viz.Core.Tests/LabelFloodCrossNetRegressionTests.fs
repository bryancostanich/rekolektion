module Rekolektion.Viz.Core.Tests.LabelFloodCrossNetRegressionTests

// Regression for "every net claims every wire" — Bryan reported
// 2026-06-05 that a via dropped on a met2 wire near a SEL label
// got attributed to NAND_OUT.  Headless probe confirmed: that
// wire was claimed by seven different nets simultaneously, and
// `Obstacles.netOf` returned the first claimant in map order.
//
// Root cause: the licon (66/44) contact-layer bridges in
// `LabelFlood.contactBridges` listed diff (65/20), tap (65/44),
// and poly (66/20) alongside li1 (67/20).  Once the flood
// reached a licon it would walk into diff regions; diff polygons
// of touching FETs got connected via same-layer flood; from
// there the flood climbed back up other licons on other nets'
// li1.  Every net's label ended up claiming the entire cell.
//
// Correct behaviour: licon connects a single li1 polygon to a
// single diff / poly terminal.  It does NOT bridge across diff
// or poly regions to other FETs' contacts.

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Net
open Rekolektion.Viz.Core.Layout

let private floodMacroPath =
    "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/wl_tap_mux/tap_mux_decoder_slot.rkt"

[<Fact>]
let ``no net claims every wire — SEL excludes far-left polys, NAND_OUT excludes the SEL wire`` () =
    let doc, _ = LayoutLoader.load floodMacroPath
    let nets = LabelFlood.derive doc
    // The met2 wire at (2785..3885, 2865..3005), source
    // `tap_mux_decoder_slot.104`, sits under SEL labels at
    // (2855, 2935) and (3815, 2935) — both on li1_label with the
    // wire's li1/met1 pin stacks beneath the labels.  It must
    // belong to SEL, not NAND_OUT.
    let inNet (net: string) (structure: string) (idx: int) (layerN: int) (dt: int) =
        match Map.tryFind net nets with
        | None -> false
        | Some entry ->
            entry.Polygons
            |> List.exists (fun p ->
                p.Structure = structure && p.Index = idx
                && p.Layer = layerN && p.DataType = dt)
    inNet "SEL"      "tap_mux_decoder_slot" 104 69 20 |> should equal true
    inNet "NAND_OUT" "tap_mux_decoder_slot" 104 69 20 |> should equal false

[<Fact>]
let ``no net claims every poly — big nets diverge in poly count`` () =
    // Pre-fix every "big" net (SEL, NAND_OUT, VDD, VSS, mid_1,
    // mid_2, mid_3) had identical 191 polygons because they all
    // flooded through the same diff/poly bridges.  Post-fix the
    // counts MUST differ — VDD and VSS reach the tap rails;
    // SEL/NAND_OUT/mid_* reach only signal-routed polys; no
    // single number should match across all of them.
    let doc, _ = LayoutLoader.load floodMacroPath
    let nets = LabelFlood.derive doc
    let bigNetNames = ["SEL"; "NAND_OUT"; "VDD"; "VSS"; "mid_1"; "mid_2"; "mid_3"]
    let counts =
        bigNetNames
        |> List.choose (fun n ->
            Map.tryFind n nets
            |> Option.map (fun e -> n, e.Polygons.Length))
    // Assert: the seven big-net polygon counts are NOT all equal.
    let distinct = counts |> List.map snd |> List.distinct
    if List.length distinct <= 1 then
        failwithf "All big nets claim the same number of polys: %A" counts
