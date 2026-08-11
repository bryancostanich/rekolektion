module Rekolektion.Viz.Core.Tests.RoutingPadsTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Core.Drc.Rules
open Rekolektion.Viz.Core.Routing

let private units1nm : Units = { DbuNm = 1; UuUm = 1 }

let private met1Key : int * int = (68, 20)
let private met2Key : int * int = (69, 20)
let private met3Key : int * int = (70, 20)

// --- DRC-driven pad sizes against the real Rules.allRules --------------

[<Fact>]
let ``met1 endpoint pad is 320 nm (via.4b enclosure dominates)`` () =
    // via.4b AsymEnclosure(met1, via, 0.085, 0.055) + via width 0.15
    // → 150 + 2*85 = 320 nm. met1.5 mcon enclosure → 290 nm.
    // met1.6 min-area 0.083 µm² → 288 nm. Max = 320 nm.
    Pads.endpointPadSide Rules.defaultView units1nm met1Key
    |> should equal (Some 320L)

[<Fact>]
let ``met2 endpoint pad is 370 nm (via2.4 long axis dominates)`` () =
    // via2.4 AsymEnclosure(met2, via2, 0.04, 0.085) + via2 width 0.20
    // → 200 + 2*85 = 370 nm. met2.6 min-area 0.0676 µm² → 260 nm.
    // via.5a Enclosure(met2, via, 0.055) + via1 width 0.15 → 260 nm.
    // Max = 370 nm.
    Pads.endpointPadSide Rules.defaultView units1nm met2Key
    |> should equal (Some 370L)

[<Fact>]
let ``met3 endpoint pad is 489 nm (met3.6 min-area dominates)`` () =
    // via2.5 AsymEnclosure(met3, via2, 0.065, 0.095) + via2 width 0.20
    // → 200 + 2*95 = 390 nm. met3.6 min-area 0.240 µm² → sqrt ≈
    // 0.490 µm = 489 nm (int64 truncation). Min-area wins.
    Pads.endpointPadSide Rules.defaultView units1nm met3Key
    |> should equal (Some 489L)

[<Fact>]
let ``li1 endpoint pad is None — primitives manage their own li1 pin patches`` () =
    // li1 is explicitly excluded from router-emitted pads. Pin
    // patches on li1 come from the primitive generators
    // (gen_*_core → pin_patch). Painting a knuckle here would
    // either visually duplicate the existing patch or trip
    // `mcon.2` against the primitive's mcons.
    let li1Key : int * int = (67, 20)
    Pads.endpointPadSide Rules.defaultView units1nm li1Key
    |> should equal (None : int64 option)

[<Fact>]
let ``endpointPadSide returns None for a layer absent from the rule table`` () =
    // A synthetic layer key that no rule mentions → callers leave
    // the endpoint bare. (Every routing layer in current sky130
    // does have at least one enclosure-as-outer rule; this is the
    // "ruleset doesn't cover this layer at all" case.)
    let unknownKey : int * int = (999, 99)
    Pads.endpointPadSide Rules.defaultView units1nm unknownKey
    |> should equal (None : int64 option)

[<Fact>]
let ``endpointPadSide respects a custom view's rules (not Rules.allRules)`` () =
    // A view whose ONLY enclosure rule for met1 says "200 nm
    // enclosure around a 100 nm inner" → 100 + 400 = 500 nm.
    let met1 : LayerKey = { Number = 68; DataType = 20 }
    let dummy : LayerKey = { Number = 999; DataType = 0 }
    let view : RulesetView = {
        Rules = [
            Width ("dummy.width", dummy, 0.10)
            Enclosure ("custom.met1.encl", met1, dummy, 0.20, Always)
        ]
        Provenance = Map.empty
    }
    Pads.endpointPadSide view units1nm met1Key
    |> should equal (Some 500L)

// --- Draft.endpointPads ----------------------------------------------

[<Fact>]
let ``endpointPads with a single-point route emits one pad at the anchor`` () =
    let r = Draft.start met1Key 320L (0L, 0L)
    let pads = Draft.endpointPads 290L r
    pads.Length |> should equal 1
    pads.[0].X1 |> should equal -145L
    pads.[0].X2 |> should equal 145L

