module Rekolektion.Viz.Core.Mag.Types

/// One rectangle on a layer. Coordinates are in the cell's internal
/// units (Magic-side); conversion to the shared GDS-style DBU is
/// done by the Reader at load time via the file's `magscale` and
/// the SKY130 lambda value.
type MagRect = {
    Layer: string
    X1: int64; Y1: int64
    X2: int64; Y2: int64
}

/// One subcell instance.
///
/// Magic stores transforms as a 6-element row-major affine
///     [ a b ] [ x ]   [ e ]
///     [ c d ] [ y ] + [ f ]
/// where (x, y) are subcell-internal and the result is parent-
/// internal. `Box` is the parent-frame bbox emitted alongside the
/// transform — Magic uses it as the subcell's outline for its own
/// hit-testing; we don't need it for rendering but keep it around
/// for diagnostics.
type MagInstance = {
    CellName: string
    InstanceName: string option
    A: float; B: float
    C: float; D: float
    Tx: float; Ty: float
    Box: (int64 * int64 * int64 * int64) option
}

/// `rlabel` directive — point or rectangle label on a layer.
/// Magic carries a port direction byte we ignore for now.
type MagLabel = {
    Layer: string
    X1: int64; Y1: int64
    X2: int64; Y2: int64
    Text: string
}

/// One parsed `.mag` file. `Tech` is the value of the `tech` line
/// (e.g. "sky130A"); `MagscaleNum/Denom` carry the scale ratio
/// from the file (default 1/1 if absent). `BBox` is the
/// `<< properties >> string FIXED_BBOX ...` value if present.
type MagCell = {
    Name: string
    Tech: string
    MagscaleNum: int
    MagscaleDenom: int
    Rects: MagRect list
    Instances: MagInstance list
    Labels: MagLabel list
    BBox: (int64 * int64 * int64 * int64) option
}

/// Magic-internal-unit size in nanometers for sky130A/B at the
/// default `magscale 1 1`. Comes from the tech file's
/// `cifoutput scalefactor 10 nanometers` directive — one magic
/// unit = 10 nm on this PDK. With `magscale a b` in a .mag, the
/// effective scale is `sky130MagicUnitNm * (a / b)`. Hard-coded
/// here so the parser doesn't need to read the .tech file; if we
/// ever support a different process, plumb this through.
let sky130MagicUnitNm : float = 10.0
