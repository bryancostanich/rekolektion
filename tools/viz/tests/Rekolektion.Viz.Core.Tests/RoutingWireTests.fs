module Rekolektion.Viz.Core.Tests.RoutingWireTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Routing

let private met1 : Layer = Named ("sky130", "met1")
let private met2 : Layer = Named ("sky130", "met2")
let private met3 : Layer = Named ("sky130", "met3")
let private li1  : Layer = Named ("sky130", "li1")

let private mkRect (x1, y1, x2, y2) : Rectangle = {
    Layer = met1
    X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
    Net = None
    Props = []
    Comments = []
    SubFormComments = Map.empty
}

let private mkRectOn (layer : Layer) (x1, y1, x2, y2) : Rectangle =
    { mkRect (x1, y1, x2, y2) with Layer = layer }

let private mkCell (name : string) (rects : Rectangle list) : Cell = {
    Name = name
    Meta = None
    Elements = rects |> List.map RectEl
    Comments = []
    SubFormComments = Map.empty
}

let private mkDoc (cells : Cell list) : Document =
    { emptyDocument with
        Cells = cells
        TopCell = cells |> List.tryHead |> Option.map (fun c -> c.Name) }

[<Fact>]
let ``getWireId returns None for a rect without the property`` () =
    let r = mkRect (0L, 0L, 100L, 320L)
    Wire.getWireId r |> should equal (None : int option)

[<Fact>]
let ``setWireId then getWireId round-trips the value`` () =
    let r =
        mkRect (0L, 0L, 100L, 320L)
        |> Wire.setWireId 42
    Wire.getWireId r |> should equal (Some 42)

[<Fact>]
let ``setWireId replaces an existing wire-id instead of duplicating`` () =
    let r =
        mkRect (0L, 0L, 100L, 320L)
        |> Wire.setWireId 1
        |> Wire.setWireId 99
    Wire.getWireId r |> should equal (Some 99)
    r.Props
    |> List.filter (fun p -> p.Key = Wire.wireIdKey)
    |> List.length
    |> should equal 1

[<Fact>]
let ``setWireId preserves unrelated Props`` () =
    let r =
        { mkRect (0L, 0L, 100L, 320L) with
            Props = [ { Key = "comment"; Value = PvString "x" } ] }
        |> Wire.setWireId 7
    Wire.getWireId r |> should equal (Some 7)
    r.Props
    |> List.exists (fun p -> p.Key = "comment")
    |> should equal true

[<Fact>]
let ``nextWireId on an empty document is 1`` () =
    let doc = mkDoc [ mkCell "top" [] ]
    Wire.nextWireId doc |> should equal 1

[<Fact>]
let ``nextWireId returns max+1 across all cells`` () =
    let r1 = mkRect (0L, 0L, 100L, 320L) |> Wire.setWireId 3
    let r2 = mkRect (0L, 0L, 100L, 320L) |> Wire.setWireId 17
    let r3 = mkRect (0L, 0L, 100L, 320L) |> Wire.setWireId 5
    let doc =
        mkDoc [
            mkCell "top" [ r1; r2 ]
            mkCell "sub" [ r3 ]
        ]
    Wire.nextWireId doc |> should equal 18

[<Fact>]
let ``nextWireId ignores rectangles without a wire-id`` () =
    let r1 = mkRect (0L, 0L, 100L, 320L) |> Wire.setWireId 4
    let r2 = mkRect (0L, 0L, 100L, 320L)  // no id
    let doc = mkDoc [ mkCell "top" [ r1; r2 ] ]
    Wire.nextWireId doc |> should equal 5

[<Fact>]
let ``segmentsOf returns every rect carrying the queried id, in document order`` () =
    let r1 = mkRect (0L, 0L, 100L, 320L) |> Wire.setWireId 1
    let r2 = mkRect (0L, 0L, 100L, 320L) |> Wire.setWireId 2
    let r3 = mkRect (0L, 0L, 100L, 320L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r1; r2; r3 ] ]
    let hits = Wire.segmentsOf 1 doc
    hits |> List.length |> should equal 2
    let indices = hits |> List.map (fun (_, idx, _) -> idx)
    indices |> should equal [0; 2]

