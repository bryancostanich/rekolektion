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

// ─── .rkt save + reopen round-trip (Step 4) ──────────────────────────

[<Fact>]
let ``LayoutLoader.load handles a .rkt path`` () =
    // Write a minimal .rkt to a temp file, load it back through
    // LayoutLoader, verify the AST contents.
    let tmp = Path.Combine(Path.GetTempPath(),
                           "rkt-load-" + System.Guid.NewGuid().ToString("N") + ".rkt")
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (cell c\n"
        + "    (poly (layer sky130:met1) (points (0 0) (10 0) (10 10) (0 10)))))\n"
    File.WriteAllText(tmp, src)
    try
        let doc, warnings = Layout.LayoutLoader.load tmp
        warnings |> should be Empty
        doc.Cells |> List.length |> should equal 1
        let cell = List.head doc.Cells
        cell.Name |> should equal "c"
    finally
        try File.Delete tmp with _ -> ()

[<Fact>]
let ``LayoutLoader.load warns when a .rkt has unresolved imports`` () =
    let tmp = Path.Combine(Path.GetTempPath(),
                           "rkt-imp-" + System.Guid.NewGuid().ToString("N") + ".rkt")
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (import \"sibling.rkt\")\n"
        + "  (cell c))\n"
    File.WriteAllText(tmp, src)
    try
        let _, warnings = Layout.LayoutLoader.load tmp
        warnings |> List.length |> should equal 1
        warnings.[0] |> should haveSubstring "import"
    finally
        try File.Delete tmp with _ -> ()

[<Fact>]
let ``Library -> rkt text -> document round-trip preserves geometry`` () =
    // Mirrors what `App.Services.EditSession.saveTo` does on a
    // `.rkt` target, then re-reads via the LayoutLoader.
    let lib : Gds.Types.Library = {
        Name = "rt"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1.0e-9
        Structures = [
            { Name = "c"
              Elements = [
                  Gds.Types.Boundary {
                      Layer = 68; DataType = 20
                      Points = [
                          { X = 0L; Y = 0L }
                          { X = 100L; Y = 0L }
                          { X = 100L; Y = 50L }
                          { X = 0L; Y = 50L }
                          { X = 0L; Y = 0L }
                      ]
                  }
              ] }
        ]
    }
    let tmp = Path.Combine(Path.GetTempPath(),
                           "rkt-save-" + System.Guid.NewGuid().ToString("N") + ".rkt")
    try
        let doc = Rkt.OfGds.fromLibrary lib
        File.WriteAllText(tmp, Rkt.Writer.write doc)
        let reloaded, _ = Layout.LayoutLoader.load tmp
        reloaded.Cells |> List.length |> should equal 1
        let cell = List.head reloaded.Cells
        match cell.Elements with
        | [ PolyEl p ] ->
            p.Layer |> should equal (Named ("sky130", "met1"))
            p.Points |> List.length |> should equal 5
        | _ -> failwithf "unexpected elements: %A" cell.Elements
    finally
        try File.Delete tmp with _ -> ()
