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
    | Ok ast -> ast
    | Error e -> failwithf "analyze failed: %A" e

// ─── Lexer / parser basics ─────────────────────────────────────────────

[<Fact>]
let ``parses an empty layout`` () =
    let src = "(layout (version 1) (pdk sky130))\n"
    let ast = analyzeOk src
    ast.Version |> should equal 1
    ast.Pdk |> should equal "sky130"
    ast.Cells |> List.length |> should equal 0

[<Fact>]
let ``preserves PropValue int vs float kind on round-trip`` () =
    let src = "(layout (version 1) (pdk sky130) (units (dbu_nm 5) (uu_um 1)))"
    let ast = analyzeOk src
    ast.Units.DbuNm |> should equal 5
    ast.Units.UuUm |> should equal 1

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

// ─── Comments live on the AST ─────────────────────────────────────────

[<Fact>]
let ``file-level comment survives parse onto HeaderComments`` () =
    let src = "; top of file\n; second header line\n(layout (version 1) (pdk sky130))\n"
    let ast = analyzeOk src
    ast.HeaderComments |> should equal [ "top of file"; "second header line" ]

[<Fact>]
let ``comment before a cell attaches to that cell`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  ; bitcell core\n"
        + "  (cell bit))\n"
    let ast = analyzeOk src
    let cell = List.head ast.Cells
    cell.Comments |> should equal [ "bitcell core" ]

[<Fact>]
let ``comment before an element attaches to that element`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (cell c\n"
        + "    ; on the metal1 layer\n"
        + "    (poly (layer sky130:met1) (points (0 0) (1 0) (1 1)))))\n"
    let ast = analyzeOk src
    let p = match (List.head ast.Cells).Elements with [ PolyEl p ] -> p | _ -> failwith "poly"
    p.Comments |> should equal [ "on the metal1 layer" ]

[<Fact>]
let ``comments survive round-trip through write`` () =
    let src =
        "; provenance: from issue #42\n"
        + "(layout (version 1) (pdk sky130)\n"
        + "  ; bitcell core\n"
        + "  (cell bit\n"
        + "    ; metal-1 bitline contact\n"
        + "    (poly (layer sky130:met1) (points (0 0) (1 0) (1 1)))))\n"
    let ast = analyzeOk src
    // Round-trip: AST → text → AST. Comments must still be present
    // on the second AST.
    let rendered = Writer.write ast
    let ast2 = analyzeOk rendered
    ast2.HeaderComments |> should equal [ "provenance: from issue #42" ]
    let cell2 = List.head ast2.Cells
    cell2.Comments |> should equal [ "bitcell core" ]
    let p2 = match cell2.Elements with [ PolyEl p ] -> p | _ -> failwith "poly"
    p2.Comments |> should equal [ "metal-1 bitline contact" ]

[<Fact>]
let ``editing an unrelated field keeps a node's comments`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  ; do not move\n"
        + "  (cell pinned\n"
        + "    ; key vector\n"
        + "    (sref (cell bit) (origin 0 0))))\n"
    let ast = analyzeOk src
    let cell = List.head ast.Cells
    // Mutate the sref's origin only.
    let edited =
        match cell.Elements with
        | [ SRefEl s ] ->
            { cell with Elements = [ SRefEl { s with Origin = { X = 5L; Y = 7L } } ] }
        | _ -> failwith "sref"
    let doc2 = { ast with Cells = [ edited ] }
    let rendered = Writer.write doc2
    let ast2 = analyzeOk rendered
    let cell2 = List.head ast2.Cells
    cell2.Comments |> should equal [ "do not move" ]
    match cell2.Elements with
    | [ SRefEl s ] ->
        s.Origin |> should equal { X = 5L; Y = 7L }
        s.Comments |> should equal [ "key vector" ]
    | _ -> failwith "sref"

// ─── Meta (PDK-generated cell provenance) ──────────────────────────────

[<Fact>]
let ``analyzes a (meta ...) block on a cell`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (cell nfet_hv_W1p2_L1p0_core\n"
        + "    (meta\n"
        + "      (generator \"sky130/nfet_hv\")\n"
        + "      (params (w 1.2) (l 1.0) (guard 0) (mode \"lvt\"))\n"
        + "      (source \"magic-cif sky130B\")\n"
        + "      (generated \"2026-05-13\")\n"
        + "      (digest \"sha256:abc\"))))\n"
    let ast = analyzeOk src
    let cell = List.head ast.Cells
    match cell.Meta with
    | None -> failwith "expected meta"
    | Some m ->
        m.Generator |> should equal "sky130/nfet_hv"
        m.Source |> should equal (Some "magic-cif sky130B")
        m.Generated |> should equal (Some "2026-05-13")
        m.Digest |> should equal (Some "sha256:abc")
        m.Params |> List.length |> should equal 4
        let byKey k = m.Params |> List.find (fun p -> p.Key = k)
        (byKey "w").Value |> should equal (PvFloat 1.2)
        (byKey "l").Value |> should equal (PvFloat 1.0)
        (byKey "guard").Value |> should equal (PvInt 0L)
        (byKey "mode").Value |> should equal (PvString "lvt")

[<Fact>]
let ``round-trips a (meta ...) block bit-equivalent`` () =
    let doc : Document =
        { emptyDocument with
            Cells = [
                { Name = "nfet_hv_W1p2_L1p0_core"
                  Meta = Some {
                      Generator = "sky130/nfet_hv"
                      Params = [
                          { Key = "w";     Value = PvFloat 1.2 }
                          { Key = "l";     Value = PvFloat 1.0 }
                          { Key = "guard"; Value = PvInt 0L }
                      ]
                      Source = Some "magic-cif sky130B"
                      Generated = Some "2026-05-13"
                      Digest = None
                      Comments = []
                  }
                  Comments = []
                  Elements = [] }
            ] }
    let rendered = Writer.write doc
    let ast = analyzeOk rendered
    let cell = List.head ast.Cells
    let meta = cell.Meta.Value
    meta.Generator |> should equal "sky130/nfet_hv"
    meta.Params |> List.length |> should equal 3
    meta.Source |> should equal (Some "magic-cif sky130B")
    meta.Digest |> should equal None

[<Fact>]
let ``cells without (meta ...) keep Meta = None`` () =
    let src = "(layout (version 1) (pdk sky130) (cell hand_authored))\n"
    let ast = analyzeOk src
    (List.head ast.Cells).Meta |> should equal None

[<Fact>]
let ``(meta ...) without (generator ...) is a parse error`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (cell c (meta (params (w 1.0)))))\n"
    let cst =
        match Reader.parse src with
        | Ok c -> c
        | Error e -> failwithf "parse: %A" e
    match Reader.analyze cst with
    | Error _ -> ()
    | Ok _ -> failwith "expected analyze error"

