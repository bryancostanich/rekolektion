module Rekolektion.Viz.Core.Tests.MarqueeTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Layout.Marquee
open Rekolektion.Viz.Core.Rkt.Types

[<Fact>]
let ``modeOfDirection: end >= start is Enclose`` () =
    modeOfDirection 100L 200L |> should equal Enclose
    modeOfDirection 100L 100L |> should equal Enclose

[<Fact>]
let ``modeOfDirection: end < start is Touch`` () =
    modeOfDirection 200L 100L |> should equal Touch

[<Fact>]
let ``normalize swaps reversed coords`` () =
    normalize 200L 50L 100L 150L
    |> should equal (100L, 50L, 200L, 150L)

[<Fact>]
let ``Enclose: bbox fully inside marquee fits`` () =
    bboxFits Enclose (0L, 0L, 100L, 100L) (10L, 10L, 90L, 90L)
    |> should equal true

[<Fact>]
let ``Enclose: bbox partially outside does not fit`` () =
    bboxFits Enclose (0L, 0L, 100L, 100L) (50L, 50L, 150L, 90L)
    |> should equal false

[<Fact>]
let ``Enclose: bbox touching marquee edge fits`` () =
    bboxFits Enclose (0L, 0L, 100L, 100L) (0L, 0L, 100L, 100L)
    |> should equal true

[<Fact>]
let ``Touch: bbox crossing edge fits`` () =
    bboxFits Touch (0L, 0L, 100L, 100L) (50L, 50L, 150L, 90L)
    |> should equal true

[<Fact>]
let ``Touch: bbox entirely outside does not fit`` () =
    bboxFits Touch (0L, 0L, 100L, 100L) (200L, 200L, 300L, 300L)
    |> should equal false

[<Fact>]
let ``Touch: bbox sharing only an edge fits`` () =
    // Shared edge counts as touching — matches the "any touch" CAD behaviour.
    bboxFits Touch (0L, 0L, 100L, 100L) (100L, 0L, 200L, 100L)
    |> should equal true

[<Fact>]
let ``Touch: gap of 1 DBU does not fit`` () =
    bboxFits Touch (0L, 0L, 100L, 100L) (101L, 0L, 200L, 100L)
    |> should equal false

[<Fact>]
let ``Enclose: bbox fully containing marquee does not fit`` () =
    bboxFits Enclose (10L, 10L, 90L, 90L) (0L, 0L, 100L, 100L)
    |> should equal false

[<Fact>]
let ``pointsBbox: empty list returns None`` () =
    pointsBbox [] |> should equal (None : (int64 * int64 * int64 * int64) option)

[<Fact>]
let ``pointsBbox: single point degenerate bbox`` () =
    pointsBbox [ { X = 5L; Y = 7L } ]
    |> should equal (Some (5L, 7L, 5L, 7L))

[<Fact>]
let ``pointsBbox: typical rectangle`` () =
    let pts = [
        { X = 0L; Y = 0L }
        { X = 100L; Y = 0L }
        { X = 100L; Y = 50L }
        { X = 0L; Y = 50L }
    ]
    pointsBbox pts |> should equal (Some (0L, 0L, 100L, 50L))

[<Fact>]
let ``pointsBbox: mixed-sign coordinates`` () =
    let pts = [
        { X = -10L; Y = -20L }
        { X = 30L; Y = 40L }
    ]
    pointsBbox pts |> should equal (Some (-10L, -20L, 30L, 40L))
