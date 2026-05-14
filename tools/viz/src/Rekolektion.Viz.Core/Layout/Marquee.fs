module Rekolektion.Viz.Core.Layout.Marquee

open Rekolektion.Viz.Core.Rkt.Types

/// Marquee mode derived from drag direction. Left→right reads the
/// rectangle as enclose-only; right→left reads it as touch-select.
/// CAD convention.
type Mode = Enclose | Touch

let modeOfDirection (startX: int64) (endX: int64) : Mode =
    if endX >= startX then Enclose else Touch

/// Normalize raw start/end DBU corners to (xmin, ymin, xmax, ymax)
/// so callers don't have to remember which side is which.
let normalize (startX: int64) (startY: int64) (endX: int64) (endY: int64)
        : int64 * int64 * int64 * int64 =
    min startX endX, min startY endY, max startX endX, max startY endY

/// Hit test a candidate bbox against the marquee. `bbox` is
/// (xmin, ymin, xmax, ymax); `marquee` is (xmin, ymin, xmax, ymax)
/// already normalized by `normalize`.
let bboxFits (mode: Mode)
        (marquee: int64 * int64 * int64 * int64)
        (bbox: int64 * int64 * int64 * int64) : bool =
    let mxMin, myMin, mxMax, myMax = marquee
    let a, b, c, d = bbox
    match mode with
    | Enclose ->
        a >= mxMin && c <= mxMax
        && b >= myMin && d <= myMax
    | Touch ->
        not (c < mxMin || a > mxMax
             || d < myMin || b > myMax)

/// Compute the axis-aligned bbox of a list of points. Returns None
/// for an empty list.
let pointsBbox (pts: Point list) : (int64 * int64 * int64 * int64) option =
    let mutable minX = System.Int64.MaxValue
    let mutable minY = System.Int64.MaxValue
    let mutable maxX = System.Int64.MinValue
    let mutable maxY = System.Int64.MinValue
    let mutable any = false
    for p in pts do
        any <- true
        if p.X < minX then minX <- p.X
        if p.X > maxX then maxX <- p.X
        if p.Y < minY then minY <- p.Y
        if p.Y > maxY then maxY <- p.Y
    if any then Some (minX, minY, maxX, maxY) else None
