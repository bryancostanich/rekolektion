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
        let lib, _warns = Layout.LayoutLoader.loadAsLibrary fixturePath
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
        let lib, _ = Layout.LayoutLoader.loadAsLibrary fixturePath
        // magscale 1 2 + 10 nm internal → 5 nm/DBU → grid step = 1 DBU
        let units = Snap.unitsOfLibrary lib
        let step = Snap.gridDbu units Snap.sky130MfgGridNm
        step |> should equal 1L
        let (dx, dy) = Snap.snapDeltaDbu units Snap.sky130MfgGridNm 7L 0L
        dx |> should equal 7L
        dy |> should equal 0L

[<Fact>]
let ``translateSelection moves only selected indices`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.loadAsLibrary fixturePath
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
let ``layerBboxesOf groups polygons by layer + datatype`` () =
    let pts (xs: (int64 * int64) list) =
        xs
        |> List.map (fun (x, y) ->
            ({ X = x; Y = y } : Rekolektion.Viz.Core.Rkt.Types.Point))
        |> List.toArray
    let polys : Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array = [|
        { Layer = 67; DataType = 20; SourceStructure = "x"; SourceIndex = 0
          Points = pts [(0L,0L); (10L,0L); (10L,5L); (0L,5L); (0L,0L)] }
        { Layer = 67; DataType = 20; SourceStructure = "x"; SourceIndex = 1
          Points = pts [(20L,0L); (30L,0L); (30L,5L); (20L,5L); (20L,0L)] }
        { Layer = 68; DataType =  0; SourceStructure = "x"; SourceIndex = 2
          Points = pts [(0L,0L); (5L,0L); (5L,5L); (0L,5L); (0L,0L)] }
    |]
    let m = Layout.Instances.layerBboxesOf polys
    Map.find (67, 20) m |> should equal (0L, 0L, 30L, 5L)
    Map.find (68,  0) m |> should equal (0L, 0L,  5L, 5L)

[<Fact>]
let ``flattenInstance returns only one instance's polygons`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.loadAsLibrary fixturePath
        let allPolys = Layout.Flatten.flatten (Rkt.OfGds.fromLibrary lib)
        let instances = Layout.Instances.enumerate lib
        instances.Length |> should equal 2
        let polys0 = Layout.Flatten.flattenInstance (Rkt.OfGds.fromLibrary lib) instances.[0].Index
        let polys1 = Layout.Flatten.flattenInstance (Rkt.OfGds.fromLibrary lib) instances.[1].Index
        // Each per-instance flatten is non-empty and strictly
        // smaller than the full flatten (we drop top-level polys
        // and the OTHER instance's subtree).
        polys0.Length |> should be (greaterThan 0)
        polys1.Length |> should be (greaterThan 0)
        (polys0.Length + polys1.Length) |> should be (lessThanOrEqualTo allPolys.Length)

