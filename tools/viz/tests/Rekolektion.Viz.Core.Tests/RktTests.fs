module Rekolektion.Viz.Core.Tests.RktTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt
open Rekolektion.Viz.Core.Rkt.Types

let private parseOk (src: string) =
    match Reader.parse src with
    | Ok d -> d
    | Error e -> failwithf "parse failed: %A" e

let private analyzeOk (src: string) =
    let cst = parseOk src
    match Reader.analyze cst with
    | Ok ast -> cst, ast
    | Error e -> failwithf "analyze failed: %A" e

// ─── Lexer / parser basics ─────────────────────────────────────────────

[<Fact>]
let ``parses an empty layout`` () =
    let src = "(layout (version 1) (pdk sky130))\n"
    let _, ast = analyzeOk src
    ast.Version |> should equal 1
    ast.Pdk |> should equal "sky130"
    ast.Cells |> List.length |> should equal 0

[<Fact>]
let ``round-trip is byte-exact for untouched source`` () =
    let src = "(layout (version 1) (pdk sky130)\n  (cell foo\n    (poly (layer sky130:met1)\n          (points (0 0) (10 0) (10 5) (0 5)))))\n"
    let cst = parseOk src
    Writer.renderCst cst |> should equal src

[<Fact>]
let ``preserves line comments in round-trip`` () =
    let src = "; top of file\n(layout (version 1) (pdk sky130)\n  ; before cell\n  (cell c1))\n"
    let cst = parseOk src
    Writer.renderCst cst |> should equal src

[<Fact>]
let ``classifies integer and float atoms by lexeme`` () =
    let src = "(layout (version 1) (pdk sky130) (units (dbu_nm 5) (uu_um 1)))"
    let cst = parseOk src
    // Walk into the units form and check atom kinds.
    let layout =
        match cst.Roots with
        | [ Rekolektion.Viz.Core.Rkt.Cst.SList l ] -> l
        | _ -> failwith "expected one layout form"
    let units =
        layout.Children
        |> List.pick (fun c ->
            match c with
            | Rekolektion.Viz.Core.Rkt.Cst.SList l when
                (match l.Children with
                 | Rekolektion.Viz.Core.Rkt.Cst.SAtom a :: _ -> a.Text = "units"
                 | _ -> false) -> Some l
            | _ -> None)
    let dbuNm =
        units.Children
        |> List.pick (fun c ->
            match c with
            | Rekolektion.Viz.Core.Rkt.Cst.SList l ->
                match l.Children with
                | Rekolektion.Viz.Core.Rkt.Cst.SAtom { Text = "dbu_nm" } :: Rekolektion.Viz.Core.Rkt.Cst.SAtom value :: _ ->
                    Some value
                | _ -> None
            | _ -> None)
    dbuNm.Kind |> should equal Rekolektion.Viz.Core.Rkt.Cst.IntLit

[<Fact>]
let ``reports unterminated string`` () =
    match Reader.parse "(layout \"oops" with
    | Ok _ -> failwith "should not parse"
    | Error e -> e.Message |> should haveSubstring "unterminated"

[<Fact>]
let ``reports unexpected close paren`` () =
    match Reader.parse "(layout))" with
    | Ok _ -> failwith "should not parse"
    | Error e -> e.Message |> should haveSubstring ")"

// ─── Analyze pulls semantic fields ──────────────────────────────────────

[<Fact>]
let ``analyzes a poly element`` () =
    let src = """
(layout (version 1) (pdk sky130)
  (cell c
    (poly (layer sky130:met1)
          (points (0 0) (100 0) (100 50) (0 50))
          (net BL))))
"""
    let _, ast = analyzeOk src
    let cell = List.head ast.Cells
    cell.Name |> should equal "c"
    match cell.Elements with
    | [ PolyEl p ] ->
        p.Layer |> should equal (Named ("sky130", "met1"))
        p.Points |> List.length |> should equal 4
        p.Net |> should equal (Some "BL")
    | _ -> failwith "expected one poly element"

[<Fact>]
let ``unknown layer survives import`` () =
    let src = "(layout (version 1) (pdk sky130) (cell c (poly (layer unknown:94/20) (points (0 0) (1 0) (1 1)))))"
    let _, ast = analyzeOk src
    let p = match (List.head ast.Cells).Elements with [ PolyEl p ] -> p | _ -> failwith "poly"
    p.Layer |> should equal (Unknown (94, 20))