[<Fact>]
let ``endpointPads with anchor + cursor emits a pad at each`` () =
    let r =
        Draft.start met1Key 320L (0L, 0L)
        |> Draft.setCursor (1000L, 500L)
    let pads = Draft.endpointPads 290L r
    pads.Length |> should equal 2
    // First pad centered at anchor (0,0).
    pads.[0].X1 |> should equal -145L
    pads.[0].Y1 |> should equal -145L
    // Second pad centered at cursor (1000,500).
    pads.[1].X1 |> should equal 855L
    pads.[1].Y1 |> should equal 355L

[<Fact>]
let ``endpointPads uses the last fixed point when cursor is None`` () =
    let r =
        Draft.start met1Key 320L (0L, 0L)
        |> Draft.setCursor (500L, 0L)
        |> Draft.fix
    let pads = Draft.endpointPads 290L r
    pads.Length |> should equal 2
    pads.[1].X1 |> should equal 355L   // pad at (500, 0)

// --- wireWidthFor -----------------------------------------------------

[<Fact>]
let ``wireWidthFor met1 is 140 nm (met1.1 width rule)`` () =
    Pads.wireWidthFor Rules.defaultView units1nm met1Key
    |> should equal (Some 140L)

[<Fact>]
let ``wireWidthFor met2 is 140 nm`` () =
    Pads.wireWidthFor Rules.defaultView units1nm met2Key
    |> should equal (Some 140L)

[<Fact>]
let ``wireWidthFor met3 is 300 nm`` () =
    Pads.wireWidthFor Rules.defaultView units1nm met3Key
    |> should equal (Some 300L)

[<Fact>]
let ``wireWidthFor li1 is 170 nm (li.1 width rule)`` () =
    let li1Key : int * int = (67, 20)
    Pads.wireWidthFor Rules.defaultView units1nm li1Key
    |> should equal (Some 170L)

[<Fact>]
let ``wireWidthFor returns None for a layer without a Width rule`` () =
    let unknownKey : int * int = (999, 99)
    Pads.wireWidthFor Rules.defaultView units1nm unknownKey
    |> should equal (None : int64 option)

[<Fact>]
let ``endpointPads emits nothing when padSide is zero or negative`` () =
    let r = Draft.start met1Key 320L (0L, 0L)
    Draft.endpointPads 0L r |> should be Empty
    Draft.endpointPads -1L r |> should be Empty

// --- dropPadsContainedByForeignPolys ----------------------------------
//
// Synthetic enclosure pads (the snap-layer pad from `ViaStack.emitAt`,
// plus any future snap-side pad on the wire's own layer) exist only to
// give an adjacent via the metal enclosure DRC demands. When the
// caller is snapping onto an EXISTING foreign polygon that is itself
// big enough to enclose the via cut by the DRC rule, the synthetic
// pad is redundant geometry — a "knuckle" stacked on top of the
// foreign poly.
//
// User report (tap_mux_input_inv.rkt, 2026-05-31): the bottom VSS
// route is a li1 wire dropping into the parent VSS rail on met1. The
// rail covers 1995 × 260 µm; the mcon cut is 170 × 170 nm centered
// inside it. The synthetic met1 snap-pad emitted by `ViaStack.emitAt`
// stacks a 290 × 290 nm square on top of the rail — visible as the
// "knuckle".
//
// The filter is restricted to pad-shaped segments (metal layers); via
// cuts (mcon, via, via2…) are NEVER dropped because the via is the
// physical layer transition. Removing the cut would leave the
// connection electrically broken even if the foreign poly is wide
// enough to LOOK like enclosure.

let private li1Key  : int * int = (67, 20)
let private mconKey : int * int = (67, 44)

let private foreignPoly (layer : int * int) (x1 : int64) (y1 : int64) (x2 : int64) (y2 : int64)
        : Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon =
    {
        Layer = fst layer
        DataType = snd layer
        Points = [|
            { X = x1; Y = y1 }
            { X = x2; Y = y1 }
            { X = x2; Y = y2 }
            { X = x1; Y = y2 }
            { X = x1; Y = y1 }
        |]
        SourceStructure = "test"
        SourceIndex = 0
        TopInstanceIndex = None
        Net = None
    }

