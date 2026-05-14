module Rekolektion.Viz.Core.Rkt.Reader

open System
open System.IO
open Rekolektion.Viz.Core.Rkt.Cst
open Rekolektion.Viz.Core.Rkt.Types

// ─── Errors ──────────────────────────────────────────────────────────────

type ReaderError = {
    Path: string option
    Pos: SourcePos option
    Message: string
}

let private err (path: string option) (pos: SourcePos option) (msg: string) : ReaderError =
    { Path = path; Pos = pos; Message = msg }

/// Extract `;`-prefixed comment lines from a leading-trivia run.
///
/// Whitespace between comments is dropped. Each `;` line lands in the
/// result as its body text — leading semicolons stripped, one optional
/// space after the `;` stripped (so `; foo` and `;foo` both yield
/// `foo`), trailing CR stripped. The writer puts them back on
/// synthesis with a single leading `; ` and a trailing newline.
let private extractComments (leading: string) : string list =
    if String.IsNullOrEmpty leading then []
    else
        let lines = leading.Split('\n')
        [ for raw in lines do
            let line =
                if raw.Length > 0 && raw.[raw.Length - 1] = '\r'
                then raw.Substring(0, raw.Length - 1)
                else raw
            let trimmed = line.TrimStart()
            if trimmed.StartsWith ";" then
                let body = trimmed.Substring 1
                let body =
                    if body.StartsWith " " then body.Substring 1 else body
                yield body ]

/// Comments preceding a CST node, extracted from its leading trivia.
let private commentsOf (s: Sexp) : string list =
    extractComments (Cst.leadingOf s)

// ─── Lexer ───────────────────────────────────────────────────────────────

/// Mutable cursor through the source string. Held by a `ParseState`
/// (one per file). Not a struct — record-of-mutables on the heap is
/// the simplest way to mutate from helpers without byref ceremony.
type private LexState() =
    let mutable pos = 0
    let mutable line = 1
    let mutable col = 1
    member _.Pos with get () = pos and set v = pos <- v
    member _.Line with get () = line and set v = line <- v
    member _.Col with get () = col and set v = col <- v

type private Token =
    | TLParen of SourcePos * string
    | TRParen of SourcePos * string
    | TAtom of SourcePos * string * AtomKind * string
    | TEof of string

let private isSymbolChar (c: char) : bool =
    System.Char.IsLetterOrDigit c
    || c = '_' || c = '-' || c = '+' || c = '.' || c = ':'
    || c = '/' || c = '*' || c = '?' || c = '!' || c = '$'
    || c = '%' || c = '&' || c = '<' || c = '>' || c = '='
    || c = '@' || c = '#' || c = '^' || c = '~'

let private bumpChar (st: LexState) (c: char) =
    st.Pos <- st.Pos + 1
    if c = '\n' then
        st.Line <- st.Line + 1
        st.Col <- 1
    else
        st.Col <- st.Col + 1

let private posOf (st: LexState) : SourcePos =
    { Line = st.Line; Col = st.Col }

/// Read whitespace and `;...` line comments, returning the consumed
/// run as leading trivia for the next token.
let private readTrivia (src: string) (st: LexState) : string =
    let sb = System.Text.StringBuilder()
    let mutable keepGoing = true
    while keepGoing && st.Pos < src.Length do
        let c = src.[st.Pos]
        if c = ' ' || c = '\t' || c = '\n' || c = '\r' then
            sb.Append c |> ignore
            bumpChar st c
        elif c = ';' then
            while st.Pos < src.Length && src.[st.Pos] <> '\n' do
                sb.Append src.[st.Pos] |> ignore
                bumpChar st src.[st.Pos]
            // newline (if any) consumed in the outer loop's whitespace pass
        else
            keepGoing <- false
    sb.ToString()

let private readStringLit
    (src: string)
    (st: LexState)
    : Result<string, ReaderError> =
    // st.Pos points at the opening '"'
    let sb = System.Text.StringBuilder()
    sb.Append '"' |> ignore
    bumpChar st '"'
    let mutable closed = false
    let mutable errMsg : string option = None
    while not closed && errMsg.IsNone && st.Pos < src.Length do
        let c = src.[st.Pos]
        if c = '"' then
            sb.Append '"' |> ignore
            bumpChar st c
            closed <- true
        elif c = '\\' then
            // Take backslash + next char verbatim. The writer emits
            // the verbatim text, so escape decoding only happens when
            // we hand a string to a consumer.
            sb.Append c |> ignore
            bumpChar st c
            if st.Pos < src.Length then
                let n = src.[st.Pos]
                sb.Append n |> ignore
                bumpChar st n
            else
                errMsg <- Some "unterminated escape at end of input"
        else
            sb.Append c |> ignore
            bumpChar st c
    if not closed && errMsg.IsNone then
        errMsg <- Some "unterminated string literal"
    match errMsg with
    | Some m ->
        Error (err None (Some { Line = st.Line; Col = st.Col }) m)
    | None -> Ok (sb.ToString())

