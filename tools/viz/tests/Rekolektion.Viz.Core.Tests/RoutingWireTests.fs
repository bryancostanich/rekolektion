module Rekolektion.Viz.Core.Tests.RoutingWireTests

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
}

let private li1 : Layer = Named ("sky130", "li1")

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