[<Fact>]
let ``cim fixture has a shared physical layer with a positive per-poly gap`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.loadAsLibrary fixturePath
        let map = Layout.Instances.layerPolyBboxesByInstance lib
        let instances = Layout.Instances.enumerate lib
        instances.Length |> should equal 2
        let i0 = instances.[0].Index
        let i1 = instances.[1].Index
        let lay0 = Map.find i0 map
        let lay1 = Map.find i1 map
        let shared =
            lay0
            |> Map.toSeq
            |> Seq.choose (fun (k, _) ->
                if Map.containsKey k lay1 then Some k else None)
            |> Seq.toList
        shared |> List.length |> should be (greaterThan 0)
        let bboxGap (a1, b1, a2, b2) (c1, d1, c2, d2) =
            let xGap =
                if a2 < c1 then c1 - a2
                elif c2 < a1 then a1 - c2
                else 0L
            let yGap =
                if b2 < d1 then d1 - b2
                elif d2 < b1 then b1 - d2
                else 0L
            xGap > 0L || yGap > 0L
        // The per-layer union-bbox approach used to fail here:
        // two side-by-side instances always have overlapping
        // per-layer unions even when actual silicon footprints are
        // separated. The per-poly bboxes restore the precondition
        // the dimension overlay needs.
        let positiveGap =
            shared
            |> List.exists (fun key ->
                let (l, dt) = key
                if Layout.Layer.isNonPhysical l dt then false
                else
                    let arr0 = lay0.[key]
                    let arr1 = lay1.[key]
                    arr0
                    |> Array.exists (fun s ->
                        arr1 |> Array.exists (fun n -> bboxGap s n)))
        positiveGap |> should equal true

[<Fact>]
let ``physical bboxes overlap in cim fixture (transistor abutment)`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        // The cim_reram_4t2r_wip.mag fixture is two transistor
        // stacks placed to ABUT — diff/tap is shared between them
        // by design. So their physical bboxes overlap on x (≈1.5
        // µm of x-overlap, full y-overlap). The dim overlay
        // correctly classifies them as "no orthogonal adjacency"
        // and draws no arrows. To exercise the live overlay,
        // drag one cell clear of the other and arrows appear.
        let lib, _ = Layout.LayoutLoader.loadAsLibrary fixturePath
        let map = Layout.Instances.physicalBboxesByInstance lib
        let bbs = map |> Map.toList |> List.map snd
        bbs.Length |> should equal 2
        match bbs with
        | [(a1, _, a2, _); (c1, _, c2, _)] ->
            let xDisjoint = a2 < c1 || c2 < a1
            xDisjoint |> should equal false
        | _ -> failwith "expected exactly two instances"

[<Fact>]
let ``rotate90 around origin sends (10,0) to (0,10) at fixture instance`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.loadAsLibrary fixturePath
        let instances = Layout.Instances.enumerate lib
        instances.Length |> should equal 2
        // Move the first instance's origin to (10, 0) so we can
        // assert what 90° CCW rotation around the world origin does.
        let pickIdx = instances.[0].Index
        let lib0 = Layout.Instances.translateSelection
                       lib (Set.singleton pickIdx)
                       (10L - instances.[0].Sref.Origin.X)
                       (0L  - instances.[0].Sref.Origin.Y)
        let after =
            Layout.Instances.rotate90Selection
                lib0 (Set.singleton pickIdx) (0L, 0L)
        let updated =
            Layout.Instances.enumerate after
            |> Array.find (fun i -> i.Index = pickIdx)
        // (10, 0) → (0, 10) for R = [[0,-1],[1,0]] · (10,0) = (0,10)
        updated.Sref.Origin.X |> should equal 0L
        updated.Sref.Origin.Y |> should equal 10L

[<Fact>]
let ``mirrorX flips Y origin around pivot`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.loadAsLibrary fixturePath
        let instances = Layout.Instances.enumerate lib
        let pickIdx = instances.[0].Index
        let lib0 = Layout.Instances.translateSelection
                       lib (Set.singleton pickIdx)
                       (0L - instances.[0].Sref.Origin.X)
                       (50L - instances.[0].Sref.Origin.Y)
        let after =
            Layout.Instances.mirrorXSelection
                lib0 (Set.singleton pickIdx) (0L, 0L)
        let updated =
            Layout.Instances.enumerate after
            |> Array.find (fun i -> i.Index = pickIdx)
        updated.Sref.Origin.X |> should equal 0L
        updated.Sref.Origin.Y |> should equal -50L

[<Fact>]
let ``snap helper handles negative deltas symmetrically`` () =
    let units : Rekolektion.Viz.Core.Rkt.Types.Units = { DbuNm = 1; UuUm = 1 }
    let step = Snap.gridDbu units Snap.sky130MfgGridNm
    step |> should equal 5L
    Snap.snapCoord step 7L  |> should equal 5L
    Snap.snapCoord step 8L  |> should equal 10L
    Snap.snapCoord step (-7L) |> should equal -5L
    Snap.snapCoord step (-8L) |> should equal -10L