let private classifyAtom (text: string) : AtomKind =
    if text.Length = 0 then Symbol
    else
        let mutable i = 0
        let mutable sawSign = false
        if text.[0] = '+' || text.[0] = '-' then
            sawSign <- true
            i <- 1
        let mutable sawDigit = false
        let mutable sawDot = false
        let mutable sawExp = false
        let mutable valid = true
        while valid && i < text.Length do
            let c = text.[i]
            if c >= '0' && c <= '9' then
                sawDigit <- true
                i <- i + 1
            elif c = '.' && not sawDot && not sawExp then
                sawDot <- true
                i <- i + 1
            elif (c = 'e' || c = 'E') && sawDigit && not sawExp then
                sawExp <- true
                i <- i + 1
                if i < text.Length && (text.[i] = '+' || text.[i] = '-') then
                    i <- i + 1
            else
                valid <- false
        if not valid || not sawDigit then Symbol
        elif sawDot || sawExp then FloatLit
        else
            // bare "+" or "-" alone has no digit and was filtered above
            ignore sawSign
            IntLit

let private readBareAtom (src: string) (st: LexState) : string =
    let start = st.Pos
    while st.Pos < src.Length && isSymbolChar src.[st.Pos] do
        bumpChar st src.[st.Pos]
    src.Substring(start, st.Pos - start)

let private nextToken
    (src: string)
    (st: LexState)
    : Result<Token, ReaderError> =
    let leading = readTrivia src st
    if st.Pos >= src.Length then
        Ok (TEof leading)
    else
        let pos = posOf st
        let c = src.[st.Pos]
        if c = '(' then
            bumpChar st c
            Ok (TLParen (pos, leading))
        elif c = ')' then
            bumpChar st c
            Ok (TRParen (pos, leading))
        elif c = '"' then
            match readStringLit src st with
            | Ok text -> Ok (TAtom (pos, leading, StringLit, text))
            | Error e -> Error { e with Pos = Some pos }
        elif isSymbolChar c then
            let text = readBareAtom src st
            let kind = classifyAtom text
            Ok (TAtom (pos, leading, kind, text))
        else
            Error (err None (Some pos) (sprintf "unexpected character '%c'" c))

// ─── Parser ──────────────────────────────────────────────────────────────

type private ParseState = {
    Source: string
    Path: string option
    Lex: LexState
    /// Pending one-token lookahead; consumed when read.
    mutable Pending: Token option
}

let private peek (ps: ParseState) : Result<Token, ReaderError> =
    match ps.Pending with
    | Some t -> Ok t
    | None ->
        match nextToken ps.Source ps.Lex with
        | Ok t ->
            ps.Pending <- Some t
            Ok t
        | Error e -> Error { e with Path = ps.Path }

let private consume (ps: ParseState) : Result<Token, ReaderError> =
    match peek ps with
    | Error e -> Error e
    | Ok t ->
        ps.Pending <- None
        Ok t

let rec private parseSexp
    (ps: ParseState)
    : Result<Sexp, ReaderError> =
    match consume ps with
    | Error e -> Error e
    | Ok (TLParen (pos, leading)) -> parseList ps pos leading
    | Ok (TAtom (pos, leading, kind, text)) ->
        Ok (SAtom { Leading = leading; Pos = pos; Kind = kind; Text = text })
    | Ok (TRParen (pos, _)) ->
        Error (err ps.Path (Some pos) "unexpected ')'")
    | Ok (TEof _) ->
        Error (err ps.Path None "unexpected end of input")

and private parseList
    (ps: ParseState)
    (openPos: SourcePos)
    (leading: string)
    : Result<Sexp, ReaderError> =
    let rec loop (acc: Sexp list) =
        match peek ps with
        | Error e -> Error e
        | Ok (TRParen (closePos, trailing)) ->
            ps.Pending <- None
            let lst =
                { Leading = leading
                  OpenPos = openPos
                  Children = List.rev acc
                  Trailing = trailing
                  ClosePos = closePos }
            Ok (SList lst)
        | Ok (TEof _) ->
            Error (err ps.Path (Some openPos) "unterminated '('")
        | Ok _ ->
            match parseSexp ps with
            | Error e -> Error e
            | Ok s -> loop (s :: acc)
    loop []

let private parseFile (source: string) (path: string option)
    : Result<Cst.Document, ReaderError> =
    let ps =
        { Source = source
          Path = path
          Lex = LexState()
          Pending = None }
    let rec loop (acc: Sexp list) =
        match peek ps with
        | Error e -> Error e
        | Ok (TEof trailing) ->
            ps.Pending <- None
            Ok { Roots = List.rev acc
                 Trailing = trailing
                 SourcePath = path }
        | Ok _ ->
            match parseSexp ps with
            | Error e -> Error e
            | Ok s -> loop (s :: acc)
    loop []

let parse (source: string) : Result<Cst.Document, ReaderError> =
    parseFile source None

let parseWithPath (source: string) (path: string)
    : Result<Cst.Document, ReaderError> =
    parseFile source (Some path)

// ─── Analyzer (CST → AST) ────────────────────────────────────────────────

let private symbolText (s: Sexp) : string option =
    match s with
    | SAtom { Kind = Symbol; Text = t } -> Some t
    | _ -> None

let private stringText (s: Sexp) : string option =
    match s with
    | SAtom { Kind = StringLit; Text = t } ->
        // Strip the surrounding quotes and decode the few escapes we
        // care about. Verbatim text (for round-trip) lives in the
        // CST; the AST gets the decoded value.
        if t.Length < 2 then None
        else
            let inner = t.Substring(1, t.Length - 2)
            let sb = System.Text.StringBuilder(inner.Length)
            let mutable i = 0
            while i < inner.Length do
                let c = inner.[i]
                if c = '\\' && i + 1 < inner.Length then
                    let n = inner.[i + 1]
                    let decoded =
                        match n with
                        | 'n' -> '\n'
                        | 't' -> '\t'
                        | 'r' -> '\r'
                        | '\\' -> '\\'
                        | '"' -> '"'
                        | other -> other
                    sb.Append decoded |> ignore
                    i <- i + 2
                else
                    sb.Append c |> ignore
                    i <- i + 1
            Some (sb.ToString())
    | _ -> None

