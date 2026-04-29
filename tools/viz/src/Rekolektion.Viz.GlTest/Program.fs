module Rekolektion.Viz.GlTest.Program

// Headless OpenGL regression check for the 3D rendering pipeline used
// by Rekolektion.Viz.App.Canvas3D.StackCanvasControl. Renders a unit
// cube with 6 distinct face colors via the SAME MVP construction +
// shader pipeline as the live app. Runs in a hidden GLFW window via
// Silk.NET.Windowing — gives us a real GPU GL context so we can verify
// rendering without booting Avalonia (Avalonia.Headless does not fire
// OnOpenGlInit, so screenshots from the Avalonia-headless path can't
// validate the 3D pipeline).
//
// History: a transpose-flag bug in StackCanvasControl was caught here
// — with transpose=true the cube rendered as a 2D billboard rotating
// in screen space; with transpose=false it renders as a true 3D box.
// Keep this tool around so any future MVP changes can be validated
// against a known-good reference shape (cube with predictable face
// colors at predictable yaw/pitch).
//
// Usage:
//   dotnet run --project tools/viz/src/Rekolektion.Viz.GlTest -- \
//       --output /tmp/cube.png --yaw 30 --pitch 20

open System
open System.Numerics
open Silk.NET.OpenGL
open Silk.NET.Windowing
open Silk.NET.Maths
open SkiaSharp

type Args = {
    Output: string
    Yaw   : float
    Pitch : float
    Width : int
    Height: int
}

let private defaults = {
    Output = "/tmp/cube_test.png"
    Yaw    = 30.0
    Pitch  = 20.0
    Width  = 800
    Height = 600
}

let rec private parseArgs (acc: Args) (rest: string list) : Args =
    match rest with
    | [] -> acc
    | "--output" :: v :: tail -> parseArgs { acc with Output = v } tail
    | "--yaw"    :: v :: tail -> parseArgs { acc with Yaw = float v } tail
    | "--pitch"  :: v :: tail -> parseArgs { acc with Pitch = float v } tail
    | "--width"  :: v :: tail -> parseArgs { acc with Width = int v } tail
    | "--height" :: v :: tail -> parseArgs { acc with Height = int v } tail
    | unknown :: _ -> failwithf "unknown arg: %s" unknown

// Mirrors Matrix4x4Helpers.buildOrbitMvp in the live app.
let private deg2rad d = float32 (d * Math.PI / 180.0)

let private buildOrbitMvp (yawDeg: float) (pitchDeg: float) (target: Vector3) (extent: float) (w: int) (h: int) : Matrix4x4 =
    let aspect = float32 (float w / float (max h 1))
    let radius = float32 (extent * 1.5)
    let yaw = deg2rad yawDeg
    let pitch = deg2rad pitchDeg
    let camOffset =
        Vector3(
            radius * MathF.Cos(pitch) * MathF.Sin(yaw),
            radius * MathF.Cos(pitch) * MathF.Cos(yaw),
            radius * MathF.Sin(pitch))
    let fovY = deg2rad 60.0
    let near = max 0.01f (radius * 0.05f)
    let far  = radius * 10.0f
    let proj = Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, near, far)
    let view = Matrix4x4.CreateLookAt(target + camOffset, target, Vector3.UnitZ)
    view * proj

let private toFloatArray (m: Matrix4x4) : float32 array =
    [| m.M11; m.M12; m.M13; m.M14
       m.M21; m.M22; m.M23; m.M24
       m.M31; m.M32; m.M33; m.M34
       m.M41; m.M42; m.M43; m.M44 |]

