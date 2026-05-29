module Rekolektion.Viz.Core.Rkt.Writer

open System.Text
open System.Globalization
open Rekolektion.Viz.Core.Rkt.Cst
open Rekolektion.Viz.Core.Rkt.Types

// `Cst` types still appear here as an internal scaffold for emitting
// the AST in a single linear pass — they are not part of the public
// `Rkt` surface and consumers should not depend on this dependency.

// ─── Helpers ───────────────────────────────────────────────────────────

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
    let raw = v.ToString("R", CultureInfo.InvariantCulture)
    let text =
        if raw.Contains '.' || raw.Contains 'e' || raw.Contains 'E'
        then raw
        else raw + ".0"
    atom leading FloatLit text

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
    | PvTuple _ ->
        // Tuples expand inline at the property-form level via
        // `propertyChildren`; reaching this branch means someone
        // called `propValueAtom` directly on a tuple, which is a
        // programmer error.
        failwith "propValueAtom: PvTuple expands at the form level (see propertyChildren)"

/// Build the child sequence inside a `(key …)` property form.
/// Single-value variants emit `[key; value]`; `PvTuple` flattens
/// to `[key; v1; v2; …]`.
let private propertyChildren (p: Property) : Sexp list =
    match p.Value with
    | PvTuple values ->
        sym "" p.Key :: (values |> List.map (propValueAtom " "))
    | _ ->
        [ sym "" p.Key; propValueAtom " " p.Value ]

let private propForm (leading: string) (p: Property) : Sexp =
    mkList leading (propertyChildren p) ""

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

/// 2-space indentation. Layout per the design doc's schema sketch.
let private indentStr (n: int) : string =
    String.replicate n "  "

let private indent (n: int) : string =
    "\n" + indentStr n

/// Render a comment block as the prefix portion of a form's leading
/// trivia. Returns an empty string if the comment list is empty so
/// `leading = commentBlock i cs + indent i` reduces to plain indent
/// when no comments exist.
let private commentBlock (i: int) (comments: string list) : string =
    if List.isEmpty comments then ""
    else
        let pad = indentStr i
        let sb = StringBuilder()
        for c in comments do
            sb.Append '\n' |> ignore
            sb.Append pad |> ignore
            sb.Append "; " |> ignore
            sb.Append c |> ignore
        sb.ToString()

let private leading (i: int) (comments: string list) : string =
    commentBlock i comments + indent i

/// Compute the leading-trivia string for a sub-form inside an
/// element. When `SubFormComments[key]` is non-empty, the sub-form is
/// forced onto its own line (`\n` + indent at depth `i+1`) with the
/// comment block prepended; otherwise the caller's `defaultLead`
/// stands. v1 doesn't try to keep comments on same-line sub-forms —
/// emitting a sub-form comment promotes the sub-form to its own line.
let private subFormLead
    (i: int)
    (key: string)
    (sfc: Map<string, string list>)
    (defaultLead: string)
    : string =
    match Map.tryFind key sfc with
    | Some comments when not (List.isEmpty comments) ->
        commentBlock (i + 1) comments + indent (i + 1)
    | _ -> defaultLead

// ─── Element synthesizers ─────────────────────────────────────────────

let private synthesizePoly (i: int) (poly: Poly) : Sexp =
    let lead = leading i poly.Comments
    let inner = indent (i + 1)
    let sfc = poly.SubFormComments
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "poly")
    kids.Add (mkList (subFormLead i "layer" sfc " ")
                     [ sym "" "layer"; layerAtom " " poly.Layer ] "")
    kids.Add (pointsForm (subFormLead i "points" sfc inner) poly.Points)
    match poly.Net with
    | Some n -> kids.Add (netForm (subFormLead i "net" sfc inner) n)
    | None -> ()
    match propsForm (subFormLead i "props" sfc inner) poly.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizePath (i: int) (p: Path) : Sexp =
    let lead = leading i p.Comments
    let inner = indent (i + 1)
    let sfc = p.SubFormComments
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "path")
    kids.Add (mkList (subFormLead i "layer" sfc " ")
                     [ sym "" "layer"; layerAtom " " p.Layer ] "")
    kids.Add (mkList (subFormLead i "width" sfc " ")
                     [ sym "" "width"; intAtom " " p.Width ] "")
    kids.Add (pointsForm (subFormLead i "points" sfc inner) p.Points)
    match p.Cap with
    | Some c ->
        kids.Add (mkList (subFormLead i "cap" sfc inner)
                         [ sym "" "cap"; sym " " c ] "")
    | None -> ()
    match p.Net with
    | Some n -> kids.Add (netForm (subFormLead i "net" sfc inner) n)
    | None -> ()
    match propsForm (subFormLead i "props" sfc inner) p.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeRect (i: int) (r: Rectangle) : Sexp =
    let lead = leading i r.Comments
    let inner = indent (i + 1)
    let sfc = r.SubFormComments
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "rect")
    kids.Add (mkList (subFormLead i "layer" sfc " ")
                     [ sym "" "layer"; layerAtom " " r.Layer ] "")
    kids.Add (intAtom " " r.X1)
    kids.Add (intAtom " " r.Y1)
    kids.Add (intAtom " " r.X2)
    kids.Add (intAtom " " r.Y2)
    match r.Net with
    | Some n -> kids.Add (netForm (subFormLead i "net" sfc inner) n)
    | None -> ()
    match propsForm (subFormLead i "props" sfc inner) r.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizePortShape (lead: string) (shape: PortShape) : Sexp =
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
    mkList lead [ sym "" "shape"; inner ] ""

