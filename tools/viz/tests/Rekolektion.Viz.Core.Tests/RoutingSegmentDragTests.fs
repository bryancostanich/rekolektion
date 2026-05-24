module Rekolektion.Viz.Core.Tests.RoutingSegmentDragTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Routing

let private met1 : Layer = Named ("sky130", "met1")

let private mkRect (x1, y1, x2, y2) : Rectangle = {
    Layer = met1
    X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
    Net = None
    Props = []
    Comments = []
}

let private mkCell (name : string) (rects : Rectangle list) : Cell = {
    Name = name
    Meta = None
    Elements = rects |> List.map RectEl
    Comments = []
}

let private mkDoc (cells : Cell list) : Document =
    { emptyDocument with
        Cells = cells
        TopCell = cells |> List.tryHead |> Option.map (fun c -> c.Name) }

// --- setCursor: Manhattan-only perpendicular delta ----------------------

[<Fact>]
let ``setCursor: horizontal segment ignores X movement, captures Y`` () =
    // Horizontal wire — segment slides up/down. X mouse motion
    // doesn't change the geometry; only the perpendicular axis
    // (Y for a horizontal seg) feeds the delta.
    let r = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let s0 = SegmentDrag.start 1 "top" 0 r 500L 160L
    let s1 = SegmentDrag.setCursor 900L 500L s0
    // dx = 400 but ignored; dy = 340
    s1.Delta |> should equal 340L

[<Fact>]
let ``setCursor: vertical segment ignores Y movement, captures X`` () =
    let r = mkRect (0L, 0L, 320L, 1000L) |> Wire.setWireId 1
    let s0 = SegmentDrag.start 1 "top" 0 r 160L 500L
    let s1 = SegmentDrag.setCursor 600L 800L s0
    // dx = 440 captured; dy = 300 ignored
    s1.Delta |> should equal 440L

// --- draggedSegment: perpendicular translation only --------------------

[<Fact>]
let ``draggedSegment: horizontal slides on Y by Delta`` () =
    let r = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let s = SegmentDrag.start 1 "top" 0 r 500L 160L
            |> SegmentDrag.setCursor 500L 660L  // dy = 500
    let d = SegmentDrag.draggedSegment s
    d.X1 |> should equal 0L
    d.X2 |> should equal 1000L
    d.Y1 |> should equal 500L
    d.Y2 |> should equal 820L

[<Fact>]
let ``draggedSegment: vertical slides on X by Delta`` () =
    let r = mkRect (0L, 0L, 320L, 1000L) |> Wire.setWireId 1
    let s = SegmentDrag.start 1 "top" 0 r 160L 500L
            |> SegmentDrag.setCursor 660L 500L  // dx = 500
    let d = SegmentDrag.draggedSegment s
    d.X1 |> should equal 500L
    d.X2 |> should equal 820L
    d.Y1 |> should equal 0L
    d.Y2 |> should equal 1000L

// --- projectGeometry: single-segment wire, anchored both ends ----------

[<Fact>]
let ``projectGeometry: zero delta returns the original rect unchanged`` () =
    let r = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s = SegmentDrag.start 1 "top" 0 r 500L 160L
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 1
    let only = geom.[0]
    (only.X1, only.Y1, only.X2, only.Y2)
    |> should equal (0L, 0L, 1000L, 320L)

[<Fact>]
let ``projectGeometry: horizontal single-segment wire produces 3 rects`` () =
    // Horizontal wire (0,0)-(1000,320). Drag down by 500 (Y goes
    // from 160 to 660). Expected: left bridge + dragged + right
    // bridge, all wire-width = 320.
    let r = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s = SegmentDrag.start 1 "top" 0 r 500L 160L
            |> SegmentDrag.setCursor 500L 660L
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 3
    // Left bridge: vertical rect at x=0 spanning Y=0 to Y=820.
    let lb = geom.[0]
    (lb.X1, lb.X2) |> should equal (0L, 320L)
    // Wait — bridge width = original wire width = 320. xL endpoint
    // = 0 + half = 160. Bridge rect = (160-160, ...) to (160+160, ...)
    // = (0, ...) to (320, ...). ✓
    (lb.Y1, lb.Y2) |> should equal (0L, 820L)
    // Dragged segment.
    let mid = geom.[1]
    (mid.X1, mid.Y1, mid.X2, mid.Y2)
    |> should equal (0L, 500L, 1000L, 820L)
    // Right bridge: vertical at x=1000.
    let rb = geom.[2]
    (rb.X1, rb.X2) |> should equal (680L, 1000L)
    (rb.Y1, rb.Y2) |> should equal (0L, 820L)

[<Fact>]
let ``projectGeometry: vertical single-segment wire produces 3 rects`` () =
    let r = mkRect (0L, 0L, 320L, 1000L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s = SegmentDrag.start 1 "top" 0 r 160L 500L
            |> SegmentDrag.setCursor 660L 500L  // dx = 500
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 3
    // Bottom bridge: horizontal at y=0 spanning x=0 to x=820.
    let bb = geom.[0]
    (bb.X1, bb.X2) |> should equal (0L, 820L)
    (bb.Y1, bb.Y2) |> should equal (0L, 320L)
    // Dragged.
    let mid = geom.[1]
    (mid.X1, mid.Y1, mid.X2, mid.Y2)
    |> should equal (500L, 0L, 820L, 1000L)
    // Top bridge.
    let tb = geom.[2]
    (tb.X1, tb.X2) |> should equal (0L, 820L)
    (tb.Y1, tb.Y2) |> should equal (680L, 1000L)

// --- projectGeometry: multi-segment wire (MVP fallback) ----------------

[<Fact>]
let ``projectGeometry: multi-segment wire replaces only the dragged rect`` () =
    // Three-segment wire (Z-shape). Drag the middle vertical
    // sideways. MVP behaviour: the other two segments stay put;
    // the dragged segment moves. Visually disconnected at the
    // bend points — the stretch-flanking case is a follow-up.
    let h1 = mkRect (0L,    0L,   500L,  100L) |> Wire.setWireId 1
    let v  = mkRect (400L,  0L,   500L,  500L) |> Wire.setWireId 1
    let h2 = mkRect (400L,  400L, 1000L, 500L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ h1; v; h2 ] ]
    let s = SegmentDrag.start 1 "top" 1 v 450L 250L
            |> SegmentDrag.setCursor 750L 250L  // dx = 300
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 3
    // h1 and h2 unchanged; v translated by 300 on X.
    let dragged = geom |> List.find (fun r -> r.X1 = 700L)
    (dragged.X1, dragged.Y1, dragged.X2, dragged.Y2)
    |> should equal (700L, 0L, 800L, 500L)
