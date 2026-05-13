module Rekolektion.Viz.Core.Rkt.Writer

open System.Text
open System.Globalization
open Rekolektion.Viz.Core.Rkt.Cst
open Rekolektion.Viz.Core.Rkt.Types

// ─── CST → string (byte-exact for untouched input) ────────────────────────

let rec private emitSexp (sb: StringBuilder) (s: Sexp) : unit =
    match s with
    | SAtom a ->
        sb.Append a.Leading |> ignore
        sb.Append a.Text |> ignore
    | SList l ->
        sb.Append l.Leading |> ignore
        sb.Append '(' |> ignore
        for c in l.Children do emitSexp sb c
        sb.Append l.Trailing |> ignore
        sb.Append ')' |> ignore

let renderCst (doc: Cst.Document) : string =
    let sb = StringBuilder()
    for r in doc.Roots do emitSexp sb r
    sb.Append doc.Trailing |> ignore
    sb.ToString()

// ─── AST → CST (canonical synthesizer) ────────────────────────────────────

let private dummyPos : SourcePos = { Line = 0; Col = 0 }

let private atom (leading: string) (kind: AtomKind) (text: string) : Sexp =
    SAtom { Leading = leading; Pos = dummyPos; Kind = kind; Text = text }

let private mkList (leading: string) (children: Sexp list) (trailing: string) : Sexp =
    SList {
        Leading = leading
        OpenPos = dummyPos
        Children = children
        Trailing = trailing
        ClosePos = dummyPos
    }

let private sym (leading: string) (text: string) : Sexp =
    atom leading Symbol text

let private intAtom (leading: string) (v: int64) : Sexp =
    atom leading IntLit (v.ToString CultureInfo.InvariantCulture)

let private floatAtom (leading: string) (v: float) : Sexp =
    // Always emit at least one decimal so the reader classifies as
    // FloatLit. "R" gives round-trip precision but skips the dot for
    // whole numbers — append ".0" in that case.
    let raw = v.ToString("R", CultureInfo.InvariantCulture)
    let text =
        if raw.Contains '.' || raw.Contains 'e' || raw.Contains 'E'
        then raw
        else raw + ".0"
    atom leading FloatLit text

/// Emits a JSON-style escaped string literal. Mirrors the decoder in
/// `Reader.stringText` for the four escapes that survive round-trip.
let private stringAtom (leading: string) (text: string) : Sexp =
    let sb = StringBuilder()
    sb.Append '"' |> ignore
    for c in text do
        match c with
        | '\\' -> sb.Append "\\\\" |> ignore
        | '"'  -> sb.Append "\\\"" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | other -> sb.Append other |> ignore
    sb.Append '"' |> ignore
    atom leading StringLit (sb.ToString())

let private layerAtom (leading: string) (layer: Layer) : Sexp =
    let text =
        match layer with
        | Named (pdk, name) -> sprintf "%s:%s" pdk name
        | Unknown (n, d) -> sprintf "unknown:%d/%d" n d
    sym leading text

let private propValueAtom (leading: string) (v: PropValue) : Sexp =
    match v with
    | PvAtom t -> sym leading t
    | PvString t -> stringAtom leading t
    | PvInt v -> intAtom leading v
    | PvFloat v -> floatAtom leading v

let private propForm (leading: string) (p: Property) : Sexp =
    mkList leading
        [ sym "" p.Key; propValueAtom " " p.Value ]
        ""

let private propsForm (leading: string) (props: Property list) : Sexp option =
    if List.isEmpty props then None
    else
        let kids = sym "" "props" :: (props |> List.map (propForm " "))
        Some (mkList leading kids "")

let private pointForm (leading: string) (p: Point) : Sexp =
    mkList leading [ intAtom "" p.X; intAtom " " p.Y ] ""

let private pointsForm (leading: string) (pts: Point list) : Sexp =
    let kids = sym "" "points" :: (pts |> List.map (pointForm " "))
    mkList leading kids ""

let private netForm (leading: string) (netName: string) : Sexp =
    mkList leading [ sym "" "net"; sym " " netName ] ""

let private dirSymbol (d: PortDirection) : string =
    match d with
    | Input -> "input"
    | Output -> "output"
    | Inout -> "inout"
    | Unspecified -> "unspecified"

let private flagSymbol (f: PortFlag) : string =
    match f with
    | Signal -> "signal"
    | Power -> "power"
    | Ground -> "ground"
    | Clock -> "clock"
    | Analog -> "analog"
    | Scan -> "scan"

