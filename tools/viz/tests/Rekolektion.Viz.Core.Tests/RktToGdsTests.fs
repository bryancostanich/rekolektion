module Rekolektion.Viz.Core.Tests.RktToGdsTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt
open Rekolektion.Viz.Core.Rkt.Types

[<Fact>]
let ``Named sky130:met1 resolves to (68, 20)`` () =
    ToGds.layerToGds (Named ("sky130", "met1")) |> should equal (68, 20)

[<Fact>]
let ``Unknown(N, D) passes through verbatim`` () =
    ToGds.layerToGds (Unknown (9999, 7)) |> should equal (9999, 7)

[<Fact>]
let ``Poly becomes Boundary with point list preserved`` () =
    let p : Poly = {
        Layer = Named ("sky130", "met1")
        Points = [
            { X = 0L; Y = 0L }
            { X = 100L; Y = 0L }
            { X = 100L; Y = 50L }
            { X = 0L; Y = 50L }
            { X = 0L; Y = 0L }
        ]
        Net = Some "BL"
        Props = []
        Comments = []
    }
    let b = ToGds.polyToBoundary p
    b.Layer |> should equal 68
    b.DataType |> should equal 20
    b.Points |> List.length |> should equal 5

[<Fact>]
let ``Path width and points preserved`` () =
    let p : Path = {
        Layer = Named ("sky130", "li1")
        Width = 170L
        Points = [ { X = 0L; Y = 0L }; { X = 500L; Y = 0L } ]
        Net = None
        Cap = Some "round"
        Props = []
        Comments = []
    }
    let g = ToGds.pathToGds p
    g.Width |> should equal 170
    g.Layer |> should equal 67

[<Fact>]
let ``SRef preserves origin and orientation`` () =
    let s : SRef = {
        Cell = "bitcell"
        Origin = { X = 100L; Y = 200L }
        Rot = 90.0
        Mag = 1.0
        Reflect = true
        Props = []
        Comments = []
    }
    let g = ToGds.srefToGds s
    g.StructureName |> should equal "bitcell"
    g.Origin |> should equal { Gds.Types.X = 100L; Gds.Types.Y = 200L }
    g.Angle |> should equal 90.0
    g.Reflected |> should equal true

[<Fact>]
let ``ARef preserves rows cols pitches`` () =
    let a : ARef = {
        Cell = "wl"
        Origin = { X = 0L; Y = 0L }
        Cols = 64
        Rows = 1
        ColPitch = { X = 10L; Y = 0L }
        RowPitch = { X = 0L; Y = 5L }
        Rot = 0.0
        Mag = 1.0
        Reflect = false
        Props = []
        Comments = []
    }
    let g = ToGds.arefToGds a
    g.Cols |> should equal 64
    g.Rows |> should equal 1
    g.ColPitch |> should equal { Gds.Types.X = 10L; Gds.Types.Y = 0L }

[<Fact>]
let ``Port emits one geometry element and one text label`` () =
    let p : Port = {
        Name = "BL"
        Direction = Input
        Layer = Named ("sky130", "met1")
        Flags = [ Signal ]
        Shape = RectShape (0L, 0L, 10L, 50L)
        Net = None
        Props = []
        Comments = []
    }
    let elements = ToGds.portToGds p
    elements |> List.length |> should equal 2
    let hasBoundary = elements |> List.exists (function Gds.Types.Boundary _ -> true | _ -> false)
    let hasText = elements |> List.exists (function Gds.Types.Text _ -> true | _ -> false)
    hasBoundary |> should equal true
    hasText |> should equal true

[<Fact>]
let ``PropsEl drops from output`` () =
    let p : Props = { Items = [ { Key = "k"; Value = PvAtom "v" } ]; Comments = [] }
    ToGds.elementToGds (PropsEl p) |> should be Empty

// ─── Round-trip via OfGds ───────────────────────────────────────────────

