module Rekolektion.Viz.App.Canvas3D.Matrix4x4Helpers

open System
open System.Numerics

let private deg2rad d = float32 (d * Math.PI / 180.0)

/// Build a perspective MVP that frames a sphere of `extent` diameter
/// centered on `target`. Camera orbits at `radius = extent * 1.5` at
/// zoom=1 (close enough that perspective parallax across the bbox
/// is visually obvious). Yaw/pitch are in degrees relative to the
/// standard "+Y is forward, +Z is up" basis: pitch=0 puts the camera
/// at the horizon, pitch=90 directly above.
///
/// Zoom is split into three regimes to avoid the camera tunnelling
/// into the macro AABB on big cells while still allowing unbounded
/// "look closer":
///   zoom < 1                →  dolly OUT  (radius grows, FOV 60°)
///   zoom = 1                →  reference framing
///   1 ≤ zoom ≤ fovFloorZoom →  TELEPHOTO (radius held, FOV = 60°/zoom)
///   zoom > fovFloorZoom     →  dolly IN   (FOV held at fovFloor,
///                                          radius shrinks toward `target`)
/// `fovFloorZoom = 60° / fovFloorDeg`. The dolly-in stage drives the
/// camera toward `target` (the user's pan-target), not bboxCenter, so
/// the dive goes where the user is actually looking. Earlier "always
/// dolly toward bboxCenter" design put the camera inside the AABB
/// sphere for any zoom > ~1.5 on a big cell — wide-span frustums,
/// wrecked depth precision, "wiggy" view of whatever single voxel was
/// under the camera. Telephoto-then-dolly keeps the camera outside
/// the macro until the user has panned to choose a target.
///
/// Perspective (rather than orthographic) matches what users see in
/// MeshLab / Preview / Blender when opening a GLB — far things look
/// smaller, depth is unambiguous, and asymmetric bboxes don't
/// produce the parallax-free "everything sheared" look that the ortho
/// renderer was producing at certain camera angles.
let buildOrbitMvp
        (yawDeg: float)
        (pitchDeg: float)
        (zoom: float)
        (target: Vector3)
        (extent: float)
        (bboxCenter: Vector3)
        (bboxHalf: Vector3)
        (worldOffset: Vector3)
        (bounds: float * float)
        : Matrix4x4 =
    let w, h = bounds
    let aspect = float32 (w / max h 1.0)
    // Three-stage zoom (see header comment).
    let fovFloorDeg = 2.0
    let fovFloorZoom = 60.0 / fovFloorDeg   // = 30
    let z = max zoom 0.05
    let dollyOutZoom = min z 1.0                  // < 1 → camera pulls back
    let telephotoZoom = max 1.0 (min z fovFloorZoom)
    let dollyInZoom = max 1.0 (z / fovFloorZoom)  // > 1 → camera dives toward target
    let radius =
        float32 (extent * 1.5 / dollyOutZoom / dollyInZoom)
    let yaw = deg2rad yawDeg
    let pitch = deg2rad pitchDeg
    let camOffset =
        Vector3(
            radius * MathF.Cos(pitch) * MathF.Sin(yaw),
            radius * MathF.Cos(pitch) * MathF.Cos(yaw),
            radius * MathF.Sin(pitch))
    let camPos = target + camOffset
    let fovY = deg2rad (60.0 / telephotoZoom)
    // Bbox-aware near/far. Project the 8 world-space AABB corners
    // onto the view direction and bracket the frustum to that
    // interval (plus a 10% pad — generous enough for ratline and
    // label overshoot at the bbox boundary without trashing depth
    // precision the way the old 200% pad did). Uses `bboxCenter`,
    // not `target` — the user's pan drifts target away from the
    // bbox center, but the bbox itself stays put in world coords.
    let forward =
        if radius > 0f then -camOffset / radius
        else Vector3.UnitY
    let mutable minD = System.Single.MaxValue
    let mutable maxD = System.Single.MinValue
    for sx in [| -1.0f; 1.0f |] do
        for sy in [| -1.0f; 1.0f |] do
            for sz in [| -1.0f; 1.0f |] do
                let corner =
                    bboxCenter
                    + Vector3(sx * bboxHalf.X, sy * bboxHalf.Y, sz * bboxHalf.Z)
                let d = Vector3.Dot(corner - camPos, forward)
                if d < minD then minD <- d
                if d > maxD then maxD <- d
    let span = maxD - minD
    let pad = max 5.0f (span * 0.1f)
    let near = max 0.01f (minD - pad)
    let far  = maxD + pad
    let proj = Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, near, far)
    let view = Matrix4x4.CreateLookAt(camPos, target, Vector3.UnitZ)
    // worldOffset translates all geometry by -worldOffset before the
    // view/projection chain. F# sets target/bboxCenter to post-shift
    // coords (typically (0, 0, zMid)) so the camera looks at the
    // shifted geometry. Net effect: cells render at world origin
    // regardless of where they were originally authored, eliminating
    // the "different cells appear at different viewport positions"
    // divergence.
    let modelShift = Matrix4x4.CreateTranslation(-worldOffset)
    modelShift * view * proj

let toFloatArray (m: Matrix4x4) : float32 array =
    [| m.M11; m.M12; m.M13; m.M14
       m.M21; m.M22; m.M23; m.M24
       m.M31; m.M32; m.M33; m.M34
       m.M41; m.M42; m.M43; m.M44 |]
