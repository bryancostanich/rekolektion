module Rekolektion.Viz.App.Canvas3D.Matrix4x4Helpers

open System
open System.Numerics

let private deg2rad d = float32 (d * Math.PI / 180.0)

let buildOrbitMvp (yawDeg: float) (pitchDeg: float) (zoom: float) (bounds: float * float) : Matrix4x4 =
    let w, h = bounds
    let aspect = float32 (w / max h 1.0)
    let proj = Matrix4x4.CreateOrthographic(80.0f / float32 zoom * aspect, 80.0f / float32 zoom, 0.1f, 1000.0f)
    let radius = 100.0f / float32 zoom
    let yaw = deg2rad yawDeg
    let pitch = deg2rad pitchDeg
    let camX = radius * MathF.Cos(pitch) * MathF.Sin(yaw)
    let camY = radius * MathF.Cos(pitch) * MathF.Cos(yaw)
    let camZ = radius * MathF.Sin(pitch)
    let view = Matrix4x4.CreateLookAt(Vector3(camX, camY, camZ), Vector3.Zero, Vector3.UnitZ)
    view * proj

let toFloatArray (m: Matrix4x4) : float32 array =
    [| m.M11; m.M12; m.M13; m.M14
       m.M21; m.M22; m.M23; m.M24
       m.M31; m.M32; m.M33; m.M34
       m.M41; m.M42; m.M43; m.M44 |]