// --- segmentAxis -------------------------------------------------------

[<Fact>]
let ``segmentAxis: long-on-X rect is Horizontal`` () =
    Wire.segmentAxis (mkRect (0L, 0L, 1000L, 100L))
    |> should equal Wire.Horizontal

[<Fact>]
let ``segmentAxis: long-on-Y rect is Vertical`` () =
    Wire.segmentAxis (mkRect (0L, 0L, 100L, 1000L))
    |> should equal Wire.Vertical

[<Fact>]
let ``segmentAxis: square defaults to Horizontal`` () =
    Wire.segmentAxis (mkRect (0L, 0L, 100L, 100L))
    |> should equal Wire.Horizontal

// --- containsPoint -----------------------------------------------------

[<Fact>]
let ``containsPoint: inside the bbox returns true`` () =
    let r = mkRect (0L, 0L, 1000L, 320L)
    Wire.containsPoint 500L 160L r |> should equal true

[<Fact>]
let ``containsPoint: on the bbox edge returns true (inclusive)`` () =
    let r = mkRect (0L, 0L, 1000L, 320L)
    Wire.containsPoint 0L 0L r |> should equal true
    Wire.containsPoint 1000L 320L r |> should equal true

[<Fact>]
let ``containsPoint: outside returns false`` () =
    let r = mkRect (0L, 0L, 1000L, 320L)
    Wire.containsPoint -1L 0L r |> should equal false
    Wire.containsPoint 1001L 0L r |> should equal false

// --- findSegmentAt -----------------------------------------------------

[<Fact>]
let ``findSegmentAt picks the wire-tagged rect containing the cursor`` () =
    let r1 = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let r2 = mkRect (5000L, 0L, 6000L, 320L) |> Wire.setWireId 2
    let doc = mkDoc [ mkCell "top" [ r1; r2 ] ]
    let hit = Wire.findSegmentAt 500L 160L doc
    match hit with
    | Some (wid, cell, idx, _) ->
        wid |> should equal (Some 1)
        cell |> should equal "top"
        idx |> should equal 0
    | None -> Assert.Fail("expected a hit")

[<Fact>]
let ``findSegmentAt returns None when cursor misses every rect`` () =
    let r1 = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r1 ] ]
    Wire.findSegmentAt 5000L 5000L doc
    |> should equal (None : (int option * string * int * Rectangle) option)

[<Fact>]
let ``findSegmentAt also picks rects with NO WireId (single-rect drag)`` () =
    // Pre-WireId or hand-drawn geometry is still pickable for
    // segment-drag — the segment-drag commit allocates a fresh
    // WireId for the new rects, so the rect graduates to a
    // first-class wire after its first drag.
    let plain = mkRect (0L, 0L, 1000L, 320L)
    let doc = mkDoc [ mkCell "top" [ plain ] ]
    let hit = Wire.findSegmentAt 500L 160L doc
    match hit with
    | Some (wid, _, idx, _) ->
        wid |> should equal (None : int option)
        idx |> should equal 0
    | None -> Assert.Fail("expected a hit on the untagged rect")

[<Fact>]
let ``findSegmentAt picks the later rect when two same-wire rects overlap`` () =
    // L-corner case: a horizontal segment and the vertical segment
    // that touches its end share a bbox square at the corner. The
    // later-in-document rect wins, matching renderer paint order.
    let h = mkRect (0L, 0L, 1000L, 320L) |> Wire.setWireId 1
    let v = mkRect (900L, 0L, 1000L, 5000L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ h; v ] ]
    let hit = Wire.findSegmentAt 950L 160L doc
    match hit with
    | Some (_, _, idx, _) -> idx |> should equal 1
    | None -> Assert.Fail("expected a hit")

