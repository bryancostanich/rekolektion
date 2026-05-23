module Rekolektion.Viz.App.Canvas3D.Matrix4x4Helpers

open System
open System.Numerics

let private deg2rad d = float32 (d * Math.PI / 180.0)

/// Build a perspective MVP that frames a sphere of `extent` diameter
/// centered on `target`. Camera orbits at `radius = extent * 2.5`,
/// giving a comfortable FOV without near-clipping the closest face
/// of the bbox. Yaw/pitch are in degrees relative to the standard
/// "+Y is forward, +Z is up" basis: pitch=0 puts the camera at the
/// horizon, pitch=90 directly above. Zoom narrows/widens the FOV
/// (zoom>1 zooms in).
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
    // Camera at 1.5× extent from target at zoom=1 — close enough
    // that perspective parallax across the bbox is visually obvious
    // (the near edge is ~3× the size of the far edge with 60° FOV).
    // Wheel zoom scales RADIUS, not FOV: zoom>1 pulls the camera
    // closer along the same view ray, zoom<1 pushes it back. FOV
    // stays at a comfortable 60° at every zoom level, so a heavily
    // zoomed-in view doesn't degenerate into a 1°-FOV telephoto
    // cone. (Previous design narrowed FOV with zoom — fine for
    // small zoom values on similar-sized cells, but on big cells
    // the user had to scroll the wheel hard to compensate, driving
    // FOV down to a few degrees and producing pathological
    // perspective distortion.)
    let radius =
        float32 (extent * 1.5 / max zoom 0.05)
    let yaw = deg2rad yawDeg
    let pitch = deg2rad pitchDeg
    let camOffset =
        Vector3(
            radius * MathF.Cos(pitch) * MathF.Sin(yaw),
            radius * MathF.Cos(pitch) * MathF.Cos(yaw),
            radius * MathF.Sin(pitch))
    let camPos = target + camOffset
    // 60° vertical FOV — fixed regardless of zoom.
    let fovY = deg2rad 60.0
    // Bbox-aware near/far. Tying both to `radius` (the old
    // formula) coupled the frustum depth to zoom: on a deep zoom
    // the far plane could come in front of the macro's far edge
    // and clip it. Instead, project the 8 world-space AABB
    // corners onto the view direction and bracket the frustum to
    // that interval (plus a 5% pad). Uses `bboxCenter`, not
    // `target` — the user's pan drifts target away from the bbox
    // center, but the bbox itself stays put in world coords.
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
    // Extreme pad — pushes far well past anything the silicon bbox
    // sees, so auxiliary 3D content (labels, ruler text, axis
    // markers, ratlines extending to off-bbox labels, mesh-extruder
    // overshoot) can't get sliced. The 24-bit depth buffer still
    // has plenty of precision at µm scale even with 3× span.
    let pad = max 50.0f (span * 2.0f)
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
