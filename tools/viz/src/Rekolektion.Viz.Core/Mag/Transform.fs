module Rekolektion.Viz.Core.Mag.Transform

open Rekolektion.Viz.Core.Gds.Types

/// Magic stores subcell transforms as a 6-element row-major affine:
///     parent_x = a * sub_x + b * sub_y + e
///     parent_y = c * sub_x + d * sub_y + f
/// In practice the linear part is one of the eight orientations
/// formed by 90° rotations + reflection: a, b, c, d ∈ {-1, 0, 1}
/// with |det| = 1. (Magnification isn't a standard Magic feature.)
///
/// GDS / our `SRef` instead carries `(Mag, Angle, Reflected)`.
/// This module does the decomposition. We pick `Reflected` based
/// on the sign of the determinant; the angle comes from the
/// remaining linear part. See `Layout.Flatten.fromSref` for the
/// matching forward direction.

let private rad2deg r = r * 180.0 / System.Math.PI

/// Decompose Magic's [a b; c d] linear part + (e, f) translation
/// into a `Gds.Types.SRef`. The caller supplies `cellName` since
/// `transform` itself doesn't carry it — it sits beside the
/// preceding `use` directive.
///
/// `originDbu` lets the caller convert (e, f) from Magic internal
/// units into the Library's DBU convention.
let toSref
        (cellName: string)
        (a: float) (b: float)
        (c: float) (d: float)
        (e: float) (f: float)
        : SRef =
    let det = a * d - b * c
    let mag = sqrt (abs det)
    let mag = if mag < 1e-9 then 1.0 else mag
    let reflected = det < 0.0
    // For non-reflected:
    //   M = mag * [cos -sin; sin cos]
    //     a = mag*cos    b = -mag*sin
    //     c = mag*sin    d =  mag*cos
    // For reflected (reflect about X then rotate):
    //   M = mag * [cos sin; sin -cos]
    //     a = mag*cos    b =  mag*sin
    //     c = mag*sin    d = -mag*cos
    let cosA, sinA =
        if reflected then a / mag, b / mag
        else a / mag, -b / mag
    let angleDeg = rad2deg (System.Math.Atan2(sinA, cosA))
    {
        StructureName = cellName
        Origin = { X = int64 (System.Math.Round e); Y = int64 (System.Math.Round f) }
        Mag = mag
        Angle = angleDeg
        Reflected = reflected
    }