// 6 faces × 4 vertices × (pos.xyz, color.rgb) = 24 vertices.
// 12 triangles × 3 indices = 36 indices.
let private cubeVerts () : float32 array * uint32 array =
    let v = ResizeArray<float32>()
    let i = ResizeArray<uint32>()
    let face (corners: (float32*float32*float32) list) (r: float32) (g: float32) (b: float32) =
        let baseIdx = uint32 (v.Count / 6)
        for (x, y, z) in corners do
            v.Add x; v.Add y; v.Add z
            v.Add r; v.Add g; v.Add b
        i.Add(baseIdx + 0u); i.Add(baseIdx + 1u); i.Add(baseIdx + 2u)
        i.Add(baseIdx + 0u); i.Add(baseIdx + 2u); i.Add(baseIdx + 3u)
    // +X = red, -X = orange, +Y = green, -Y = blue, +Z = yellow, -Z = gray
    face [( 1.0f, -1.0f, -1.0f); ( 1.0f,  1.0f, -1.0f); ( 1.0f,  1.0f,  1.0f); ( 1.0f, -1.0f,  1.0f)] 1.0f 0.2f 0.2f
    face [(-1.0f, -1.0f,  1.0f); (-1.0f,  1.0f,  1.0f); (-1.0f,  1.0f, -1.0f); (-1.0f, -1.0f, -1.0f)] 1.0f 0.6f 0.0f
    face [( 1.0f,  1.0f,  1.0f); ( 1.0f,  1.0f, -1.0f); (-1.0f,  1.0f, -1.0f); (-1.0f,  1.0f,  1.0f)] 0.2f 0.8f 0.2f
    face [(-1.0f, -1.0f,  1.0f); (-1.0f, -1.0f, -1.0f); ( 1.0f, -1.0f, -1.0f); ( 1.0f, -1.0f,  1.0f)] 0.2f 0.4f 1.0f
    face [(-1.0f, -1.0f,  1.0f); ( 1.0f, -1.0f,  1.0f); ( 1.0f,  1.0f,  1.0f); (-1.0f,  1.0f,  1.0f)] 1.0f 1.0f 0.2f
    face [(-1.0f, -1.0f, -1.0f); (-1.0f,  1.0f, -1.0f); ( 1.0f,  1.0f, -1.0f); ( 1.0f, -1.0f, -1.0f)] 0.5f 0.5f 0.5f
    v.ToArray(), i.ToArray()

let private vsSrc = """
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aColor;
uniform mat4 uMVP;
out vec3 vColor;
out vec3 vWorldPos;
void main() {
    gl_Position = uMVP * vec4(aPos, 1.0);
    vColor = aColor;
    vWorldPos = aPos;
}
"""

let private fsSrc = """
#version 330 core
in vec3 vColor;
in vec3 vWorldPos;
out vec4 FragColor;
uniform vec3 uLightDir;
void main() {
    vec3 n = normalize(cross(dFdx(vWorldPos), dFdy(vWorldPos)));
    float lambert = max(dot(n, -uLightDir), 0.0);
    float intensity = 0.20 + 0.80 * lambert;
    FragColor = vec4(vColor * intensity, 1.0);
}
"""

let private compileShader (gl: GL) (src: string) (kind: ShaderType) : uint32 =
    let s = gl.CreateShader kind
    gl.ShaderSource(s, src)
    gl.CompileShader s
    let mutable status = 0
    gl.GetShader(s, ShaderParameterName.CompileStatus, &status)
    if status = 0 then
        let log = gl.GetShaderInfoLog s
        failwithf "shader %A compile failed: %s" kind log
    s

let private linkProgram (gl: GL) (vs: uint32) (fs: uint32) : uint32 =
    let p = gl.CreateProgram()
    gl.AttachShader(p, vs)
    gl.AttachShader(p, fs)
    gl.LinkProgram p
    let mutable status = 0
    gl.GetProgram(p, ProgramPropertyARB.LinkStatus, &status)
    if status = 0 then
        let log = gl.GetProgramInfoLog p
        failwithf "program link failed: %s" log
    p