// --- neighborsOf -------------------------------------------------------

[<Fact>]
let ``neighborsOf: middle segment has two neighbours, terminus has one`` () =
    // Horizontal-Vertical-Horizontal wire (a Z). The middle vertical
    // touches both horizontals at its endpoints.
    let h1 = mkRect (0L,    0L,   500L,  100L) |> Wire.setWireId 1
    let v  = mkRect (400L,  0L,   500L,  500L) |> Wire.setWireId 1
    let h2 = mkRect (400L,  400L, 1000L, 500L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ h1; v; h2 ] ]
    Wire.neighborsOf 1 "top" 1 v doc |> List.length |> should equal 2
    Wire.neighborsOf 1 "top" 0 h1 doc |> List.length |> should equal 1
    Wire.neighborsOf 1 "top" 2 h2 doc |> List.length |> should equal 1

[<Fact>]
let ``neighborsOf excludes the segment itself and ignores other wires`` () =
    let r1 = mkRect (0L, 0L, 500L, 100L) |> Wire.setWireId 1
    let r2 = mkRect (400L, 0L, 500L, 500L) |> Wire.setWireId 2  // different wire, touching
    let doc = mkDoc [ mkCell "top" [ r1; r2 ] ]
    Wire.neighborsOf 1 "top" 0 r1 doc |> should be Empty

// --- bboxesTouch + connectedComponent ----------------------------------

[<Fact>]
let ``bboxesTouch: edge-to-edge abutting counts as touching`` () =
    let a = mkRect (0L, 0L, 100L, 100L)
    let b = mkRect (100L, 0L, 200L, 100L)   // right edge of a = left edge of b
    Wire.bboxesTouch a b |> should equal true

[<Fact>]
let ``bboxesTouch: gap = NOT touching`` () =
    let a = mkRect (0L, 0L, 100L, 100L)
    let b = mkRect (101L, 0L, 200L, 100L)
    Wire.bboxesTouch a b |> should equal false

[<Fact>]
let ``connectedComponent: walks bbox-touching chain`` () =
    // Three abutting rects forming a line.
    let r0 = mkRect (0L,    0L, 1000L, 100L)
    let r1 = mkRect (1000L, 0L, 2000L, 100L)
    let r2 = mkRect (2000L, 0L, 3000L, 100L)
    let doc = mkDoc [ mkCell "top" [ r0; r1; r2 ] ]
    let always _ _ = true
    Wire.connectedComponent "top" 0 always always doc
    |> List.sort |> should equal [0; 1; 2]

[<Fact>]
let ``connectedComponent: propagate=false on a rect blocks expansion through it`` () =
    // Three abutting rects. The middle one is a "terminus" (pin).
    // Starting from r0, BFS reaches r1 (which is terminated) and
    // INCLUDES it, but doesn't expand through it to r2.
    let r0 = mkRect (0L,    0L, 1000L, 100L)
    let r1 = mkRect (1000L, 0L, 2000L, 100L)
    let r2 = mkRect (2000L, 0L, 3000L, 100L)
    let doc = mkDoc [ mkCell "top" [ r0; r1; r2 ] ]
    let always _ _ = true
    // r1 (idx=1) is the terminus.
    let propagate i _ = i <> 1
    Wire.connectedComponent "top" 0 always propagate doc
    |> List.sort |> should equal [0; 1]

[<Fact>]
let ``connectedComponent: seed always seeds expansion even if it's a pin`` () =
    // Seed is a "pin" (propagate=false). It should still find its
    // neighbours on the first pass — otherwise clicking on the
    // pin would select just the pin, not the wire connected to it.
    let r0 = mkRect (0L,    0L, 1000L, 100L)   // seed (pin)
    let r1 = mkRect (1000L, 0L, 2000L, 100L)   // wire
    let doc = mkDoc [ mkCell "top" [ r0; r1 ] ]
    let always _ _ = true
    let propagate i _ = i <> 0  // r0 is a pin
    Wire.connectedComponent "top" 0 always propagate doc
    |> List.sort |> should equal [0; 1]