let private padSeg (layer : int * int) (x1 : int64) (y1 : int64) (x2 : int64) (y2 : int64)
        : Draft.DraftSegment =
    { Layer = layer; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2 }

[<Fact>]
let ``dropPadsContainedByForeignPolys with no foreign polys returns input unchanged`` () =
    let segs = [ padSeg met1Key -145L -145L 145L 145L ]
    Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [||] segs
    |> should equal segs

[<Fact>]
let ``met1 pad fully inside a met1 foreign poly is dropped`` () =
    // Mirrors the tap_mux_input_inv VSS case: huge met1 rail covers
    // the entire mcon footprint, so the synthetic 290-nm met1
    // snap-pad is redundant.
    let rail = foreignPoly met1Key -600L -1260L 1395L -1000L
    let pad  = padSeg met1Key 252L -1275L 542L -985L
    // Pad's Y extends past rail's Y on both ends (-1275 < -1260, -985 > -1000)
    // — NOT fully contained. Use a pad inside rail:
    let pad' = padSeg met1Key 252L -1255L 542L -1005L
    Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| rail |] [ pad' ]
    |> should be Empty

[<Fact>]
let ``met1 pad sticking past the rail with NO paired via cut is kept`` () =
    // Pad sticks past the rail and there's no co-centered via cut
    // in the batch to test against. The filter can't prove the
    // pad's role is redundant → keep.
    let rail = foreignPoly met1Key -600L -1260L 1395L -1000L
    let pad  = padSeg met1Key 252L -1275L 542L -985L
    Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| rail |] [ pad ]
    |> should equal [ pad ]

[<Fact>]
let ``met1 pad sticking past the rail IS dropped when its paired mcon fits inside the rail`` () =
    // The actual user case (tap_mux_input_inv): the rail is narrow
    // (260 nm Y) so the 290 nm synthetic pad sticks 15 nm proud on
    // each Y side. But the 170 nm mcon at the same centre fits
    // cleanly inside the rail — so the rail provides legal
    // enclosure on its own and the pad is redundant.
    let rail = foreignPoly met1Key -600L -1260L 1395L -1000L
    let pad  = padSeg met1Key 252L -1275L 542L -985L    // sticks past
    let mcon = padSeg mconKey 312L -1215L 482L -1045L   // inside rail
    let result = Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| rail |] [ pad; mcon ]
    // Pad dropped, mcon kept.
    result |> List.exists (fun s -> s.Layer = met1Key) |> should equal false
    result |> List.exists (fun s -> s.Layer = mconKey) |> should equal true

[<Fact>]
let ``met1 pad is kept when paired mcon also sticks past the foreign poly`` () =
    // Negative control: rail too narrow to enclose the mcon. The
    // pad is genuinely needed to top up the enclosure on the
    // exposed sides.
    let narrowRail = foreignPoly met1Key -600L -1180L 1395L -1080L
    let pad  = padSeg met1Key 252L -1275L 542L -985L
    let mcon = padSeg mconKey 312L -1215L 482L -1045L   // sticks past narrowRail
    let result = Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| narrowRail |] [ pad; mcon ]
    result |> List.exists (fun s -> s.Layer = met1Key) |> should equal true
    result |> List.exists (fun s -> s.Layer = mconKey) |> should equal true

[<Fact>]
let ``mcon CUT is never dropped, even when fully inside a met1 foreign poly`` () =
    // The mcon is the layer transition itself — dropping it would
    // electrically break the route. Only pad-shaped (metal-layer)
    // segments are eligible for suppression.
    let rail = foreignPoly met1Key -600L -1260L 1395L -1000L
    let mcon = padSeg mconKey 312L -1215L 482L -1045L
    Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| rail |] [ mcon ]
    |> should equal [ mcon ]

