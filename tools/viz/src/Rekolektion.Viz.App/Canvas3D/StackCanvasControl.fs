module Rekolektion.Viz.App.Canvas3D.StackCanvasControl

open System
open System.Numerics
open Avalonia
open Avalonia.Input
open Avalonia.OpenGL
open Avalonia.OpenGL.Controls
open Silk.NET.OpenGL
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
// `Rkt.Types` opened after Gds.Types so `Point` resolves to the
// Rkt-flavored point Flatten now emits. `Library` stays
// Gds-flavored (Rkt has no Library type).
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Render.Mesh
open Rekolektion.Viz.Render.Skia

/// 5×7 bitmap-font column data for digits + '-' + '.' (12 glyphs).
/// Each byte is one column; bit 0 = top row, bit 6 = bottom row.
/// Standard ASCII-ish 5×7 font (compact, public-domain encoding).
let private fontGlyphCols : byte[][] = [|
    [| 0x3Euy; 0x51uy; 0x49uy; 0x45uy; 0x3Euy |]   // 0
    [| 0x00uy; 0x42uy; 0x7Fuy; 0x40uy; 0x00uy |]   // 1
    [| 0x42uy; 0x61uy; 0x51uy; 0x49uy; 0x46uy |]   // 2
    [| 0x21uy; 0x41uy; 0x45uy; 0x4Buy; 0x31uy |]   // 3
    [| 0x18uy; 0x14uy; 0x12uy; 0x7Fuy; 0x10uy |]   // 4
    [| 0x27uy; 0x45uy; 0x45uy; 0x45uy; 0x39uy |]   // 5
    [| 0x3Cuy; 0x4Auy; 0x49uy; 0x49uy; 0x30uy |]   // 6
    [| 0x01uy; 0x71uy; 0x09uy; 0x05uy; 0x03uy |]   // 7
    [| 0x36uy; 0x49uy; 0x49uy; 0x49uy; 0x36uy |]   // 8
    [| 0x06uy; 0x49uy; 0x49uy; 0x29uy; 0x1Euy |]   // 9
    [| 0x08uy; 0x08uy; 0x08uy; 0x08uy; 0x08uy |]   // -
    [| 0x00uy; 0x60uy; 0x60uy; 0x00uy; 0x00uy |]   // .
|]

let private fontGlyphIndex (c: char) : int =
    match c with
    | c when c >= '0' && c <= '9' -> int c - int '0'
    | '-' -> 10
    | '.' -> 11
    | _ -> 11    // unknown chars render as '.' (smallest visual)

[<Literal>]
let private FONT_GLYPH_W = 5
[<Literal>]
let private FONT_GLYPH_H = 7
[<Literal>]
let private FONT_GLYPH_COUNT = 12
[<Literal>]
let private FONT_ATLAS_W = 60   // 12 glyphs × 5 px
[<Literal>]
let private FONT_ATLAS_H = 7

/// Bake the column-encoded glyphs into a single linear byte array
/// suitable for upload as a GL_R8 texture. atlas[row*W + col] = 0
/// or 255.
let private buildFontAtlas () : byte[] =
    let pixels = Array.zeroCreate<byte> (FONT_ATLAS_W * FONT_ATLAS_H)
    for g in 0 .. FONT_GLYPH_COUNT - 1 do
        let cols = fontGlyphCols.[g]
        for col in 0 .. FONT_GLYPH_W - 1 do
            let bits = cols.[col]
            for row in 0 .. FONT_GLYPH_H - 1 do
                let bit = (int bits >>> row) &&& 1
                if bit = 1 then
                    let x = g * FONT_GLYPH_W + col
                    let y = row
                    pixels.[y * FONT_ATLAS_W + x] <- 255uy
    pixels

