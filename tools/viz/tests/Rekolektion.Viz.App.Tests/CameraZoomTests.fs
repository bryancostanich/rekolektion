module Rekolektion.Viz.App.Tests.CameraZoomTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Canvas3D

// ─────────────────────────────────────────────────────────────────
// cameraStateForZoom — three-stage zoom verification.
//
// Regimes:
//   zoom < 1          →  dolly OUT  (radius = 1.5*extent / zoom, fov=60°)
//   1 ≤ zoom ≤ 30     →  TELEPHOTO  (radius = 1.5*extent, fov = 60°/zoom)
//   zoom > 30         →  dolly IN   (radius = 1.5*extent * 30/zoom, fov=2°)
//
// Lower floor on zoom is 0.05. Past `fovFloorZoom = 30` the FOV is
// held at 2° so the perspective matrix stays well-conditioned.
//
// These tests are float math, so use a tolerance.
// ─────────────────────────────────────────────────────────────────

let private extent = 100.0
let private radius1x : float32 = 150.0f   // = 1.5 * extent

let private nearlyEq (tol: float32) (expected: float32) (actual: float32) =
    abs (expected - actual) |> should be (lessThanOrEqualTo tol)

let private deg2rad (d: float) : float32 = float32 (d * System.Math.PI / 180.0)

// --- Regime boundaries -------------------------------------------

[<Fact>]
let ``zoom = 1 (reference framing): radius = 1.5*extent, fovY = 60°`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 1.0 extent
    s.Radius |> nearlyEq 0.001f radius1x
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 60.0)

[<Fact>]
let ``zoom = 0.5 (dolly out): radius doubles, fov stays at 60°`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 0.5 extent
    s.Radius |> nearlyEq 0.001f (radius1x * 2.0f)
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 60.0)

[<Fact>]
let ``zoom = 0.05 (lower clamp): radius = 1.5*extent / 0.05 = 30x`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 0.05 extent
    s.Radius |> nearlyEq 0.01f (radius1x * 20.0f)   // 1.5*100/0.05 = 3000
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 60.0)

[<Fact>]
let ``zoom = 0.001 clamps to 0.05 (no further dolly out)`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 0.001 extent
    // Clamped result should match the 0.05 case exactly.
    let s05 = Matrix4x4Helpers.cameraStateForZoom 0.05 extent
    s.Radius |> should equal s05.Radius
    s.FovYRad |> should equal s05.FovYRad

[<Fact>]
let ``zoom = 2 (telephoto): radius held at 1.5*extent, fov = 30°`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 2.0 extent
    s.Radius |> nearlyEq 0.001f radius1x
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 30.0)

[<Fact>]
let ``zoom = 10 (telephoto): radius held, fov = 6°`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 10.0 extent
    s.Radius |> nearlyEq 0.001f radius1x
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 6.0)

[<Fact>]
let ``zoom = 30 (FOV floor): radius held, fov = 2°`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 30.0 extent
    s.Radius |> nearlyEq 0.001f radius1x
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 2.0)

[<Fact>]
let ``zoom = 60 (past floor, dolly in): radius halves, fov pinned at 2°`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 60.0 extent
    // dollyIn = 60/30 = 2 → radius = 150/2 = 75
    s.Radius |> nearlyEq 0.001f (radius1x / 2.0f)
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 2.0)

[<Fact>]
let ``zoom = 300 (deep dolly in): radius/10, fov still 2°`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 300.0 extent
    // dollyIn = 300/30 = 10 → radius = 150/10 = 15
    s.Radius |> nearlyEq 0.001f (radius1x / 10.0f)
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 2.0)

[<Fact>]
let ``zoom = 1000 (sanity cap): radius = 1.5*extent / (1000/30)`` () =
    let s = Matrix4x4Helpers.cameraStateForZoom 1000.0 extent
    let expected = radius1x * (30.0f / 1000.0f)
    s.Radius |> nearlyEq 0.001f expected
    s.FovYRad |> nearlyEq 0.0001f (deg2rad 2.0)

// --- Monotonicity properties -------------------------------------

[<Fact>]
let ``radius is monotonically non-increasing in zoom over [0.05, 1000]`` () =
    let samples = [ 0.05; 0.1; 0.5; 1.0; 2.0; 10.0; 30.0; 60.0; 300.0; 1000.0 ]
    let radii =
        samples
        |> List.map (fun z -> (Matrix4x4Helpers.cameraStateForZoom z extent).Radius)
    let pairs = List.pairwise radii
    pairs
    |> List.forall (fun (a, b) -> b <= a)
    |> should equal true

[<Fact>]
let ``fovY is monotonically non-increasing in zoom over [0.05, 1000]`` () =
    let samples = [ 0.05; 0.1; 0.5; 1.0; 2.0; 10.0; 30.0; 60.0; 300.0; 1000.0 ]
    let fovs =
        samples
        |> List.map (fun z -> (Matrix4x4Helpers.cameraStateForZoom z extent).FovYRad)
    let pairs = List.pairwise fovs
    pairs
    |> List.forall (fun (a, b) -> b <= a)
    |> should equal true

// --- Scaling property --------------------------------------------

[<Fact>]
let ``radius scales linearly with extent at fixed zoom`` () =
    let s1 = Matrix4x4Helpers.cameraStateForZoom 1.0 100.0
    let s2 = Matrix4x4Helpers.cameraStateForZoom 1.0 250.0
    // 250 / 100 = 2.5
    let ratio = s2.Radius / s1.Radius
    nearlyEq 0.001f 2.5f ratio
