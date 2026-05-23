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
