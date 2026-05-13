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

/// Property bag value. Mirrors the smallest set of S-expression atoms
/// the writer needs to round-trip; unrecognised value shapes parse as
/// `PvAtom` and emit verbatim. Names are prefixed to avoid clashing
/// with the lexical `AtomKind` cases on the CST side.
type PropValue =
    | PvAtom of string
    | PvString of string
    | PvInt of int64
    | PvFloat of float

type Property = { Key: string; Value: PropValue }

type Poly = {
    Layer: Layer
    Points: Point list
    Net: string option
    Props: Property list
}

type Path = {
    Layer: Layer
    Width: int64
    Points: Point list
    Net: string option
    Cap: string option
    Props: Property list
}

type Rect = {
    Layer: Layer
    X1: int64
    Y1: int64
    X2: int64
    Y2: int64
    Net: string option
    Props: Property list
}

type Port = {
    Name: string
    Direction: PortDirection
    Layer: Layer
    Flags: PortFlag list
    Shape: PortShape
    Net: string option
    Props: Property list
}

type Label = {
    Layer: Layer
    Text: string
    Origin: Point
    Class: string option
    Props: Property list
}

type SRef = {
    Cell: string
    Origin: Point
    Rot: float
    Mag: float
    Reflect: bool
    Props: Property list
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
}

type Element =
    | PolyEl of Poly
    | PathEl of Path
    | RectEl of Rect
    | PortEl of Port
    | LabelEl of Label
    | SRefEl of SRef
    | ARefEl of ARef
    | PropsEl of Property list

type Cell = {
    Name: string
    Elements: Element list
}

type Net = {
    Name: string
    Domain: string
    Voltage: float option
    NetClass: string option
    Props: Property list
}

type Units = {
    DbuNm: int
    UuUm: int
}

/// `(import "path")` form. Path is verbatim from source — relative to
/// the importing file. Resolution happens in a separate pass.
type Import = { Path: string }

/// A single `.rkt` file's parsed semantic content.
///
/// `Pdk` is the default PDK used to resolve bare layer references
/// during analyze. `TopCell` reflects an explicit `(top cell-name)` if
/// present; otherwise the first cell in `Cells` is the document's top
/// by convention.
type Document = {
    Version: int
    Pdk: string
    Units: Units
    Imports: Import list
    Nets: Net list
    Cells: Cell list
    TopCell: string option
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
}