let private renderToPng (args: Args) =
    let mutable opts = WindowOptions.Default
    opts.IsVisible <- false
    opts.Size <- Vector2D<int>(args.Width, args.Height)
    opts.Title <- "rekolektion-viz-gltest"
    let api = opts.API
    let mutable apiCopy = api
    apiCopy.API <- ContextAPI.OpenGL
    apiCopy.Profile <- ContextProfile.Core
    apiCopy.Version <- APIVersion(3, 3)
    opts.API <- apiCopy
    use win = Window.Create opts
    win.Initialize ()
    let gl = win.CreateOpenGL()

    // Explicit FBO with color + depth attachments. GLFW's default
    // framebuffer doesn't reliably include depth on hidden windows;
    // without depth, all 6 cube faces render in draw-order and the
    // back faces paint over the front. Explicit FBO guarantees
    // depth works.
    let fbo = gl.GenFramebuffer()
    let colorRbo = gl.GenRenderbuffer()
    let depthRbo = gl.GenRenderbuffer()
    gl.BindFramebuffer(GLEnum.Framebuffer, fbo)
    gl.BindRenderbuffer(GLEnum.Renderbuffer, colorRbo)
    gl.RenderbufferStorage(GLEnum.Renderbuffer, GLEnum.Rgba8, uint32 args.Width, uint32 args.Height)
    gl.FramebufferRenderbuffer(GLEnum.Framebuffer, GLEnum.ColorAttachment0, GLEnum.Renderbuffer, colorRbo)
    gl.BindRenderbuffer(GLEnum.Renderbuffer, depthRbo)
    gl.RenderbufferStorage(GLEnum.Renderbuffer, GLEnum.DepthComponent24, uint32 args.Width, uint32 args.Height)
    gl.FramebufferRenderbuffer(GLEnum.Framebuffer, GLEnum.DepthAttachment, GLEnum.Renderbuffer, depthRbo)
    let fboStatus = gl.CheckFramebufferStatus GLEnum.Framebuffer
    if fboStatus <> GLEnum.FramebufferComplete then
        failwithf "FBO incomplete: %A" fboStatus

    let vs = compileShader gl vsSrc ShaderType.VertexShader
    let fs = compileShader gl fsSrc ShaderType.FragmentShader
    let prog = linkProgram gl vs fs
    gl.DeleteShader vs
    gl.DeleteShader fs

    let vao = gl.GenVertexArray()
    let vbo = gl.GenBuffer()
    let ebo = gl.GenBuffer()
    gl.BindVertexArray vao

    let verts, indices = cubeVerts ()
    gl.BindBuffer(GLEnum.ArrayBuffer, vbo)
    gl.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(verts), GLEnum.StaticDraw)
    gl.BindBuffer(GLEnum.ElementArrayBuffer, ebo)
    gl.BufferData(GLEnum.ElementArrayBuffer, ReadOnlySpan<uint32>(indices), GLEnum.StaticDraw)

    let strideBytes = uint32 (6 * sizeof<float32>)
    gl.EnableVertexAttribArray 0u
    gl.VertexAttribPointer(0u, 3, GLEnum.Float, false, strideBytes, nativeint 0)
    gl.EnableVertexAttribArray 1u
    gl.VertexAttribPointer(1u, 3, GLEnum.Float, false, strideBytes, nativeint (3 * sizeof<float32>))

    gl.Viewport(0, 0, uint32 args.Width, uint32 args.Height)
    gl.Enable GLEnum.DepthTest
    gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f)
    gl.Clear(uint32 (GLEnum.ColorBufferBit ||| GLEnum.DepthBufferBit))
    gl.UseProgram prog

    let mvp = buildOrbitMvp args.Yaw args.Pitch Vector3.Zero 2.5 args.Width args.Height
    let mvpArr = toFloatArray mvp
    let loc = gl.GetUniformLocation(prog, "uMVP")
    // transpose=FALSE for System.Numerics matrices — see comment in
    // StackCanvasControl.fs for why.
    gl.UniformMatrix4(loc, 1u, false, ReadOnlySpan<float32>(mvpArr))

    let yawRad = deg2rad args.Yaw
    let pitchRad = deg2rad args.Pitch
    let camForward = Vector3(
                        -MathF.Cos(pitchRad) * MathF.Sin(yawRad),
                        -MathF.Cos(pitchRad) * MathF.Cos(yawRad),
                        -MathF.Sin(pitchRad))
    let lightLoc = gl.GetUniformLocation(prog, "uLightDir")
    gl.Uniform3(lightLoc, camForward)

    gl.DrawElements(GLEnum.Triangles, uint32 indices.Length, GLEnum.UnsignedInt, IntPtr.Zero.ToPointer())
    let err = gl.GetError()
    if err <> GLEnum.NoError then
        eprintfn "[gltest] GL error after draw: %A" err

    let pixCount = args.Width * args.Height * 4
    let pixels = Array.zeroCreate<byte> pixCount
    gl.ReadPixels(0, 0, uint32 args.Width, uint32 args.Height, GLEnum.Rgba, GLEnum.UnsignedByte, Span<byte>(pixels))

    // OpenGL rows are bottom-up; SkiaSharp expects top-down. Flip.
    let stride = args.Width * 4
    let flipped = Array.zeroCreate<byte> pixCount
    for y in 0 .. args.Height - 1 do
        Array.blit pixels (y * stride) flipped ((args.Height - 1 - y) * stride) stride

    let info = SKImageInfo(args.Width, args.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
    use bmp = new SKBitmap(info)
    System.Runtime.InteropServices.Marshal.Copy(flipped, 0, bmp.GetPixels(), pixCount)
    use img = SKImage.FromBitmap bmp
    use data = img.Encode(SKEncodedImageFormat.Png, 100)
    use stream = System.IO.File.OpenWrite args.Output
    data.SaveTo stream

    printfn "wrote %s (%dx%d, yaw=%.1f, pitch=%.1f)" args.Output args.Width args.Height args.Yaw args.Pitch

[<EntryPoint>]
let main argv =
    let args = parseArgs defaults (List.ofArray argv)
    renderToPng args
    0