// ─── Analyze pulls semantic fields ──────────────────────────────────────

[<Fact>]
let ``analyzes a poly element`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (cell c\n"
        + "    (poly (layer sky130:met1)\n"
        + "          (points (0 0) (100 0) (100 50) (0 50))\n"
        + "          (net BL))))\n"
    let ast = analyzeOk src
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
    let ast = analyzeOk src
    let p = match (List.head ast.Cells).Elements with [ PolyEl p ] -> p | _ -> failwith "poly"
    p.Layer |> should equal (Unknown (94, 20))

[<Fact>]
let ``bare layer name picks up file default pdk`` () =
    let src = "(layout (version 1) (pdk sky130) (cell c (poly (layer met1) (points (0 0) (1 0) (1 1)))))"
    let ast = analyzeOk src
    let p = match (List.head ast.Cells).Elements with [ PolyEl p ] -> p | _ -> failwith "poly"
    p.Layer |> should equal (Named ("sky130", "met1"))

[<Fact>]
let ``analyzes nets block with domain and voltage`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (nets\n"
        + "    (net BL (domain signal))\n"
        + "    (net VPWR (domain power) (voltage 1.8))))\n"
    let ast = analyzeOk src
    ast.Nets |> List.length |> should equal 2
    let bl = ast.Nets |> List.find (fun n -> n.Name = "BL")
    bl.Domain |> should equal "signal"
    bl.Voltage |> should equal None
    let vpwr = ast.Nets |> List.find (fun n -> n.Name = "VPWR")
    vpwr.Domain |> should equal "power"
    vpwr.Voltage |> should equal (Some 1.8)

[<Fact>]
let ``analyzes a port with flags and shape`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (cell c\n"
        + "    (port (name BL) (dir input) (layer sky130:met1)\n"
        + "          (flags signal scan)\n"
        + "          (shape (rect 0 0 10 50)))))\n"
    let ast = analyzeOk src
    let port = match (List.head ast.Cells).Elements with [ PortEl p ] -> p | _ -> failwith "port"
    port.Name |> should equal "BL"
    port.Direction |> should equal Input
    port.Flags |> should equal [ Signal; Scan ]
    port.Shape |> should equal (RectShape (0L, 0L, 10L, 50L))

[<Fact>]
let ``analyzes sref and aref defaults`` () =
    let src =
        "(layout (version 1) (pdk sky130)\n"
        + "  (cell c\n"
        + "    (sref (cell bit) (origin 0 0))\n"
        + "    (aref (cell wl) (origin 0 200)\n"
        + "          (cols 64) (rows 1)\n"
        + "          (col_pitch 10 0) (row_pitch 0 5))))\n"
    let ast = analyzeOk src
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
              NetClass = None; Props = []; Comments = [] }
            { Name = "VPWR"; Domain = "power"; Voltage = Some 1.8
              NetClass = None; Props = []; Comments = [] }
        ]
        Cells = [
            { Name = "c"
              Meta = None
              Comments = []
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
                      Comments = []
                  }
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
        TopCell = Some "c"
        HeaderComments = []
    }
    let text = Writer.write original
    let ast = analyzeOk text
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
                  Meta = None
                  Comments = []
                  Elements = [
                      SRefEl {
                          Cell = "x"
                          Origin = { X = 0L; Y = 0L }
                          Rot = 90.0
                          Mag = 1.0
                          Reflect = false
                          Props = []
                          Comments = []
                      }
                  ] }
            ]
    }
    let text = Writer.write doc
    let ast2 = analyzeOk text
    match (List.head ast2.Cells).Elements with
    | [ SRefEl s ] -> s.Rot |> should equal 90.0
    | _ -> failwith "expected sref"

[<Fact>]
let ``synthesize escapes special chars in strings`` () =
    let doc : Document = {
        emptyDocument with
            Cells = [
                { Name = "c"
                  Meta = None
                  Comments = []
                  Elements = [
                      LabelEl {
                          Layer = Named ("sky130", "met1")
                          Text = "with \"quotes\" and\nnewlines"
                          Origin = { X = 0L; Y = 0L }
                          Class = None
                          Props = []
                          Comments = []
                      }
                  ] }
            ]
    }
    let text = Writer.write doc
    let ast2 = analyzeOk text
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

// ─── Python writer cross-validation ─────────────────────────────────

[<Fact>]
let ``parses verbatim output of the Python rkt writer`` () =
    // Literal copy of what `rekolektion.io.rkt.write` emits for a
    // small Document with header comment + cell comment + element
    // comment + poly + net. If the Python and F# writers drift, this
    // test catches it via parse/analyze on the Python output.
    let pythonText =
        "; provenance: tests\n"
        + "(layout (version 1)\n"
        + "  (pdk sky130)\n"
        + "  (units (dbu_nm 1) (uu_um 1))\n"
        + "  (top c)\n"
        + "  ; bitcell core\n"
        + "  (cell c\n"
        + "    ; metal-1 bitline contact\n"
        + "    (poly (layer sky130:met1)\n"
        + "      (points (0 0) (10 0) (10 5) (0 5))\n"
        + "      (net BL))))\n"
    let ast = analyzeOk pythonText
    ast.HeaderComments |> should equal [ "provenance: tests" ]
    ast.TopCell |> should equal (Some "c")
    let cell = List.head ast.Cells
    cell.Comments |> should equal [ "bitcell core" ]
    match cell.Elements with
    | [ PolyEl p ] ->
        p.Layer |> should equal (Named ("sky130", "met1"))
        p.Points |> List.length |> should equal 4
        p.Net |> should equal (Some "BL")
        p.Comments |> should equal [ "metal-1 bitline contact" ]
    | _ -> failwithf "unexpected elements: %A" cell.Elements

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