[<Fact>]
let ``connectedComponent: keep=false on a rect excludes it from the set`` () =
    // Two rects, but the second isn't "ours" per the keep filter.
    let r0 = mkRect (0L,    0L, 1000L, 100L)
    let r1 = mkRect (1000L, 0L, 2000L, 100L)
    let doc = mkDoc [ mkCell "top" [ r0; r1 ] ]
    let keep i _ = i = 0
    let always _ _ = true
    Wire.connectedComponent "top" 0 keep always doc
    |> should equal [0]

// --- touchingNeighbors --------------------------------------------------
//
// Audit regression: pre-fix, this returned every rect whose bbox
// overlapped the picked wire — including foreign layers and chip-
// boundary rails. The downstream drag commit then re-stamped 40+
// unrelated cell rects with the picked wire's WireId.

let private mkRectLayer (layer : Layer) (x1, y1, x2, y2) : Rectangle = {
    Layer = layer
    X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
    Net = None
    Props = []
    Comments = []
    SubFormComments = Map.empty
}

// (li1 declared near the top of the file alongside met2 / met3.)

[<Fact>]
let ``touchingNeighbors: vertical cross-wire endpoint inside horizontal picked wire is included`` () =
    // Horizontal picked wire at y=0..100, x=0..2000 on met1.
    // Vertical cross-wire on met1 at x=950..1050, y=100..500 — its
    // bottom endpoint sits ON the picked wire's top edge.
    let picked = mkRect (0L, 0L, 2000L, 100L)
    let crossWire = mkRect (950L, 100L, 1050L, 500L)
    let doc = mkDoc [ mkCell "top" [ picked; crossWire ] ]
    let neighbours = Wire.touchingNeighbors "top" Set.empty picked doc
    neighbours |> List.map fst |> should equal [ 1 ]

[<Fact>]
let ``touchingNeighbors: cross-wire on a different layer is rejected`` () =
    // Audit-regression: pre-fix, a chip-boundary rail on a foreign
    // layer (l65/44 nsdm) whose bbox overlapped the picked wire
    // was returned. Now layer must match.
    let picked = mkRect (0L, 0L, 2000L, 100L)              // met1
    let foreignLayer = mkRectLayer li1 (950L, 100L, 1050L, 500L) // li1
    let doc = mkDoc [ mkCell "top" [ picked; foreignLayer ] ]
    Wire.touchingNeighbors "top" Set.empty picked doc
    |> should be Empty

[<Fact>]
let ``touchingNeighbors: chip-rail spanning entire macro on different layer is rejected`` () =
    // Direct audit reproduction. Picked is a normal met1 wire.
    // Chip-boundary rail is on l65/44 (nsdm) spanning the macro
    // width. Pre-fix this got pulled in; now layer mismatch stops it.
    let picked = mkRect (1000L, 1000L, 5000L, 1100L)
    let nsdm : Layer = Named ("sky130", "nsdm")
    let chipRail = mkRectLayer nsdm (-1000L, -800L, 20000L, -300L)
    let doc = mkDoc [ mkCell "top" [ picked; chipRail ] ]
    Wire.touchingNeighbors "top" Set.empty picked doc
    |> should be Empty

[<Fact>]
let ``touchingNeighbors: same-axis parallel rect on same layer is rejected`` () =
    // Two horizontal segments side by side — collinear-or-parallel,
    // not endpoint-touching. `collinearGroupOf` handles same-axis.
    let picked = mkRect (0L, 0L, 1000L, 100L)
    let parallel_ = mkRect (1000L, 0L, 2000L, 100L)  // abuts at x=1000
    let doc = mkDoc [ mkCell "top" [ picked; parallel_ ] ]
    Wire.touchingNeighbors "top" Set.empty picked doc
    |> should be Empty

