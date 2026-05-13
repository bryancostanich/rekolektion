module Rekolektion.Viz.Core.Rkt.Cst

/// 1-based line/column. Tracked on every CST node for diagnostics
/// during analyze and import resolution.
type SourcePos = { Line: int; Col: int }

/// Lexical kind of an atom. Determines how `Text` was scanned and how
/// the writer re-emits it. `IntLit` and `FloatLit` are stored as
/// verbatim text so that round-trip is byte-exact (e.g. `1.0e3`
/// re-emits as `1.0e3`, not `1000.0`).
type AtomKind =
    | Symbol
    | StringLit
    | IntLit
    | FloatLit

/// Atom node — symbol, string, integer, or float.
///
/// `Leading` holds the whitespace + comments immediately preceding the
/// atom in source order. `Text` is verbatim source text (for
/// `StringLit` it includes the surrounding quotes and any escapes).
type SexpAtom = {
    Leading: string
    Pos: SourcePos
    Kind: AtomKind
    Text: string
}

/// List node — a parenthesised S-expression.
///
/// `Leading` is the trivia before `(`. `Trailing` is the trivia
/// between the last child (or `(` for an empty list) and `)`. These
/// two fields together with each child's `Leading` reproduce the
/// original byte sequence on round-trip.
type SexpList = {
    Leading: string
    OpenPos: SourcePos
    Children: Sexp list
    Trailing: string
    ClosePos: SourcePos
}

and Sexp =
    | SAtom of SexpAtom
    | SList of SexpList

/// Whole parsed file. `Trailing` captures any whitespace + comments
/// after the last top-level form (typical EOF newline).
type Document = {
    Roots: Sexp list
    Trailing: string
    SourcePath: string option
}

let posOf (s: Sexp) : SourcePos =
    match s with
    | SAtom a -> a.Pos
    | SList l -> l.OpenPos

let leadingOf (s: Sexp) : string =
    match s with
    | SAtom a -> a.Leading
    | SList l -> l.Leading

/// True when the sexp is a list whose first child is a symbol matching
/// `head`. Convenience for analyze passes.
let isHead (head: string) (s: Sexp) : bool =
    match s with
    | SList { Children = SAtom { Kind = Symbol; Text = t } :: _ } -> t = head
    | _ -> false

let asAtom (s: Sexp) : SexpAtom option =
    match s with
    | SAtom a -> Some a
    | _ -> None

let asList (s: Sexp) : SexpList option =
    match s with
    | SList l -> Some l
    | _ -> None
