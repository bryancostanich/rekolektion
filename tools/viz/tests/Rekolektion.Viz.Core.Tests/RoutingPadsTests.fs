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
let ``met1 endpoint pad is 290 nm (mcon enclosure dominates min-area)`` () =
    // met1.5 AsymEnclosure(met1, mcon, 0.06, 0.03) + mcon width 0.17
    // → 170 + 2*60 = 290 nm. met1.6 min-area 0.083 µm² → 288 nm.
    // Max = 290 nm.
    Pads.endpointPadSide Rules.defaultView units1nm met1Key
    |> should equal (Some 290L)

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
let ``li1 endpoint pad is 330 nm (li.5 around licon dominates)`` () =
    // li.5 AsymEnclosure(li1, licon1, 0.08, 0.0) + licon.1 width 0.17
    // → 170 + 2*80 = 330 nm. li.6 min-area 0.0561 µm² → 237 nm.
    // Max = 330 nm.
    let li1Key : int * int = (67, 20)
    Pads.endpointPadSide Rules.defaultView units1nm li1Key
    |> should equal (Some 330L)

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

[<Fact>]
let ``endpointPads emits nothing when padSide is zero or negative`` () =
    let r = Draft.start met1Key 320L (0L, 0L)
    Draft.endpointPads 0L r |> should be Empty
    Draft.endpointPads -1L r |> should be Empty
