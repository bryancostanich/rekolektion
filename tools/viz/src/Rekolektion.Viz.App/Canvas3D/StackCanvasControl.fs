module Rekolektion.Viz.App.Canvas3D.StackCanvasControl

open System
open Avalonia
open Avalonia.OpenGL
open Avalonia.OpenGL.Controls
open Silk.NET.OpenGL
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Render.Mesh

/// Avalonia OpenGlControlBase loads a GL context for us; we use
/// Silk.NET.OpenGL.GL on top of it for typed bindings. The control
/// owns one VBO + one shader program. Mesh changes when Library
/// changes; toggle changes are handled by per-vertex layer visibility
/// uniforms.
type StackCanvasControl() =
    inherit OpenGlControlBase()

    let mutable gl : GL option = None
    let mutable vbo : uint32 = 0u
    let mutable ebo : uint32 = 0u
    let mutable vao : uint32 = 0u
    let mutable program : uint32 = 0u
    let mutable indexCount : int = 0
    let mutable yawDeg = 30.0
    let mutable pitchDeg = -25.0
    let mutable zoom = 1.0

    static member val LibraryProperty : StyledProperty<Library option> =
        AvaloniaProperty.Register<StackCanvasControl, Library option>("Library", None)
        with get
    static member val ToggleProperty : StyledProperty<Visibility.ToggleState> =
        AvaloniaProperty.Register<StackCanvasControl, Visibility.ToggleState>("Toggle", Visibility.empty)
        with get

    member this.Library
        with get() : Library option = this.GetValue(StackCanvasControl.LibraryProperty)
        and set(v: Library option) = this.SetValue(StackCanvasControl.LibraryProperty, v) |> ignore

    member this.Toggle
        with get() : Visibility.ToggleState = this.GetValue(StackCanvasControl.ToggleProperty)
        and set(v: Visibility.ToggleState) = this.SetValue(StackCanvasControl.ToggleProperty, v) |> ignore

    member this.SetCamera (yaw: float) (pitch: float) (z: float) =
        yawDeg <- yaw
        pitchDeg <- pitch
        zoom <- z
        this.RequestNextFrameRendering()

    override this.OnPropertyChanged e =
        base.OnPropertyChanged e
        if e.Property = StackCanvasControl.LibraryProperty || e.Property = StackCanvasControl.ToggleProperty then
            this.RequestNextFrameRendering()

    override this.OnOpenGlInit(gli) =
        let g = GL.GetApi(fun n -> gli.GetProcAddress(n))
        gl <- Some g
        vbo <- g.GenBuffer()
        ebo <- g.GenBuffer()
        vao <- g.GenVertexArray()
        // Minimal vertex shader (position + color + visibility) + frag shader
        let vsSrc = "
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aColor;
            layout(location=2) in float aLayerVisible;
            uniform mat4 uMVP;
            out vec3 vColor;
            out float vVis;
            void main() {
                gl_Position = uMVP * vec4(aPos, 1.0);
                vColor = aColor;
                vVis = aLayerVisible;
            }
        "
        let fsSrc = "
            #version 330 core
            in vec3 vColor;
            in float vVis;
            out vec4 FragColor;
            void main() {
                if (vVis < 0.5) discard;
                FragColor = vec4(vColor, 1.0);
            }
        "
        let compile (src: string) (kind: ShaderType) =
            let s = g.CreateShader(kind)
            g.ShaderSource(s, src)
            g.CompileShader(s)
            let mutable status = 0
            g.GetShader(s, ShaderParameterName.CompileStatus, &status)
            if status = 0 then
                let log = g.GetShaderInfoLog s
                eprintfn "[viz3d] shader compile failed (%A): %s" kind log
            s
        let vs = compile vsSrc ShaderType.VertexShader
        let fs = compile fsSrc ShaderType.FragmentShader
        program <- g.CreateProgram()
        g.AttachShader(program, vs)
        g.AttachShader(program, fs)
        g.LinkProgram(program)
        let mutable linkStatus = 0
        g.GetProgram(program, ProgramPropertyARB.LinkStatus, &linkStatus)
        if linkStatus = 0 then
            let log = g.GetProgramInfoLog program
            eprintfn "[viz3d] program link failed: %s" log
        g.DeleteShader(vs)
        g.DeleteShader(fs)

    override this.OnOpenGlDeinit(_gli) =
        match gl with
        | Some g ->
            g.DeleteBuffer(vbo)
            g.DeleteBuffer(ebo)
            g.DeleteVertexArray vao
            g.DeleteProgram(program)
        | None -> ()

    override this.OnOpenGlRender(_gli, _fb) =
        match gl, this.Library with
        | Some g, Some lib ->
            let mesh = Extruder.extrude lib
            indexCount <- mesh.Indices.Length
            // Build interleaved buffer: pos(3) + color(3) + layerVisible(1) = 7 floats per vertex
            let toggle = this.Toggle
            let stride = 7
            let arr = Array.zeroCreate<float32> (mesh.Vertices.Length * stride)
            for i in 0 .. mesh.Vertices.Length - 1 do
                let v = mesh.Vertices.[i]
                let layerOpt = Layout.Layer.bySky130Number (fst v.LayerKey) (snd v.LayerKey)
                let r, gC, b =
                    match layerOpt with
                    | Some l -> float32 l.Color.R / 255.0f, float32 l.Color.G / 255.0f, float32 l.Color.B / 255.0f
                    | None -> 0.5f, 0.5f, 0.5f
                let vis = if Visibility.isLayerVisible toggle v.LayerKey then 1.0f else 0.0f
                let off = i * stride
                arr.[off]     <- v.X
                arr.[off + 1] <- v.Y
                arr.[off + 2] <- v.Z
                arr.[off + 3] <- r
                arr.[off + 4] <- gC
                arr.[off + 5] <- b
                arr.[off + 6] <- vis

            g.BindVertexArray(vao)
            g.BindBuffer(GLEnum.ArrayBuffer, vbo)
            g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(arr), GLEnum.DynamicDraw)
            g.BindBuffer(GLEnum.ElementArrayBuffer, ebo)
            g.BufferData(GLEnum.ElementArrayBuffer, ReadOnlySpan<int>(mesh.Indices), GLEnum.DynamicDraw)

            g.ClearColor(0.0f, 0.0f, 0.0f, 1.0f)
            g.Clear(uint32 (GLEnum.ColorBufferBit ||| GLEnum.DepthBufferBit))
            g.Enable(GLEnum.DepthTest)

            g.UseProgram(program)
            // Vertex attribs: stride is bytes, offset is byte offset as void*
            let strideBytes = uint32 (stride * sizeof<float32>)
            g.EnableVertexAttribArray(0u)
            g.VertexAttribPointer(0u, 3, GLEnum.Float, false, strideBytes, nativeint 0)
            g.EnableVertexAttribArray(1u)
            g.VertexAttribPointer(1u, 3, GLEnum.Float, false, strideBytes, nativeint (3 * sizeof<float32>))
            g.EnableVertexAttribArray(2u)
            g.VertexAttribPointer(2u, 1, GLEnum.Float, false, strideBytes, nativeint (6 * sizeof<float32>))

            // Build a simple MVP: orthographic projection looking down at the die,
            // then rotate by yaw/pitch.
            let mvp = Matrix4x4Helpers.buildOrbitMvp yawDeg pitchDeg zoom (this.Bounds.Width, this.Bounds.Height)
            let loc = g.GetUniformLocation(program, "uMVP")
            let mvpArr = Matrix4x4Helpers.toFloatArray mvp
            g.UniformMatrix4(loc, 1u, true, ReadOnlySpan<float32>(mvpArr))
            g.DrawElements(GLEnum.Triangles, uint32 indexCount, GLEnum.UnsignedInt, IntPtr.Zero.ToPointer())
        | _ -> ()