/// Indent string for canonical synthesis. Two-space indentation
/// matches the design doc's schema-sketch style.
let private indent (n: int) : string =
    "\n" + String.replicate n "  "

let private synthesizePoly (i: int) (poly: Poly) : Sexp =
    let lead = indent i
    let inner = indent (i + 1)
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "poly")
    kids.Add (mkList " " [ sym "" "layer"; layerAtom " " poly.Layer ] "")
    kids.Add (pointsForm inner poly.Points)
    match poly.Net with
    | Some n -> kids.Add (netForm inner n)
    | None -> ()
    match propsForm inner poly.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizePath (i: int) (p: Path) : Sexp =
    let lead = indent i
    let inner = indent (i + 1)
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "path")
    kids.Add (mkList " " [ sym "" "layer"; layerAtom " " p.Layer ] "")
    kids.Add (mkList " " [ sym "" "width"; intAtom " " p.Width ] "")
    kids.Add (pointsForm inner p.Points)
    match p.Cap with
    | Some c -> kids.Add (mkList inner [ sym "" "cap"; sym " " c ] "")
    | None -> ()
    match p.Net with
    | Some n -> kids.Add (netForm inner n)
    | None -> ()
    match propsForm inner p.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeRect (i: int) (r: Rect) : Sexp =
    let lead = indent i
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "rect")
    kids.Add (mkList " " [ sym "" "layer"; layerAtom " " r.Layer ] "")
    kids.Add (intAtom " " r.X1)
    kids.Add (intAtom " " r.Y1)
    kids.Add (intAtom " " r.X2)
    kids.Add (intAtom " " r.Y2)
    let inner = indent (i + 1)
    match r.Net with
    | Some n -> kids.Add (netForm inner n)
    | None -> ()
    match propsForm inner r.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizePortShape (leading: string) (shape: PortShape) : Sexp =
    let inner =
        match shape with
        | RectShape (x1, y1, x2, y2) ->
            mkList " "
                [ sym "" "rect"
                  intAtom " " x1; intAtom " " y1
                  intAtom " " x2; intAtom " " y2 ]
                ""
        | PolyShape pts ->
            let kids =
                sym "" "poly"
                :: (pts
                    |> List.map (fun p ->
                        mkList " " [ intAtom "" p.X; intAtom " " p.Y ] ""))
            mkList " " kids ""
    mkList leading [ sym "" "shape"; inner ] ""

let private synthesizePort (i: int) (p: Port) : Sexp =
    let lead = indent i
    let inner = indent (i + 1)
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "port")
    kids.Add (mkList " " [ sym "" "name"; sym " " p.Name ] "")
    kids.Add (mkList " " [ sym "" "dir"; sym " " (dirSymbol p.Direction) ] "")
    kids.Add (mkList inner [ sym "" "layer"; layerAtom " " p.Layer ] "")
    if not (List.isEmpty p.Flags) then
        let flagKids =
            sym "" "flags"
            :: (p.Flags |> List.map (fun f -> sym " " (flagSymbol f)))
        kids.Add (mkList inner flagKids "")
    kids.Add (synthesizePortShape inner p.Shape)
    match p.Net with
    | Some n -> kids.Add (netForm inner n)
    | None -> ()
    match propsForm inner p.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeLabel (i: int) (l: Label) : Sexp =
    let lead = indent i
    let inner = indent (i + 1)
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "label")
    kids.Add (mkList " " [ sym "" "layer"; layerAtom " " l.Layer ] "")
    kids.Add (mkList " " [ sym "" "text"; stringAtom " " l.Text ] "")
    kids.Add (mkList " "
        [ sym "" "origin"; intAtom " " l.Origin.X; intAtom " " l.Origin.Y ] "")
    match l.Class with
    | Some c -> kids.Add (mkList inner [ sym "" "class"; sym " " c ] "")
    | None -> ()
    match propsForm inner l.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeSRef (i: int) (r: SRef) : Sexp =
    let lead = indent i
    let inner = indent (i + 1)
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "sref")
    kids.Add (mkList " " [ sym "" "cell"; sym " " r.Cell ] "")
    kids.Add (mkList " "
        [ sym "" "origin"; intAtom " " r.Origin.X; intAtom " " r.Origin.Y ] "")
    if r.Rot <> 0.0 then
        kids.Add (mkList " " [ sym "" "rot"; floatAtom " " r.Rot ] "")
    if r.Mag <> 1.0 then
        kids.Add (mkList " " [ sym "" "mag"; floatAtom " " r.Mag ] "")
    if r.Reflect then
        kids.Add (mkList " " [ sym "" "reflect"; sym " " "true" ] "")
    match propsForm inner r.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeARef (i: int) (r: ARef) : Sexp =
    let lead = indent i
    let inner = indent (i + 1)
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "aref")
    kids.Add (mkList " " [ sym "" "cell"; sym " " r.Cell ] "")
    kids.Add (mkList " "
        [ sym "" "origin"; intAtom " " r.Origin.X; intAtom " " r.Origin.Y ] "")
    kids.Add (mkList inner [ sym "" "cols"; intAtom " " (int64 r.Cols) ] "")
    kids.Add (mkList " " [ sym "" "rows"; intAtom " " (int64 r.Rows) ] "")
    kids.Add (mkList inner
        [ sym "" "col_pitch"
          intAtom " " r.ColPitch.X
          intAtom " " r.ColPitch.Y ] "")
    kids.Add (mkList " "
        [ sym "" "row_pitch"
          intAtom " " r.RowPitch.X
          intAtom " " r.RowPitch.Y ] "")
    if r.Rot <> 0.0 then
        kids.Add (mkList inner [ sym "" "rot"; floatAtom " " r.Rot ] "")
    if r.Mag <> 1.0 then
        kids.Add (mkList " " [ sym "" "mag"; floatAtom " " r.Mag ] "")
    if r.Reflect then
        kids.Add (mkList " " [ sym "" "reflect"; sym " " "true" ] "")
    match propsForm inner r.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeElement (i: int) (e: Element) : Sexp =
    match e with
    | PolyEl p -> synthesizePoly i p
    | PathEl p -> synthesizePath i p
    | RectEl r -> synthesizeRect i r
    | PortEl p -> synthesizePort i p
    | LabelEl l -> synthesizeLabel i l
    | SRefEl r -> synthesizeSRef i r
    | ARefEl r -> synthesizeARef i r
    | PropsEl props ->
        let lead = indent i
        let kids = sym "" "props" :: (props |> List.map (propForm " "))
        mkList lead kids ""

