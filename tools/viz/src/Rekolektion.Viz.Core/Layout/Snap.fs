module Rekolektion.Viz.Core.Layout.Snap

open Rekolektion.Viz.Core.Gds.Types

/// SKY130 manufacturing grid: 5 nm. The interactive editor snaps
/// every translate / rotate / mirror result to this grid so an
/// in-memory edit can never sit off-grid.
let sky130MfgGridNm : float = 5.0

/// DBU step that corresponds to `gridNm` nanometers given the
/// library's `UserUnitsPerDbUnit` (micrometers per DBU). For a
/// .mag file with `magscale 1 2` the internal unit is 5 nm so the
/// grid step is 1 DBU; .gds files with 1 nm DBU give a 5 DBU step.
/// Returns at least 1 — a non-positive grid wedges round-to-nearest.
let gridDbu (lib: Library) (gridNm: float) : int64 =
    let umPerDbu = lib.UserUnitsPerDbUnit
    if umPerDbu <= 0.0 || gridNm <= 0.0 then 1L
    else
        let nmPerDbu = umPerDbu * 1000.0
        let step = gridNm / nmPerDbu
        max 1L (int64 (System.Math.Round step))

/// Round-half-away-from-zero snap of a single coordinate to the
/// nearest multiple of `step`. Symmetric across the origin so a
/// drag that crosses 0 doesn't bias one direction.
let snapCoord (step: int64) (v: int64) : int64 =
    if step <= 1L then v
    else
        let q = if v >= 0L then (v + step / 2L) / step else (v - step / 2L) / step
        q * step

/// Snap a Δ vector (in DBU) to the mfg grid for `lib`. Use this on
/// the *delta* the user is dragging by so an instance's origin
/// stays grid-aligned relative to its starting position even when
/// the original origin was already on grid.
let snapDeltaDbu (lib: Library) (gridNm: float) (dx: int64) (dy: int64)
                 : int64 * int64 =
    let step = gridDbu lib gridNm
    snapCoord step dx, snapCoord step dy

/// Snap an absolute point (in DBU) to the mfg grid for `lib`.
let snapPointDbu (lib: Library) (gridNm: float) (p: Point) : Point =
    let step = gridDbu lib gridNm
    { X = snapCoord step p.X; Y = snapCoord step p.Y }
