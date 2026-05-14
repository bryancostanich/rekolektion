module Rekolektion.Viz.Core.Rkt.Types

/// Integer database unit. The file's `(units (dbu_nm N))` header
/// declares how many nanometers one DBU represents (default 1).
type Point = { X: int64; Y: int64 }

/// Layer reference. After analyze, every layer is either
/// `Named(pdk, name)` (e.g. sky130/met1) or `Unknown(number, datatype)`
/// (a GDS pair we don't have a PDK mapping for — kept visible, not
/// dropped).
type Layer =
    | Named of pdk: string * name: string
    | Unknown of number: int * datatype: int

type PortDirection =
    | Input
    | Output
    | Inout
    | Unspecified

type PortFlag =
    | Signal
    | Power
    | Ground
    | Clock
    | Analog
    | Scan

/// Geometry attached to a `(port ...)` form.
type PortShape =
    | RectShape of x1: int64 * y1: int64 * x2: int64 * y2: int64
    | PolyShape of Point list

/// Property bag value.
type PropValue =
    | PvAtom of string
    | PvString of string
    | PvInt of int64
    | PvFloat of float

type Property = { Key: string; Value: PropValue }

/// `Comments` on every node holds the `;`-prefixed lines that
/// preceded the form in source order. Storage strips the `;` and the
/// trailing newline — one list entry per `;` line. The writer puts
/// them back on synthesis. New nodes default to `Comments = []`;
/// untouched nodes retain whatever the reader populated.
type Poly = {
    Layer: Layer
    Points: Point list
    Net: string option
    Props: Property list
    Comments: string list
}

type Path = {
    Layer: Layer
    Width: int64
    Points: Point list
    Net: string option
    Cap: string option
    Props: Property list
    Comments: string list
}

/// Renamed from `Rect` to dodge a conflict with `Avalonia.Rect` in
/// the App project. Used by the canonical model as the inner record
/// of `RectEl`. Outside the format module, callers see only
/// `RectEl` and don't touch this name.
type Rectangle = {
    Layer: Layer
    X1: int64
    Y1: int64
    X2: int64
    Y2: int64
    Net: string option
    Props: Property list
    Comments: string list
}

type Port = {
    Name: string
    Direction: PortDirection
    Layer: Layer
    Flags: PortFlag list
    Shape: PortShape
    Net: string option
    Props: Property list
    Comments: string list
}

type Label = {
    Layer: Layer
    Text: string
    Origin: Point
    Class: string option
    Props: Property list
    Comments: string list
}

type SRef = {
    Cell: string
    Origin: Point
    Rot: float
    Mag: float
    Reflect: bool
    Props: Property list
    Comments: string list
}

type ARef = {
    Cell: string
    Origin: Point
    Cols: int
    Rows: int
    ColPitch: Point
    RowPitch: Point
    Rot: float
    Mag: float
    Reflect: bool
    Props: Property list
    Comments: string list
}

/// Cell-level `(props ...)` wrapper. `Comments` belong to the form's
/// position within the parent cell.
type Props = {
    Items: Property list
    Comments: string list
}

type Element =
    | PolyEl of Poly
    | PathEl of Path
    | RectEl of Rectangle
    | PortEl of Port
    | LabelEl of Label
    | SRefEl of SRef
    | ARefEl of ARef
    | PropsEl of Props

/// Leading comments of an element, regardless of variant. Useful for
/// editor tooling that wants to display or move comments without
/// caring about element shape.
let elementComments (e: Element) : string list =
    match e with
    | PolyEl p -> p.Comments
    | PathEl p -> p.Comments
    | RectEl r -> r.Comments
    | PortEl p -> p.Comments
    | LabelEl l -> l.Comments
    | SRefEl s -> s.Comments
    | ARefEl a -> a.Comments
    | PropsEl p -> p.Comments

let withElementComments (comments: string list) (e: Element) : Element =
    match e with
    | PolyEl p -> PolyEl { p with Comments = comments }
    | PathEl p -> PathEl { p with Comments = comments }
    | RectEl r -> RectEl { r with Comments = comments }
    | PortEl p -> PortEl { p with Comments = comments }
    | LabelEl l -> LabelEl { l with Comments = comments }
    | SRefEl s -> SRefEl { s with Comments = comments }
    | ARefEl a -> ARefEl { a with Comments = comments }
    | PropsEl p -> PropsEl { p with Comments = comments }

/// Provenance for a PDK-generated cell. Present only on primitives
/// minted by a registered generator (`sky130/nfet_hv`, etc.). When
/// `Meta = Some _` the cell is treated as PDK-owned: the viz editor
/// refuses interior edits, the inspector exposes a "Regenerate"
/// action driven by `Generator` + `Params`, and the cache uses
/// `Digest` (when set) as the lookup key.
///
/// Tape-out ignores this block entirely — geometry alone determines
/// GDS output. The reader is forgiving: unknown sub-forms are
/// dropped (additive schema), `Generator` is the only required
/// field. `Comments` follow the same leading-trivia convention as
/// every other AST node.
type Meta = {
    Generator: string
    Params: Property list
    Source: string option
    Generated: string option
    Digest: string option
    Comments: string list
}

type Cell = {
    Name: string
    Meta: Meta option
    Elements: Element list
    Comments: string list
}

type Net = {
    Name: string
    Domain: string
    Voltage: float option
    NetClass: string option
    Props: Property list
    Comments: string list
}

type Units = {
    DbuNm: int
    UuUm: int
}

/// `(import "path")` form. Path is verbatim from source — relative to
/// the importing file. Resolution happens in a separate pass.
type Import = {
    Path: string
    Comments: string list
}

/// A single `.rkt` file's parsed semantic content.
///
/// `Pdk` is the default PDK used to resolve bare layer references
/// during analyze. `TopCell` reflects an explicit `(top cell-name)` if
/// present; otherwise the first cell in `Cells` is the document's top
/// by convention. `HeaderComments` holds comments that appear before
/// the `(layout ...)` form (typical file headers).
type Document = {
    Version: int
    Pdk: string
    Units: Units
    Imports: Import list
    Nets: Net list
    Cells: Cell list
    TopCell: string option
    HeaderComments: string list
}

/// Defaults applied when a header field is omitted.
module Defaults =
    let units : Units = { DbuNm = 1; UuUm = 1 }
    let pdk : string = "sky130"
    let version : int = 1

let emptyDocument : Document = {
    Version = Defaults.version
    Pdk = Defaults.pdk
    Units = Defaults.units
    Imports = []
    Nets = []
    Cells = []
    TopCell = None
    HeaderComments = []
}
