module Rekolektion.Viz.Core.Tests.SmokeP0Instances

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout

let private fixturePath =
    "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cim/cim_reram_4t2r_wip.mag"

[<Fact>]
let ``cim_reram_4t2r_wip enumerates two top-level SRef instances`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _warns = Layout.LayoutLoader.load fixturePath
        let inst = Instances.enumerate lib
        inst.Length |> should equal 2
        for i in inst do
            let (x1, y1, x2, y2) = i.BBox
            x1 |> should be (lessThan x2)
            y1 |> should be (lessThan y2)

[<Fact>]
let ``snap delta to 5 nm grid for mag library is 1 DBU step`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.load fixturePath
        // magscale 1 2 + 10 nm internal → 5 nm/DBU → grid step = 1 DBU
        let step = Snap.gridDbu lib Snap.sky130MfgGridNm
        step |> should equal 1L
        let (dx, dy) = Snap.snapDeltaDbu lib Snap.sky130MfgGridNm 7L 0L
        dx |> should equal 7L
        dy |> should equal 0L

[<Fact>]
let ``translateSelection moves only selected indices`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.load fixturePath
        let before = Instances.enumerate lib
        before.Length |> should equal 2
        let pickIdx = before.[0].Index
        let pickedSet = Set.ofList [pickIdx]
        let lib' = Instances.translateSelection lib pickedSet 100L 50L
        let after = Instances.enumerate lib'
        after.Length |> should equal 2
        let movedSref = after |> Array.find (fun i -> i.Index = pickIdx)
        let oldOrigin = before.[0].Sref.Origin
        movedSref.Sref.Origin.X |> should equal (oldOrigin.X + 100L)
        movedSref.Sref.Origin.Y |> should equal (oldOrigin.Y + 50L)
        let untouched = after |> Array.find (fun i -> i.Index <> pickIdx)
        let priorUntouched = before |> Array.find (fun i -> i.Index <> pickIdx)
        untouched.Sref.Origin |> should equal priorUntouched.Sref.Origin

[<Fact>]
let ``snap helper handles negative deltas symmetrically`` () =
    let lib : Rekolektion.Viz.Core.Gds.Types.Library = {
        Name = "x"
        UserUnitsPerDbUnit = 0.001       // 1 nm / DBU
        DbUnitsInMeters = 1.0e-9
        Structures = []
    }
    let step = Snap.gridDbu lib Snap.sky130MfgGridNm
    step |> should equal 5L
    Snap.snapCoord step 7L  |> should equal 5L
    Snap.snapCoord step 8L  |> should equal 10L
    Snap.snapCoord step (-7L) |> should equal -5L
    Snap.snapCoord step (-8L) |> should equal -10L