[<Fact>]
let ``bare layer name picks up file default pdk`` () =
    let src = "(layout (version 1) (pdk sky130) (cell c (poly (layer met1) (points (0 0) (1 0) (1 1)))))"
    let _, ast = analyzeOk src
    let p = match (List.head ast.Cells).Elements with [ PolyEl p ] -> p | _ -> failwith "poly"
    p.Layer |> should equal (Named ("sky130", "met1"))

[<Fact>]
let ``analyzes nets block with domain and voltage`` () =
    let src = """
(layout (version 1) (pdk sky130)
  (nets
    (net BL (domain signal))
    (net VPWR (domain power) (voltage 1.8))))
"""
    let _, ast = analyzeOk src
    ast.Nets |> List.length |> should equal 2
    let bl = ast.Nets |> List.find (fun n -> n.Name = "BL")
    bl.Domain |> should equal "signal"
    bl.Voltage |> should equal None
    let vpwr = ast.Nets |> List.find (fun n -> n.Name = "VPWR")
    vpwr.Domain |> should equal "power"
    vpwr.Voltage |> should equal (Some 1.8)

[<Fact>]
let ``analyzes a port with flags and shape`` () =
    let src = """
(layout (version 1) (pdk sky130)
  (cell c
    (port (name BL) (dir input) (layer sky130:met1)
          (flags signal scan)
          (shape (rect 0 0 10 50)))))
"""
    let _, ast = analyzeOk src
    let port = match (List.head ast.Cells).Elements with [ PortEl p ] -> p | _ -> failwith "port"
    port.Name |> should equal "BL"
    port.Direction |> should equal Input
    port.Flags |> should equal [ Signal; Scan ]
    port.Shape |> should equal (RectShape (0L, 0L, 10L, 50L))

[<Fact>]
let ``analyzes sref and aref defaults`` () =
    let src = """
(layout (version 1) (pdk sky130)
  (cell c
    (sref (cell bit) (origin 0 0))
    (aref (cell wl) (origin 0 200)
          (cols 64) (rows 1)
          (col_pitch 10 0) (row_pitch 0 5))))
"""
    let _, ast = analyzeOk src
    let elements = (List.head ast.Cells).Elements
    let sref = match elements with [ SRefEl s; _ ] -> s | _ -> failwith "sref"
    sref.Cell |> should equal "bit"
    sref.Mag |> should equal 1.0
    sref.Rot |> should equal 0.0
    sref.Reflect |> should equal false
    let aref = match elements with [ _; ARefEl a ] -> a | _ -> failwith "aref"
    aref.Cols |> should equal 64
    aref.Rows |> should equal 1
    aref.ColPitch |> should equal { X = 10L; Y = 0L }

// ─── Writer (synthesize from AST) ───────────────────────────────────────

[<Fact>]
let ``synthesize then parse yields the same AST`` () =
    let original : Document = {
        Version = 1
        Pdk = "sky130"
        Units = Defaults.units
        Imports = []
        Nets = [
            { Name = "BL"; Domain = "signal"; Voltage = None
              NetClass = None; Props = [] }
            { Name = "VPWR"; Domain = "power"; Voltage = Some 1.8
              NetClass = None; Props = [] }
        ]
        Cells = [
            { Name = "c"
              Elements = [
                  PolyEl {
                      Layer = Named ("sky130", "met1")
                      Points = [
                          { X = 0L; Y = 0L }
                          { X = 100L; Y = 0L }
                          { X = 100L; Y = 50L }
                          { X = 0L; Y = 50L }
                      ]
                      Net = Some "BL"
                      Props = []
                  }
                  PortEl {
                      Name = "BL"
                      Direction = Input
                      Layer = Named ("sky130", "met1")
                      Flags = [ Signal ]
                      Shape = RectShape (0L, 0L, 10L, 50L)
                      Net = None
                      Props = []
                  }
              ] }
        ]
        TopCell = Some "c"
    }
    let text = Writer.write original
    let _, ast = analyzeOk text
    ast.Pdk |> should equal original.Pdk
    ast.TopCell |> should equal original.TopCell
    ast.Nets |> List.length |> should equal 2
    ast.Cells |> List.length |> should equal 1
    let cell = List.head ast.Cells
    cell.Name |> should equal "c"
    cell.Elements |> List.length |> should equal 2

