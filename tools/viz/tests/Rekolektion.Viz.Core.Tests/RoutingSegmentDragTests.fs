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
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s0 = SegmentDrag.start (Some 1) "top" 0 r 500L 160L false Set.empty doc
    let s1 = SegmentDrag.setCursor 900L 500L s0
    // dx = 400 but ignored; dy = 340
    s1.Delta |> should equal 340L

[<Fact>]
let ``setCursor: vertical segment ignores Y movement, captures X`` () =
    let r = mkRect (0L, 0L, 320L, 1000L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s0 = SegmentDrag.start (Some 1) "top" 0 r 160L 500L false Set.empty doc
    let s1 = SegmentDrag.setCursor 600L 800L s0
    // dx = 440 captured; dy = 300 ignored
    s1.Delta |> should equal 440L

// --- draggedSegment: perpendicular translation only --------------------

[<Fact>]
let ``draggedSegment: horizontal slides on Y by Delta`` () =
    let r = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s = SegmentDrag.start (Some 1) "top" 0 r 500L 160L false Set.empty doc
            |> SegmentDrag.setCursor 500L 660L  // dy = 500
    let d = SegmentDrag.draggedSegment s
    d.X1 |> should equal 0L
    d.X2 |> should equal 1000L
    d.Y1 |> should equal 500L
    d.Y2 |> should equal 820L

[<Fact>]
let ``draggedSegment: vertical slides on X by Delta`` () =
    let r = mkRect (0L, 0L, 320L, 1000L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s = SegmentDrag.start (Some 1) "top" 0 r 160L 500L false Set.empty doc
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
    let s = SegmentDrag.start (Some 1) "top" 0 r 500L 160L false Set.empty doc
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 1
    let only = geom.[0]
    (only.X1, only.Y1, only.X2, only.Y2)
    |> should equal (0L, 0L, 1000L, 320L)

[<Fact>]
let ``start: auto-groups collinear-abutting rects into one virtual segment`` () =
    // Three abutting horizontal rects sharing the same Y line —
    // logically one wire segment stored as three rects (the
    // common "wire drawn in three legs" case the user described).
    // Pickup on the middle one should produce GroupIndices = [0;1;2]
    // and an Original whose X span is the union of all three.
    let r0 = mkRect (0L,    0L, 1000L, 320L)
    let r1 = mkRect (1000L, 0L, 2000L, 320L)
    let r2 = mkRect (2000L, 0L, 3000L, 320L)
    let doc = mkDoc [ mkCell "top" [ r0; r1; r2 ] ]
    let s = SegmentDrag.start None "top" 1 r1 1500L 160L false Set.empty doc
    // All three rects are in the group.
    s.GroupIndices |> List.sort |> should equal [0; 1; 2]
    // Virtual segment spans the union: x ∈ [0, 3000], y ∈ [0, 320].
    (s.Original.X1, s.Original.Y1, s.Original.X2, s.Original.Y2)
    |> should equal (0L, 0L, 3000L, 320L)

[<Fact>]
let ``start: gap between rects breaks the collinear group`` () =
    // Two rects on the same Y line but with a gap — NOT a group.
    let a = mkRect (0L,    0L, 1000L, 320L)
    let b = mkRect (1500L, 0L, 2500L, 320L)  // 500-unit gap
    let doc = mkDoc [ mkCell "top" [ a; b ] ]
    let s = SegmentDrag.start None "top" 0 a 500L 160L false Set.empty doc
    s.GroupIndices |> should equal [0]
    (s.Original.X1, s.Original.X2) |> should equal (0L, 1000L)

[<Fact>]
let ``start: extras populated from selection when picked is part of it`` () =
    // Two wires (separate, not touching). Pre-select both.
    // Pick wire A. Extras should contain wire B.
    let a = mkRect (0L,    0L, 1000L, 320L)         // wire A
    let b = mkRect (5000L, 0L, 6000L, 320L)         // wire B (separate)
    let doc = mkDoc [ mkCell "top" [ a; b ] ]
    let selection : Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey> =
        Set.ofList
            [ ({ Cell = "top"; Index = 0; TopInstance = None } : Rekolektion.Viz.Core.Layout.Flatten.PolyKey)
              ({ Cell = "top"; Index = 1; TopInstance = None } : Rekolektion.Viz.Core.Layout.Flatten.PolyKey) ]
    let s =
        SegmentDrag.start None "top" 0 a 500L 160L false selection doc
    s.Extras |> List.length |> should equal 1
    s.Extras.[0].GroupIndices |> should equal [1]

[<Fact>]
let ``start: extras empty when picked rect isn't in selection`` () =
    // Picking a non-selected rect shouldn't drag the selected
    // ones along — only when the picked rect IS the selection
    // (or part of it) does multi-wire drag fire.
    let a = mkRect (0L,    0L, 1000L, 320L)
    let b = mkRect (5000L, 0L, 6000L, 320L)
    let doc = mkDoc [ mkCell "top" [ a; b ] ]
    let selection : Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey> =
        Set.singleton
            ({ Cell = "top"; Index = 1; TopInstance = None } : Rekolektion.Viz.Core.Layout.Flatten.PolyKey)
    let s =
        SegmentDrag.start None "top" 0 a 500L 160L false selection doc
    s.Extras |> should be Empty

[<Fact>]
let ``projectGeometry: extras translate by the drag vector`` () =
    // Two wires, both selected. Drag wire A down by 500.
    // Wire B should translate by (0, 500) in lockstep.
    let a = mkRect (0L,    0L, 1000L, 320L)
    let b = mkRect (5000L, 0L, 6000L, 320L)
    let doc = mkDoc [ mkCell "top" [ a; b ] ]
    let selection : Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey> =
        Set.ofList
            [ ({ Cell = "top"; Index = 0; TopInstance = None } : Rekolektion.Viz.Core.Layout.Flatten.PolyKey)
              ({ Cell = "top"; Index = 1; TopInstance = None } : Rekolektion.Viz.Core.Layout.Flatten.PolyKey) ]
    let s =
        SegmentDrag.start None "top" 0 a 500L 160L false selection doc
        |> SegmentDrag.setCursor 500L 660L   // dy = 500
    let geom = SegmentDrag.projectGeometry s doc
    // Wire A: dragged + 2 bridges = 3 rects, Y in [0, 820].
    // Wire B: 1 translated rect at (5000, 500, 6000, 820).
    geom |> List.length |> should equal 4
    let bMoved =
        geom |> List.find (fun rr -> rr.X1 = 5000L && rr.X2 = 6000L)
    (bMoved.Y1, bMoved.Y2) |> should equal (500L, 820L)

[<Fact>]
let ``projectGeometry: no-WireId rect drags as a single-segment wire`` () =
    // Pre-WireId / hand-drawn geometry: no wire tag, just a rect.
    // Pickup with WireId = None should produce the same 3-rect
    // L-shape commit as a tagged single-segment wire would.
    let r = mkRect (0L, 0L, 1000L, 320L)   // no setWireId call
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s = SegmentDrag.start None "top" 0 r 500L 160L false Set.empty doc
            |> SegmentDrag.setCursor 500L 660L  // dy = 500
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 3
    let dragged = geom |> List.find (fun rr -> rr.X1 = 0L && rr.X2 = 1000L)
    (dragged.Y1, dragged.Y2) |> should equal (500L, 820L)

[<Fact>]
let ``projectGeometry: horizontal single-segment wire produces 3 rects`` () =
    // Horizontal wire (0,0)-(1000,320). Drag down by 500 (Y goes
    // from 160 to 660). Expected: dragged segment + two L-corner
    // bridges (one at each anchored end). Tests look up rects by
    // content so the projection's emit order isn't load-bearing.
    let r = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s = SegmentDrag.start (Some 1) "top" 0 r 500L 160L false Set.empty doc
            |> SegmentDrag.setCursor 500L 660L
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 3
    // Dragged segment — full original X span, Y shifted by 500.
    let dragged = geom |> List.find (fun rr -> rr.X1 = 0L && rr.X2 = 1000L)
    (dragged.Y1, dragged.Y2) |> should equal (500L, 820L)
    // Left bridge — vertical, spans Y from original top (0) to
    // dragged bottom (820), width = wire width (320) centered at
    // x = 160.
    let lb = geom |> List.find (fun rr -> rr.X1 = 0L && rr.X2 = 320L)
    (lb.Y1, lb.Y2) |> should equal (0L, 820L)
    // Right bridge — same on the other side.
    let rb = geom |> List.find (fun rr -> rr.X1 = 680L && rr.X2 = 1000L)
    (rb.Y1, rb.Y2) |> should equal (0L, 820L)

[<Fact>]
let ``projectGeometry: vertical single-segment wire produces 3 rects`` () =
    let r = mkRect (0L, 0L, 320L, 1000L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r ] ]
    let s = SegmentDrag.start (Some 1) "top" 0 r 160L 500L false Set.empty doc
            |> SegmentDrag.setCursor 660L 500L  // dx = 500
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 3
    let dragged = geom |> List.find (fun rr -> rr.Y1 = 0L && rr.Y2 = 1000L)
    (dragged.X1, dragged.X2) |> should equal (500L, 820L)
    let bb = geom |> List.find (fun rr -> rr.Y1 = 0L && rr.Y2 = 320L)
    (bb.X1, bb.X2) |> should equal (0L, 820L)
    let tb = geom |> List.find (fun rr -> rr.Y1 = 680L && rr.Y2 = 1000L)
    (tb.X1, tb.X2) |> should equal (0L, 820L)

// --- projectGeometry: multi-segment wire (stretching) ------------------

[<Fact>]
let ``projectGeometry: Z-shape wire stretches both flanking segments`` () =
    // Z-shape: horizontal (0..500) → vertical (400..500, 0..500) →
    // horizontal (400..1000, 400..500). Drag the middle vertical
    // right by 300. The two horizontals must follow so the wire
    // stays continuous; no bridges (both ends had perpendicular
    // neighbours).
    let h1 = mkRect (0L,    0L,   500L,  100L) |> Wire.setWireId 1
    let v  = mkRect (400L,  0L,   500L,  500L) |> Wire.setWireId 1
    let h2 = mkRect (400L,  400L, 1000L, 500L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ h1; v; h2 ] ]
    let s = SegmentDrag.start (Some 1) "top" 1 v 450L 250L false Set.empty doc
            |> SegmentDrag.setCursor 750L 250L  // dx = 300
    let geom = SegmentDrag.projectGeometry s doc
    geom |> List.length |> should equal 3
    // Dragged vertical at new X.
    let dragged = geom |> List.find (fun r -> r.X1 = 700L && r.Y1 = 0L && r.Y2 = 500L)
    (dragged.X1, dragged.Y1, dragged.X2, dragged.Y2)
    |> should equal (700L, 0L, 800L, 500L)
    // h1 stretched to the right (its right edge follows the
    // dragged vertical). Original right was 500; new right = 800.
    let h1' = geom |> List.find (fun r -> r.X1 = 0L && r.Y2 = 100L)
    (h1'.X1, h1'.Y1, h1'.X2, h1'.Y2) |> should equal (0L, 0L, 800L, 100L)
    // h2 stretched on the left (its left edge follows). Original
    // left = 400; new left = 700.
    let h2' = geom |> List.find (fun r -> r.X2 = 1000L && r.Y2 = 500L)
    (h2'.X1, h2'.Y1, h2'.X2, h2'.Y2) |> should equal (700L, 400L, 1000L, 500L)