/// Even-odd point-in-polygon. `poly` is in GDS DB units; `qx`/`qy`
/// are in user µm. `uupdb` converts DBU → µm.
let private pointInPolygon
        (poly: Point array)
        (qx: float32) (qy: float32)
        (uupdb: float) : bool =
    let n = poly.Length
    if n < 3 then false
    else
        let mutable inside = false
        let mutable j = n - 1
        for i in 0 .. n - 1 do
            let xi = float32 (float poly.[i].X * uupdb)
            let yi = float32 (float poly.[i].Y * uupdb)
            let xj = float32 (float poly.[j].X * uupdb)
            let yj = float32 (float poly.[j].Y * uupdb)
            let cross =
                ((yi > qy) <> (yj > qy)) &&
                (qx < (xj - xi) * (qy - yi) / (yj - yi) + xi)
            if cross then inside <- not inside
            j <- i
        inside

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
    // Separate VBO carrying one float per vertex: 1.0 when the
    // source polygon is in the active highlighted net, 0.0
    // otherwise. Lives in its own buffer so toggling a net only
    // re-uploads N float32s instead of the full ~80MB mesh VBO.
    let mutable netVbo : uint32 = 0u
    let mutable vao : uint32 = 0u
    let mutable program : uint32 = 0u
    let mutable indexCount : int = 0
    // Net highlight state. `lastHighlightedNets` mirrors the value
    // the GPU is currently configured for; when it differs from
    // `this.Toggle.HighlightedNets` we re-upload netVbo on the next
    // render. The set lets multiple nets light at once.
    let mutable lastHighlightedNets : Set<string> = Set.empty
    // Block isolation state. Mirror of the net flag pattern: per-
    // vertex 1.0 if the source polygon's structure is inside the
    // isolated block's transitive closure, else 0.0. Shader
    // discards everything outside the block when the uniform
    // uIsolateActive is set, so 'isolate block' really hides
    // other geometry instead of just dimming.
    let mutable blockVbo : uint32 = 0u
    let mutable lastIsolatedBlock : string option = None
    // Ruler state. Two lines from origin along +X (red) and +Y
    // (green) with tick marks. Sit just below the lowest layer
    // (z = -0.5 µm) so they don't z-fight with geometry. Extent
    // tracks the cell's silicon bbox; rebuilt when FitCameraTo
    // sees new bounds.
    let mutable rulerProgram : uint32 = 0u
    let mutable rulerVao : uint32 = 0u
    let mutable rulerVbo : uint32 = 0u
    let mutable rulerVertexCount : int = 0
    // Ratlines: re-use rulerProgram (it's just MVP-transformed
    // (x,y,z, r,g,b) Lines). Vertex buffer is rebuilt every
    // frame when the overlay is enabled — handful of nets, a
    // few dozen vertices, cheap. Drawn at a fixed Z above the
    // metal stack so the lines float over the cell instead of
    // intersecting geometry.
    let mutable ratlineVao : uint32 = 0u
    let mutable ratlineVbo : uint32 = 0u
    let mutable ratlineVertexCount : int = 0
    let mutable rulerXMin : float32 = 0.0f
    let mutable rulerXMax : float32 = 0.0f
    let mutable rulerYMin : float32 = 0.0f
    let mutable rulerYMax : float32 = 0.0f
    let mutable rulerStep : float = 1.0   // major step (µm)
    let mutable rulerDirty : bool = true
    // Major-tick world positions kept around for the Render
    // override to draw screen-space numeric labels via Avalonia.
    let mutable rulerXMajors : float[] = [||]
    let mutable rulerYMajors : float[] = [||]
    // Cached ruler placement constants the per-frame text-quad
    // builder needs (the line-geometry pass that wrote them only
    // runs when rulerDirty fires; text rebuilds every frame so
    // its glyphs scale with the camera distance, not the cell).
    let mutable rulerCornerX : float32 = 0.0f
    let mutable rulerCornerY : float32 = 0.0f
    let mutable rulerMajorTickLen : float32 = 0.0f
    // Bitmap font for ruler tick labels. Avalonia 11.3 composes the
    // GL FBO on top of Control.Render output, so DrawText paint
    // doesn't reach the screen — we render text via GL textured
    // quads sampled from a baked 5x7 atlas (digits, '-', '.', 'µ',
    // 'm'). Atlas + shader live alongside the ruler.
    let mutable fontTex : uint32 = 0u
    let mutable textProgram : uint32 = 0u
    let mutable textVao : uint32 = 0u
    let mutable textVbo : uint32 = 0u
    let mutable textVertexCount : int = 0
    // MVP from the last GL frame; used by Render to project the
    // ruler tick world positions into screen pixels for label
    // placement. Updated at the bottom of OnOpenGlRender.
    let mutable lastMvp : System.Numerics.Matrix4x4 =
        System.Numerics.Matrix4x4.Identity
    // Mesh upload caching. Re-extruding 400k polygons (production
    // SRAM macro) every frame would saturate the CPU and drop the
    // canvas to <1 fps. We extrude + upload only when FlatPolygons
    // changes; rotate / zoom / pan / layer-toggle never re-upload.
    let mutable meshDirty : bool = true
    let mutable cachedMesh : Extruder.ExtrudedMesh option = None
    let mutable hasUploadedAny : bool = false
    // Layer-key → slot index for the uLayerVis uniform array.
    // Built once per extrusion. Capped at 32 entries (matches
    // shader array size); SKY130 has 18 drawing layers so this
    // has plenty of headroom.
    let mutable layerSlotMap : System.Collections.Generic.Dictionary<int * int, int> =
        System.Collections.Generic.Dictionary()
    // Avalonia.OpenGlControlBase 11.3.14 doesn't include a depth
    // attachment on the FBO it provides. Without depth, the cube
    // collapses into a 2D draw-order collage. We allocate our own
    // depth renderbuffer and attach it to the FBO each frame
    // (cheap, since it's the same RBO unless the size changes).
    let mutable depthRbo : uint32 = 0u
    let mutable depthRboW : int = 0
    let mutable depthRboH : int = 0
    // Back-right isometric: camera in the (-X, -Y, +Z) octant
    // (yaw=135 rotated 90° CCW around the up axis — yaw
    // increases CW when viewed from above in this
    // parameterization, so CCW means +90°). Maps the world axes
    // so origin renders in the LOWER LEFT of the viewport with
    // the cell extending up-and-right.
    let mutable yawDeg = 225.0
    // ~35.26° = arctan(1/√2) is the true isometric pitch — equal
    // foreshortening on every axis. Steeper than the prior 20°
    // but still shows layer thickness clearly.
    let mutable pitchDeg = 35.0
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
    // Mouse-down position used to distinguish a click (small total
    // travel, fires a pick) from a drag (large travel, just orbits
    // / pans). Compared in OnPointerReleased.
    let mutable pressStart : Avalonia.Point = Avalonia.Point()
    let mutable pressedButton : DragMode = NoDrag

    static member val LibraryProperty : StyledProperty<Document option> =
        AvaloniaProperty.Register<StackCanvasControl, Document option>("Library", None)
        with get
    static member val FlatPolygonsProperty : StyledProperty<Layout.Flatten.FlatPolygon array> =
        AvaloniaProperty.Register<StackCanvasControl, Layout.Flatten.FlatPolygon array>("FlatPolygons", [||])
        with get
    static member val ToggleProperty : StyledProperty<Visibility.ToggleState> =
        AvaloniaProperty.Register<StackCanvasControl, Visibility.ToggleState>("Toggle", Visibility.empty)
        with get
    /// Picking callback. The host wires this to dispatch a
    /// `PolygonPicked` Msg. Holds an Action(structure, index) — null
    /// means "no host listener", which silently no-ops on click.
    static member val PolygonPickedHandlerProperty : StyledProperty<Action<string, int>> =
        AvaloniaProperty.Register<StackCanvasControl, Action<string, int>>("PolygonPickedHandler", null)
        with get
    /// Set of net names whose ratlines render on the 3D canvas.
    /// Mirrors GdsCanvasControl.VisibleRatlinesProperty so 2D and
    /// 3D agree on which nets are showing.
    static member val VisibleRatlinesProperty : StyledProperty<Set<string>> =
        AvaloniaProperty.Register<StackCanvasControl, Set<string>>(
            "VisibleRatlines", Set.empty)
        with get

    member this.Library
        with get() : Document option = this.GetValue(StackCanvasControl.LibraryProperty)
        and set(v: Document option) = this.SetValue(StackCanvasControl.LibraryProperty, v) |> ignore

    member this.FlatPolygons
        with get() : Layout.Flatten.FlatPolygon array = this.GetValue(StackCanvasControl.FlatPolygonsProperty)
        and set(v: Layout.Flatten.FlatPolygon array) = this.SetValue(StackCanvasControl.FlatPolygonsProperty, v) |> ignore

    member this.Toggle
        with get() : Visibility.ToggleState = this.GetValue(StackCanvasControl.ToggleProperty)
        and set(v: Visibility.ToggleState) = this.SetValue(StackCanvasControl.ToggleProperty, v) |> ignore

    member this.PolygonPickedHandler
        with get() : Action<string, int> = this.GetValue(StackCanvasControl.PolygonPickedHandlerProperty)
        and set(v: Action<string, int>) = this.SetValue(StackCanvasControl.PolygonPickedHandlerProperty, v) |> ignore

    member this.VisibleRatlines
        with get() : Set<string> = this.GetValue(StackCanvasControl.VisibleRatlinesProperty)
        and set(v: Set<string>) = this.SetValue(StackCanvasControl.VisibleRatlinesProperty, v) |> ignore

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
    member private this.FitCameraTo (lib: Document) (flat: Layout.Flatten.FlatPolygon array) =
        let mutable xMin, xMax = System.Single.MaxValue, System.Single.MinValue
        let mutable yMin, yMax = System.Single.MaxValue, System.Single.MinValue
        let mutable zMinPhysical = System.Double.MaxValue
        let mutable zMaxPhysical = System.Double.MinValue
        // Use FlatPolygons (post-hierarchy) so the bbox correctly
        // includes SRef/ARef-instanced content (e.g. an SRAM macro's
        // bitcell array). With raw lib.Cells the bbox would
        // only cover the top cell's polygons.
        for poly in flat do
            // Skip Magic-internal markers (255, *) so the camera
            // frames silicon, not bookkeeping rectangles. Mirrors
            // the 2D AutoFit / LayerPainter.bboxOf behavior. Also
            // applied to the Z bbox so the marker layer sitting
            // above met5 doesn't pull the frustum vertically.
            if not (Layout.Layer.isNonPhysical poly.Layer poly.DataType) then
              for p in poly.Points do
                let x = float32 ((float p.X) * (float lib.Units.DbuNm * 1.0e-3))
                let y = float32 ((float p.Y) * (float lib.Units.DbuNm * 1.0e-3))
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
            // Ruler spans the full silicon bbox — origin (0,0) is
            // marked but the rule itself runs from xMin..xMax /
            // yMin..yMax, so a cell centered around origin (e.g.
            // many foundry layouts) shows the negative half too.
            rulerXMin <- xMin
            rulerXMax <- xMax
            rulerYMin <- yMin
            rulerYMax <- yMax
            rulerDirty <- true
            // Diagnostic — surfaces what the camera is actually
            // framing so we can tell "marker filter not running"
            // from "cell is genuinely tall" when a render looks
            // larger than expected.
            let mutable nonPhysCount = 0
            for poly in flat do
                if Layout.Layer.isNonPhysical poly.Layer poly.DataType then
                    nonPhysCount <- nonPhysCount + 1
            eprintfn "[viz3d] FitCameraTo: silicon bbox %.3f x %.3f x %.3f µm (extent=%.3f); skipped %d non-physical polys"
                xExt yExt zExt extent nonPhysCount

    /// Fill a transparent rect covering the control bounds so
    /// Avalonia's hit-test treats every point inside Bounds as a
    /// hit. Without this, pointer events fall THROUGH the GL canvas
    /// (the GL framebuffer isn't part of Avalonia's visual tree for
    /// hit-test purposes) and PointerPressed never fires.
    /// Format a ruler label. Drops a trailing ".0" so integer
    /// values read as plain numbers.
    member private _.FormatRulerLabel (v: float) (step: float) : string =
        let decimals =
            if step >= 1.0 then 0
            elif step >= 0.1 then 1
            else 2
        let s = v.ToString("F" + string decimals, System.Globalization.CultureInfo.InvariantCulture)
        if decimals > 0 && s.EndsWith ".0" then s.Substring(0, s.Length - 2) else s

    /// Project a world-space point through the most recent MVP
    /// into Avalonia control-space pixels. Returns None if the
    /// point's clip-space w is non-positive (point behind camera).
    member private this.ProjectWorldToScreen
            (worldX: float32) (worldY: float32) (worldZ: float32)
            : Avalonia.Point option =
        let v =
            System.Numerics.Vector4.Transform(
                System.Numerics.Vector4(worldX, worldY, worldZ, 1.0f),
                lastMvp)
        if v.W <= 1e-6f then None
        else
            let ndcX = v.X / v.W
            let ndcY = v.Y / v.W
            let sx = (float ndcX + 1.0) * 0.5 * this.Bounds.Width
            let sy = (1.0 - float ndcY) * 0.5 * this.Bounds.Height
            Some (Avalonia.Point(sx, sy))

    override this.Render (context: Avalonia.Media.DrawingContext) =
        base.Render context
        context.FillRectangle(
            Avalonia.Media.Brushes.Transparent,
            Avalonia.Rect(this.Bounds.Size))
        // Numeric tick labels are NOT drawn here — Avalonia 11.3's
        // OpenGlControlBase composes the GL FBO on top of this
        // control's DrawingContext output, so anything painted via
        // context here gets covered by the 3D scene. A future
        // commit will either bake a GL bitmap-font atlas or move
        // labels into a sibling overlay panel above the GL canvas.
        // For now the GL ruler ticks (major / minor at the bbox
        // corner, every 5 µm) are the only on-canvas indicator;
        // the user reads off the count visually.
        ()

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
        pressedButton <- dragMode
        if dragMode <> NoDrag then
            lastPos <- e.GetPosition this
            pressStart <- lastPos
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
        // Treat as a click if the pointer barely moved while held.
        // 4px threshold matches what feels intentional vs an
        // accidental wiggle during a quick click.
        let release = e.GetPosition this
        let dx = release.X - pressStart.X
        let dy = release.Y - pressStart.Y
        let travel = sqrt (dx * dx + dy * dy)
        let wasOrbitClick = pressedButton = OrbitDrag && travel < 4.0
        dragMode <- NoDrag
        pressedButton <- NoDrag
        e.Pointer.Capture null
        if wasOrbitClick then
            this.PickAt(release)

    /// Cast a ray from the camera through the screen point and find
    /// the closest visible polygon prism it pierces. Polygon storage
    /// is in DBU; mesh world coords are µm = DBU × UserUnitsPerDbUnit
    /// with Z multiplied by Z_EXAGGERATION (matches what we upload to
    /// the VBO). Hit dispatches via PolygonPickedHandler.
    member private this.PickAt (screen: Avalonia.Point) =
        let handler = this.PolygonPickedHandler
        if isNull (box handler) then () else
        match this.Library with
        | None -> ()
        | Some lib ->
            let flat = this.FlatPolygons
            if flat.Length = 0 then () else
            let w = this.Bounds.Width
            let h = this.Bounds.Height
            if w < 1.0 || h < 1.0 then () else
            let ndcX = float32 (2.0 * screen.X / w - 1.0)
            let ndcY = float32 (1.0 - 2.0 * screen.Y / h)
            let mvp =
                Matrix4x4Helpers.buildOrbitMvp
                    yawDeg pitchDeg zoom target extent (w, h)
            match Matrix4x4.Invert(mvp) with
            | false, _ -> ()
            | true, inv ->
                let unproj (z: float32) =
                    let v = Vector4(ndcX, ndcY, z, 1.0f)
                    let r = Vector4.Transform(v, inv)
                    Vector3(r.X / r.W, r.Y / r.W, r.Z / r.W)
                let nearW = unproj -1.0f
                let farW  = unproj 1.0f
                let rayO = nearW
                let rayD = Vector3.Normalize(farW - nearW)
                let toggle = this.Toggle
                let mutable bestT = System.Single.MaxValue
                let mutable best : (string * int) option = None
                for poly in flat do
                    let key = (poly.Layer, poly.DataType)
                    if Visibility.isLayerVisible toggle key then
                        match Layout.Layer.bySky130Number poly.Layer poly.DataType with
                        | None -> ()
                        | Some layer ->
                            let zBot = float32 (layer.StackZ * Z_EXAGGERATION)
                            let zTop = float32 ((layer.StackZ + layer.Thickness) * Z_EXAGGERATION)
                            let dz = rayD.Z
                            if MathF.Abs(dz) > 1.0e-6f then
                                let tA = (zBot - rayO.Z) / dz
                                let tB = (zTop - rayO.Z) / dz
                                let tIn  = MathF.Min(tA, tB)
                                let tOut = MathF.Max(tA, tB)
                                if tOut > 0.0f && tIn < bestT then
                                    // Sample at slab entry, midpoint,
                                    // and exit. Top-down clicks hit
                                    // the top face (entry); a steep
                                    // grazing ray may only intersect
                                    // the side wall (mid/exit).
                                    let t0 = MathF.Max(tIn, 0.0f)
                                    let t1 = (tIn + tOut) * 0.5f
                                    let t2 = tOut
                                    let samples = [| t0; t1; t2 |]
                                    let mutable hitT = System.Single.MaxValue
                                    for s in samples do
                                        if s >= 0.0f && s < hitT then
                                            let px = rayO.X + rayD.X * s
                                            let py = rayO.Y + rayD.Y * s
                                            if pointInPolygon poly.Points px py (float lib.Units.DbuNm * 1.0e-3) then
                                                hitT <- s
                                    if hitT < bestT then
                                        bestT <- hitT
                                        best <- Some (poly.SourceStructure, poly.SourceIndex)
                match best with
                | Some (s, i) -> handler.Invoke(s, i)
                | None -> ()

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
        netVbo <- g.GenBuffer()
        blockVbo <- g.GenBuffer()
        vao <- g.GenVertexArray()
        // Avalonia tears down + recreates the GL context when the
        // tab is hidden / reshown. Reset the upload-state so the
        // mesh re-uploads against the freshly-created VBO/EBO —
        // otherwise the VBO is empty after tab-switch and the 3D
        // canvas renders blank.
        meshDirty <- true
        hasUploadedAny <- false
        layerSlotMap.Clear()
        // Force a net-flag re-upload after re-init too. The
        // sentinel ensures the next render's `<>` test fires; any
        // non-empty value the user can't actually pick works.
        lastHighlightedNets <- Set.singleton "<<force-mismatch-on-reinit>>"
        lastIsolatedBlock <- Some "<<force-mismatch-on-reinit>>"
        depthRbo <- 0u
        depthRboW <- 0
        depthRboH <- 0
        // Vertex shader passes world position so the fragment shader
        // can compute a flat per-triangle normal via screen-space
        // derivatives — avoids needing per-vertex normals in the
        // buffer.
        // aLayerSlot holds a small int (0..31) that indexes into
        // uLayerVis[]. Visibility moved to a uniform array because
        // updating per-vertex visibility on toggle required re-
        // uploading the entire VBO (~80MB for a production macro,
        // taking >1s). Now toggle just updates 32 floats.
        let vsSrc = "
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aColor;
            layout(location=2) in float aLayerSlot;
            layout(location=3) in float aInNet;
            layout(location=4) in float aInBlock;
            uniform mat4 uMVP;
            uniform float uLayerVis[32];
            out vec3 vColor;
            out float vVis;
            out vec3 vWorldPos;
            out float vInNet;
            out float vInBlock;
            void main() {
                gl_Position = uMVP * vec4(aPos, 1.0);
                vColor = aColor;
                int slot = int(aLayerSlot);
                if (slot < 0) slot = 0;
                if (slot > 31) slot = 31;
                vVis = uLayerVis[slot];
                vWorldPos = aPos;
                vInNet = aInNet;
                vInBlock = aInBlock;
            }
        "
        // uLightDir is camera-forward (head-mounted light): faces
        // facing the camera light up, faces facing away dim. A
        // world-fixed light makes camera rotation read as flat
        // because face brightness is invariant. uHighlightActive
        // dims polygons whose source isn't part of the highlighted
        // net so the matching geometry pops; matches the 2D net
        // highlight behavior.
        let fsSrc = "
            #version 330 core
            in vec3 vColor;
            in float vVis;
            in vec3 vWorldPos;
            in float vInNet;
            in float vInBlock;
            out vec4 FragColor;
            uniform vec3 uLightDir;
            uniform float uHighlightActive;
            uniform float uIsolateActive;
            void main() {
                if (vVis < 0.5) discard;
                // Block isolation hides anything outside the block
                // — semantics of Visibility.isBlockVisible.
                if (uIsolateActive > 0.5 && vInBlock < 0.5) discard;
                vec3 n = normalize(cross(dFdx(vWorldPos), dFdy(vWorldPos)));
                float lambert = max(dot(n, -uLightDir), 0.0);
                float intensity = 0.20 + 0.80 * lambert;
                vec3 base = vColor * intensity;
                if (uHighlightActive > 0.5 && vInNet < 0.5) {
                    base *= 0.25;
                }
                FragColor = vec4(base, 1.0);
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

        // Minimal ruler shader: position + color, no lighting / no
        // visibility flags. Drawn as GL_LINES after the main mesh.
        let rulerVsSrc = "
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aColor;
            uniform mat4 uMVP;
            out vec3 vColor;
            void main() {
                gl_Position = uMVP * vec4(aPos, 1.0);
                vColor = aColor;
            }
        "
        let rulerFsSrc = "
            #version 330 core
            in vec3 vColor;
            out vec4 FragColor;
            void main() { FragColor = vec4(vColor, 1.0); }
        "
        let rvs = compile rulerVsSrc ShaderType.VertexShader
        let rfs = compile rulerFsSrc ShaderType.FragmentShader
        rulerProgram <- g.CreateProgram()
        g.AttachShader(rulerProgram, rvs)
        g.AttachShader(rulerProgram, rfs)
        g.LinkProgram(rulerProgram)
        let mutable rulerLink = 0
        g.GetProgram(rulerProgram, ProgramPropertyARB.LinkStatus, &rulerLink)
        if rulerLink = 0 then
            eprintfn "[viz3d] ruler program link failed: %s" (g.GetProgramInfoLog rulerProgram)
        g.DeleteShader rvs
        g.DeleteShader rfs
        rulerVao <- g.GenVertexArray()
        rulerVbo <- g.GenBuffer()
        rulerDirty <- true
        // Shared with the ruler program; ratlines are just
        // (x,y,z, r,g,b) Lines, same vertex layout.
        ratlineVao <- g.GenVertexArray()
        ratlineVbo <- g.GenBuffer()

        // ---- Bitmap font ----
        let textVsSrc = "
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec2 aUv;
            layout(location=2) in vec3 aColor;
            uniform mat4 uMVP;
            out vec2 vUv;
            out vec3 vColor;
            void main() {
                gl_Position = uMVP * vec4(aPos, 1.0);
                vUv = aUv;
                vColor = aColor;
            }
        "
        let textFsSrc = "
            #version 330 core
            in vec2 vUv;
            in vec3 vColor;
            uniform sampler2D uFont;
            out vec4 FragColor;
            void main() {
                float a = texture(uFont, vUv).r;
                if (a < 0.5) discard;
                FragColor = vec4(vColor, 1.0);
            }
        "
        let tvs = compile textVsSrc ShaderType.VertexShader
        let tfs = compile textFsSrc ShaderType.FragmentShader
        textProgram <- g.CreateProgram()
        g.AttachShader(textProgram, tvs)
        g.AttachShader(textProgram, tfs)
        g.LinkProgram textProgram
        let mutable tlink = 0
        g.GetProgram(textProgram, ProgramPropertyARB.LinkStatus, &tlink)
        if tlink = 0 then
            eprintfn "[viz3d] text program link failed: %s" (g.GetProgramInfoLog textProgram)
        g.DeleteShader tvs
        g.DeleteShader tfs

        textVao <- g.GenVertexArray ()
        textVbo <- g.GenBuffer ()
        // Atlas: single-channel R8 texture, no filtering (so the
        // bitmap stays crisp), no wrapping (UVs are in-bounds by
        // construction).
        let atlas = buildFontAtlas ()
        fontTex <- g.GenTexture ()
        g.BindTexture(GLEnum.Texture2D, fontTex)
        g.PixelStore(GLEnum.UnpackAlignment, 1)
        g.TexImage2D(
            GLEnum.Texture2D,
            0,
            int InternalFormat.R8,
            uint32 FONT_ATLAS_W, uint32 FONT_ATLAS_H,
            0,
            GLEnum.Red,
            GLEnum.UnsignedByte,
            ReadOnlySpan<byte>(atlas))
        g.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, int GLEnum.Nearest)
        g.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, int GLEnum.Nearest)
        g.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, int GLEnum.ClampToEdge)
        g.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, int GLEnum.ClampToEdge)

    override this.OnOpenGlDeinit(_gli) =
        match gl with
        | Some g ->
            g.DeleteBuffer(vbo)
            g.DeleteBuffer(ebo)
            g.DeleteBuffer(netVbo)
            g.DeleteBuffer(blockVbo)
            g.DeleteVertexArray vao
            g.DeleteProgram(program)
            if rulerVao <> 0u then g.DeleteVertexArray rulerVao
            if rulerVbo <> 0u then g.DeleteBuffer rulerVbo
            if rulerProgram <> 0u then g.DeleteProgram rulerProgram
            if textVao <> 0u then g.DeleteVertexArray textVao
            if textVbo <> 0u then g.DeleteBuffer textVbo
            if textProgram <> 0u then g.DeleteProgram textProgram
            if fontTex <> 0u then g.DeleteTexture fontTex
            if depthRbo <> 0u then
                g.DeleteRenderbuffer depthRbo
                depthRbo <- 0u
        | None -> ()

    override this.OnOpenGlRender(_gli, fb) =
        match gl, this.Library with
        | Some g, None ->
            // No active macro — close happened or nothing loaded.
            // Bind the FBO and clear so the prior frame's geometry
            // doesn't linger ('closed tab still showing in 3D' bug).
            let scale =
                match this.VisualRoot with
                | null -> 1.0
                | vr -> vr.RenderScaling
            let fbW = max 1 (int (this.Bounds.Width * scale))
            let fbH = max 1 (int (this.Bounds.Height * scale))
            g.BindFramebuffer(GLEnum.Framebuffer, uint32 fb)
            g.Viewport(0, 0, uint32 fbW, uint32 fbH)
            g.ClearColor(0.0f, 0.0f, 0.0f, 1.0f)
            g.Clear(uint32 (GLEnum.ColorBufferBit ||| GLEnum.DepthBufferBit))
            // Forget the cached mesh so reopening a file extrudes
            // afresh against the next library.
            cachedMesh <- None
            meshDirty <- true
            hasUploadedAny <- false
            layerSlotMap.Clear()
        | Some g, Some lib ->
            let flat = this.FlatPolygons
            let toggle = this.Toggle
            // (Re-)extrude only when geometry source changed.
            if meshDirty && flat.Length > 0 then
                cachedMesh <- Some (Extruder.extrude (float lib.Units.DbuNm * 1.0e-3) flat)
                meshDirty <- false
                hasUploadedAny <- false
                layerSlotMap.Clear()
            // Upload VBO only on first frame after a re-extrude.
            // Toggling layer visibility no longer touches the VBO —
            // see uLayerVis uniform write below.
            match cachedMesh with
            | Some mesh when not hasUploadedAny ->
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
                    // Assign a small slot index per unique layer key.
                    // Layers beyond slot 31 (shouldn't happen for
                    // SKY130's 18 layers) get slot 0 — visible.
                    let slot =
                        match layerSlotMap.TryGetValue v.LayerKey with
                        | true, s -> s
                        | false, _ ->
                            let next = if layerSlotMap.Count < 32 then layerSlotMap.Count else 0
                            layerSlotMap.[v.LayerKey] <- next
                            next
                    let off = i * stride
                    arr.[off]     <- v.X
                    arr.[off + 1] <- v.Y
                    arr.[off + 2] <- v.Z * float32 Z_EXAGGERATION
                    arr.[off + 3] <- r
                    arr.[off + 4] <- gC
                    arr.[off + 5] <- b
                    arr.[off + 6] <- float32 slot
                g.BindVertexArray(vao)
                g.BindBuffer(GLEnum.ArrayBuffer, vbo)
                g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(arr), GLEnum.StaticDraw)
                g.BindBuffer(GLEnum.ElementArrayBuffer, ebo)
                g.BufferData(GLEnum.ElementArrayBuffer, ReadOnlySpan<int>(mesh.Indices), GLEnum.StaticDraw)
                // Initialize netVbo + blockVbo with all zeros; the
                // re-upload passes below fill them for the current
                // HighlightNet / IsolatedBlock, if any.
                let zeros = Array.zeroCreate<float32> mesh.Vertices.Length
                g.BindBuffer(GLEnum.ArrayBuffer, netVbo)
                g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(zeros), GLEnum.DynamicDraw)
                g.BindBuffer(GLEnum.ArrayBuffer, blockVbo)
                g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(zeros), GLEnum.DynamicDraw)
                hasUploadedAny <- true
                // Force per-vertex flag refresh on first draw.
                lastHighlightedNets <- Set.singleton "<<force-mismatch-after-upload>>"
                lastIsolatedBlock <- Some "<<force-mismatch-after-upload>>"
            | _ -> ()
            // Re-upload the net-flag attribute when the highlighted
            // set changes (or after a fresh mesh upload). Cheap —
            // one float per vertex; for a 400k-poly macro that's
            // ~12MB but only runs on net click, not every frame.
            match cachedMesh with
            | Some mesh when hasUploadedAny && lastHighlightedNets <> toggle.HighlightedNets ->
                let n = mesh.Vertices.Length
                let flags = Array.zeroCreate<float32> n
                if not toggle.HighlightedNets.IsEmpty then
                    let hits = LayerPainter.highlightedPolyKeys lib flat toggle.HighlightedNets
                    if hits.Count > 0 then
                        for i in 0 .. n - 1 do
                            let polyIdx = mesh.VertexPolyIndex.[i]
                            if polyIdx >= 0 && polyIdx < flat.Length then
                                let p = flat.[polyIdx]
                                if hits.Contains((p.SourceStructure, p.SourceIndex)) then
                                    flags.[i] <- 1.0f
                g.BindBuffer(GLEnum.ArrayBuffer, netVbo)
                g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(flags), GLEnum.DynamicDraw)
                lastHighlightedNets <- toggle.HighlightedNets
            | _ -> ()
            // Same dance for the block-isolation flag. Recomputed
            // when IsolatedBlock changes; one float per vertex.
            match cachedMesh with
            | Some mesh when hasUploadedAny && lastIsolatedBlock <> toggle.IsolatedBlock ->
                let n = mesh.Vertices.Length
                let flags = Array.zeroCreate<float32> n
                match toggle.IsolatedBlock with
                | Some name ->
                    // Hierarchy.closure now consumes Rkt.Document; convert
                    // at the call site until the App's model migrates.
                    let closure =
                        Layout.Hierarchy.closure (lib) name
                    if not (Set.isEmpty closure) then
                        for i in 0 .. n - 1 do
                            let polyIdx = mesh.VertexPolyIndex.[i]
                            if polyIdx >= 0 && polyIdx < flat.Length then
                                let p = flat.[polyIdx]
                                if Set.contains p.SourceStructure closure then
                                    flags.[i] <- 1.0f
                | None -> ()
                g.BindBuffer(GLEnum.ArrayBuffer, blockVbo)
                g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(flags), GLEnum.DynamicDraw)
                lastIsolatedBlock <- toggle.IsolatedBlock
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
            g.BindBuffer(GLEnum.ArrayBuffer, vbo)
            g.EnableVertexAttribArray(0u)
            g.VertexAttribPointer(0u, 3, GLEnum.Float, false, strideBytes, nativeint 0)
            g.EnableVertexAttribArray(1u)
            g.VertexAttribPointer(1u, 3, GLEnum.Float, false, strideBytes, nativeint (3 * sizeof<float32>))
            g.EnableVertexAttribArray(2u)
            g.VertexAttribPointer(2u, 1, GLEnum.Float, false, strideBytes, nativeint (6 * sizeof<float32>))
            // Net-highlight flag, one float per vertex from netVbo.
            g.BindBuffer(GLEnum.ArrayBuffer, netVbo)
            g.EnableVertexAttribArray(3u)
            g.VertexAttribPointer(3u, 1, GLEnum.Float, false, uint32 sizeof<float32>, nativeint 0)
            // Block-isolation flag, one float per vertex from blockVbo.
            g.BindBuffer(GLEnum.ArrayBuffer, blockVbo)
            g.EnableVertexAttribArray(4u)
            g.VertexAttribPointer(4u, 1, GLEnum.Float, false, uint32 sizeof<float32>, nativeint 0)

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
            let highlightLoc = g.GetUniformLocation(program, "uHighlightActive")
            g.Uniform1(highlightLoc, if not toggle.HighlightedNets.IsEmpty then 1.0f else 0.0f)
            let isolateLoc = g.GetUniformLocation(program, "uIsolateActive")
            g.Uniform1(isolateLoc, if toggle.IsolatedBlock.IsSome then 1.0f else 0.0f)
            // Upload per-layer visibility as a uniform array. Cheap
            // (32 floats = 128 bytes) and runs every frame; toggle
            // changes show up next frame without touching the VBO.
            let visArr = Array.create 32 1.0f
            for KeyValue (layerKey, slot) in layerSlotMap do
                if slot >= 0 && slot < 32 then
                    visArr.[slot] <-
                        if Visibility.isLayerVisible toggle layerKey then 1.0f else 0.0f
            let visLoc = g.GetUniformLocation(program, "uLayerVis")
            g.Uniform1(visLoc, ReadOnlySpan<float32>(visArr))
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

            // ---- Ruler overlay ----
            // Substrate-level reference grid at z = -0.5 µm (just
            // below nwell). Span the FULL silicon bbox on each
            // axis (including negative regions) so a cell centered
            // around origin still shows the full ruler. Major
            // ticks every `step` µm via 1-2-5 nice-numbers picker;
            // minor ticks at step / 5. Major ticks are longer and
            // get numeric labels in the Render() Avalonia overlay.
            if rulerDirty && rulerProgram <> 0u then
                let xRange = float (rulerXMax - rulerXMin)
                let yRange = float (rulerYMax - rulerYMin)
                let longest = max xRange yRange
                let niceStep (range: float) =
                    if range <= 0.0 then 1.0
                    else
                        // ~4 major ticks across the longer axis;
                        // 1-2-5 series feels natural for cell sizes
                        // (e.g. 23 µm → 5 µm step, 80 µm → 10 µm,
                        // 1.8 µm → 0.5 µm).
                        let target = range / 4.0
                        let mag = System.Math.Pow(10.0, floor (log10 target))
                        let ratio = target / mag
                        let mult =
                            if ratio < 1.5 then 1.0
                            elif ratio < 3.5 then 2.0
                            elif ratio < 7.5 then 5.0
                            else 10.0
                        mag * mult
                // Major step: prefer 5 µm so tick numbers land on
                // 0, 5, 10, 15... For tiny cells (< 5 µm) fall
                // back to 1 µm or 0.5 µm so SOME ticks fit. For
                // very large macros (> 200 µm) the 1-2-5 picker
                // kicks in to avoid hundreds of labels.
                let step =
                    if longest >= 200.0 then niceStep longest
                    elif longest >= 5.0 then 5.0
                    elif longest >= 1.0 then 1.0
                    elif longest >= 0.2 then 0.2
                    else niceStep longest
                rulerStep <- step
                let minor = step / 5.0
                let z = -0.5f
                let majorTick = float32 (step * 0.20)
                let minorTick = float32 (step * 0.08)
                let xColor = struct (1.0f, 0.35f, 0.35f)
                let yColor = struct (0.35f, 1.0f, 0.35f)
                let verts = ResizeArray<float32>()
                let push (x: float32) (y: float32) (zz: float32) (struct (r, g, b)) =
                    verts.Add x;  verts.Add y;  verts.Add zz
                    verts.Add r;  verts.Add g;  verts.Add b
                let snap (lo: float) (hi: float) (s: float) : float seq =
                    seq {
                        let first = ceil (lo / s) * s
                        let mutable t = first
                        while t <= hi + s * 1e-6 do
                            yield t
                            t <- t + s
                    }
                // Anchor the ruler to the cell's bbox corner, NOT
                // world (0, 0). Foundry cells (e.g. SkyWater 4T1R)
                // have their design origin sitting INSIDE the cell
                // — using world origin makes the ruler cross the
                // middle of the cell and reads as wrong. The
                // 'starting at 0,0,0' user ask is best satisfied
                // by treating the bbox corner as the ruler's 0
                // and counting along the cell. Tick values are
                // offsets from the corner (0, 5, 10, … µm).
                let cornerX = float32 rulerXMin
                let cornerY = float32 rulerYMin
                rulerCornerX <- cornerX
                rulerCornerY <- cornerY
                rulerMajorTickLen <- majorTick
                // Label scheme: place labels at world-space `step`
                // intervals (the 1-2-5 nice-numbers picker above
                // already chose `step` to give ~4-6 ticks across the
                // longer axis). Same per-axis count on small and
                // large cells; the count is purely a function of the
                // bbox and never of camera state, so labels do NOT
                // appear or disappear when the user zooms.
                let labelPositions (axisRange: float) : float[] =
                    if axisRange <= 0.0 then [||]
                    else
                        let result = ResizeArray<float>()
                        let mutable t = 0.0
                        while t <= axisRange + step * 1e-6 do
                            result.Add t
                            t <- t + step
                        result.ToArray()
                // Long tick marks coincide with labels — gives the
                // user a clear visual anchor at every numbered tick.
                let outerMajorPositions (axisRange: float) : float seq =
                    labelPositions axisRange :> _
                let minorPositions (axisRange: float) : float seq =
                    seq {
                        let s = if axisRange < 1.0 then niceStep axisRange / 5.0 else 1.0
                        let mutable t = 0.0
                        while t <= axisRange + s * 1e-6 do
                            yield t
                            t <- t + s
                    }
                // X axis spine + ticks along the bottom edge.
                if xRange > 0.0 then
                    push cornerX cornerY z xColor
                    push (cornerX + float32 xRange) cornerY z xColor
                    for t in minorPositions xRange do
                        let tf = cornerX + float32 t
                        push tf cornerY               z xColor
                        push tf (cornerY - minorTick) z xColor
                    for t in outerMajorPositions xRange do
                        let tf = cornerX + float32 t
                        push tf cornerY               z xColor
                        push tf (cornerY - majorTick) z xColor
                    rulerXMajors <- labelPositions xRange
                else
                    rulerXMajors <- [||]
                // Y axis spine + ticks along the left edge.
                if yRange > 0.0 then
                    push cornerX cornerY                    z yColor
                    push cornerX (cornerY + float32 yRange) z yColor
                    for t in minorPositions yRange do
                        let tf = cornerY + float32 t
                        push cornerX               tf z yColor
                        push (cornerX - minorTick) tf z yColor
                    for t in outerMajorPositions yRange do
                        let tf = cornerY + float32 t
                        push cornerX               tf z yColor
                        push (cornerX - majorTick) tf z yColor
                    rulerYMajors <- labelPositions yRange
                else
                    rulerYMajors <- [||]
                // Explicit origin marker at the bbox corner — a
                // small white cross so the user can spot where the
                // ruler's "0" sits even at oblique camera angles.
                let originColor = struct (1.0f, 1.0f, 1.0f)
                let originSize = float32 (max minor 0.05)
                push (cornerX - originSize) cornerY               z originColor
                push (cornerX + originSize) cornerY               z originColor
                push cornerX               (cornerY - originSize) z originColor
                push cornerX               (cornerY + originSize) z originColor
                rulerVertexCount <- verts.Count / 6
                if rulerVertexCount > 0 then
                    let arr = verts.ToArray()
                    g.BindVertexArray rulerVao
                    g.BindBuffer(GLEnum.ArrayBuffer, rulerVbo)
                    g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(arr), GLEnum.StaticDraw)
                rulerDirty <- false

            // Text quad geometry rebuilt EVERY frame so glyph
            // size scales with camera distance, not the cell —
            // user expectation: ruler numbers stay readable at
            // the same on-screen pixel height regardless of zoom.
            // Cheap: ~20 majors × ~3 chars × 6 verts = ~360 verts.
            if rulerXMajors.Length > 0 || rulerYMajors.Length > 0 then
                let xColor = struct (1.0f, 0.35f, 0.35f)
                let yColor = struct (0.35f, 1.0f, 0.35f)
                // Font size in world µm — fixed to the drawing,
                // not to the screen, so labels don't bunch up when
                // zoomed out. Sized to fit between 1-µm-spaced
                // labels: char height = 0.55 µm, width = 0.40 µm,
                // so '10' (~0.85 µm wide) clears the next tick.
                // Sub-µm cells scale down via rulerStep.
                let baseHeight =
                    if rulerStep >= 1.0 then 0.55
                    else rulerStep * 0.55
                let charH = float32 baseHeight
                let charW = charH * (5.0f / 7.0f)
                let charGap = charW * 0.2f
                let labelGap = charH * 0.5f
                let textVerts = ResizeArray<float32>()
                let zText = -0.5f
                let pushQuad
                        (x0: float32) (y0: float32)
                        (x1: float32) (y1: float32)
                        (u0: float32) (v0: float32)
                        (u1: float32) (v1: float32)
                        (struct (r, gr, b)) =
                    let push3 px py uvx uvy =
                        textVerts.Add px;  textVerts.Add py;  textVerts.Add zText
                        textVerts.Add uvx; textVerts.Add uvy
                        textVerts.Add r;   textVerts.Add gr;  textVerts.Add b
                    push3 x0 y1 u0 v0
                    push3 x1 y1 u1 v0
                    push3 x1 y0 u1 v1
                    push3 x0 y1 u0 v0
                    push3 x1 y0 u1 v1
                    push3 x0 y0 u0 v1
                let pushString
                        (text: string)
                        (originX: float32) (originY: float32)
                        (color: struct (float32 * float32 * float32)) =
                    let mutable cx = originX
                    for c in text do
                        let glyph = fontGlyphIndex c
                        let u0 = float32 (glyph * FONT_GLYPH_W) / float32 FONT_ATLAS_W
                        let u1 = float32 ((glyph + 1) * FONT_GLYPH_W) / float32 FONT_ATLAS_W
                        pushQuad cx originY (cx + charW) (originY + charH) u0 0.0f u1 1.0f color
                        cx <- cx + charW + charGap
                let formatLabel (v: float) =
                    // 1-µm-step ticks need integer formatting; sub-µm
                    // fallback (cells < 1 µm) gets one decimal.
                    let dec = if abs v < 1e-6 || abs (v - round v) < 1e-6 then 0 else 1
                    let s = v.ToString("F" + string dec, System.Globalization.CultureInfo.InvariantCulture)
                    if dec > 0 && s.EndsWith ".0" then s.Substring(0, s.Length - 2) else s
                let cornerX = rulerCornerX
                let cornerY = rulerCornerY
                let majorTick = rulerMajorTickLen
                if rulerXMajors.Length > 0 then
                    for v in rulerXMajors do
                        let txt = formatLabel v
                        let approxW = float32 txt.Length * (charW + charGap) - charGap
                        let originX = cornerX + float32 v - approxW * 0.5f
                        let originY = cornerY - majorTick - charH - labelGap
                        pushString txt originX originY xColor
                if rulerYMajors.Length > 0 then
                    for v in rulerYMajors do
                        let txt = formatLabel v
                        let approxW = float32 txt.Length * (charW + charGap) - charGap
                        let originX = cornerX - majorTick - approxW - labelGap
                        let originY = cornerY + float32 v - charH * 0.5f
                        pushString txt originX originY yColor
                textVertexCount <- textVerts.Count / 8
                if textVertexCount > 0 then
                    let arr = textVerts.ToArray()
                    g.BindVertexArray textVao
                    g.BindBuffer(GLEnum.ArrayBuffer, textVbo)
                    g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(arr), GLEnum.DynamicDraw)
            else
                textVertexCount <- 0

            if rulerVertexCount > 0 && rulerProgram <> 0u then
                g.UseProgram rulerProgram
                g.BindVertexArray rulerVao
                g.BindBuffer(GLEnum.ArrayBuffer, rulerVbo)
                let rulerStride = uint32 (6 * sizeof<float32>)
                g.EnableVertexAttribArray 0u
                g.VertexAttribPointer(0u, 3, GLEnum.Float, false, rulerStride, nativeint 0)
                g.EnableVertexAttribArray 1u
                g.VertexAttribPointer(1u, 3, GLEnum.Float, false, rulerStride, nativeint (3 * sizeof<float32>))
                let rulerLoc = g.GetUniformLocation(rulerProgram, "uMVP")
                let mvpArr2 = Matrix4x4Helpers.toFloatArray mvp
                g.UniformMatrix4(rulerLoc, 1u, false, ReadOnlySpan<float32>(mvpArr2))
                g.LineWidth 1.5f
                g.DrawArrays(GLEnum.Lines, 0, uint32 rulerVertexCount)

            // Ratlines — rebuild + draw at a fixed Z above the
            // metal stack so they float over the geometry. Only
            // when at least one net's ratline is on (visibility is
            // explicit per-net now). A tab with empty
            // VisibleRatlines pays no cost.
            let visibleRatlines = this.VisibleRatlines
            ratlineVertexCount <- 0
            if not visibleRatlines.IsEmpty && rulerProgram <> 0u then
                match this.Library with
                | Some lib ->
                    let routes = Net.Ratlines.compute lib (this.FlatPolygons)
                    let filtered =
                        routes |> Array.filter (fun r -> visibleRatlines.Contains r.Name)
                    // World-space DBU → user µm divisor.
                    let umPer = (float lib.Units.DbuNm * 1.0e-3)
                    // Ratline Z comes from the pin itself now — each
                    // endpoint sits at the top of its anchoring
                    // polygon's layer, with a small lift so it doesn't
                    // Z-fight the metal. Cross-layer hops slant.
                    let zLift = 0.10f
                    // amber, matches 2D ratline overlay color
                    let r = 1.0f
                    let g_ = 0.78f
                    let b = 0.25f
                    let verts = System.Collections.Generic.List<float32>()
                    // Walk the pre-computed rectilinear MST instead
                    // of all pin pairs. Matches the 2D RatlineOverlay
                    // path; collapses N(N-1)/2 line draws to N-1
                    // edges per net, which is the difference between
                    // a hairball and a readable overlay on power
                    // nets with hundreds of pins.
                    for route in filtered do
                        let pins = route.Pins
                        for edge in route.Mst do
                            if edge.From >= 0 && edge.From < pins.Length
                               && edge.To >= 0 && edge.To < pins.Length then
                                let pinI = pins.[edge.From]
                                let pinJ = pins.[edge.To]
                                let pi = pinI.Position
                                let pj = pinJ.Position
                                let xi = float32 (float pi.X * umPer)
                                let yi = float32 (float pi.Y * umPer)
                                let zi = float32 pinI.ZUm + zLift
                                let xj = float32 (float pj.X * umPer)
                                let yj = float32 (float pj.Y * umPer)
                                let zj = float32 pinJ.ZUm + zLift
                                verts.AddRange([| xi; yi; zi; r; g_; b |])
                                verts.AddRange([| xj; yj; zj; r; g_; b |])
                    if verts.Count > 0 then
                        let arr = verts.ToArray()
                        g.BindVertexArray ratlineVao
                        g.BindBuffer(GLEnum.ArrayBuffer, ratlineVbo)
                        g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(arr), GLEnum.DynamicDraw)
                        ratlineVertexCount <- verts.Count / 6
                | None -> ()
            if ratlineVertexCount > 0 then
                g.UseProgram rulerProgram
                g.BindVertexArray ratlineVao
                g.BindBuffer(GLEnum.ArrayBuffer, ratlineVbo)
                let stride = uint32 (6 * sizeof<float32>)
                g.EnableVertexAttribArray 0u
                g.VertexAttribPointer(0u, 3, GLEnum.Float, false, stride, nativeint 0)
                g.EnableVertexAttribArray 1u
                g.VertexAttribPointer(1u, 3, GLEnum.Float, false, stride, nativeint (3 * sizeof<float32>))
                let loc = g.GetUniformLocation(rulerProgram, "uMVP")
                let mvpArr = Matrix4x4Helpers.toFloatArray mvp
                g.UniformMatrix4(loc, 1u, false, ReadOnlySpan<float32>(mvpArr))
                g.LineWidth 2.0f
                g.DrawArrays(GLEnum.Lines, 0, uint32 ratlineVertexCount)
            if textVertexCount > 0 && textProgram <> 0u && fontTex <> 0u then
                g.UseProgram textProgram
                g.BindVertexArray textVao
                g.BindBuffer(GLEnum.ArrayBuffer, textVbo)
                let textStride = uint32 (8 * sizeof<float32>)
                g.EnableVertexAttribArray 0u
                g.VertexAttribPointer(0u, 3, GLEnum.Float, false, textStride, nativeint 0)
                g.EnableVertexAttribArray 1u
                g.VertexAttribPointer(1u, 2, GLEnum.Float, false, textStride, nativeint (3 * sizeof<float32>))
                g.EnableVertexAttribArray 2u
                g.VertexAttribPointer(2u, 3, GLEnum.Float, false, textStride, nativeint (5 * sizeof<float32>))
                let mvpArr3 = Matrix4x4Helpers.toFloatArray mvp
                let textMvpLoc = g.GetUniformLocation(textProgram, "uMVP")
                g.UniformMatrix4(textMvpLoc, 1u, false, ReadOnlySpan<float32>(mvpArr3))
                g.ActiveTexture GLEnum.Texture0
                g.BindTexture(GLEnum.Texture2D, fontTex)
                let fontLoc = g.GetUniformLocation(textProgram, "uFont")
                g.Uniform1(fontLoc, 0)
                g.DrawArrays(GLEnum.Triangles, 0, uint32 textVertexCount)
            // Stash MVP for the Avalonia text-overlay pass in
            // Render() — labels project the same camera the GL
            // ruler lines just drew.
            lastMvp <- mvp
        | _ -> ()
