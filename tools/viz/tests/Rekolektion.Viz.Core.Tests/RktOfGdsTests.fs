module Rekolektion.Viz.Core.Tests.RktOfGdsTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt
open Rekolektion.Viz.Core.Rkt.Types

// ─── Layer mapping ───────────────────────────────────────────────────────

[<Fact>]
let ``layer 68/20 maps to sky130:met1`` () =
    OfGds.layerFromGds 68 20 |> should equal (Named ("sky130", "met1"))

[<Fact>]
let ``layer 66/20 maps to sky130:poly`` () =
    OfGds.layerFromGds 66 20 |> should equal (Named ("sky130", "poly"))

[<Fact>]
let ``unknown layer pair becomes Unknown`` () =
    OfGds.layerFromGds 9999 7 |> should equal (Unknown (9999, 7))

// ─── Element conversions ─────────────────────────────────────────────────

[<Fact>]
let ``Boundary becomes PolyEl with preserved points`` () =
    let b : Gds.Types.Boundary = {
        Layer = 68
        DataType = 20
        Points = [
            { X = 0L; Y = 0L }
            { X = 100L; Y = 0L }
            { X = 100L; Y = 50L }
            { X = 0L; Y = 0L }
        ]
    }
    match OfGds.fromBoundary b with
    | PolyEl p ->
        p.Layer |> should equal (Named ("sky130", "met1"))
        p.Points |> List.length |> should equal 4
        p.Net |> should equal None
        p.Props |> should be Empty
    | _ -> failwith "expected PolyEl"

[<Fact>]
let ``Path becomes PathEl with width preserved`` () =
    let p : Gds.Types.Path = {
        Layer = 67
        DataType = 20
        Width = 170
        Points = [ { X = 0L; Y = 0L }; { X = 500L; Y = 0L } ]
    }
    match OfGds.fromPath p with
    | PathEl r ->
        r.Layer |> should equal (Named ("sky130", "li1"))
        r.Width |> should equal 170L
        r.Points |> List.length |> should equal 2
        r.Cap |> should equal None
    | _ -> failwith "expected PathEl"

[<Fact>]
let ``SRef becomes SRefEl with origin and orientation preserved`` () =
    let s : Gds.Types.SRef = {
        StructureName = "bitcell"
        Origin = { X = 100L; Y = 200L }
        Mag = 1.0
        Angle = 90.0
        Reflected = true
    }
    match OfGds.fromSRef s with
    | SRefEl r ->
        r.Cell |> should equal "bitcell"
        r.Origin |> should equal { X = 100L; Y = 200L }
        r.Rot |> should equal 90.0
        r.Mag |> should equal 1.0
        r.Reflect |> should equal true
    | _ -> failwith "expected SRefEl"

[<Fact>]
let ``ARef becomes ARefEl with rows cols pitches preserved`` () =
    let a : Gds.Types.ARef = {
        StructureName = "wl"
        Origin = { X = 0L; Y = 0L }
        Cols = 64
        Rows = 2
        ColPitch = { X = 10L; Y = 0L }
        RowPitch = { X = 0L; Y = 5L }
        Mag = 1.0
        Angle = 0.0
        Reflected = false
    }
    match OfGds.fromARef a with
    | ARefEl r ->
        r.Cell |> should equal "wl"
        r.Cols |> should equal 64
        r.Rows |> should equal 2
        r.ColPitch |> should equal { X = 10L; Y = 0L }
        r.RowPitch |> should equal { X = 0L; Y = 5L }
    | _ -> failwith "expected ARefEl"

[<Fact>]
let ``Text becomes LabelEl`` () =
    let t : Gds.Types.TextLabel = {
        Layer = 68
        TextType = 5
        Origin = { X = 10L; Y = 10L }
        Text = "BL"
    }
    match OfGds.fromText t with
    | LabelEl l ->
        l.Text |> should equal "BL"
        l.Origin |> should equal { X = 10L; Y = 10L }
    | _ -> failwith "expected LabelEl"

// ─── Library shape ───────────────────────────────────────────────────────

[<Fact>]
let ``fromLibrary copies structures into cells in order`` () =
    let lib : Gds.Types.Library = {
        Name = "test"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1.0e-9
        Structures = [
            { Name = "top"
              Elements = [
                  Gds.Types.SRef {
                      StructureName = "leaf"
                      Origin = { X = 0L; Y = 0L }
                      Mag = 1.0; Angle = 0.0; Reflected = false
                  }
              ] }
            { Name = "leaf"
              Elements = [
                  Gds.Types.Boundary {
                      Layer = 68; DataType = 20
                      Points = [
                          { X = 0L; Y = 0L }
                          { X = 10L; Y = 0L }
                          { X = 10L; Y = 10L }
                          { X = 0L; Y = 10L }
                          { X = 0L; Y = 0L }
                      ]
                  }
              ] }
        ]
    }
    let doc = OfGds.fromLibrary lib
    doc.Pdk |> should equal "sky130"
    doc.Units.DbuNm |> should equal 1
    doc.Cells |> List.length |> should equal 2
    doc.TopCell |> should equal (Some "top")
    let leaf = doc.Cells |> List.find (fun c -> c.Name = "leaf")
    match leaf.Elements with
    | [ PolyEl p ] -> p.Layer |> should equal (Named ("sky130", "met1"))
    | _ -> failwith "leaf should have one PolyEl"

[<Fact>]
let ``fromLibrary produces a TopCell of None for an empty library`` () =
    let lib : Gds.Types.Library = {
        Name = "empty"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1.0e-9
        Structures = []
    }
    let doc = OfGds.fromLibrary lib
    doc.Cells |> List.length |> should equal 0
    doc.TopCell |> should equal None

[<Fact>]
let ``fromLibrary deduces dbu_nm from DbUnitsInMeters`` () =
    // 1 nm/DBU → dbu_nm 1; 10 nm/DBU → dbu_nm 10
    let mkLib (dbuInMeters: float) : Gds.Types.Library =
        { Name = "x"
          UserUnitsPerDbUnit = 0.001
          DbUnitsInMeters = dbuInMeters
          Structures = [] }
    (OfGds.fromLibrary (mkLib 1.0e-9)).Units.DbuNm |> should equal 1
    (OfGds.fromLibrary (mkLib 1.0e-8)).Units.DbuNm |> should equal 10
