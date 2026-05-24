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
