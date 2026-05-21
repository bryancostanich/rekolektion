module Rekolektion.Viz.Core.Tests.RoutingDraftTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Routing

let private met1 = (68, 20)
let private width = 320L

[<Fact>]
let ``start initializes anchor, no cursor, no segments`` () =
    let r = Draft.start met1 width (0L, 0L)
    r.Points |> should equal [(0L, 0L)]
    r.Cursor |> should equal (None : (int64 * int64) option)
    Draft.fixedSegments r |> should be Empty
    Draft.tentativeSegments r |> should be Empty
    Draft.allSegments r |> should be Empty

[<Fact>]
let ``setCursor produces tentative L from anchor`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 500L)
    r.Cursor |> should equal (Some (1000L, 500L))
    // Diagonal under HorizontalFirst posture → 2 rects.
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 2
    Draft.fixedSegments r |> should be Empty
    Draft.allSegments r |> should haveLength 2

[<Fact>]
let ``fix appends cursor to Points and clears cursor`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
    r.Points |> should equal [(0L, 0L); (1000L, 0L)]
    r.Cursor |> should equal (None : (int64 * int64) option)
    // Fixed straight-line → 1 rect, no tentative.
    Draft.fixedSegments r |> should haveLength 1
    Draft.tentativeSegments r |> should be Empty

[<Fact>]
let ``fix is a no-op when cursor is None`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.fix
    r.Points |> should equal [(0L, 0L)]

[<Fact>]
let ``pop removes the last fixed point`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
        |> Draft.setCursor (1000L, 500L)
        |> Draft.fix
        |> Draft.pop
    r.Points |> should equal [(0L, 0L); (1000L, 0L)]

[<Fact>]
let ``pop preserves the anchor (never removes it)`` () =
    let r = Draft.start met1 width (0L, 0L) |> Draft.pop |> Draft.pop
    r.Points |> should equal [(0L, 0L)]

[<Fact>]
let ``flipPosture alternates HorizontalFirst and VerticalFirst`` () =
    let r1 = Draft.start met1 width (0L, 0L)
    r1.Posture |> should equal Draft.HorizontalFirst
    let r2 = Draft.flipPosture r1
    r2.Posture |> should equal Draft.VerticalFirst
    let r3 = Draft.flipPosture r2
    r3.Posture |> should equal Draft.HorizontalFirst

[<Fact>]
let ``axis-aligned segment produces exactly one rect`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 1
    let s = segs.[0]
    s.Layer |> should equal met1
    // 0 → 1000 horizontal sweep, width=320 → half=160; bbox y spans ±160.
    s.X1 |> should equal -160L
    s.X2 |> should equal 1160L
    s.Y1 |> should equal -160L
    s.Y2 |> should equal 160L

[<Fact>]
let ``diagonal segment produces two rects under HorizontalFirst`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 500L)
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 2
    // First rect runs along Y=0 from X=0 to X=1000 (horizontal first).
    let h = segs.[0]
    h.X1 |> should equal -160L
    h.X2 |> should equal 1160L
    h.Y1 |> should equal -160L
    h.Y2 |> should equal 160L
    // Second rect runs along X=1000 from Y=0 to Y=500 (vertical second).
    let v = segs.[1]
    v.X1 |> should equal 840L
    v.X2 |> should equal 1160L
    v.Y1 |> should equal -160L
    v.Y2 |> should equal 660L

[<Fact>]
let ``diagonal segment produces two rects under VerticalFirst`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.flipPosture
        |> Draft.setCursor (1000L, 500L)
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 2
    // First rect runs along X=0 from Y=0 to Y=500 (vertical first).
    let v = segs.[0]
    v.X1 |> should equal -160L
    v.X2 |> should equal 160L
    v.Y1 |> should equal -160L
    v.Y2 |> should equal 660L

[<Fact>]
let ``degenerate zero-length segment produces no rects`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (0L, 0L)
    Draft.tentativeSegments r |> should be Empty

[<Fact>]
let ``finishSegments includes tentative when cursor set`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
        |> Draft.setCursor (1000L, 500L)
    // Two fixed points + a tentative third → 1 fixed straight + 1 tentative straight.
    let segs = Draft.finishSegments r
    segs.Length |> should equal 2

[<Fact>]
let ``finishSegments uses just Points when cursor is None`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
    Draft.finishSegments r |> should haveLength 1