[<Fact>]
let ``touchingNeighbors: cross-wire whose endpoint is OUTSIDE picked's X range is rejected`` () =
    // Vertical cross-wire's Y endpoint touches y=100 (picked's top
    // edge) but its X is OUTSIDE picked's X range — it's not
    // physically connected to the picked wire.
    let picked = mkRect (0L, 0L, 1000L, 100L)
    let detached = mkRect (5000L, 100L, 5100L, 500L)
    let doc = mkDoc [ mkCell "top" [ picked; detached ] ]
    Wire.touchingNeighbors "top" Set.empty picked doc
    |> should be Empty

[<Fact>]
let ``touchingNeighbors: excludeIndices is honoured`` () =
    let picked = mkRect (0L, 0L, 2000L, 100L)
    let crossWire = mkRect (950L, 100L, 1050L, 500L)
    let doc = mkDoc [ mkCell "top" [ picked; crossWire ] ]
    Wire.touchingNeighbors "top" (Set.ofList [1]) picked doc
    |> should be Empty

// --- scrubDispersedWireIds ----------------------------------------------

[<Fact>]
let ``scrubDispersedWireIds: connected wire (all rects touch) is preserved`` () =
    // 3 rects sharing WireId 1, all touching: a horizontal at y=0,
    // a vertical at x=1000 starting at y=0, and a horizontal at
    // y=2000 starting at x=1000. One contiguous L-Z shape.
    let r0 = mkRect (0L,    0L, 1000L,  100L) |> Wire.setWireId 1
    let r1 = mkRect (1000L, 0L, 1100L, 2000L) |> Wire.setWireId 1
    let r2 = mkRect (1000L, 1900L, 3000L, 2000L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r0; r1; r2 ] ]
    let doc', stripped = Wire.scrubDispersedWireIds doc
    stripped |> should equal 0
    // All three retain their WireId.
    let cell = doc'.Cells |> List.head
    cell.Elements
    |> List.iter (fun el ->
        match el with
        | RectEl r -> Wire.getWireId r |> should equal (Some 1)
        | _ -> ())

[<Fact>]
let ``scrubDispersedWireIds: disjoint rects sharing a WireId get scrubbed`` () =
    // Two rects sharing WireId 1 but spatially disjoint —
    // corruption from a past drag that re-stamped unrelated rects.
    let r0 = mkRect (0L,     0L,  100L,  100L) |> Wire.setWireId 1
    let r1 = mkRect (10000L, 10000L, 10100L, 10100L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ r0; r1 ] ]
    let doc', stripped = Wire.scrubDispersedWireIds doc
    stripped |> should equal 1
    // Both rects lose the WireId; geometry untouched.
    let cell = doc'.Cells |> List.head
    cell.Elements
    |> List.iter (fun el ->
        match el with
        | RectEl r ->
            Wire.getWireId r |> should equal (None : int option)
        | _ -> ())

[<Fact>]
let ``scrubDispersedWireIds: independent connected wires keep their ids`` () =
    // Two distinct WireIds, each one a connected pair. Neither
    // should be scrubbed — they're not sharing an id.
    let a0 = mkRect (0L, 0L, 500L, 100L) |> Wire.setWireId 1
    let a1 = mkRect (500L, 0L, 1000L, 100L) |> Wire.setWireId 1
    let b0 = mkRect (5000L, 5000L, 5500L, 5100L) |> Wire.setWireId 2
    let b1 = mkRect (5500L, 5000L, 6000L, 5100L) |> Wire.setWireId 2
    let doc = mkDoc [ mkCell "top" [ a0; a1; b0; b1 ] ]
    let doc', stripped = Wire.scrubDispersedWireIds doc
    stripped |> should equal 0

