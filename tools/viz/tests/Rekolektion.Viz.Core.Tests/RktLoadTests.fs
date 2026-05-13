module Rekolektion.Viz.Core.Tests.RktLoadTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types

let private fixturePath name =
    Path.Combine(System.AppContext.BaseDirectory, "testdata", name)

// ─── Gds.Reader.readGds (flipped) ───────────────────────────────────

[<Fact>]
let ``Gds.Reader.readGds returns a Rkt.Document`` () =
    let doc : Document = Gds.Reader.readGds (fixturePath "bitcell_lr.gds")
    doc.Pdk |> should equal "sky130"
    doc.Cells |> List.isEmpty |> should equal false

[<Fact>]
let ``Gds.Reader.readGds resolves layer numbers to named layers`` () =
    let doc = Gds.Reader.readGds (fixturePath "bitcell_lr.gds")
    let allLayers =
        doc.Cells
        |> List.collect (fun c -> c.Elements)
        |> List.choose (fun e ->
            match e with
            | PolyEl p -> Some p.Layer
            | PathEl p -> Some p.Layer
            | RectEl r -> Some r.Layer
            | LabelEl l -> Some l.Layer
            | PortEl p -> Some p.Layer
            | _ -> None)
        |> List.distinct
    // The bitcell uses metal1/poly/diff/etc. — at least one named
    // layer must appear after the readGds path resolves numbers.
    let hasNamed = allLayers |> List.exists (function Named _ -> true | _ -> false)
    hasNamed |> should equal true

// ─── Layer table legacy aliases ──────────────────────────────────────

[<Fact>]
let ``layer alias 8 0 resolves to met1`` () =
    Rkt.OfGds.layerFromGds 8 0 |> should equal (Named ("sky130", "met1"))

[<Fact>]
let ``layer alias 6 0 resolves to diff`` () =
    Rkt.OfGds.layerFromGds 6 0 |> should equal (Named ("sky130", "diff"))

[<Fact>]
let ``layer alias 7 0 resolves to licon`` () =
    Rkt.OfGds.layerFromGds 7 0 |> should equal (Named ("sky130", "licon"))

[<Fact>]
let ``layer alias 40 0 resolves to reram`` () =
    Rkt.OfGds.layerFromGds 40 0 |> should equal (Named ("sky130", "reram"))

[<Fact>]
let ``unknown layer pair still surfaces as Unknown`` () =
    Rkt.OfGds.layerFromGds 9999 7 |> should equal (Unknown (9999, 7))

// ─── LayoutLoader.load (flipped) ─────────────────────────────────────

[<Fact>]
let ``LayoutLoader.load returns a Rkt.Document for a .gds path`` () =
    let doc, warnings = Layout.LayoutLoader.load (fixturePath "bitcell_lr.gds")
    warnings |> should be Empty
    doc.Pdk |> should equal "sky130"
    doc.Cells |> List.isEmpty |> should equal false

[<Fact>]
let ``LayoutLoader.loadAsLibrary still produces the legacy shape`` () =
    let lib, _ = Layout.LayoutLoader.loadAsLibrary (fixturePath "bitcell_lr.gds")
    lib.Structures |> List.isEmpty |> should equal false