let private synthesizeCell (i: int) (c: Cell) : Sexp =
    let lead = indent i
    let kids =
        sym "" "cell"
        :: sym " " c.Name
        :: (c.Elements |> List.map (synthesizeElement (i + 1)))
    mkList lead kids ""

let private synthesizeNet (leading: string) (n: Net) : Sexp =
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "net")
    kids.Add (sym " " n.Name)
    kids.Add (mkList " " [ sym "" "domain"; sym " " n.Domain ] "")
    match n.Voltage with
    | Some v -> kids.Add (mkList " " [ sym "" "voltage"; floatAtom " " v ] "")
    | None -> ()
    match n.NetClass with
    | Some c -> kids.Add (mkList " " [ sym "" "class"; sym " " c ] "")
    | None -> ()
    for p in n.Props do
        kids.Add (propForm " " p)
    mkList leading (List.ofSeq kids) ""

let private synthesizeNetsBlock (i: int) (nets: Net list) : Sexp option =
    if List.isEmpty nets then None
    else
        let lead = indent i
        let inner = indent (i + 1)
        let kids = sym "" "nets" :: (nets |> List.map (synthesizeNet inner))
        Some (mkList lead kids "")

let private synthesizeImport (i: int) (imp: Import) : Sexp =
    mkList (indent i) [ sym "" "import"; stringAtom " " imp.Path ] ""

let synthesize (doc: Document) : Cst.Document =
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "layout")
    kids.Add (mkList " "
        [ sym "" "version"; intAtom " " (int64 doc.Version) ] "")
    kids.Add (mkList (indent 1) [ sym "" "pdk"; sym " " doc.Pdk ] "")
    kids.Add (mkList (indent 1)
        [ sym "" "units"
          mkList " " [ sym "" "dbu_nm"; intAtom " " (int64 doc.Units.DbuNm) ] ""
          mkList " " [ sym "" "uu_um"; intAtom " " (int64 doc.Units.UuUm) ] "" ]
        "")
    for imp in doc.Imports do
        kids.Add (synthesizeImport 1 imp)
    match doc.TopCell with
    | Some t -> kids.Add (mkList (indent 1) [ sym "" "top"; sym " " t ] "")
    | None -> ()
    match synthesizeNetsBlock 1 doc.Nets with
    | Some n -> kids.Add n
    | None -> ()
    for c in doc.Cells do
        kids.Add (synthesizeCell 1 c)
    let layout = mkList "" (List.ofSeq kids) ""
    { Roots = [ layout ]
      Trailing = "\n"
      SourcePath = None }

let write (doc: Document) : string =
    renderCst (synthesize doc)