[<Fact>]
let ``scrubDispersedWireIds: untagged rects are ignored`` () =
    let r0 = mkRect (0L, 0L, 100L, 100L)
    let r1 = mkRect (10000L, 10000L, 10100L, 10100L)
    let doc = mkDoc [ mkCell "top" [ r0; r1 ] ]
    let doc', stripped = Wire.scrubDispersedWireIds doc
    stripped |> should equal 0
    doc' |> should equal doc

// ─────────────────────────────────────────────────────────────────
// isKnuckleShape — square-vs-wire classification.  Drives the
// canvas's wire-select short-circuit so clicking a knuckle
// doesn't drag the whole connected wire chain into the selection.
// 1.5× aspect threshold (max-side / min-side); mirrors the
// `Routing.ViaTool` snap classification so the V tool and
// selection agree on what counts as a knuckle.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``isKnuckleShape: 1x1 square is a knuckle`` () =
    let r = mkRect (0L, 0L, 320L, 320L)
    Wire.isKnuckleShape r |> should be True

[<Fact>]
let ``isKnuckleShape: 1.4x aspect still classifies as knuckle`` () =
    // Slightly elongated pad — within sky130 enclosure-asymmetry
    // range (different met-side margin on each axis).  Must stay
    // knuckle so the user can still single-click it.
    let r = mkRect (0L, 0L, 200L, 280L)
    Wire.isKnuckleShape r |> should be True

[<Fact>]
let ``isKnuckleShape: 2x aspect classifies as wire`` () =
    let r = mkRect (0L, 0L, 200L, 400L)
    Wire.isKnuckleShape r |> should be False

[<Fact>]
let ``isKnuckleShape: long horizontal wire is not a knuckle`` () =
    let r = mkRect (0L, 0L, 5000L, 320L)
    Wire.isKnuckleShape r |> should be False

[<Fact>]
let ``isKnuckleShape: long vertical wire is not a knuckle`` () =
    let r = mkRect (0L, 0L, 320L, 5000L)
    Wire.isKnuckleShape r |> should be False

[<Fact>]
let ``isKnuckleShape: degenerate zero-width rect is treated as knuckle (no div by zero)`` () =
    // A zero-width rect would crash a naive max/min computation.
    // Guard pins the 1L floor — degenerate stays classified as
    // "pick just this rect" which is the safer default.
    let r = mkRect (100L, 100L, 100L, 100L)
    Wire.isKnuckleShape r |> should be True

[<Fact>]
let ``isKnuckleShape: negative-extent rect handled by abs`` () =
    // X2 < X1 happens after a flipped drag; abs guard means the
    // aspect-ratio math doesn't go negative and underflow the
    // threshold comparison.
    let r = mkRect (200L, 0L, 0L, 200L)
    Wire.isKnuckleShape r |> should be True

// ─────────────────────────────────────────────────────────────────
// findSegmentAt — visual-topmost hit-test per selection spec
// rules 1.2 and 1.3.  When the cursor sits on multiple rects,
// the rect on the highest GDS layer number wins; ties on the
// same layer go to later-in-document.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``findSegmentAt picks the higher-layer rect when two layers contain cursor`` () =
    // li1 rect (layer 67) authored LATER in the file than a met2
    // rect (layer 69) sitting on top of it.  The pre-2026-06-04
    // behaviour returned li1 (last in doc order wins); the new
    // behaviour returns met2 (visually on top).
    let lower = mkRectOn met2 (0L, 0L, 1000L, 1000L)
    let upper = mkRectOn li1  (0L, 0L, 1000L, 1000L)
    // met2 first, li1 later — exercise the "earlier in file is
    // still visually-topmost when its layer is higher" path.
    let doc = mkDoc [ mkCell "top" [ lower; upper ] ]
    let hit = Wire.findSegmentAt 500L 500L doc
    match hit with
    | Some (_, _, _, r) -> r.Layer |> should equal met2
    | None -> Assert.Fail("expected a hit on met2")