[<Fact>]
let ``synthesize emits floats with at least one decimal`` () =
    let doc : Document = {
        emptyDocument with
            Cells = [
                { Name = "c"
                  Elements = [
                      SRefEl {
                          Cell = "x"
                          Origin = { X = 0L; Y = 0L }
                          Rot = 90.0
                          Mag = 1.0
                          Reflect = false
                          Props = []
                      }
                  ] }
            ]
    }
    let text = Writer.write doc
    // The reader must classify the rotation as a float, not int, on
    // re-parse — guarantees the writer kept the decimal.
    let _, ast2 = analyzeOk text
    match (List.head ast2.Cells).Elements with
    | [ SRefEl s ] -> s.Rot |> should equal 90.0
    | _ -> failwith "expected sref"

[<Fact>]
let ``synthesize escapes special chars in strings`` () =
    let doc : Document = {
        emptyDocument with
            Cells = [
                { Name = "c"
                  Elements = [
                      LabelEl {
                          Layer = Named ("sky130", "met1")
                          Text = "with \"quotes\" and\nnewlines"
                          Origin = { X = 0L; Y = 0L }
                          Class = None
                          Props = []
                      }
                  ] }
            ]
    }
    let text = Writer.write doc
    let _, ast2 = analyzeOk text
    let lbl = match (List.head ast2.Cells).Elements with [ LabelEl l ] -> l | _ -> failwith "label"
    lbl.Text |> should equal "with \"quotes\" and\nnewlines"

// ─── Import resolution + cycle detection ────────────────────────────────

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(),
                           "rkt-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try f dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``load resolves a simple transitive import`` () =
    withTempDir (fun dir ->
        let a = Path.Combine(dir, "a.rkt")
        let b = Path.Combine(dir, "b.rkt")
        File.WriteAllText(a, "(layout (version 1) (pdk sky130)\n  (import \"b.rkt\")\n  (cell topA))\n")
        File.WriteAllText(b, "(layout (version 1) (pdk sky130)\n  (cell deepB))\n")
        match Reader.loadSingle a with
        | Error e -> failwithf "load failed: %A" e
        | Ok lib ->
            lib.Documents |> Map.count |> should equal 2
            lib.CellIndex |> Map.containsKey "topA" |> should equal true
            lib.CellIndex |> Map.containsKey "deepB" |> should equal true)

[<Fact>]
let ``load detects import cycles`` () =
    withTempDir (fun dir ->
        let a = Path.Combine(dir, "a.rkt")
        let b = Path.Combine(dir, "b.rkt")
        File.WriteAllText(a, "(layout (version 1) (pdk sky130) (import \"b.rkt\") (cell a1))")
        File.WriteAllText(b, "(layout (version 1) (pdk sky130) (import \"a.rkt\") (cell b1))")
        match Reader.loadSingle a with
        | Ok _ -> failwith "should have detected a cycle"
        | Error e -> e.Message |> should haveSubstring "cycle")

[<Fact>]
let ``load reports missing import file`` () =
    withTempDir (fun dir ->
        let a = Path.Combine(dir, "a.rkt")
        File.WriteAllText(a, "(layout (version 1) (pdk sky130) (import \"missing.rkt\") (cell a1))")
        match Reader.loadSingle a with
        | Ok _ -> failwith "should have failed on missing import"
        | Error _ -> ())

[<Fact>]
let ``load is idempotent when two roots share an import`` () =
    withTempDir (fun dir ->
        let a = Path.Combine(dir, "a.rkt")
        let b = Path.Combine(dir, "b.rkt")
        let common = Path.Combine(dir, "common.rkt")
        File.WriteAllText(common, "(layout (version 1) (pdk sky130) (cell shared))")
        File.WriteAllText(a, "(layout (version 1) (pdk sky130) (import \"common.rkt\") (cell aOnly))")
        File.WriteAllText(b, "(layout (version 1) (pdk sky130) (import \"common.rkt\") (cell bOnly))")
        match Reader.load [ a; b ] with
        | Error e -> failwithf "load failed: %A" e
        | Ok lib ->
            // Three files total: a, b, common. The shared one should appear once.
            lib.Documents |> Map.count |> should equal 3
            lib.CellIndex |> Map.count |> should equal 3)
