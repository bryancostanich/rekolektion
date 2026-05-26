module Rekolektion.Viz.Core.Tests.RoutingViaStackTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Routing

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