let private synthesizePort (i: int) (p: Port) : Sexp =
    let lead = leading i p.Comments
    let inner = indent (i + 1)
    let sfc = p.SubFormComments
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "port")
    kids.Add (mkList (subFormLead i "name" sfc " ")
                     [ sym "" "name"; sym " " p.Name ] "")
    kids.Add (mkList (subFormLead i "dir" sfc " ")
                     [ sym "" "dir"; sym " " (dirSymbol p.Direction) ] "")
    kids.Add (mkList (subFormLead i "layer" sfc inner)
                     [ sym "" "layer"; layerAtom " " p.Layer ] "")
    if not (List.isEmpty p.Flags) then
        let flagKids =
            sym "" "flags"
            :: (p.Flags |> List.map (fun f -> sym " " (flagSymbol f)))
        kids.Add (mkList (subFormLead i "flags" sfc inner) flagKids "")
    kids.Add (synthesizePortShape (subFormLead i "shape" sfc inner) p.Shape)
    match p.Net with
    | Some n -> kids.Add (netForm (subFormLead i "net" sfc inner) n)
    | None -> ()
    match propsForm (subFormLead i "props" sfc inner) p.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeLabel (i: int) (l: Label) : Sexp =
    let lead = leading i l.Comments
    let inner = indent (i + 1)
    let sfc = l.SubFormComments
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "label")
    kids.Add (mkList (subFormLead i "layer" sfc " ")
                     [ sym "" "layer"; layerAtom " " l.Layer ] "")
    kids.Add (mkList (subFormLead i "text" sfc " ")
                     [ sym "" "text"; stringAtom " " l.Text ] "")
    kids.Add (mkList (subFormLead i "origin" sfc " ")
        [ sym "" "origin"; intAtom " " l.Origin.X; intAtom " " l.Origin.Y ] "")
    match l.Class with
    | Some c ->
        kids.Add (mkList (subFormLead i "class" sfc inner)
                         [ sym "" "class"; sym " " c ] "")
    | None -> ()
    if l.IsInternal then
        kids.Add (mkList (subFormLead i "internal" sfc inner)
                         [ sym "" "internal"; sym " " "#t" ] "")
    // Emit the `(kind …)` annotation only when the role isn't the
    // default. `NetName` is implicit; absent annotation == net name.
    match l.Kind with
    | NetName -> ()
    | DeviceTerminal ->
        kids.Add (mkList (subFormLead i "kind" sfc inner)
                         [ sym "" "kind"; sym " " "device-terminal" ] "")
    | PortName ->
        kids.Add (mkList (subFormLead i "kind" sfc inner)
                         [ sym "" "kind"; sym " " "port-name" ] "")
    match propsForm (subFormLead i "props" sfc inner) l.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeSRef (i: int) (r: SRef) : Sexp =
    let lead = leading i r.Comments
    let inner = indent (i + 1)
    let sfc = r.SubFormComments
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "sref")
    kids.Add (mkList (subFormLead i "cell" sfc " ")
                     [ sym "" "cell"; sym " " r.Cell ] "")
    kids.Add (mkList (subFormLead i "origin" sfc " ")
        [ sym "" "origin"; intAtom " " r.Origin.X; intAtom " " r.Origin.Y ] "")
    if r.Rot <> 0.0 then
        // `.rkt` schema stores rotation in degrees (matches GDS ANGLE
        // and Python's rkt.SRef.rot — degrees throughout).
        kids.Add (mkList (subFormLead i "rot" sfc " ")
                         [ sym "" "rot"; floatAtom " " r.Rot ] "")
    if r.Mag <> 1.0 then
        kids.Add (mkList (subFormLead i "mag" sfc " ")
                         [ sym "" "mag"; floatAtom " " r.Mag ] "")
    if r.Reflect then
        kids.Add (mkList (subFormLead i "reflect" sfc " ")
                         [ sym "" "reflect"; sym " " "true" ] "")
    match propsForm (subFormLead i "props" sfc inner) r.Props with
    | Some f -> kids.Add f
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeARef (i: int) (r: ARef) : Sexp =
    let lead = leading i r.Comments
    let inner = indent (i + 1)
    let sfc = r.SubFormComments
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "aref")
    kids.Add (mkList (subFormLead i "cell" sfc " ")
                     [ sym "" "cell"; sym " " r.Cell ] "")
    kids.Add (mkList (subFormLead i "origin" sfc " ")
        [ sym "" "origin"; intAtom " " r.Origin.X; intAtom " " r.Origin.Y ] "")
    kids.Add (mkList (subFormLead i "cols" sfc inner)
                     [ sym "" "cols"; intAtom " " (int64 r.Cols) ] "")
    kids.Add (mkList (subFormLead i "rows" sfc " ")
                     [ sym "" "rows"; intAtom " " (int64 r.Rows) ] "")
    kids.Add (mkList (subFormLead i "col_pitch" sfc inner)
        [ sym "" "col_pitch"
          intAtom " " r.ColPitch.X
          intAtom " " r.ColPitch.Y ] "")
    kids.Add (mkList (subFormLead i "row_pitch" sfc " ")
        [ sym "" "row_pitch"
          intAtom " " r.RowPitch.X
          intAtom " " r.RowPitch.Y ] "")
    if r.Rot <> 0.0 then
        // Degrees throughout — see SRef writer.
        kids.Add (mkList (subFormLead i "rot" sfc inner)
                         [ sym "" "rot"; floatAtom " " r.Rot ] "")
    if r.Mag <> 1.0 then
        kids.Add (mkList (subFormLead i "mag" sfc " ")
                         [ sym "" "mag"; floatAtom " " r.Mag ] "")
    if r.Reflect then
        kids.Add (mkList (subFormLead i "reflect" sfc " ")
                         [ sym "" "reflect"; sym " " "true" ] "")
    match propsForm (subFormLead i "props" sfc inner) r.Props with
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
        let lead = leading i props.Comments
        // Sub-form comments inside a (props …) attach to property
        // keys (e.g. `; bbox-anchored at center` before `(bbox …)`).
        // The synth uses `subFormLead i <key> sfc " "` for each prop
        // entry so the comment renders on its own line above its
        // (key …) form.
        let sfc = props.SubFormComments
        let kids =
            sym "" "props"
            :: (props.Items
                |> List.map (fun p ->
                    let propLead = subFormLead i p.Key sfc " "
                    mkList propLead (propertyChildren p) ""))
        mkList lead kids ""

