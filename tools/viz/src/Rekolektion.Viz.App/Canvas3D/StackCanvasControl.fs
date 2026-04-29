module Rekolektion.Viz.App.Canvas3D.StackCanvasControl

open System
open Avalonia
open Avalonia.Input
open Avalonia.OpenGL
open Avalonia.OpenGL.Controls
open Silk.NET.OpenGL
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Render.Mesh

/// Pointer drag modes. Left = orbit (yaw/pitch). Right or middle =
/// pan (translate target in the screen-aligned plane). NoDrag means
/// no button is held.
type private DragMode =
    | NoDrag
    | OrbitDrag
    | PanDrag

/// Z exaggeration multiplier applied to vertex Z on upload.
/// 1.0 = physical SKY130 stack heights (matches the legacy GLB
/// tool). For a typical bitcell with xy ≈ 2.4×3.7 µm and Z stack
/// ≈ 3.6 µm the proportions are roughly cubic at 1.0; bumping
/// above 1.0 turns small cells into towers.
[<Literal>]
let private Z_EXAGGERATION = 1.0

/// Avalonia OpenGlControlBase loads a GL context for us; we use
/// Silk.NET.OpenGL.GL on top of it for typed bindings. The control
/// owns one VBO + one EBO + one shader program. Mesh changes when
/// Library changes; toggle changes are handled by per-vertex layer
/// visibility uniforms.
type StackCanvasControl() =
    inherit OpenGlControlBase()

    let mutable gl : GL option = None
    let mutable vbo : uint32 = 0u
    let mutable ebo : uint32 = 0u
    let mutable vao : uint32 = 0u
    let mutable program : uint32 = 0u
    let mutable indexCount : int = 0
    // Mesh upload caching. Re-extruding 400k polygons (production
    // SRAM macro) every frame would saturate the CPU and drop the
    // canvas to <1 fps. We extrude + upload only when the input
    // (FlatPolygons) or per-vertex visibility (Toggle) actually
    // changes; rotate / zoom / pan re-use the existing GPU buffers.
    let mutable meshDirty : bool = true
    // The cached extruded mesh (positions + per-vertex layer key).
    // Kept across frames so toggle changes can rebuild only the
    // visibility flag without re-running the triangulator.
    let mutable cachedMesh : Extruder.ExtrudedMesh option = None
    let mutable lastUploadedToggle : Visibility.ToggleState = Visibility.empty
    let mutable hasUploadedAny : bool = false
    // Avalonia.OpenGlControlBase 11.3.14 doesn't include a depth
    // attachment on the FBO it provides. Without depth, the cube
    // collapses into a 2D draw-order collage. We allocate our own
    // depth renderbuffer and attach it to the FBO each frame
    // (cheap, since it's the same RBO unless the size changes).
    let mutable depthRbo : uint32 = 0u
    let mutable depthRboW : int = 0
    let mutable depthRboH : int = 0
    let mutable yawDeg = 30.0
    // ~20° pitch keeps the camera above the substrate but at a
    // shallow enough angle to see layer thickness in the metal
    // stack rather than just the top of met5.
    let mutable pitchDeg = 20.0
    let mutable zoom = 1.0
    // Camera target + extent. Set by FitCameraTo when a Library is
    // assigned; defaults work if Library is never set.
    let mutable target : System.Numerics.Vector3 = System.Numerics.Vector3.Zero
    let mutable extent : float = 80.0
    // Drag state for pointer-driven orbit + pan. `Avalonia.Point` is
    // qualified because Rekolektion.Viz.Core.Gds.Types.Point is
    // also in scope and would otherwise shadow it. `dragMode` is
    // None when no mouse button is held, OrbitDrag for left button
    // (rotates yaw/pitch), PanDrag for right or middle button
    // (translates target in the screen-aligned plane).
    let mutable dragMode : DragMode = NoDrag
    let mutable lastPos : Avalonia.Point = Avalonia.Point()

    static member val LibraryProperty : StyledProperty<Library option> =
        AvaloniaProperty.Register<StackCanvasControl, Library option>("Library", None)
        with get
    static member val FlatPolygonsProperty : StyledProperty<Layout.Flatten.FlatPolygon array> =
        AvaloniaProperty.Register<StackCanvasControl, Layout.Flatten.FlatPolygon array>("FlatPolygons", [||])
        with get
    static member val ToggleProperty : StyledProperty<Visibility.ToggleState> =
        AvaloniaProperty.Register<StackCanvasControl, Visibility.ToggleState>("Toggle", Visibility.empty)
        with get

    member this.Library
        with get() : Library option = this.GetValue(StackCanvasControl.LibraryProperty)
        and set(v: Library option) = this.SetValue(StackCanvasControl.LibraryProperty, v) |> ignore

    member this.FlatPolygons
        with get() : Layout.Flatten.FlatPolygon array = this.GetValue(StackCanvasControl.FlatPolygonsProperty)
        and set(v: Layout.Flatten.FlatPolygon array) = this.SetValue(StackCanvasControl.FlatPolygonsProperty, v) |> ignore

    member this.Toggle
        with get() : Visibility.ToggleState = this.GetValue(StackCanvasControl.ToggleProperty)
        and set(v: Visibility.ToggleState) = this.SetValue(StackCanvasControl.ToggleProperty, v) |> ignore

    member this.SetCamera (yaw: float) (pitch: float) (z: float) =
        yawDeg <- yaw
        pitchDeg <- pitch
        zoom <- z
        this.RequestNextFrameRendering()

    /// Auto-fit `target` (camera look-at point) and `extent` (longest
    /// axis of the macro 3D bbox) so the camera frames whatever's in
    /// `lib`. Called when Library is assigned. Without this, a small
    /// bitcell renders as a single pixel inside the default 80-µm
    /// frustum.
    member private this.FitCameraTo (lib: Library) (flat: Layout.Flatten.FlatPolygon array) =
        let mutable xMin, xMax = System.Single.MaxValue, System.Single.MinValue
        let mutable yMin, yMax = System.Single.MaxValue, System.Single.MinValue
        let mutable zMinPhysical = System.Double.MaxValue
        let mutable zMaxPhysical = System.Double.MinValue
        // Use FlatPolygons (post-hierarchy) so the bbox correctly
        // includes SRef/ARef-instanced content (e.g. an SRAM macro's
        // bitcell array). With raw lib.Structures the bbox would
        // only cover the top cell's polygons.
        for poly in flat do
            for p in poly.Points do
                let x = float32 ((float p.X) * lib.UserUnitsPerDbUnit)
                let y = float32 ((float p.Y) * lib.UserUnitsPerDbUnit)
                if x < xMin then xMin <- x
                if x > xMax then xMax <- x
                if y < yMin then yMin <- y
                if y > yMax then yMax <- y
            match Layout.Layer.bySky130Number poly.Layer poly.DataType with
            | Some (layer: Layout.Layer.Layer) ->
                let zBot = layer.StackZ
                let zTop = layer.StackZ + layer.Thickness
                if zBot < zMinPhysical then zMinPhysical <- zBot
                if zTop > zMaxPhysical then zMaxPhysical <- zTop
            | None -> ()
        let zMin = zMinPhysical * Z_EXAGGERATION
        let zMax = zMaxPhysical * Z_EXAGGERATION
        if xMin > xMax then
            target <- System.Numerics.Vector3.Zero
            extent <- 80.0
        else
            let zMid =
                if zMin > zMax then 0.0
                else (zMin + zMax) * 0.5
            target <- System.Numerics.Vector3(
                            (xMin + xMax) * 0.5f,
                            (yMin + yMax) * 0.5f,
                            float32 zMid)
            // Use the largest 3D dimension so the frustum encloses
            // the entire mesh at any rotation. Min 5 µm so a tiny
            // bitcell doesn't render at sub-pixel scale.
            let xExt = float (xMax - xMin)
            let yExt = float (yMax - yMin)
            let zExt = if zMax > zMin then zMax - zMin else 0.0
            extent <- max xExt (max yExt zExt) |> max 5.0

    /// Fill a transparent rect covering the control bounds so
    /// Avalonia's hit-test treats every point inside Bounds as a
    /// hit. Without this, pointer events fall THROUGH the GL canvas
    /// (the GL framebuffer isn't part of Avalonia's visual tree for
    /// hit-test purposes) and PointerPressed never fires.
    override this.Render (context: Avalonia.Media.DrawingContext) =
        base.Render context
        context.FillRectangle(
            Avalonia.Media.Brushes.Transparent,
            Avalonia.Rect(this.Bounds.Size))

    override this.OnPropertyChanged e =
        base.OnPropertyChanged e
        if e.Property = StackCanvasControl.LibraryProperty
           || e.Property = StackCanvasControl.FlatPolygonsProperty then
            // Either a new GDS or a re-flatten — extruded mesh is
            // stale, recompute on next render.
            meshDirty <- true
            match this.Library with
            | Some lib -> this.FitCameraTo lib this.FlatPolygons
            | None -> ()
            this.RequestNextFrameRendering()
        elif e.Property = StackCanvasControl.ToggleProperty then
            this.RequestNextFrameRendering()

    // ---- Pointer-driven orbit / pan + wheel zoom ----

    override this.OnPointerPressed e =
        base.OnPointerPressed e
        let props = e.GetCurrentPoint(this).Properties
        dragMode <-
            if props.IsRightButtonPressed || props.IsMiddleButtonPressed then PanDrag
            elif props.IsLeftButtonPressed then OrbitDrag
            else NoDrag
        if dragMode <> NoDrag then
            lastPos <- e.GetPosition this
            e.Pointer.Capture this
            this.Focus () |> ignore

    override this.OnPointerMoved e =
        base.OnPointerMoved e
        match dragMode with
        | NoDrag -> ()
        | OrbitDrag ->
            let p = e.GetPosition this
            let dx = p.X - lastPos.X
            let dy = p.Y - lastPos.Y
            yawDeg   <- yawDeg + dx * 0.4
            pitchDeg <- max -89.0 (min 89.0 (pitchDeg + dy * 0.4))
            lastPos <- p
            this.RequestNextFrameRendering()
        | PanDrag ->
            // Translate `target` in the screen-aligned plane so the
            // geometry under the cursor moves with the pointer. dx
            // moves along camera-right, dy along camera-up. Scale by
            // (extent / canvas height) so a one-canvas-height drag
            // pans by roughly one extent — matches what users
            // expect from CAD viewers.
            let p = e.GetPosition this
            let dxPx = p.X - lastPos.X
            let dyPx = p.Y - lastPos.Y
            let scale = extent / max this.Bounds.Height 1.0
            let yawRad = float32 (yawDeg * System.Math.PI / 180.0)
            let pitchRad = float32 (pitchDeg * System.Math.PI / 180.0)
            // zaxis (back) = camOffset.normalized = camera-relative
            // forward axis pointing AWAY from target.
            let zaxis = System.Numerics.Vector3(
                            MathF.Cos(pitchRad) * MathF.Sin(yawRad),
                            MathF.Cos(pitchRad) * MathF.Cos(yawRad),
                            MathF.Sin(pitchRad))
            let up = System.Numerics.Vector3.UnitZ
            let right = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(up, zaxis))
            let camUp = System.Numerics.Vector3.Cross(zaxis, right)
            // Drag right (positive dxPx) moves geometry right, so
            // target moves LEFT in world (camera target translates
            // -right). Drag down (positive dyPx) moves geometry
            // down, so target moves UP in world (camera target
            // translates +camUp).
            let panRight = float32 (-dxPx * scale)
            let panUp    = float32 (dyPx * scale)
            target <- target + right * panRight + camUp * panUp
            lastPos <- p
            this.RequestNextFrameRendering()

    override this.OnPointerReleased e =
        base.OnPointerReleased e
        dragMode <- NoDrag
        e.Pointer.Capture null

    override this.OnPointerWheelChanged e =
        base.OnPointerWheelChanged e
        let factor = if e.Delta.Y > 0.0 then 1.15 else 1.0 / 1.15
        zoom <- max 0.05 (min 50.0 (zoom * factor))
        this.RequestNextFrameRendering()

    override this.OnOpenGlInit(gli) =
        let g = GL.GetApi(fun n -> gli.GetProcAddress(n))
        gl <- Some g
        vbo <- g.GenBuffer()
        ebo <- g.GenBuffer()
        vao <- g.GenVertexArray()
        // Vertex shader passes world position so the fragment shader
        // can compute a flat per-triangle normal via screen-space
        // derivatives — avoids needing per-vertex normals in the
        // buffer.
        let vsSrc = "
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aColor;
            layout(location=2) in float aLayerVisible;
            uniform mat4 uMVP;
            out vec3 vColor;
            out float vVis;
            out vec3 vWorldPos;
            void main() {
                gl_Position = uMVP * vec4(aPos, 1.0);
                vColor = aColor;
                vVis = aLayerVisible;
                vWorldPos = aPos;
            }
        "
        // uLightDir is camera-forward (head-mounted light): faces
        // facing the camera light up, faces facing away dim. A
        // world-fixed light makes camera rotation read as flat
        // because face brightness is invariant.
        let fsSrc = "
            #version 330 core
            in vec3 vColor;
            in float vVis;
            in vec3 vWorldPos;
            out vec4 FragColor;
            uniform vec3 uLightDir;
            void main() {
                if (vVis < 0.5) discard;
                vec3 n = normalize(cross(dFdx(vWorldPos), dFdy(vWorldPos)));
                float lambert = max(dot(n, -uLightDir), 0.0);
                float intensity = 0.20 + 0.80 * lambert;
                FragColor = vec4(vColor * intensity, 1.0);
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
            if depthRbo <> 0u then
                g.DeleteRenderbuffer depthRbo
                depthRbo <- 0u
        | None -> ()

    override this.OnOpenGlRender(_gli, fb) =
        match gl, this.Library with
        | Some g, Some lib ->
            let flat = this.FlatPolygons
            let toggle = this.Toggle
            // (Re-)extrude only when geometry source changed.
            if meshDirty && flat.Length > 0 then
                cachedMesh <- Some (Extruder.extrude lib.UserUnitsPerDbUnit flat)
                meshDirty <- false
            // (Re-)upload buffer only when extrusion changed OR
            // visibility toggle changed. Rotation / zoom / pan
            // never trigger a re-upload.
            let toggleChanged = not (System.Object.ReferenceEquals(toggle, lastUploadedToggle))
            match cachedMesh with
            | Some mesh when (not hasUploadedAny || toggleChanged) ->
                indexCount <- mesh.Indices.Length
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
                    arr.[off + 2] <- v.Z * float32 Z_EXAGGERATION
                    arr.[off + 3] <- r
                    arr.[off + 4] <- gC
                    arr.[off + 5] <- b
                    arr.[off + 6] <- vis
                g.BindVertexArray(vao)
                g.BindBuffer(GLEnum.ArrayBuffer, vbo)
                g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(arr), GLEnum.DynamicDraw)
                g.BindBuffer(GLEnum.ElementArrayBuffer, ebo)
                g.BufferData(GLEnum.ElementArrayBuffer, ReadOnlySpan<int>(mesh.Indices), GLEnum.DynamicDraw)
                hasUploadedAny <- true
                lastUploadedToggle <- toggle
            | _ -> ()
            g.BindVertexArray(vao)

            // Avalonia's OpenGlControlBase doesn't pre-set the
            // viewport; set it ourselves to the FBO's physical pixel
            // size (logical bounds × DPI scale).
            let scale =
                match this.VisualRoot with
                | null -> 1.0
                | vr -> vr.RenderScaling
            let fbW = max 1 (int (this.Bounds.Width * scale))
            let fbH = max 1 (int (this.Bounds.Height * scale))
            g.BindFramebuffer(GLEnum.Framebuffer, uint32 fb)
            // Lazily create / resize a depth renderbuffer matching
            // the FBO and attach it. Avalonia's FBO arrives without
            // depth; without depth, glEnable(DEPTH_TEST) is a no-op
            // and triangles render in draw-order rather than by
            // distance to camera.
            if depthRbo = 0u then
                depthRbo <- g.GenRenderbuffer()
            if depthRboW <> fbW || depthRboH <> fbH then
                g.BindRenderbuffer(GLEnum.Renderbuffer, depthRbo)
                g.RenderbufferStorage(
                    GLEnum.Renderbuffer,
                    GLEnum.DepthComponent24,
                    uint32 fbW, uint32 fbH)
                depthRboW <- fbW
                depthRboH <- fbH
            g.FramebufferRenderbuffer(
                GLEnum.Framebuffer,
                GLEnum.DepthAttachment,
                GLEnum.Renderbuffer,
                depthRbo)
            g.Viewport(0, 0, uint32 fbW, uint32 fbH)
            g.ClearColor(0.0f, 0.0f, 0.0f, 1.0f)
            g.Clear(uint32 (GLEnum.ColorBufferBit ||| GLEnum.DepthBufferBit))
            g.Enable(GLEnum.DepthTest)

            g.UseProgram(program)
            let strideBytes = uint32 (7 * sizeof<float32>)
            g.EnableVertexAttribArray(0u)
            g.VertexAttribPointer(0u, 3, GLEnum.Float, false, strideBytes, nativeint 0)
            g.EnableVertexAttribArray(1u)
            g.VertexAttribPointer(1u, 3, GLEnum.Float, false, strideBytes, nativeint (3 * sizeof<float32>))
            g.EnableVertexAttribArray(2u)
            g.VertexAttribPointer(2u, 1, GLEnum.Float, false, strideBytes, nativeint (6 * sizeof<float32>))

            let mvp =
                Matrix4x4Helpers.buildOrbitMvp
                    yawDeg pitchDeg zoom target extent
                    (this.Bounds.Width, this.Bounds.Height)
            // Camera-forward direction in world space — used as the
            // light direction so faces facing the camera are lit
            // and faces facing away dim. Recomputed each frame from
            // the same yaw/pitch as the camera; light orbits with
            // the camera (head-mounted), which is what gives camera
            // rotation a true 3D feel rather than a 2D-billboard
            // rotation.
            let yawRad = float32 (yawDeg * System.Math.PI / 180.0)
            let pitchRad = float32 (pitchDeg * System.Math.PI / 180.0)
            let camForward =
                System.Numerics.Vector3(
                    -MathF.Cos(pitchRad) * MathF.Sin(yawRad),
                    -MathF.Cos(pitchRad) * MathF.Cos(yawRad),
                    -MathF.Sin(pitchRad))
            let lightLoc = g.GetUniformLocation(program, "uLightDir")
            g.Uniform3(lightLoc, camForward)
            // transpose=FALSE is correct for System.Numerics matrices
            // here. .NET stores Matrix4x4 fields in row-major order
            // (M11, M12, M13, M14, M21, ...); GL with transpose=false
            // takes those bytes as column-major — the net effect is
            // GL applies the matrix correctly under row-vector
            // convention. transpose=true would double-transpose,
            // inverting axes and producing a billboard-rotation
            // pseudo-3D effect instead of true camera orbit.
            // Verified in tools/viz/src/Rekolektion.Viz.GlTest.
            let loc = g.GetUniformLocation(program, "uMVP")
            let mvpArr = Matrix4x4Helpers.toFloatArray mvp
            g.UniformMatrix4(loc, 1u, false, ReadOnlySpan<float32>(mvpArr))
            g.DrawElements(GLEnum.Triangles, uint32 indexCount, GLEnum.UnsignedInt, IntPtr.Zero.ToPointer())
        | _ -> ()
