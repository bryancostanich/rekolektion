module Rekolektion.Viz.Core.Tests.RoutingViaStackTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Routing
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Core.Rkt.Types

let private li1  = (67, 20)
let private met1 = (68, 20)
let private met2 = (69, 20)
let private met3 = (70, 20)
let private mcon = (67, 44)
let private via1 = (68, 44)
let private via2 = (69, 44)

[<Fact>]
let ``isRoutingLayer accepts li1, met1..5; rejects others`` () =
    ViaStack.isRoutingLayer li1  |> should equal true
    ViaStack.isRoutingLayer met1 |> should equal true
    ViaStack.isRoutingLayer met2 |> should equal true
    ViaStack.isRoutingLayer (72, 20) |> should equal true   // met5
    ViaStack.isRoutingLayer mcon |> should equal false      // contact, not routing
    ViaStack.isRoutingLayer (67, 5)  |> should equal false  // li1.pin
    ViaStack.isRoutingLayer (66, 20) |> should equal false  // poly

[<Fact>]
let ``between same layer is empty`` () =
    ViaStack.between met1 met1 |> should be Empty

[<Fact>]
let ``between li1 and met1 is just mcon`` () =
    ViaStack.between li1 met1 |> should equal [ mcon ]

[<Fact>]
let ``between li1 and met2 is mcon, via1`` () =
    ViaStack.between li1 met2 |> should equal [ mcon; via1 ]

[<Fact>]
let ``between li1 and met3 is mcon, via1, via2`` () =
    ViaStack.between li1 met3 |> should equal [ mcon; via1; via2 ]

[<Fact>]
let ``between is order-insensitive (same vias either direction)`` () =
    let down = ViaStack.between met2 li1
    let up   = ViaStack.between li1 met2
    down |> should equal up

[<Fact>]
let ``between returns empty when one layer is not a routing layer`` () =
    ViaStack.between li1 mcon |> should be Empty
    ViaStack.between (66, 20) met1 |> should be Empty

[<Fact>]
let ``intermediateMetals adjacent layers is empty`` () =
    ViaStack.intermediateMetals li1 met1 |> should be Empty

[<Fact>]
let ``intermediateMetals li1 to met2 is just met1`` () =
    ViaStack.intermediateMetals li1 met2 |> should equal [ met1 ]

[<Fact>]
let ``intermediateMetals li1 to met3 is met1, met2`` () =
    ViaStack.intermediateMetals li1 met3 |> should equal [ met1; met2 ]

// --- padSideForVia: regression guards for via1 enclosure ---------------
//
// sky130 via1 enclosure is asymmetric: 0.085 µm on one axis, 0.055 µm
// on the other. A square pad must satisfy the strict axis, so for a
// 0.15 µm via cut the pad must be >= 0.15 + 2*0.085 = 0.32 µm.
// Pre-2026-05 only the 0.055 (via.4a/via.5a) rule was encoded, so
// `padSideForVia` returned 0.26 µm and routes hit DRC.

let private testUnits : Units = { DbuNm = 1; UuUm = 1 }

[<Fact>]
let ``padSideForVia met1 via1 returns 320 nm (via.4b)`` () =
    ViaStack.padSideForVia Rules.defaultView testUnits met1 via1
    |> should equal (Some 320L)

[<Fact>]
let ``padSideForVia met2 via1 returns 320 nm (via.5b)`` () =
    ViaStack.padSideForVia Rules.defaultView testUnits met2 via1
    |> should equal (Some 320L)

[<Fact>]
let ``emitAt same layer (li1 -> li1) returns empty`` () =
    // Sanity guard: a li1 wire snapping onto a li1 pin has no via
    // stack to emit. `between li1 li1` is empty, so the whole stack
    // collapses — including any candidate snap pad.
    ViaStack.emitAt Rules.defaultView testUnits li1 li1 0L 0L
    |> should be Empty

[<Fact>]
let ``emitAt li1 wire -> met1 snap emits one snap-pad on met1 and one mcon cut`` () =
    // Document the current (knuckle-emitting) behavior so the dropPads
    // filter that follows can be tested against a realistic input. wire
    // is on li1, snap target is on met1 — same direction as the
    // tap_mux_input_inv bottom VSS route the user flagged
    // (2026-05-31).
    let segs =
        ViaStack.emitAt Rules.defaultView testUnits met1 li1 397L -1130L
    // mcon cut on (67, 44).
    segs
    |> List.exists (fun s -> s.Layer = mcon)
    |> should equal true
    // Synthetic met1 snap-pad on (68, 20) — this is the "knuckle"
    // the foreign-poly filter is meant to suppress when the rail
    // itself already encloses the mcon.
    segs
    |> List.exists (fun s -> s.Layer = met1)
    |> should equal true