[<Fact>]
let ``findSegmentAt prefers met5 over met1 regardless of doc order`` () =
    // Three stacked rects on different layers.  Highest layer
    // wins independently of where it appears in the cell.
    let m1 = mkRectOn met1 (0L, 0L, 1000L, 1000L)
    let m3 = mkRectOn met3 (0L, 0L, 1000L, 1000L)
    let m2 = mkRectOn met2 (0L, 0L, 1000L, 1000L)
    let doc = mkDoc [ mkCell "top" [ m1; m3; m2 ] ]
    let hit = Wire.findSegmentAt 500L 500L doc
    match hit with
    | Some (_, _, _, r) -> r.Layer |> should equal met3
    | None -> Assert.Fail("expected a hit on met3")

[<Fact>]
let ``findSegmentAt ties on same layer go to later-in-document rect`` () =
    // Both met1; cursor in their shared overlap.  Later-authored
    // wins because the renderer paints later rects on top within
    // a single layer.  This matches the previous test's intent
    // (existing test exercises same-WireId; this one removes
    // WireId from the picture so the tiebreak rule is exercised
    // in isolation).
    let earlier = mkRectOn met1 (0L, 0L, 1000L, 1000L)
    let later   = mkRectOn met1 (0L, 0L, 1000L, 1000L)
    let doc = mkDoc [ mkCell "top" [ earlier; later ] ]
    let hit = Wire.findSegmentAt 500L 500L doc
    match hit with
    | Some (_, _, idx, _) -> idx |> should equal 1
    | None -> Assert.Fail("expected a hit")

// ─────────────────────────────────────────────────────────────────
// connectedComponentWireBodiesOnly — wire-body chain walk that
// crosses knuckles as bridges but excludes them from the result.
// Pins selection spec rules 3.1 and 3.2.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``chain crosses a knuckle to reach the wire body on the other side`` () =
    // Vertical wire body, square knuckle on top, horizontal wire
    // body above the knuckle.  All same layer, same WireId, all
    // bbox-touching.  Click the vertical → selection contains
    // BOTH wire bodies, NOT the knuckle.
    //
    // Knuckle dims: 200×200 (aspect 1.0 < 1.5 threshold).
    // Earlier draft used 400×200 (aspect 2.0) which classifies
    // as a wire, not a knuckle — produced false-passing
    // [0; 1; 2] from the helper.
    let vbody = mkRect (450L,  0L,  550L, 1000L) |> Wire.setWireId 1   // 100×1000 vertical
    let knuck = mkRect (400L, 900L, 600L, 1100L) |> Wire.setWireId 1   // 200×200 square
    let hbody = mkRect (0L,  1050L, 1000L, 1150L) |> Wire.setWireId 1  // 1000×100 horizontal
    let doc = mkDoc [ mkCell "top" [ vbody; knuck; hbody ] ]
    let pred _ (r : Rectangle) =
        r.Layer = vbody.Layer && Wire.getWireId r = Some 1
    let result =
        Wire.connectedComponentWireBodiesOnly "top" 0 pred doc
    // vertical body (0) and horizontal body (2) — knuckle (1) excluded.
    result |> List.sort |> should equal [ 0; 2 ]

[<Fact>]
let ``chain on a knuckle seed still returns just the seed`` () =
    // Belt-and-braces guard described in the helper's docstring:
    // if a caller hands a knuckle seed to this helper, we keep
    // the seed in the result instead of returning empty (which
    // would silently lose selection state).
    let knuck = mkRect (400L, 900L, 600L, 1100L) |> Wire.setWireId 1
    let body  = mkRect (450L,  0L,  550L, 1000L) |> Wire.setWireId 1
    let doc = mkDoc [ mkCell "top" [ knuck; body ] ]
    let pred _ (r : Rectangle) =
        r.Layer = knuck.Layer && Wire.getWireId r = Some 1
    // Seed = the knuckle.  Even though the body is reachable and
    // would normally be selected, we want the seed-as-knuckle
    // case to keep the seed in the result.
    let result =
        Wire.connectedComponentWireBodiesOnly "top" 0 pred doc
    result |> List.contains 0 |> should be True
    result |> List.contains 1 |> should be True