[<Fact>]
let ``Rkt -> Gds -> Rkt preserves geometry and hierarchy`` () =
    let original : Document = {
        emptyDocument with
            Cells = [
                { Name = "top"
                  Meta = None
                  Comments = []
                  Elements = [
                      SRefEl {
                          Cell = "leaf"
                          Origin = { X = 100L; Y = 0L }
                          Rot = 0.0; Mag = 1.0; Reflect = false
                          Props = []
                          Comments = []
                      }
                  ] }
                { Name = "leaf"
                  Meta = None
                  Comments = []
                  Elements = [
                      PolyEl {
                          Layer = Named ("sky130", "met1")
                          Points = [
                              { X = 0L; Y = 0L }
                              { X = 10L; Y = 0L }
                              { X = 10L; Y = 10L }
                              { X = 0L; Y = 10L }
                              { X = 0L; Y = 0L }
                          ]
                          Net = None
                          Props = []
                          Comments = []
                      }
                      PathEl {
                          Layer = Named ("sky130", "poly")
                          Width = 17L
                          Points = [ { X = 0L; Y = 5L }; { X = 10L; Y = 5L } ]
                          Net = None
                          Cap = None
                          Props = []
                          Comments = []
                      }
                  ] }
            ]
            TopCell = Some "top"
    }
    let lib = ToGds.toLibrary original
    let roundTripped = OfGds.fromLibrary lib
    roundTripped.Cells |> List.length |> should equal 2
    roundTripped.TopCell |> should equal (Some "top")
    let leaf = roundTripped.Cells |> List.find (fun c -> c.Name = "leaf")
    match leaf.Elements with
    | [ PolyEl p1; PathEl p2 ] ->
        p1.Layer |> should equal (Named ("sky130", "met1"))
        p1.Points |> List.length |> should equal 5
        p2.Layer |> should equal (Named ("sky130", "poly"))
        p2.Width |> should equal 17L
    | _ -> failwithf "unexpected leaf elements: %A" leaf.Elements

[<Fact>]
let ``Rkt port survives as geometry + label on round trip`` () =
    let original : Document = {
        emptyDocument with
            Cells = [
                { Name = "c"
                  Meta = None
                  Comments = []
                  Elements = [
                      PortEl {
                          Name = "BL"
                          Direction = Input
                          Layer = Named ("sky130", "met1")
                          Flags = [ Signal ]
                          Shape = RectShape (0L, 0L, 10L, 50L)
                          Net = None
                          Props = []
                          Comments = []
                      }
                  ] }
            ]
    }
    let lib = ToGds.toLibrary original
    let roundTripped = OfGds.fromLibrary lib
    let cell = List.head roundTripped.Cells
    cell.Elements |> List.length |> should equal 2
    let hasPoly = cell.Elements |> List.exists (function PolyEl _ -> true | _ -> false)
    let hasLabel = cell.Elements |> List.exists (function LabelEl l -> l.Text = "BL" | _ -> false)
    hasPoly |> should equal true
    hasLabel |> should equal true

[<Fact>]
let ``unknown layer passes through to GDS and back intact`` () =
    let doc : Document = {
        emptyDocument with
            Cells = [
                { Name = "c"
                  Meta = None
                  Comments = []
                  Elements = [
                      PolyEl {
                          Layer = Unknown (1234, 56)
                          Points = [
                              { X = 0L; Y = 0L }
                              { X = 1L; Y = 0L }
                              { X = 1L; Y = 1L }
                              { X = 0L; Y = 0L }
                          ]
                          Net = None
                          Props = []
                          Comments = []
                      }
                  ] }
            ]
    }
    let lib = ToGds.toLibrary doc
    let back = OfGds.fromLibrary lib
    let cell = List.head back.Cells
    match cell.Elements with
    | [ PolyEl p ] -> p.Layer |> should equal (Unknown (1234, 56))
    | _ -> failwith "expected one poly"