/// Synthesize a `(meta ...)` block. Emitted as the first form
/// inside a cell — geometry follows. Only `generator` is required;
/// other sub-forms are conditional on Option-presence.
let private synthesizeMeta (i: int) (m: Meta) : Sexp =
    let lead = leading i m.Comments
    let innerLead = indent (i + 1)
    let kids = ResizeArray<Sexp>()
    kids.Add (sym "" "meta")
    kids.Add (mkList innerLead [ sym "" "generator"; stringAtom " " m.Generator ] "")
    // `(params ...)` is always emitted, even when empty — consumers
    // distinguish "no params" from "malformed schema" by its presence.
    let paramKids =
        sym "" "params"
        :: (m.Params |> List.map (fun p -> mkList " " (propertyChildren p) ""))
    kids.Add (mkList innerLead paramKids "")
    match m.Source with
    | Some s ->
        kids.Add (mkList innerLead [ sym "" "source"; stringAtom " " s ] "")
    | None -> ()
    match m.Generated with
    | Some g ->
        kids.Add (mkList innerLead [ sym "" "generated"; stringAtom " " g ] "")
    | None -> ()
    match m.Digest with
    | Some d ->
        kids.Add (mkList innerLead [ sym "" "digest"; stringAtom " " d ] "")
    | None -> ()
    mkList lead (List.ofSeq kids) ""

let private synthesizeCell (i: int) (c: Cell) : Sexp =
    let lead = leading i c.Comments
    let elementKids = c.Elements |> List.map (synthesizeElement (i + 1))
    let metaKid =
        match c.Meta with
        | Some m -> [ synthesizeMeta (i + 1) m ]
        | None -> []
    let kids =
        sym "" "cell"
        :: sym " " c.Name
        :: (metaKid @ elementKids)
    mkList lead kids ""

let private synthesizeImport (i: int) (imp: Import) : Sexp =
    let lead = leading i imp.Comments
    mkList lead [ sym "" "import"; stringAtom " " imp.Path ] ""

let private synthesizeLayoutForm (doc: Document) : Sexp =
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
    // No `(nets …)` block. Labels with `Kind = NetName` are the
    // source of truth for the net set; downstream consumers walk
    // labels directly. See spec.md "Source of truth for nets" and
    // Decision 4 = C in plan.md.
    for c in doc.Cells do
        kids.Add (synthesizeCell 1 c)
    // HeaderComments precede the `(layout ...)` form itself. The
    // `(layout ...)` leading is just the comment block (no indent —
    // it's column 0).
    let layoutLead =
        if List.isEmpty doc.HeaderComments then ""
        else
            let sb = StringBuilder()
            for c in doc.HeaderComments do
                if sb.Length > 0 then sb.Append '\n' |> ignore
                sb.Append "; " |> ignore
                sb.Append c |> ignore
            sb.Append '\n' |> ignore
            sb.ToString()
    mkList layoutLead (List.ofSeq kids) ""

// ─── Public surface ──────────────────────────────────────────────────

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

let write (doc: Document) : string =
    let sb = StringBuilder()
    emitSexp sb (synthesizeLayoutForm doc)
    sb.Append '\n' |> ignore
    sb.ToString()