[<Fact>]
let ``met1 pad inside a met2 foreign poly is kept (layer mismatch)`` () =
    // Cross-layer containment is irrelevant — a met2 poly above
    // cannot provide met1's enclosure of via1.
    let met2Poly = foreignPoly met2Key -600L -1260L 1395L -1000L
    let pad      = padSeg met1Key 252L -1255L 542L -1005L
    Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| met2Poly |] [ pad ]
    |> should equal [ pad ]

[<Fact>]
let ``li1 pad inside a li1 foreign poly is dropped`` () =
    // Same principle for li1 — a fat parent-painted li1 strap
    // already encloses the mcon, so the synthetic li1 snap-pad
    // is redundant (mirrors the noPadLayers logic for the wire
    // endpoint, but covers the cross-layer snap-pad emission path).
    let strap = foreignPoly li1Key 0L 0L 1000L 1000L
    let pad   = padSeg li1Key 100L 100L 400L 400L
    Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| strap |] [ pad ]
    |> should be Empty

[<Fact>]
let ``mixed segments — strict-contained pad AND pad-paired-with-contained-cut both drop`` () =
    let rail = foreignPoly met1Key -600L -1260L 1395L -1000L
    let containedPad   = padSeg met1Key 252L -1255L 542L -1005L  // drop (strict bbox)
    let exposedPad     = padSeg met1Key 252L -1275L 542L -985L   // drop (paired mcon inside)
    let mcon           = padSeg mconKey 312L -1215L 482L -1045L  // keep (via cut)
    let otherLayerPad  = padSeg li1Key  100L 100L 400L 400L      // keep (no matching foreign)
    let input = [ containedPad; exposedPad; mcon; otherLayerPad ]
    Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| rail |] input
    |> should equal [ mcon; otherLayerPad ]

[<Fact>]
let ``identical bbox (foreign equals pad) counts as contained → pad dropped`` () =
    // Edge case: foreign poly's bbox is exactly the pad's bbox. The
    // pad adds no metal beyond what the foreign already provides
    // (and is a byte-identical duplicate that dedupCoincidentRects
    // would also collapse).
    let rail = foreignPoly met1Key 0L 0L 290L 290L
    let pad  = padSeg met1Key 0L 0L 290L 290L
    Pads.dropPadsContainedByForeignPolys Rules.defaultView units1nm [| rail |] [ pad ]
    |> should be Empty

// --- enclosure-aware checks --------------------------------------
//
// User report 2026-05-31 (b1_5_stage2 tail2 jumper): a met3 wire
// descending to an li1 pin had its intermediate met1 pad silently
// dropped, leaving via1 with insufficient metal enclosure under
// magic DRC (via.4b — met1 enclosure of via1 < 0.085 µm).
//
// Root cause: the filter treated "via cut bbox contained in foreign
// poly bbox" as sufficient. But containment alone doesn't satisfy
// sky130's asymmetric via.4b/via.5b enclosure rules — those require
// ≥85 nm on the strict axis. A primitive's 290×290 nm met1 contact
// pad contains a 150×150 nm via1 cut with only 70 nm enclosure on
// every side — fails the strict axis, so the synthetic pad must
// stay so the metal merges to a wider polygon that DOES satisfy.
//
// The fix: check enclosure-rule satisfaction against the active
// DRC view (asym strict-axis / relaxed-axis semantics, per-rule
// max over multiple matching rules), fall back to simple bbox
// containment only when the pad has no paired via cut.

let private via1Key : int * int = (68, 44)
let private mconKey2 : int * int = (67, 44)

[<Fact>]
let ``met1 pad with paired via1 KEPT when foreign met1 only provides 70 nm enclosure`` () =
    // 290x290 nm met1 around a 150x150 nm via1 → 70 nm per side.
    // via.4b asym needs ≥85 nm on the strict axis → fails → KEEP pad.
    let primMet1 = foreignPoly met1Key -145L -145L 145L 145L
    let pad      = padSeg met1Key -160L -160L 160L 160L   // synthetic 320×320
    let via1     = padSeg via1Key -75L -75L 75L 75L       // 150×150
    let result =
        Pads.dropPadsContainedByForeignPolys
            Rules.defaultView units1nm
            [| primMet1 |] [ pad; via1 ]
    result |> List.exists (fun s -> s.Layer = met1Key) |> should equal true
    result |> List.exists (fun s -> s.Layer = via1Key) |> should equal true

