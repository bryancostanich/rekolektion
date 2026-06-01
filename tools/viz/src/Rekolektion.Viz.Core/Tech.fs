module Rekolektion.Viz.Core.Tech

/// Manufacturing-grid pitch per PDK, in DBU (nanometers given the
/// rekolektion default `Units.DbuNm = 1`).  Single source of truth on
/// the F# side, mirroring `rekolektion.tech._PDK_GRIDS_NM` in Python.
///
/// Track 01 (silicon_correct) keeps the two registries in lockstep —
/// see `tests/test_grid_registry_parity.py` for the cross-language
/// equality assertion.  Drift between the two is exactly the
/// foundational bug the whole track exists to prevent: a coord that
/// the Python helpers snap but the F# `to-gds` emitter doesn't (or
/// vice-versa) lands off-grid in the GDS and ships to the foundry.
let gridDbu : Map<string, int64> =
    Map.ofList [
        "sky130", 5L
        // "umc28", 1L   // add when v2_optimization needs it
    ]

/// Manufacturing grid for a PDK, in DBU.  Raises on unknown PDK so
/// silent fall-back to a default grid can't mask drift.
let gridFor (pdk: string) : int64 =
    match Map.tryFind pdk gridDbu with
    | Some g -> g
    | None ->
        failwithf
            "Unknown PDK '%s' — register in Rekolektion.Viz.Core.Tech.gridDbu"
            pdk

/// Snap a single DBU value to a manufacturing grid.  Half-away-from-
/// zero so `snap(-v) = -snap(v)` and rotated SRefs stay symmetric —
/// matches `rekolektion.layout.snap.snap_dbu` byte-for-byte.
let snapDbu (grid: int64) (v: int64) : int64 =
    if grid <= 1L then
        v
    else
        let half = grid / 2L
        if v >= 0L then ((v + half) / grid) * grid
        else -(((-v + half) / grid) * grid)
