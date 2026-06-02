module Rekolektion.Viz.Core.Tests.LayoutSnapTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

/// sky130 dbu = 5 nm → mfg-grid step is exactly 1 DBU.
let private dbuUnits : Units = { DbuNm = 5; UuUm = 1 }

/// finer-grain dbu where 1 DBU = 1 nm; mfg-grid step is 5 DBU.
let private nmUnits : Units = { DbuNm = 1; UuUm = 1 }

[<Fact>]
let ``snapDeltaForOriginDbu lands the anchor on grid when anchor was already on grid`` () =
    // Anchor on grid (15 DBU = 75 nm under nmUnits, grid step 5 DBU).
    // Raw delta of +3 DBU should snap to +5 so the new origin is 20.
    let dx, dy =
        Snap.snapDeltaForOriginDbu nmUnits Snap.sky130MfgGridNm
            15L 15L 3L 3L
    (15L + dx) % 5L |> should equal 0L
    (15L + dy) % 5L |> should equal 0L
    dx |> should equal 5L
    dy |> should equal 5L

[<Fact>]
let ``snapDeltaForOriginDbu fixes the centroid-residue bug from blc_comparator`` () =
    // Reproduces the 2026-06-02 report: a primitive with bbox
    // (xMin = 0, xMax = 999) has integer centroid 499 — at the
    // mfg grid (5 nm = 5 DBU under nmUnits) that's 4 DBU shy of
    // the next grid point. The OLD centroid-relative path,
    // applied with raw delta 0, would still produce delta = 1
    // (500 - 499) and shove the origin off-grid by that amount.
    //
    // The NEW origin-relative path, given an anchor that's
    // already on grid (SRef.origin = 0), holds the snapped
    // delta to 0 when the raw delta is 0 — and to multiples of
    // 5 DBU when the user does drag — so the origin stays on
    // grid no matter what the bbox looks like.
    let dxZero, dyZero =
        Snap.snapDeltaForOriginDbu nmUnits Snap.sky130MfgGridNm
            0L 0L 0L 0L
    dxZero |> should equal 0L
    dyZero |> should equal 0L
    let dx, dy =
        Snap.snapDeltaForOriginDbu nmUnits Snap.sky130MfgGridNm
            0L 0L 7L 7L
    // 7 → 5 (round-half-away from 0 at step 5: 7 - 5/2 = 4 -> q = 1 -> 5).
    dx |> should equal 5L
    dy |> should equal 5L

[<Fact>]
let ``snapDeltaForOriginDbu lifts an off-grid anchor to grid`` () =
    // Anchor at 13 (off grid by 3). A zero drag returns (0, 0)
    // — the zero guard, so a bare click doesn't yank an
    // already-off-grid cell. A non-zero drag of +5 (one step)
    // produces delta 2, landing the new origin on 15.
    let dxZero, dyZero =
        Snap.snapDeltaForOriginDbu nmUnits Snap.sky130MfgGridNm
            13L 13L 0L 0L
    dxZero |> should equal 0L
    dyZero |> should equal 0L
    let dx, dy =
        Snap.snapDeltaForOriginDbu nmUnits Snap.sky130MfgGridNm
            13L 13L 5L 5L
    (13L + dx) % 5L |> should equal 0L
    (13L + dy) % 5L |> should equal 0L

[<Fact>]
let ``snapDeltaForOriginDbu is a no-op when step is 1`` () =
    // .rkt-native 5 nm/DBU files have a mfg-grid step of 1 DBU
    // — every coord is already aligned, so snap should pass
    // through unchanged.
    Snap.gridDbu dbuUnits Snap.sky130MfgGridNm |> should equal 1L
    let dx, dy =
        Snap.snapDeltaForOriginDbu dbuUnits Snap.sky130MfgGridNm
            7L 7L 13L 17L
    dx |> should equal 13L
    dy |> should equal 17L
