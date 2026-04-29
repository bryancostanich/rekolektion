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
        (bounds: float * float)
        : Matrix4x4 =
    let w, h = bounds
    let aspect = float32 (w / max h 1.0)
    // Camera at 1.5× extent from target — close enough that
    // perspective parallax across the bbox is visually obvious
    // (the near edge is ~3× the size of the far edge at this
    // distance with 60° FOV). At 2.5× radius the perspective ratio
    // dropped to ~1.27× — barely perceptible, which is why the
    // rendering looked like a flat 2D image rotating: there were
    // no perspective cues differentiating angles.
    let radius = float32 (extent * 1.5)
    let yaw = deg2rad yawDeg
    let pitch = deg2rad pitchDeg
    let camOffset =
        Vector3(
            radius * MathF.Cos(pitch) * MathF.Sin(yaw),
            radius * MathF.Cos(pitch) * MathF.Cos(yaw),
            radius * MathF.Sin(pitch))
    // 60° vertical FOV at zoom=1 — typical for CAD/3D viewers.
    // Wider than the previous 30° so perspective foreshortening
    // is unambiguous.
    let fovY = deg2rad (60.0 / max zoom 0.05)
    // Near tight enough to maximize depth-buffer precision; far
    // generous enough to never clip the bbox.
    let near = max 0.01f (radius * 0.05f)
    let far  = radius * 10.0f
    let proj = Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, near, far)
    let view = Matrix4x4.CreateLookAt(target + camOffset, target, Vector3.UnitZ)
    view * proj

let toFloatArray (m: Matrix4x4) : float32 array =
    [| m.M11; m.M12; m.M13; m.M14
       m.M21; m.M22; m.M23; m.M24
       m.M31; m.M32; m.M33; m.M34
       m.M41; m.M42; m.M43; m.M44 |]