[<Fact>]
let ``met1 pad with paired via1 DROPPED when foreign met1 provides ≥85 nm enclosure on every side`` () =
    // 320x320 nm foreign met1 = full enclosure pad-equivalent.
    // 85 nm per side ≥ via.4b strict axis → drop synthetic pad.
    let foreign = foreignPoly met1Key -160L -160L 160L 160L
    let pad     = padSeg met1Key -160L -160L 160L 160L
    let via1    = padSeg via1Key -75L -75L 75L 75L
    let result =
        Pads.dropPadsContainedByForeignPolys
            Rules.defaultView units1nm
            [| foreign |] [ pad; via1 ]
    result |> List.exists (fun s -> s.Layer = met1Key) |> should equal false

[<Fact>]
let ``met1 pad KEPT when foreign strap satisfies mcon but NOT via1 (b1_5_stage2)`` () =
    // The b1_5_stage2 tail2-pin geometry (2026-05-31):
    //   - primitive li1/mcon/met1 contact stack on a multi-finger
    //     nfet
    //   - met1 strap 2000 × 230 nm at (1203, 3005)–(3203, 3235)
    //     around the via centre (2203, 3120)
    //   - via1 cut 150×150 nm: enclosure on Y axis is only 40 nm
    //   - mcon 170×170 nm: enclosure on Y axis is 30 nm
    //
    // Two enclosure rules apply to the synthetic met1 pad:
    //   * met1.5 (met1 around mcon): asym 60/30 — strap provides
    //     X=915, Y=30 → satisfied (60 on X axis, 30 on Y).
    //   * via.4b (met1 around via1): asym 85/55 — strap provides
    //     X=925, Y=40 → NOT satisfied (40 < 55 on Y axis even
    //     though 925 ≥ 85 on X).
    //
    // The strap services mcon but not via1, so the synthetic met1
    // pad must be KEPT. Pre-this-fix the filter dropped it because
    // it used List.exists (any one cut "covered" → drop), letting
    // the mcon match veto the via1 deficit.
    let strap = foreignPoly met1Key 1203L 3005L 3203L 3235L
    let met1Pad = padSeg met1Key 2043L 2960L 2363L 3280L  // 320×320
    let mconCut = padSeg mconKey 2118L 3035L 2288L 3205L  // 170×170
    let via1Cut = padSeg via1Key 2128L 3045L 2278L 3195L  // 150×150
    let result =
        Pads.dropPadsContainedByForeignPolys
            Rules.defaultView units1nm
            [| strap |] [ met1Pad; mconCut; via1Cut ]
    result |> List.exists (fun s -> s.Layer = met1Key) |> should equal true
    result |> List.exists (fun s -> s.Layer = mconKey) |> should equal true
    result |> List.exists (fun s -> s.Layer = via1Key) |> should equal true

[<Fact>]
let ``asym 0/80 strap satisfies enclosure on long axis only (j_az_col case)`` () =
    // Wide met1 rail provides ~285 nm on X (one axis) and ~45 nm
    // on Y (other axis) around a 170 nm mcon. met1.5 asym 0/60
    // wants ≥60 nm on one axis, ≥0 on the other. X-axis 285 ≥ 60
    // ✓; Y-axis 45 ≥ 0 ✓ → drop synthetic pad. Regression guard
    // for the j_az_col bottom VSS knuckle fix (bbfe649).
    let rail = foreignPoly met1Key -600L -1260L 1395L -1000L
    let pad  = padSeg met1Key 252L -1275L 542L -985L     // sticks past
    let mcon = padSeg mconKey2 312L -1215L 482L -1045L
    let result =
        Pads.dropPadsContainedByForeignPolys
            Rules.defaultView units1nm
            [| rail |] [ pad; mcon ]
    result |> List.exists (fun s -> s.Layer = met1Key) |> should equal false
    result |> List.exists (fun s -> s.Layer = mconKey2) |> should equal true