let private intValue (s: Sexp) : int64 option =
    match s with
    | SAtom { Kind = IntLit; Text = t } ->
        match Int64.TryParse t with
        | true, v -> Some v
        | _ -> None
    | _ -> None

let private floatValue (s: Sexp) : float option =
    match s with
    | SAtom { Kind = FloatLit; Text = t } ->
        match Double.TryParse(t, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None
    | SAtom { Kind = IntLit; Text = t } ->
        match Double.TryParse(t, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None
    | _ -> None

let private listChildren (s: Sexp) : Sexp list option =
    match s with
    | SList l -> Some l.Children
    | _ -> None

let private head (s: Sexp) : string option =
    match s with
    | SList { Children = h :: _ } -> symbolText h
    | _ -> None

let private childrenAfterHead (s: Sexp) : Sexp list option =
    match s with
    | SList { Children = _ :: rest } -> Some rest
    | _ -> None

/// Find the first child form with the given head symbol.
let private findForm (head: string) (children: Sexp list) : Sexp option =
    children |> List.tryFind (fun c -> Cst.isHead head c)

/// Find all child forms with the given head symbol.
let private findForms (head: string) (children: Sexp list) : Sexp list =
    children |> List.filter (fun c -> Cst.isHead head c)

let private analyzeLayer
    (path: string option)
    (defaultPdk: string)
    (s: Sexp)
    : Result<Layer, ReaderError> =
    match s with
    | SAtom { Kind = Symbol; Text = t; Pos = pos } ->
        // Accept "pdk:name" or bare "name" (defaulted to pdk) or
        // "unknown:N/D".
        let colon = t.IndexOf ':'
        if colon < 0 then
            Ok (Named (defaultPdk, t))
        else
            let prefix = t.Substring(0, colon)
            let rest = t.Substring(colon + 1)
            if prefix = "unknown" then
                let slash = rest.IndexOf '/'
                if slash < 0 then
                    Error (err path (Some pos) "unknown layer must be 'unknown:N/D'")
                else
                    let nStr = rest.Substring(0, slash)
                    let dStr = rest.Substring(slash + 1)
                    match Int32.TryParse nStr, Int32.TryParse dStr with
                    | (true, n), (true, d) -> Ok (Unknown (n, d))
                    | _ ->
                        Error (err path (Some pos)
                            (sprintf "unknown layer needs integer pair, got '%s'" rest))
            else
                Ok (Named (prefix, rest))
    | _ ->
        Error (err path (Some (Cst.posOf s)) "expected layer reference")

let private analyzePoint
    (path: string option)
    (s: Sexp)
    : Result<Point, ReaderError> =
    match s with
    | SList { Children = [ x; y ] } ->
        match intValue x, intValue y with
        | Some xi, Some yi -> Ok { X = xi; Y = yi }
        | _ ->
            Error (err path (Some (Cst.posOf s)) "point coordinates must be integers")
    | _ ->
        Error (err path (Some (Cst.posOf s)) "expected (x y) point")

let private analyzePoints
    (path: string option)
    (s: Sexp)
    : Result<Point list, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "expected (points ...)")
    | Some ps ->
        let rec walk acc = function
            | [] -> Ok (List.rev acc)
            | p :: rest ->
                match analyzePoint path p with
                | Error e -> Error e
                | Ok pt -> walk (pt :: acc) rest
        walk [] ps

let private propValueOf (s: Sexp) : PropValue =
    match s with
    | SAtom { Kind = StringLit } ->
        match stringText s with
        | Some t -> PvString t
        | None -> PvAtom ""
    | SAtom { Kind = IntLit; Text = t } ->
        match Int64.TryParse t with
        | true, v -> PvInt v
        | _ -> PvAtom t
    | SAtom { Kind = FloatLit; Text = t } ->
        match Double.TryParse(t, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> PvFloat v
        | _ -> PvAtom t
    | SAtom { Text = t } -> PvAtom t
    | SList _ ->
        // Nested property values flatten to atom-of-source as a
        // pragmatic v1 escape hatch; round-trip via CST keeps them
        // byte-exact even when the AST loses structure.
        PvAtom "_nested_"

let private analyzeProperty
    (path: string option)
    (s: Sexp)
    : Result<Property, ReaderError> =
    match s with
    | SList { Children = SAtom { Kind = Symbol; Text = key } :: value :: [] } ->
        Ok { Key = key; Value = propValueOf value }
    | SList { Children = SAtom { Kind = Symbol; Text = key } :: [] } ->
        Ok { Key = key; Value = PvAtom "" }
    | _ ->
        Error (err path (Some (Cst.posOf s)) "property must be (key value)")

let private analyzeProps
    (path: string option)
    (s: Sexp)
    : Result<Property list, ReaderError> =
    match childrenAfterHead s with
    | None -> Ok []
    | Some ps ->
        let rec walk acc = function
            | [] -> Ok (List.rev acc)
            | p :: rest ->
                match analyzeProperty path p with
                | Error e -> Error e
                | Ok prop -> walk (prop :: acc) rest
        walk [] ps

let private findNet (children: Sexp list) : string option =
    findForm "net" children
    |> Option.bind (fun f ->
        match f with
        | SList { Children = [ _; name ] } -> symbolText name
        | _ -> None)

let private findChildProps
    (path: string option)
    (children: Sexp list)
    : Result<Property list, ReaderError> =
    match findForm "props" children with
    | None -> Ok []
    | Some f -> analyzeProps path f

let private analyzePortDir (s: Sexp) : PortDirection option =
    match symbolText s with
    | Some "input" -> Some Input
    | Some "output" -> Some Output
    | Some "inout" -> Some Inout
    | Some "unspecified" -> Some Unspecified
    | _ -> None

let private analyzePortFlag (s: Sexp) : PortFlag option =
    match symbolText s with
    | Some "signal" -> Some Signal
    | Some "power" -> Some Power
    | Some "ground" -> Some Ground
    | Some "clock" -> Some Clock
    | Some "analog" -> Some Analog
    | Some "scan" -> Some Scan
    | _ -> None

let private analyzePortShape
    (path: string option)
    (s: Sexp)
    : Result<PortShape, ReaderError> =
    match s with
    | SList { Children = SAtom { Kind = Symbol; Text = "rect" }
                         :: x1 :: y1 :: x2 :: y2 :: [] } ->
        match intValue x1, intValue y1, intValue x2, intValue y2 with
        | Some a, Some b, Some c, Some d -> Ok (RectShape (a, b, c, d))
        | _ -> Error (err path (Some (Cst.posOf s)) "rect needs four integers")
    | SList { Children = SAtom { Kind = Symbol; Text = "poly" } :: rest } ->
        let rec walk acc = function
            | [] -> Ok (List.rev acc)
            | p :: ps ->
                match analyzePoint path p with
                | Error e -> Error e
                | Ok pt -> walk (pt :: acc) ps
        match walk [] rest with
        | Error e -> Error e
        | Ok pts -> Ok (PolyShape pts)
    | _ ->
        Error (err path (Some (Cst.posOf s)) "expected (rect ...) or (poly ...) shape")

let private analyzePoly
    (path: string option)
    (defaultPdk: string)
    (s: Sexp)
    : Result<Element, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (poly ...)")
    | Some children ->
        match findForm "layer" children with
        | None -> Error (err path (Some (Cst.posOf s)) "(poly ...) requires (layer ...)")
        | Some layerForm ->
            match childrenAfterHead layerForm with
            | Some [ layerAtom ] ->
                match analyzeLayer path defaultPdk layerAtom with
                | Error e -> Error e
                | Ok layer ->
                    match findForm "points" children with
                    | None -> Error (err path (Some (Cst.posOf s)) "(poly ...) requires (points ...)")
                    | Some ptsForm ->
                        match analyzePoints path ptsForm with
                        | Error e -> Error e
                        | Ok pts ->
                            match findChildProps path children with
                            | Error e -> Error e
                            | Ok props ->
                                Ok (PolyEl {
                                    Layer = layer
                                    Points = pts
                                    Net = findNet children
                                    Props = props
                                    Comments = commentsOf s
                                })
            | _ -> Error (err path (Some (Cst.posOf layerForm)) "(layer X) takes one argument")

let private analyzePath
    (path: string option)
    (defaultPdk: string)
    (s: Sexp)
    : Result<Element, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (path ...)")
    | Some children ->
        match findForm "layer" children, findForm "width" children, findForm "points" children with
        | Some layerForm, Some widthForm, Some ptsForm ->
            match childrenAfterHead layerForm, childrenAfterHead widthForm with
            | Some [ layerAtom ], Some [ w ] ->
                match analyzeLayer path defaultPdk layerAtom, intValue w with
                | Ok layer, Some width ->
                    match analyzePoints path ptsForm with
                    | Error e -> Error e
                    | Ok pts ->
                        let cap =
                            findForm "cap" children
                            |> Option.bind childrenAfterHead
                            |> Option.bind (function [c] -> symbolText c | _ -> None)
                        match findChildProps path children with
                        | Error e -> Error e
                        | Ok props ->
                            Ok (PathEl {
                                Layer = layer
                                Width = width
                                Points = pts
                                Net = findNet children
                                Cap = cap
                                Props = props
                                Comments = commentsOf s
                            })
                | Error e, _ -> Error e
                | _, None -> Error (err path (Some (Cst.posOf widthForm)) "(width ...) needs an integer")
            | _ ->
                Error (err path (Some (Cst.posOf s)) "(path ...) layer/width must be unary forms")
        | _ ->
            Error (err path (Some (Cst.posOf s)) "(path ...) requires layer, width, and points")

let private analyzeRect
    (path: string option)
    (defaultPdk: string)
    (s: Sexp)
    : Result<Element, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (rect ...)")
    | Some children ->
        match findForm "layer" children with
        | None -> Error (err path (Some (Cst.posOf s)) "(rect ...) requires (layer ...)")
        | Some layerForm ->
            match childrenAfterHead layerForm with
            | Some [ layerAtom ] ->
                match analyzeLayer path defaultPdk layerAtom with
                | Error e -> Error e
                | Ok layer ->
                    // Rect coords land as four bare integers immediately
                    // after the layer form.
                    let coords =
                        children
                        |> List.filter (fun c ->
                            match c with
                            | SAtom { Kind = IntLit } -> true
                            | _ -> false)
                    match coords with
                    | [ a; b; c; d ] ->
                        match intValue a, intValue b, intValue c, intValue d with
                        | Some x1, Some y1, Some x2, Some y2 ->
                            match findChildProps path children with
                            | Error e -> Error e
                            | Ok props ->
                                Ok (RectEl {
                                    Layer = layer
                                    X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
                                    Net = findNet children
                                    Props = props
                                    Comments = commentsOf s
                                })
                        | _ -> Error (err path (Some (Cst.posOf s)) "rect coords must be integers")
                    | _ ->
                        Error (err path (Some (Cst.posOf s))
                            (sprintf "(rect ...) needs exactly four coordinate integers, got %d" coords.Length))
            | _ ->
                Error (err path (Some (Cst.posOf layerForm)) "(layer X) takes one argument")

let private analyzePort
    (path: string option)
    (defaultPdk: string)
    (s: Sexp)
    : Result<Element, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (port ...)")
    | Some children ->
        let nameForm = findForm "name" children
        let dirForm = findForm "dir" children
        let layerForm = findForm "layer" children
        let shapeForm = findForm "shape" children
        match nameForm, dirForm, layerForm, shapeForm with
        | Some nf, Some df, Some lf, Some sf ->
            match childrenAfterHead nf, childrenAfterHead df, childrenAfterHead lf, childrenAfterHead sf with
            | Some [ n ], Some [ d ], Some [ layerAtom ], Some [ shape ] ->
                let nameVal = symbolText n |> Option.orElseWith (fun () -> stringText n)
                match nameVal, analyzePortDir d with
                | Some name, Some dir ->
                    match analyzeLayer path defaultPdk layerAtom with
                    | Error e -> Error e
                    | Ok layer ->
                        match analyzePortShape path shape with
                        | Error e -> Error e
                        | Ok ps ->
                            let flags =
                                findForm "flags" children
                                |> Option.bind childrenAfterHead
                                |> Option.map (List.choose analyzePortFlag)
                                |> Option.defaultValue []
                            match findChildProps path children with
                            | Error e -> Error e
                            | Ok props ->
                                Ok (PortEl {
                                    Name = name
                                    Direction = dir
                                    Layer = layer
                                    Flags = flags
                                    Shape = ps
                                    Net = findNet children
                                    Props = props
                                    Comments = commentsOf s
                                })
                | None, _ -> Error (err path (Some (Cst.posOf nf)) "(name ...) requires a symbol or string")
                | _, None -> Error (err path (Some (Cst.posOf df)) "(dir ...) must be input|output|inout|unspecified")
            | _ ->
                Error (err path (Some (Cst.posOf s)) "(port ...) name/dir/layer/shape must be unary forms")
        | _ ->
            Error (err path (Some (Cst.posOf s)) "(port ...) requires name, dir, layer, shape")

let private analyzeLabel
    (path: string option)
    (defaultPdk: string)
    (s: Sexp)
    : Result<Element, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (label ...)")
    | Some children ->
        let layerForm = findForm "layer" children
        let textForm = findForm "text" children
        let originForm = findForm "origin" children
        match layerForm, textForm, originForm with
        | Some lf, Some tf, Some of_ ->
            match childrenAfterHead lf, childrenAfterHead tf, childrenAfterHead of_ with
            | Some [ layerAtom ], Some [ textAtom ], Some [ ox; oy ] ->
                match analyzeLayer path defaultPdk layerAtom with
                | Error e -> Error e
                | Ok layer ->
                    let textVal = stringText textAtom |> Option.orElseWith (fun () -> symbolText textAtom)
                    match textVal, intValue ox, intValue oy with
                    | Some text, Some x, Some y ->
                        let cls =
                            findForm "class" children
                            |> Option.bind childrenAfterHead
                            |> Option.bind (function [c] -> symbolText c |> Option.orElseWith (fun () -> stringText c) | _ -> None)
                        match findChildProps path children with
                        | Error e -> Error e
                        | Ok props ->
                            Ok (LabelEl {
                                Layer = layer
                                Text = text
                                Origin = { X = x; Y = y }
                                Class = cls
                                Props = props
                                Comments = commentsOf s
                            })
                    | _ ->
                        Error (err path (Some (Cst.posOf s)) "(label ...) requires text + integer (origin X Y)")
            | _ ->
                Error (err path (Some (Cst.posOf s)) "(label ...) requires layer, text, origin")
        | _ ->
            Error (err path (Some (Cst.posOf s)) "(label ...) requires layer, text, origin")

let private analyzeSRef
    (path: string option)
    (s: Sexp)
    : Result<Element, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (sref ...)")
    | Some children ->
        let cellForm = findForm "cell" children
        let originForm = findForm "origin" children
        match cellForm, originForm with
        | Some cf, Some of_ ->
            match childrenAfterHead cf, childrenAfterHead of_ with
            | Some [ c ], Some [ ox; oy ] ->
                match symbolText c, intValue ox, intValue oy with
                | Some cellName, Some x, Some y ->
                    let rot =
                        findForm "rot" children
                        |> Option.bind childrenAfterHead
                        |> Option.bind (function [a] -> floatValue a | _ -> None)
                        |> Option.defaultValue 0.0
                    let magV =
                        findForm "mag" children
                        |> Option.bind childrenAfterHead
                        |> Option.bind (function [a] -> floatValue a | _ -> None)
                        |> Option.defaultValue 1.0
                    let reflect =
                        findForm "reflect" children
                        |> Option.bind childrenAfterHead
                        |> Option.bind (function [a] -> symbolText a | _ -> None)
                        |> Option.map (fun t -> t = "true" || t = "yes" || t = "1")
                        |> Option.defaultValue false
                    match findChildProps path children with
                    | Error e -> Error e
                    | Ok props ->
                        Ok (SRefEl {
                            Cell = cellName
                            Origin = { X = x; Y = y }
                            Rot = rot
                            Mag = magV
                            Reflect = reflect
                            Props = props
                            Comments = commentsOf s
                        })
                | _ ->
                    Error (err path (Some (Cst.posOf s)) "(sref ...) cell name and origin must parse")
            | _ ->
                Error (err path (Some (Cst.posOf s)) "(sref ...) cell/origin must be unary")
        | _ ->
            Error (err path (Some (Cst.posOf s)) "(sref ...) requires cell and origin")

let private analyzeARef
    (path: string option)
    (s: Sexp)
    : Result<Element, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (aref ...)")
    | Some children ->
        let cellForm = findForm "cell" children
        let originForm = findForm "origin" children
        let colsForm = findForm "cols" children
        let rowsForm = findForm "rows" children
        let colPitchForm = findForm "col_pitch" children
        let rowPitchForm = findForm "row_pitch" children
        match cellForm, originForm, colsForm, rowsForm, colPitchForm, rowPitchForm with
        | Some cf, Some of_, Some csf, Some rsf, Some cpf, Some rpf ->
            match childrenAfterHead cf, childrenAfterHead of_,
                  childrenAfterHead csf, childrenAfterHead rsf,
                  childrenAfterHead cpf, childrenAfterHead rpf with
            | Some [ c ], Some [ ox; oy ], Some [ csN ], Some [ rsN ],
              Some [ cpX; cpY ], Some [ rpX; rpY ] ->
                match symbolText c,
                      intValue ox, intValue oy,
                      intValue csN, intValue rsN,
                      intValue cpX, intValue cpY,
                      intValue rpX, intValue rpY with
                | Some cellName, Some x, Some y, Some cs, Some rs,
                  Some cpx, Some cpy, Some rpx, Some rpy ->
                    let rot =
                        findForm "rot" children
                        |> Option.bind childrenAfterHead
                        |> Option.bind (function [a] -> floatValue a | _ -> None)
                        |> Option.defaultValue 0.0
                    let magV =
                        findForm "mag" children
                        |> Option.bind childrenAfterHead
                        |> Option.bind (function [a] -> floatValue a | _ -> None)
                        |> Option.defaultValue 1.0
                    let reflect =
                        findForm "reflect" children
                        |> Option.bind childrenAfterHead
                        |> Option.bind (function [a] -> symbolText a | _ -> None)
                        |> Option.map (fun t -> t = "true" || t = "yes" || t = "1")
                        |> Option.defaultValue false
                    match findChildProps path children with
                    | Error e -> Error e
                    | Ok props ->
                        Ok (ARefEl {
                            Cell = cellName
                            Origin = { X = x; Y = y }
                            Cols = int cs
                            Rows = int rs
                            ColPitch = { X = cpx; Y = cpy }
                            RowPitch = { X = rpx; Y = rpy }
                            Rot = rot
                            Mag = magV
                            Reflect = reflect
                            Props = props
                            Comments = commentsOf s
                        })
                | _ ->
                    Error (err path (Some (Cst.posOf s)) "(aref ...) integer fields didn't parse")
            | _ ->
                Error (err path (Some (Cst.posOf s)) "(aref ...) requires unary cell/cols/rows and 2-tuple origin/pitches")
        | _ ->
            Error (err path (Some (Cst.posOf s)) "(aref ...) requires cell, origin, cols, rows, col_pitch, row_pitch")

let private analyzeElement
    (path: string option)
    (defaultPdk: string)
    (s: Sexp)
    : Result<Element option, ReaderError> =
    match head s with
    | Some "poly" -> analyzePoly path defaultPdk s |> Result.map Some
    | Some "path" -> analyzePath path defaultPdk s |> Result.map Some
    | Some "rect" -> analyzeRect path defaultPdk s |> Result.map Some
    | Some "port" -> analyzePort path defaultPdk s |> Result.map Some
    | Some "label" -> analyzeLabel path defaultPdk s |> Result.map Some
    | Some "sref" -> analyzeSRef path s |> Result.map Some
    | Some "aref" -> analyzeARef path s |> Result.map Some
    | Some "props" ->
        match analyzeProps path s with
        | Error e -> Error e
        | Ok p -> Ok (Some (PropsEl { Items = p; Comments = commentsOf s }))
    | _ ->
        // Unknown forms inside a cell are not errors — the format is
        // additive. Caller can decide whether to warn.
        Ok None

/// Parse a `(meta ...)` header inside a cell. Only `generator` is
/// required; everything else is optional. Unknown sub-forms are
/// dropped — the schema is additive. Param values use the same
/// PropValue typing as `(props ...)`.
let private analyzeMeta
    (path: string option)
    (s: Sexp)
    : Result<Meta, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (meta ...)")
    | Some children ->
        let gen =
            findForm "generator" children
            |> Option.bind childrenAfterHead
            |> Option.bind (function
                | [a] ->
                    match stringText a with
                    | Some t -> Some t
                    | None -> symbolText a
                | _ -> None)
        match gen with
        | None ->
            Error (err path (Some (Cst.posOf s))
                "(meta ...) requires a (generator \"...\") sub-form")
        | Some generator ->
            let params' =
                findForm "params" children
                |> Option.bind childrenAfterHead
                |> Option.defaultValue []
                |> List.choose (fun p ->
                    match p with
                    | SList { Children = [ SAtom { Kind = Symbol; Text = key }; value ] } ->
                        Some { Key = key; Value = propValueOf value }
                    | SList { Children = [ SAtom { Kind = Symbol; Text = key } ] } ->
                        Some { Key = key; Value = PvAtom "" }
                    | _ -> None)
            let single name =
                findForm name children
                |> Option.bind childrenAfterHead
                |> Option.bind (function
                    | [a] ->
                        match stringText a with
                        | Some t -> Some t
                        | None -> symbolText a
                    | _ -> None)
            Ok {
                Generator = generator
                Params = params'
                Source = single "source"
                Generated = single "generated"
                Digest = single "digest"
                Comments = commentsOf s
            }

let private analyzeCell
    (path: string option)
    (defaultPdk: string)
    (s: Sexp)
    : Result<Cell, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (cell ...)")
    | Some children ->
        match children with
        | nameAtom :: rest ->
            match symbolText nameAtom with
            | None ->
                Error (err path (Some (Cst.posOf nameAtom)) "cell name must be a symbol")
            | Some name ->
                // `(meta ...)` is optional and, by convention, the
                // first form after the cell name. We accept it anywhere
                // (additive schema) by `findForm`-ing it out before
                // walking the element list.
                let metaResult =
                    match findForm "meta" rest with
                    | None -> Ok None
                    | Some m ->
                        match analyzeMeta path m with
                        | Ok meta -> Ok (Some meta)
                        | Error e -> Error e
                match metaResult with
                | Error e -> Error e
                | Ok meta ->
                    let elementForms =
                        rest
                        |> List.filter (fun c ->
                            match head c with
                            | Some "meta" -> false
                            | _ -> true)
                    let rec walk acc = function
                        | [] -> Ok (List.rev acc)
                        | el :: more ->
                            match analyzeElement path defaultPdk el with
                            | Error e -> Error e
                            | Ok (Some e) -> walk (e :: acc) more
                            | Ok None -> walk acc more
                    match walk [] elementForms with
                    | Error e -> Error e
                    | Ok els ->
                        Ok { Name = name
                             Meta = meta
                             Elements = els
                             Comments = commentsOf s }
        | [] ->
            Error (err path (Some (Cst.posOf s)) "(cell ...) needs a name")

let private analyzeNet
    (path: string option)
    (s: Sexp)
    : Result<Net, ReaderError> =
    match childrenAfterHead s with
    | None -> Error (err path (Some (Cst.posOf s)) "empty (net ...)")
    | Some (nameAtom :: rest) ->
        match symbolText nameAtom with
        | None -> Error (err path (Some (Cst.posOf nameAtom)) "net name must be a symbol")
        | Some name ->
            let domain =
                findForm "domain" rest
                |> Option.bind childrenAfterHead
                |> Option.bind (function [a] -> symbolText a | _ -> None)
                |> Option.defaultValue "signal"
            let voltage =
                findForm "voltage" rest
                |> Option.bind childrenAfterHead
                |> Option.bind (function [a] -> floatValue a | _ -> None)
            let cls =
                findForm "class" rest
                |> Option.bind childrenAfterHead
                |> Option.bind (function [a] -> symbolText a |> Option.orElseWith (fun () -> stringText a) | _ -> None)
            // Anything we don't recognise lands in Props for round-trip
            // fidelity at the AST layer.
            let known = Set.ofList [ "domain"; "voltage"; "class" ]
            let extras =
                rest
                |> List.choose (fun c ->
                    match head c with
                    | Some h when not (Set.contains h known) -> Some c
                    | _ -> None)
            let propsResult =
                extras
                |> List.fold (fun acc form ->
                    match acc with
                    | Error e -> Error e
                    | Ok lst ->
                        match analyzeProperty path form with
                        | Error e -> Error e
                        | Ok p -> Ok (p :: lst))
                    (Ok [])
            match propsResult with
            | Error e -> Error e
            | Ok ps ->
                Ok { Name = name
                     Domain = domain
                     Voltage = voltage
                     NetClass = cls
                     Props = List.rev ps
                     Comments = commentsOf s }
    | Some [] ->
        Error (err path (Some (Cst.posOf s)) "(net ...) requires a name")

let private analyzeNetsForm
    (path: string option)
    (s: Sexp)
    : Result<Net list, ReaderError> =
    match childrenAfterHead s with
    | None -> Ok []
    | Some children ->
        let rec walk acc = function
            | [] -> Ok (List.rev acc)
            | c :: rest ->
                match head c with
                | Some "net" ->
                    match analyzeNet path c with
                    | Error e -> Error e
                    | Ok n -> walk (n :: acc) rest
                | _ -> walk acc rest
        walk [] children

let private analyzeUnits (s: Sexp) : Units =
    let mutable u = Types.Defaults.units
    match childrenAfterHead s with
    | None -> u
    | Some children ->
        for c in children do
            match c with
            | SList { Children = [ SAtom { Kind = Symbol; Text = "dbu_nm" }; n ] } ->
                match intValue n with
                | Some v -> u <- { u with DbuNm = int v }
                | None -> ()
            | SList { Children = [ SAtom { Kind = Symbol; Text = "uu_um" }; n ] } ->
                match intValue n with
                | Some v -> u <- { u with UuUm = int v }
                | None -> ()
            | _ -> ()
        u

let analyze (cst: Cst.Document) : Result<Document, ReaderError> =
    let path = cst.SourcePath
    let rootResult =
        match cst.Roots with
        | [ SList _ as layout ] when head layout = Some "layout" -> Ok layout
        | [] -> Error (err path None "file is empty; expected (layout ...)")
        | first :: _ when head first <> Some "layout" ->
            Error (err path (Some (Cst.posOf first)) "top-level form must be (layout ...)")
        | _ ->
            Error (err path None "file must contain exactly one (layout ...) form")
    match rootResult with
    | Error e -> Error e
    | Ok layout ->
        let layoutChildren =
            match layout with
            | SList l -> l.Children |> List.tail   // drop "layout" symbol
            | _ -> []
        let version =
            findForm "version" layoutChildren
            |> Option.bind childrenAfterHead
            |> Option.bind (function [a] -> intValue a | _ -> None)
            |> Option.map int
            |> Option.defaultValue Types.Defaults.version
        let pdk =
            findForm "pdk" layoutChildren
            |> Option.bind childrenAfterHead
            |> Option.bind (function [a] -> symbolText a | _ -> None)
            |> Option.defaultValue Types.Defaults.pdk
        let units =
            findForm "units" layoutChildren
            |> Option.map analyzeUnits
            |> Option.defaultValue Types.Defaults.units
        let topCell =
            findForm "top" layoutChildren
            |> Option.bind childrenAfterHead
            |> Option.bind (function [a] -> symbolText a | _ -> None)

        let imports =
            findForms "import" layoutChildren
            |> List.choose (fun f ->
                match childrenAfterHead f with
                | Some [ a ] ->
                    stringText a
                    |> Option.map (fun p ->
                        { Path = p; Comments = commentsOf f } : Import)
                | _ -> None)

        let netsResult =
            match findForm "nets" layoutChildren with
            | None -> Ok []
            | Some f -> analyzeNetsForm path f

        match netsResult with
        | Error e -> Error e
        | Ok nets ->
            let cellForms = findForms "cell" layoutChildren
            let rec walk acc = function
                | [] -> Ok (List.rev acc)
                | c :: rest ->
                    match analyzeCell path pdk c with
                    | Error e -> Error e
                    | Ok cell -> walk (cell :: acc) rest
            match walk [] cellForms with
            | Error e -> Error e
            | Ok cells ->
                Ok { Version = version
                     Pdk = pdk
                     Units = units
                     Imports = imports
                     Nets = nets
                     Cells = cells
                     TopCell = topCell
                     HeaderComments = commentsOf layout }

// ─── Public file API + library / import resolver ─────────────────────────

let readFile (path: string) : Result<Cst.Document * Document, ReaderError> =
    let absolute = Path.GetFullPath path
    try
        let source = File.ReadAllText absolute
        match parseWithPath source absolute with
        | Error e -> Error e
        | Ok cst ->
            match analyze cst with
            | Error e -> Error e
            | Ok ast -> Ok (cst, ast)
    with
    | :? FileNotFoundException as ex ->
        Error (err (Some absolute) None ex.Message)
    | :? IOException as ex ->
        Error (err (Some absolute) None ex.Message)

type LoadedDocument = {
    Path: string
    Cst: Cst.Document
    Ast: Document
}

type Library = {
    Roots: string list
    Documents: Map<string, LoadedDocument>
    CellIndex: Map<string, string>
}

let emptyLibrary : Library =
    { Roots = []; Documents = Map.empty; CellIndex = Map.empty }

let private addDocumentToIndex
    (lib: Library)
    (doc: LoadedDocument)
    : Library =
    let cellIndex =
        doc.Ast.Cells
        |> List.fold (fun idx (c: Cell) -> Map.add c.Name doc.Path idx) lib.CellIndex
    { lib with
        Documents = Map.add doc.Path doc lib.Documents
        CellIndex = cellIndex }

let private resolveImport
    (importingFile: string)
    (importPath: string)
    : string =
    if Path.IsPathRooted importPath then
        Path.GetFullPath importPath
    else
        let dir = Path.GetDirectoryName(Path.GetFullPath importingFile)
        Path.GetFullPath(Path.Combine(dir, importPath))

let load (paths: string list) : Result<Library, ReaderError> =
    let mutable lib = { emptyLibrary with Roots = paths |> List.map Path.GetFullPath }
    let mutable error : ReaderError option = None

    let rec loadOne (filePath: string) (chain: string list) =
        if error.IsSome then ()
        elif List.contains filePath chain then
            let cycle =
                List.append (List.rev chain) [ filePath ]
                |> String.concat " -> "
            error <-
                Some (err (Some filePath) None (sprintf "import cycle: %s" cycle))
        elif Map.containsKey filePath lib.Documents then ()
        else
            match readFile filePath with
            | Error e ->
                error <- Some e
            | Ok (cst, ast) ->
                let loaded = { Path = filePath; Cst = cst; Ast = ast }
                lib <- addDocumentToIndex lib loaded
                for imp in ast.Imports do
                    if error.IsNone then
                        let target = resolveImport filePath imp.Path
                        loadOne target (filePath :: chain)

    for p in paths do
        if error.IsNone then
            loadOne (Path.GetFullPath p) []

    match error with
    | Some e -> Error e
    | None -> Ok lib

let loadSingle (path: string) : Result<Library, ReaderError> =
    load [ path ]