[<Fact>]
let ``projectGeometry: L-shape wire stretches the one perpendicular neighbour and bridges the anchored end`` () =
    // L-shape: just two segments. Drag the vertical. The horizontal
    // stretches to follow; the vertical's far end (top) was a
    // terminus → gets a bridge segment.
    let h = mkRect (0L,   0L,   500L, 100L) |> Wire.setWireId 1
    let v = mkRect (400L, 0L,   500L, 800L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ h; v ] ]
    let s = SegmentDrag.start (Some 1) "top" 1 v 450L 400L false Set.empty doc
            |> SegmentDrag.setCursor 750L 400L  // dx = 300
    let geom = SegmentDrag.projectGeometry s doc
    // Dragged + stretched h + 1 bridge for the top terminus = 3 rects.
    geom |> List.length |> should equal 3
    let dragged = geom |> List.find (fun r -> r.X1 = 700L && r.Y2 = 800L)
    (dragged.X1, dragged.X2) |> should equal (700L, 800L)
    let h' = geom |> List.find (fun r -> r.X1 = 0L)
    (h'.X1, h'.X2) |> should equal (0L, 800L)
    // The remaining rect is the top-terminus bridge: a horizontal
    // segment from old-top (450) to new-top (750) at the top end
    // (yCenter near 800-halfW = 750, since wire width 100).
    let bridge = geom |> List.find (fun r -> r <> dragged && r <> h')
    let bxLo = min bridge.X1 bridge.X2
    let bxHi = max bridge.X1 bridge.X2
    // Spans from original top center (450) to new top center (750)
    // plus halfW on each side.
    bxLo |> should equal 400L
    bxHi |> should equal 800L