[<Fact>]
let ``chain stops at WireId boundary`` () =
    // Two wire bodies share a layer but have different WireIds.
    // The walk must not cross between them even though they
    // bbox-touch.  Pins rule 3's "same-layer + same-WireId"
    // predicate explicitly.
    let wire1 = mkRect (0L, 0L, 500L, 100L) |> Wire.setWireId 1
    let wire2 = mkRect (500L, 0L, 1000L, 100L) |> Wire.setWireId 2
    let doc = mkDoc [ mkCell "top" [ wire1; wire2 ] ]
    let pred _ (r : Rectangle) =
        r.Layer = wire1.Layer && Wire.getWireId r = Some 1
    let result =
        Wire.connectedComponentWireBodiesOnly "top" 0 pred doc
    result |> should equal [ 0 ]

// ---------------------------------------------------------------
// locatePadAt: the new-wire tool's pad hit-test. A routing-layer
// SQUARE (which locateRoute rejects as a pad) must be found here,
// with its center + half-width; a wire-shaped rect must NOT be
// returned as a pad. met3 = GDS (70, 20).
// ---------------------------------------------------------------

[<Fact>]
let ``locatePadAt finds a square met3 pad and reports center + half-width`` () =
    // 490x490 pad at origin: center (245,245), min side 490 -> half 245.
    let pad = mkRectOn met3 (0L, 0L, 490L, 490L)
    let doc = mkDoc [ mkCell "top" [ pad ] ]
    match Detect.locatePadAt doc "top" 70 20 { X = 245L; Y = 245L } with
    | None -> failwith "expected a pad hit at the pad center"
    | Some hit ->
        hit.Center |> should equal { X = 245L; Y = 245L }
        hit.HalfWidth |> should equal 245L

[<Fact>]
let ``locatePadAt returns None when the click misses every pad`` () =
    let pad = mkRectOn met3 (0L, 0L, 490L, 490L)
    let doc = mkDoc [ mkCell "top" [ pad ] ]
    Detect.locatePadAt doc "top" 70 20 { X = 5000L; Y = 5000L }
    |> should equal (None : Detect.PadHit option)

[<Fact>]
let ``locatePadAt ignores a wire-shaped rect (aspect above threshold)`` () =
    // 6000x490 strap: aspect ~12 -> a WIRE, not a pad. Even a click
    // squarely inside it must not register as a pad.
    let wire = mkRectOn met3 (0L, 0L, 6000L, 490L)
    let doc = mkDoc [ mkCell "top" [ wire ] ]
    Detect.locatePadAt doc "top" 70 20 { X = 3000L; Y = 245L }
    |> should equal (None : Detect.PadHit option)

[<Fact>]
let ``locatePadAt only matches the requested layer`` () =
    // A met2 (69,20) pad must not answer a met3 (70,20) query.
    let padM2 = mkRectOn met2 (0L, 0L, 490L, 490L)
    let doc = mkDoc [ mkCell "top" [ padM2 ] ]
    Detect.locatePadAt doc "top" 70 20 { X = 245L; Y = 245L }
    |> should equal (None : Detect.PadHit option)

[<Fact>]
let ``locatePadAt prefers the smallest containing pad when pads nest`` () =
    // A big enclosure pad and a small tap pad both contain the click.
    // The small one (the actual via square) should win.
    let big = mkRectOn met3 (0L, 0L, 1000L, 1000L)
    let small = mkRectOn met3 (400L, 400L, 600L, 600L)
    let doc = mkDoc [ mkCell "top" [ big; small ] ]
    match Detect.locatePadAt doc "top" 70 20 { X = 500L; Y = 500L } with
    | None -> failwith "expected a pad hit"
    | Some hit ->
        hit.Center |> should equal { X = 500L; Y = 500L }
        hit.HalfWidth |> should equal 100L
