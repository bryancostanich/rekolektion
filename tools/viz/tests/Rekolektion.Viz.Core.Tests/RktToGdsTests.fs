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
        SubFormComments = Map.empty
    }
    let b = ToGds.polyToBoundary 5L p
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
        SubFormComments = Map.empty
    }
    let g = ToGds.pathToGds 5L p
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
        SubFormComments = Map.empty
    }
    let g = ToGds.srefToGds 5L s
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
        SubFormComments = Map.empty
    }
    let g = ToGds.arefToGds 5L a
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
        SubFormComments = Map.empty
    }
    let elements = ToGds.portToGds 5L p
    elements |> List.length |> should equal 2
    let hasBoundary = elements |> List.exists (function Gds.Types.Boundary _ -> true | _ -> false)
    let hasText = elements |> List.exists (function Gds.Types.Text _ -> true | _ -> false)
    hasBoundary |> should equal true
    hasText |> should equal true

[<Fact>]
let ``PropsEl drops from output`` () =
    let p : Props =
        { Items = [ { Key = "k"; Value = PvAtom "v" } ]
          Comments = []; SubFormComments = Map.empty }
    ToGds.elementToGds 5L (PropsEl p) |> should be Empty

// ─── Round-trip via OfGds ───────────────────────────────────────────────

[<Fact>]
let ``Rkt -> Gds -> Rkt preserves geometry and hierarchy`` () =
    let original : Document = {
        emptyDocument with
            Cells = [
                { Name = "top"
                  Meta = None
                  Comments = []
                  SubFormComments = Map.empty
                  Elements = [
                      SRefEl {
                          Cell = "leaf"
                          Origin = { X = 100L; Y = 0L }
                          Rot = 0.0; Mag = 1.0; Reflect = false
                          Props = []
                          Comments = []
                          SubFormComments = Map.empty
                      }
                  ] }
                { Name = "leaf"
                  Meta = None
                  Comments = []
                  SubFormComments = Map.empty
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
                          SubFormComments = Map.empty
                      }
                      PathEl {
                          Layer = Named ("sky130", "poly")
                          Width = 17L
                          Points = [ { X = 0L; Y = 5L }; { X = 10L; Y = 5L } ]
                          Net = None
                          Cap = None
                          Props = []
                          Comments = []
                          SubFormComments = Map.empty
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
                  SubFormComments = Map.empty
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
                          SubFormComments = Map.empty
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
                  SubFormComments = Map.empty
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
                          SubFormComments = Map.empty
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

// ─── Grid snap (Track 01) ────────────────────────────────────────────────

[<Fact>]
let ``off-grid rect corners snap on to-gds`` () =
    let doc : Document = {
        emptyDocument with
            Cells = [
                { Name = "c"
                  Meta = None
                  Comments = []
                  SubFormComments = Map.empty
                  Elements = [
                      RectEl {
                          Layer = Named ("sky130", "met1")
                          X1 = 173L; Y1 = 0L
                          X2 = 1000L; Y2 = 500L
                          Net = None
                          Props = []
                          Comments = []
                          SubFormComments = Map.empty
                      }
                  ] }
            ]
    }
    let lib = ToGds.toLibrary doc
    let struct1 = lib.Structures |> List.head
    match struct1.Elements |> List.head with
    | Gds.Types.Boundary b ->
        // 173 → 175; other corners stay put because they're already
        // on the 5-nm grid.
        let xs = b.Points |> List.map (fun p -> p.X) |> Set.ofList
        let ys = b.Points |> List.map (fun p -> p.Y) |> Set.ofList
        xs |> should equal (Set.ofList [ 175L; 1000L ])
        ys |> should equal (Set.ofList [ 0L; 500L ])
    | _ -> failwith "expected Boundary"

[<Fact>]
let ``off-grid sref origin snaps`` () =
    let doc : Document = {
        emptyDocument with
            Cells = [
                { Name = "c"
                  Meta = None
                  Comments = []
                  SubFormComments = Map.empty
                  Elements = [
                      SRefEl {
                          Cell = "leaf"
                          Origin = { X = 173L; Y = -2917L }
                          Rot = 0.0; Mag = 1.0; Reflect = false
                          Props = []; Comments = []
                          SubFormComments = Map.empty
                      }
                  ] }
            ]
    }
    let lib = ToGds.toLibrary doc
    let struct1 = lib.Structures |> List.head
    match struct1.Elements |> List.head with
    | Gds.Types.SRef s ->
        // 173 → 175; -2917 → -2915 (half-away-from-zero, symmetric).
        s.Origin.X |> should equal 175L
        s.Origin.Y |> should equal -2915L
    | _ -> failwith "expected SRef"

[<Fact>]
let ``off-grid label origin snaps`` () =
    let doc : Document = {
        emptyDocument with
            Cells = [
                { Name = "c"
                  Meta = None
                  Comments = []
                  SubFormComments = Map.empty
                  Elements = [
                      LabelEl {
                          Layer = Named ("sky130", "met1_label")
                          Text = "VSS"
                          Origin = { X = 173L; Y = 7L }
                          Class = None
                          Props = []; Comments = []
                          SubFormComments = Map.empty
                          IsInternal = false
                          Kind = NetName
                      }
                  ] }
            ]
    }
    let lib = ToGds.toLibrary doc
    let struct1 = lib.Structures |> List.head
    match struct1.Elements |> List.head with
    | Gds.Types.Text t ->
        t.Origin.X |> should equal 175L
        t.Origin.Y |> should equal 5L
    | _ -> failwith "expected Text"

[<Fact>]
let ``unknown PDK fails loudly on toLibrary`` () =
    let doc : Document = {
        emptyDocument with
            Pdk = "not_a_real_pdk"
            Cells = [
                { Name = "c"; Meta = None
                  Comments = []; SubFormComments = Map.empty
                  Elements = [] }
            ]
    }
    (fun () -> ToGds.toLibrary doc |> ignore)
    |> should throw typeof<System.Exception>
