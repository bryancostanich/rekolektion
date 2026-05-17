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
    /// Pressing on a route's track handle in Edit Routing mode.
    /// Suppresses orbit / pan so the cursor delta during the drag
    /// can be interpreted as segment slide rather than camera
    /// rotation.
    | RouteTrackDrag

/// Per-rect shift recipe used by a route-slide gesture. Each
/// pair (MxxX, MxxY) is the gesture-delta multiplier for that
/// coord: `r.X1' = r.X1 + Mx1X * dx + Mx1Y * dy` and so on. A
/// track slide fills only the perp axis (1D); a post drag fills
/// both axes (2D, no constraint).
type private SlideAdjust = {
    SourceIdx : int
    Mx1X      : int64
    Mx1Y      : int64
    My1X      : int64
    My1Y      : int64
    Mx2X      : int64
    Mx2Y      : int64
    My2X      : int64
    My2Y      : int64
}

/// Which kind of handle the user pressed — drives whether the
/// gesture is 1D (track: snap one delta axis to 0) or 2D (post).
type private SlideKind = TrackSlide | PostSlide

/// One endpoint anchor for a track slide. The dragged beam's
/// spine endpoint sits inside this anchor at press time; if the
/// drag moves the endpoint outside the anchor's bbox, viz emits
/// an extension rect on the anchor's layer to maintain the
/// electrical connection. Captured once at press so we don't
/// have to re-detect every frame.
///
/// Bbox + layer are stored directly rather than a Rectangle so
/// SRef-internal anchors work the same as top-cell anchors —
/// the anchor's source rect lives somewhere unmodifiable (inside
/// a child cell), but we still know its world bbox via the flat
/// poly walk, and that's all the extension generator needs.
type private AnchorInfo = {
    /// Endpoint at press time (one of a moved segment's spine ends).
    OrigEndpoint : Rekolektion.Viz.Core.Rkt.Types.Point
    /// Anchor's world-DBU bbox: (xMin, yMin, xMax, yMax).
    BboxDbu      : int64 * int64 * int64 * int64
    /// Layer to emit the extension rect on.
    Layer        : Rekolektion.Viz.Core.Rkt.Types.Layer
    /// How this endpoint moves under the drag delta `(dx, dy)`.
    /// new_endpoint.X = OrigEndpoint.X + EndpointMx * dx + EndpointMxY * dy
    /// new_endpoint.Y = OrigEndpoint.Y + EndpointMyX * dx + EndpointMy * dy
    /// For the dragged beam's spine endpoints these are derived from
    /// the segment's SlideAdjust + which end (Start / End) it is.
    EndpointMxX : int64
    EndpointMxY : int64
    EndpointMyX : int64
    EndpointMyY : int64
    /// Half perp thickness of the segment this anchor anchors. Used
    /// by `ComputeExtensionRect` to overhang the extension past the
    /// new endpoint into the wire's body (JOG-fix). Without this the
    /// extension stops at the wire's centerline and only the south /
    /// west half of the wire connects.
    DraggedSegHalfPerp : int64
}

/// In-flight slide state. Built on press, mutated each
/// PointerMoved, consumed on release.
type private RouteSlide = {
    Cell           : string
    Kind           : SlideKind
    /// Spine of the dragged segment for track slides — used to
    /// project the cursor delta onto the perp axis only.
    /// PostSlide: irrelevant, store Axis.X as a placeholder.
    Spine          : Rekolektion.Viz.Core.Routing.Detect.Axis
    LayerZ         : float32
    StartHitDbu    : Rekolektion.Viz.Core.Rkt.Types.Point
    /// All rects that move with the gesture.
    Adjusts        : SlideAdjust list
    /// Per-endpoint rail anchors for track slides. Each entry's
    /// extension rect re-emerges every move (size scales with the
    /// cumulative delta), so we don't store the extension itself —
    /// just the anchor it'll extend.
    Anchors        : AnchorInfo list
    /// Route geometry frozen at press time. PostSlide commit + live
    /// preview re-walk it to emit L-jog bridge rects at the dragged
    /// corner when attached segments' spine-only stretch leaves a
    /// perpendicular gap. None for synthetic slides built outside the
    /// detector (none currently).
    Route          : Rekolektion.Viz.Core.Routing.Detect.Route option
    /// Index into `Route.Posts` for the post being dragged
    /// (PostSlide). None for TrackSlide — track drags don't generate
    /// corner jogs at the dragged segment itself.
    PostIdx        : int option
    mutable LastDxDbu : int64
    mutable LastDyDbu : int64
}

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
    // Edit-routing hover state. Updated on PointerMoved while
    // EditRoutingMode is on; cleared otherwise. Owned by the canvas
    // (not the Elmish Model) because hover changes every pointer
    // tick — round-tripping through Update.fs would re-render the
    // whole view per tick, which is wasteful for purely visual
    // overlay state.
    let mutable hoveredRoute : Rekolektion.Viz.Core.Routing.Detect.Route option = None
    let mutable hoveredRouteLayerZ : float32 = 0.0f
    let mutable hoverVao : uint32 = 0u
    let mutable hoverVbo : uint32 = 0u
    // Track-slide drag state. `routeSlide` carries the immutable
    // snapshot from press time + a mutable cumulative delta.
    // `dragLiveDoc` / `dragLiveFlat` hold the speculative geometry
    // so the renderer can paint the in-flight position each frame
    // without round-tripping through the Elmish loop. Cleared on
    // release once the commit Msg has updated the model.
    let mutable routeSlide : RouteSlide option = None
    let mutable dragLiveDoc : Rekolektion.Viz.Core.Rkt.Types.Document option = None
    let mutable dragLiveFlat : Layout.Flatten.FlatPolygon array = [||]
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
    // Top cell name the camera was last fitted to. Used to skip
    // refitting when the user is just editing the same file —
    // refitting on every commit yanks the viewport and resets the
    // ruler bounds.
    let mutable lastFittedTopCell : string = ""
    // Length of the FlatPolygons array used in the last FitCameraTo
    // call. Used to detect "the first fit was against an empty
    // array because LibraryProperty fired before FlatPolygons did"
    // and force a refit once the geometry is actually available.
    let mutable lastFittedFlatLen : int = 0
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
        // Camera re-fit means re-frame from scratch — the user's
        // accumulated wheel zoom from a different file (or session)
        // would otherwise compound with the new extent and produce
        // a tiny view of a tiny chunk, with perspective gone
        // pathological. Reset to 1.0 here so the new file lands
        // framed at its own bbox.
        zoom <- 1.0
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
            // Mesh is always stale on either change. The camera fit
            // (which resets ruler bounds too) is much pickier: we
            // only want it on a NEW FILE, not on every edit of the
            // same file. Compare the top-cell NAME — that's stable
            // across edits but changes between files.
            meshDirty <- true
            match this.Library with
            | Some lib ->
                let topName =
                    match lib.TopCell with
                    | Some n -> n
                    | None ->
                        match lib.Cells with
                        | c :: _ -> c.Name
                        | _ -> ""
                let flat = this.FlatPolygons
                // Refit conditions:
                //   1. Top-cell name changed → new file → fit it.
                //   2. Last fit was against an empty FlatPolygons
                //      (LibraryProperty fires before FlatPolygons
                //      does on first load; the resulting fit has
                //      bbox=0 + dead ruler). Force a refit once
                //      real geometry shows up.
                // Otherwise (same file, edit committed): leave
                // camera + ruler alone so the user's view doesn't
                // yank around mid-session.
                let topChanged = topName <> lastFittedTopCell
                let recoverFromEmpty =
                    not topChanged
                    && lastFittedFlatLen = 0
                    && flat.Length > 0
                if topChanged || recoverFromEmpty then
                    lastFittedTopCell <- topName
                    lastFittedFlatLen <- flat.Length
                    this.FitCameraTo lib flat
                // Ruler bbox tracks the CURRENT FlatPolygons every
                // change, decoupled from the camera fit — otherwise
                // a tab switch or reload that doesn't satisfy the
                // refit gate leaves the ruler holding the previous
                // file's bbox. Mirrors 2D's per-render bbox compute.
                if flat.Length > 0 then
                    let mutable xMin = System.Single.MaxValue
                    let mutable xMax = System.Single.MinValue
                    let mutable yMin = System.Single.MaxValue
                    let mutable yMax = System.Single.MinValue
                    let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                    for fp in flat do
                        for p in fp.Points do
                            let x = float32 (float p.X * umPerDbu)
                            let y = float32 (float p.Y * umPerDbu)
                            if x < xMin then xMin <- x
                            if x > xMax then xMax <- x
                            if y < yMin then yMin <- y
                            if y > yMax then yMax <- y
                    if xMin <= xMax && yMin <= yMax then
                        if xMin <> rulerXMin || xMax <> rulerXMax
                           || yMin <> rulerYMin || yMax <> rulerYMax then
                            rulerXMin <- xMin
                            rulerXMax <- xMax
                            rulerYMin <- yMin
                            rulerYMax <- yMax
                            rulerDirty <- true
            | None ->
                lastFittedTopCell <- ""
                lastFittedFlatLen <- 0
            this.RequestNextFrameRendering()
        elif e.Property = StackCanvasControl.ToggleProperty then
            this.RequestNextFrameRendering()

    // ---- Pointer-driven orbit / pan + wheel zoom ----

    /// Hit-test screen point against the rendered track handles for
    /// `route` at `layerZ`. Each track handle is a perp-axis bar at
    /// the segment midpoint (matches what the renderer draws).
    /// Returns the segment index whose handle is closest to the
    /// cursor in screen pixels, when within the snap radius.
    /// Pure of canvas state besides MVP / bounds / Library — caller
    /// passes the route explicitly so this can also serve as a
    /// "stay hovering" check on the PRIOR route during pointer
    /// move (otherwise the bar tips, which extend outside the
    /// wire bbox, lose the hover and the click fails to capture).
    member private this.HitTestTrackHandleFor
            (route: Rekolektion.Viz.Core.Routing.Detect.Route)
            (layerZ: float32)
            (screen: Avalonia.Point)
            : int option =
        match this.Library with
        | Some lib ->
            let w = this.Bounds.Width
            let h = this.Bounds.Height
            if w < 1.0 || h < 1.0 then None
            else
                let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                let z = layerZ
                let mvp =
                    Matrix4x4Helpers.buildOrbitMvp
                        yawDeg pitchDeg zoom target extent (w, h)
                let projectToScreen (xUm: float32) (yUm: float32)
                        : (float * float) option =
                    let v =
                        Vector4.Transform(
                            Vector4(xUm, yUm, z, 1.0f),
                            mvp)
                    if v.W <= 1.0e-6f then None
                    else
                        let ndcX = float (v.X / v.W)
                        let ndcY = float (v.Y / v.W)
                        Some ((ndcX + 1.0) * 0.5 * w,
                              (1.0 - ndcY) * 0.5 * h)
                // Mirrors the renderer's track-bar geometry: bar
                // perpendicular to spine, centered on segment midpoint,
                // 0.50 µm half-length. Hit zone uses the bar's screen-
                // projected line; a click within 12 px counts.
                let trackHalfLenUm = 0.50f
                let snapPx = 12.0
                let mutable bestSeg : int option = None
                let mutable bestDist = System.Double.MaxValue
                for i in 0 .. route.Segments.Length - 1 do
                    let s = route.Segments.[i]
                    let p1Um, p2Um =
                        match s.Spine with
                        | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                            let midX =
                                float32 ((float s.Start.X + float s.End.X) * 0.5 * umPerDbu)
                            let cy = float32 (float s.Center * umPerDbu)
                            (midX, cy - trackHalfLenUm), (midX, cy + trackHalfLenUm)
                        | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                            let midY =
                                float32 ((float s.Start.Y + float s.End.Y) * 0.5 * umPerDbu)
                            let cx = float32 (float s.Center * umPerDbu)
                            (cx - trackHalfLenUm, midY), (cx + trackHalfLenUm, midY)
                    let p1Screen = projectToScreen (fst p1Um) (snd p1Um)
                    let p2Screen = projectToScreen (fst p2Um) (snd p2Um)
                    match p1Screen, p2Screen with
                    | Some (x1, y1), Some (x2, y2) ->
                        // Distance from screen.X/Y to the line segment
                        // (x1,y1)-(x2,y2). Standard parametric form.
                        let dx = x2 - x1
                        let dy = y2 - y1
                        let len2 = dx * dx + dy * dy
                        let t =
                            if len2 < 1.0e-6 then 0.0
                            else
                                let raw =
                                    ((screen.X - x1) * dx + (screen.Y - y1) * dy) / len2
                                max 0.0 (min 1.0 raw)
                        let projX = x1 + dx * t
                        let projY = y1 + dy * t
                        let dCx = screen.X - projX
                        let dCy = screen.Y - projY
                        let dist = sqrt (dCx * dCx + dCy * dCy)
                        if dist < snapPx && dist < bestDist then
                            bestDist <- dist
                            bestSeg <- Some i
                    | _ -> ()
                bestSeg
        | _ -> None

    /// Unproject a screen pixel onto the world plane at `zPlane`
    /// (µm), returning DBU coords. Used by the route-slide drag to
    /// convert cursor positions during the gesture into world-space
    /// deltas. None when the canvas isn't sized, the MVP isn't
    /// invertible, or the ray is parallel to / behind Z.
    member private this.UnprojectAtZ
            (screen: Avalonia.Point) (zPlane: float32)
            : Rekolektion.Viz.Core.Rkt.Types.Point option =
        match this.Library with
        | None -> None
        | Some lib ->
            let w = this.Bounds.Width
            let h = this.Bounds.Height
            if w < 1.0 || h < 1.0 then None
            else
                let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                let dbuPerUm = 1.0 / umPerDbu
                let ndcX = float32 (2.0 * screen.X / w - 1.0)
                let ndcY = float32 (1.0 - 2.0 * screen.Y / h)
                let mvp =
                    Matrix4x4Helpers.buildOrbitMvp
                        yawDeg pitchDeg zoom target extent (w, h)
                match Matrix4x4.Invert(mvp) with
                | false, _ -> None
                | true, inv ->
                    let unproj (z: float32) =
                        let v = Vector4(ndcX, ndcY, z, 1.0f)
                        let r = Vector4.Transform(v, inv)
                        Vector3(r.X / r.W, r.Y / r.W, r.Z / r.W)
                    let nearW = unproj -1.0f
                    let farW = unproj 1.0f
                    let rayO = nearW
                    let rayD = Vector3.Normalize(farW - nearW)
                    if MathF.Abs(rayD.Z) <= 1.0e-6f then None
                    else
                        let t = (zPlane - rayO.Z) / rayD.Z
                        if t < 0.0f then None
                        else
                            let px = rayO.X + rayD.X * t
                            let py = rayO.Y + rayD.Y * t
                            Some
                                ({ X = int64 (float px * dbuPerUm)
                                   Y = int64 (float py * dbuPerUm) }
                                 : Rekolektion.Viz.Core.Rkt.Types.Point)

    /// Snap a DBU value to the SKY130 manufacturing grid (5 nm).
    /// Round-half-to-zero so positive and negative values snap
    /// symmetrically.
    static member private SnapDbu (v: int64) : int64 =
        let g = 5L
        let off = if v >= 0L then g / 2L else -(g / 2L)
        ((v + off) / g) * g

    /// Apply a set of `SlideAdjust` recipes to `cellName`'s rects
    /// in `doc`, shifting each rect's chosen coords by `(dx, dy)`.
    /// Used by both the live preview (canvas-side speculative
    /// state) and the commit Msg (Update.fs persistence).
    static member private ApplyAdjustsToDoc
            (doc: Rekolektion.Viz.Core.Rkt.Types.Document)
            (cellName: string)
            (adjusts: SlideAdjust list)
            (dx: int64)
            (dy: int64)
            : Rekolektion.Viz.Core.Rkt.Types.Document =
        let bySource =
            adjusts
            |> List.map (fun a -> a.SourceIdx, a)
            |> Map.ofList
        let cells' =
            doc.Cells
            |> List.map (fun c ->
                if c.Name <> cellName then c
                else
                    let elems' =
                        c.Elements
                        |> List.mapi (fun i el ->
                            match Map.tryFind i bySource, el with
                            | Some a, Rekolektion.Viz.Core.Rkt.Types.RectEl r ->
                                let r' =
                                    { r with
                                        X1 = r.X1 + a.Mx1X * dx + a.Mx1Y * dy
                                        Y1 = r.Y1 + a.My1X * dx + a.My1Y * dy
                                        X2 = r.X2 + a.Mx2X * dx + a.Mx2Y * dy
                                        Y2 = r.Y2 + a.My2X * dx + a.My2Y * dy }
                                Rekolektion.Viz.Core.Rkt.Types.RectEl r'
                            | _ -> el)
                    { c with Elements = elems' })
        { doc with Cells = cells' }

    /// Property list stamped on every viz-emitted bridge / anchor-
    /// extension rect. The Value encodes the OWNING POSITION (post
    /// for PostSlide L-jog, anchor endpoint for TrackSlide extension)
    /// as `"x,y"`. The reaper in `Msg.RouteSlideCommit` matches new
    /// extensions' tag values against existing rects' tags and reaps
    /// only the matching ones — so a drag at the top corner doesn't
    /// wipe out a prior drag's bridges at the bottom corner. Tag is
    /// a no-op semantically — Magic / LVS / DRC ignore unknown props.
    static member private BridgePropsAt
            (ownerX: int64) (ownerY: int64)
            : Rekolektion.Viz.Core.Rkt.Types.Property list =
        [ { Key = "viz:bridge"
            Value =
                Rekolektion.Viz.Core.Rkt.Types.PvString
                    (sprintf "%d,%d" ownerX ownerY) } ]

    /// Zero recipe — coords don't move. Building block for the
    /// per-handle adjust constructors below.
    static member private ZeroAdjust (sourceIdx: int) : SlideAdjust =
        { SourceIdx = sourceIdx
          Mx1X = 0L; Mx1Y = 0L
          My1X = 0L; My1Y = 0L
          Mx2X = 0L; Mx2Y = 0L
          My2X = 0L; My2Y = 0L }

    /// Recipe for the dragged segment of a TRACK slide. Track
    /// slides constrain motion to the perpendicular-to-spine axis,
    /// so the dragged segment shifts both perp-axis coords only.
    static member private TrackDraggedAdjust
            (segSourceIdx: int)
            (spine: Rekolektion.Viz.Core.Routing.Detect.Axis)
            : SlideAdjust =
        let z = StackCanvasControl.ZeroAdjust segSourceIdx
        match spine with
        | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
            // Spine X (horizontal) — slides Y, so Y1 and Y2 follow dy.
            { z with My1Y = 1L; My2Y = 1L }
        | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
            // Spine Y (vertical) — slides X, so X1 and X2 follow dx.
            { z with Mx1X = 1L; Mx2X = 1L }

    /// Recipe for a perpendicular-neighbor segment at a post the
    /// dragged segment touches. Stretches/shrinks the neighbor's
    /// single endpoint that meets the post so the corner stays
    /// connected as the dragged segment slides. Returns None for
    /// collinear neighbors (skipped for v1 — sliding through a
    /// collinear chain would distort the chain's rects).
    ///
    /// We can't compare the neighbor's endpoint coord against the
    /// post coord exactly: the SKY130 `place_wire` JOG fix extends
    /// each rect a half-width past the L corner, so the neighbor's
    /// rect boundary sits HALFWIDTH outside the centerline post.
    /// Instead, pick the endpoint that falls INSIDE the dragged
    /// segment's perp-axis extent — the other one is far away.
    static member private NeighborAdjust
            (neighbor: Rekolektion.Viz.Core.Routing.Detect.Segment)
            (dragged: Rekolektion.Viz.Core.Routing.Detect.Segment)
            : SlideAdjust option =
        if neighbor.Spine = dragged.Spine then None
        else
            // L-corner discriminator: the neighbor is an L-corner
            // (and should follow the slide) when ONE of its spine
            // endpoints sits AT the dragged beam's centerline —
            // within the JOG-fix overhang (= half the dragged
            // beam's perp width). If NEITHER endpoint is "at" the
            // centerline, the neighbor passes THROUGH (T-junction
            // crosser) and stays put for the anchor / extension
            // system to handle.
            let z = StackCanvasControl.ZeroAdjust neighbor.SourceIndex
            match dragged.Spine with
            | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                // Dragged horizontal. Neighbor vertical. JOG-fix
                // overhang is half the dragged horizontal's
                // Y-extent (since the perp-axis here is Y).
                let halfH =
                    (dragged.YMax - dragged.YMin) / 2L
                let nearMin =
                    abs (neighbor.YMin - dragged.Center) <= halfH
                let nearMax =
                    abs (neighbor.YMax - dragged.Center) <= halfH
                if nearMin then Some { z with My1Y = 1L }
                elif nearMax then Some { z with My2Y = 1L }
                else None
            | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                // Dragged vertical. Neighbor horizontal. Overhang
                // is half the dragged vertical's X-extent.
                let halfV =
                    (dragged.XMax - dragged.XMin) / 2L
                let nearMin =
                    abs (neighbor.XMin - dragged.Center) <= halfV
                let nearMax =
                    abs (neighbor.XMax - dragged.Center) <= halfV
                if nearMin then Some { z with Mx1X = 1L }
                elif nearMax then Some { z with Mx2X = 1L }
                else None

    /// Cascade NeighborAdjusts across the route: for each segment
    /// already in `baseAdjusts`, walk every post it's attached to
    /// (except `originPostIdx`, the post the user dragged from —
    /// neighbors there are already in `baseAdjusts`), find
    /// perpendicular L-corner segments at those posts, and add
    /// their NeighborAdjust recipes too. Without this cascade, a
    /// beam slide propagates its corner-trim only at the press
    /// point — anything attached to the FAR end of the beam (a
    /// rail at the top of a vertical beam, etc) stays put and the
    /// route visibly "T-junctions" with a stub where the corner
    /// used to align. Deduped by source index against the existing
    /// adjusts so we don't double-stretch a neighbor.
    static member private CascadeNeighborAdjusts
            (route: Rekolektion.Viz.Core.Routing.Detect.Route)
            (baseAdjusts: SlideAdjust list)
            (originPostIdx: int option)
            : SlideAdjust list =
        let baseIds =
            baseAdjusts
            |> List.map (fun a -> a.SourceIdx)
            |> Set.ofList
        let segByRouteIdx (i: int) = route.Segments.[i]
        let routeIdxBySource =
            route.Segments
            |> Array.mapi (fun i s -> s.SourceIndex, i)
            |> Map.ofArray
        let addedIds =
            System.Collections.Generic.HashSet<int>(baseIds)
        let additions = ResizeArray<SlideAdjust>()
        for a in baseAdjusts do
            match Map.tryFind a.SourceIdx routeIdxBySource with
            | None -> ()
            | Some myRouteIdx ->
                let mySeg = segByRouteIdx myRouteIdx
                for postIdx in 0 .. route.Posts.Length - 1 do
                    if Some postIdx <> originPostIdx then
                        let p = route.Posts.[postIdx]
                        if List.contains myRouteIdx p.AttachedSegments then
                            for otherRouteIdx in p.AttachedSegments do
                                if otherRouteIdx <> myRouteIdx then
                                    let other = segByRouteIdx otherRouteIdx
                                    if not (addedIds.Contains other.SourceIndex) then
                                        match StackCanvasControl.NeighborAdjust
                                                  other mySeg with
                                        | Some adj ->
                                            additions.Add adj
                                            addedIds.Add other.SourceIndex |> ignore
                                        | None -> ()
        additions |> List.ofSeq

    /// Walk `route.Posts` for every post that the dragged segment
    /// (by `route.Segments` index) touches, collect the
    /// perpendicular-neighbor adjusts. Deduped by source index so a
    /// neighbor reachable via multiple posts (rare) gets one entry.
    static member private BuildSlideAdjusts
            (route: Rekolektion.Viz.Core.Routing.Detect.Route)
            (draggedRouteIdx: int)
            : SlideAdjust list =
        let dragged = route.Segments.[draggedRouteIdx]
        let head = StackCanvasControl.TrackDraggedAdjust
                       dragged.SourceIndex dragged.Spine
        let neighbors =
            route.Posts
            |> Array.toList
            |> List.collect (fun p ->
                if not (List.contains draggedRouteIdx p.AttachedSegments) then []
                else
                    p.AttachedSegments
                    |> List.filter (fun i -> i <> draggedRouteIdx
                                          && i < route.Segments.Length)
                    |> List.choose (fun i ->
                        StackCanvasControl.NeighborAdjust
                            route.Segments.[i] dragged))
            |> List.distinctBy (fun a -> a.SourceIdx)
        head :: neighbors

    /// Detect risers ("knuckles") sitting on the dragged beam and
    /// emit recipes that track the riser geometry + any wires
    /// connected to it on the OTHER metal layer.
    ///
    /// A riser is a via stack: a small via cut on a contact/via
    /// layer (datatype 44) sandwiched between metal pads on the
    /// via's two connected metal layers (datatype 20). Detection
    /// is VIA-ANCHORED: we only emit recipes for rects that are
    /// demonstrably part of a via stack — preventing unrelated
    /// rects that happen to overlap the beam's X range from getting
    /// dragged along (the symptom of v1's looser heuristic).
    ///
    /// Cross-layer cascade: if a riser bottom (or top) pad sits at
    /// the end of a longer wire on its layer, that wire's near-end
    /// spine-stretches with the beam — same pattern as the
    /// in-plane L-corner cascade but propagated through the via.
    /// Example: met3 beam with a riser to a met2 vertical bus
    /// below — drag met3 down, the met2 bus's top end (which was
    /// at the riser pad) tracks the new pad position.
    ///
    /// Excludes the beam's own SourceIndex so the rigid-translate
    /// recipe doesn't double-apply on top of `BuildSlideAdjusts`'s
    /// beam recipe.
    static member private BuildRiserAdjusts
            (doc: Rekolektion.Viz.Core.Rkt.Types.Document)
            (cellName: string)
            (beam: Rekolektion.Viz.Core.Routing.Detect.Segment)
            : SlideAdjust list =
        match doc.Cells |> List.tryFind (fun c -> c.Name = cellName) with
        | None -> []
        | Some cell ->
            let beamPerpExtent =
                match beam.Spine with
                | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                    beam.YMax - beam.YMin
                | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                    beam.XMax - beam.XMin
            let perpTol = beamPerpExtent
            // Index rects by (sourceIdx, gdsLayer, gdsDt, bbox).
            let rects =
                cell.Elements
                |> List.mapi (fun i el -> i, el)
                |> List.choose (fun (i, el) ->
                    match el with
                    | Rekolektion.Viz.Core.Rkt.Types.RectEl r ->
                        let n, d =
                            Rekolektion.Viz.Core.Rkt.ToGds.layerToGds r.Layer
                        let xMin = min r.X1 r.X2
                        let xMax = max r.X1 r.X2
                        let yMin = min r.Y1 r.Y2
                        let yMax = max r.Y1 r.Y2
                        Some (i, n, d, xMin, yMin, xMax, yMax)
                    | _ -> None)
                |> List.toArray
            // bbox-contains test: does outer fully contain inner?
            let contains
                    (oxMin, oyMin, oxMax, oyMax)
                    (ixMin, iyMin, ixMax, iyMax) =
                ixMin >= oxMin && iyMin >= oyMin
                && ixMax <= oxMax && iyMax <= oyMax
            // Vias: datatype 44 rects sitting in-line with the beam
            // AND with their perp centerline near the beam centerline.
            let perpCenter (xMin, yMin, xMax, yMax) =
                match beam.Spine with
                | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                    (yMin + yMax) / 2L
                | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                    (xMin + xMax) / 2L
            let inLine (xMin, yMin, xMax, yMax) =
                match beam.Spine with
                | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                    xMin >= beam.XMin && xMax <= beam.XMax
                | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                    yMin >= beam.YMin && yMax <= beam.YMax
            let viaCuts =
                rects
                |> Array.filter (fun (_, _, dt, xMin, yMin, xMax, yMax) ->
                    let bbox = (xMin, yMin, xMax, yMax)
                    dt = 44
                    && inLine bbox
                    && abs (perpCenter bbox - beam.Center) <= perpTol)
            // For each via, its two connected metal layers (sky130
            // convention: via on layer N/44 connects (N,20) + (N+1,20)).
            // Adjacent-metal candidates come from those layer numbers.
            let adjusts = ResizeArray<SlideAdjust>()
            let addedIds =
                System.Collections.Generic.HashSet<int>()
            let add idx =
                if idx <> beam.SourceIndex && addedIds.Add idx then
                    adjusts.Add (StackCanvasControl.TrackDraggedAdjust
                                     idx beam.Spine)
            let spineStretchTowardPad
                    (wireIdx: int)
                    (wireBbox: int64 * int64 * int64 * int64)
                    (padBbox: int64 * int64 * int64 * int64) =
                // Wire is a longer rect on the same layer as `padBbox`
                // with ONE of its spine ends sitting inside the pad.
                // Emit a spine-stretch recipe whose moving end is
                // whichever wire end is inside the pad bbox.
                let (wxMin, wyMin, wxMax, wyMax) = wireBbox
                let (pxMin, pyMin, pxMax, pyMax) = padBbox
                let wPerpAxisIsX =
                    (wxMax - wxMin) >= (wyMax - wyMin)
                let z = StackCanvasControl.ZeroAdjust wireIdx
                // beam.Spine is the BEAM's spine. The CROSS-LAYER
                // wire's near-end follows the beam's perp axis
                // (i.e. dy for X-spine beam, dx for Y-spine beam).
                if wPerpAxisIsX then
                    // Wire is horizontal (X-spine). Its end coord is
                    // an X end; near-pad = whichever X end is inside
                    // pad's X range.
                    let nearMin =
                        wxMin >= pxMin && wxMin <= pxMax
                    let nearMax =
                        wxMax >= pxMin && wxMax <= pxMax
                    match beam.Spine with
                    | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                        // Beam horizontal → perp is Y. Wire end moves
                        // in Y to track pad's Y shift.
                        if nearMin then
                            { z with My1Y = 1L; My2Y = 1L }
                            |> adjusts.Add
                        elif nearMax then
                            { z with My1Y = 1L; My2Y = 1L }
                            |> adjusts.Add
                    | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                        if nearMin then { z with Mx1X = 1L } |> adjusts.Add
                        elif nearMax then { z with Mx2X = 1L } |> adjusts.Add
                else
                    // Wire is vertical (Y-spine). Its end coord is a
                    // Y end.
                    let nearMin =
                        wyMin >= pyMin && wyMin <= pyMax
                    let nearMax =
                        wyMax >= pyMin && wyMax <= pyMax
                    match beam.Spine with
                    | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                        if nearMin then { z with My1Y = 1L } |> adjusts.Add
                        elif nearMax then { z with My2Y = 1L } |> adjusts.Add
                    | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                        if nearMin then
                            { z with Mx1X = 1L; Mx2X = 1L } |> adjusts.Add
                        elif nearMax then
                            { z with Mx1X = 1L; Mx2X = 1L } |> adjusts.Add
            for (vi, vn, _vd, vxMin, vyMin, vxMax, vyMax) in viaCuts do
                add vi
                let viaBbox = (vxMin, vyMin, vxMax, vyMax)
                // Two metal layers connected by this via (sky130).
                let metals = [| vn; vn + 1 |]
                for (mi, mn, md, mxMin, myMin, mxMax, myMax) in rects do
                    if md = 20 && Array.contains mn metals
                       && mi <> beam.SourceIndex then
                        let mBbox = (mxMin, myMin, mxMax, myMax)
                        if contains mBbox viaBbox then
                            // This is a riser pad: translate rigid.
                            if addedIds.Add mi then
                                adjusts.Add
                                    (StackCanvasControl.TrackDraggedAdjust
                                         mi beam.Spine)
                            // Cross-layer cascade: wires on this layer
                            // whose end touches THIS pad get spine-
                            // stretched to follow.
                            for (wi, wn, wd, wxMin, wyMin, wxMax, wyMax) in rects do
                                if wd = 20 && wn = mn && wi <> mi
                                   && wi <> beam.SourceIndex
                                   && not (addedIds.Contains wi) then
                                    let wBbox =
                                        (wxMin, wyMin, wxMax, wyMax)
                                    // Wire whose end is inside the
                                    // pad's bbox (one end overlap is
                                    // enough — wires that simply pass
                                    // through the pad without ending
                                    // there fail the end-check below).
                                    let endInPad =
                                        let endNearMinX =
                                            wxMin >= mxMin && wxMin <= mxMax
                                        let endNearMaxX =
                                            wxMax >= mxMin && wxMax <= mxMax
                                        let endNearMinY =
                                            wyMin >= myMin && wyMin <= myMax
                                        let endNearMaxY =
                                            wyMax >= myMin && wyMax <= myMax
                                        (endNearMinX || endNearMaxX)
                                        && (endNearMinY || endNearMaxY)
                                    if endInPad then
                                        addedIds.Add wi |> ignore
                                        spineStretchTowardPad wi wBbox mBbox
            adjusts |> List.ofSeq

    /// Recipe for one segment attached to a POST being dragged.
    /// Spine-axis-only stretch — the segment's near-to-post endpoint
    /// follows the gesture along the segment's own axis; perpendicular
    /// motion is absorbed by the OTHER attached segment at the corner
    /// plus an L-jog bridge rect emitted by `ComputeCornerJogs` to
    /// fill the resulting gap. Perp-sliding the whole segment was the
    /// prior behavior and broke far-end anchors (the bbox-extension
    /// "giant box" bug).
    static member private PostSegmentAdjust
            (seg: Rekolektion.Viz.Core.Routing.Detect.Segment)
            (post: Rekolektion.Viz.Core.Rkt.Types.Point)
            (_postAttachedCount: int)
            : SlideAdjust =
        let z = StackCanvasControl.ZeroAdjust seg.SourceIndex
        match seg.Spine with
        | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
            let distMin = abs (seg.XMin - post.X)
            let distMax = abs (seg.XMax - post.X)
            if distMin <= distMax then { z with Mx1X = 1L }
            else                       { z with Mx2X = 1L }
        | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
            let distMin = abs (seg.YMin - post.Y)
            let distMax = abs (seg.YMax - post.Y)
            if distMin <= distMax then { z with My1Y = 1L }
            else                       { z with My2Y = 1L }

    /// Build the adjust list for a post drag — every segment
    /// attached to `route.Posts.[postIdx]` gets a post-segment
    /// recipe. Deduped by source index.
    static member private BuildPostAdjusts
            (route: Rekolektion.Viz.Core.Routing.Detect.Route)
            (postIdx: int)
            : SlideAdjust list =
        if postIdx < 0 || postIdx >= route.Posts.Length then []
        else
            let p = route.Posts.[postIdx]
            let attachedCount = p.AttachedSegments.Length
            p.AttachedSegments
            |> List.choose (fun i ->
                if i < 0 || i >= route.Segments.Length then None
                else
                    Some (StackCanvasControl.PostSegmentAdjust
                            route.Segments.[i] p.Position attachedCount))
            |> List.distinctBy (fun a -> a.SourceIdx)

    /// Build L-jog bridge rects at a dragged corner post. After a
    /// PostSlide, each attached segment's near-to-post end has shifted
    /// only along its OWN spine — H seg corner moves in X by dx,
    /// V seg corner moves in Y by dy. The "new corner location" is
    /// `post + (dx, dy)`; the two attached segments don't reach it
    /// because spine-only stretch keeps each on its original perp
    /// axis (V seg's centerline X stays fixed, H seg's centerline Y
    /// stays fixed). The bridge is an L meeting at the new corner.
    ///
    /// For each H/V pair attached at the dragged post we emit:
    /// - A vertical leg centered at `newCornerX`, width = V seg's
    ///   perp thickness, spanning Y from H seg's centerline to
    ///   `newCornerY`.
    /// - A horizontal leg centered at `newCornerY`, height = H seg's
    ///   perp thickness, spanning X from V seg's centerline to
    ///   `newCornerX`.
    /// Either leg is skipped when its axis component is zero, so a
    /// pure-Y drag emits only the vertical leg and pure-X emits only
    /// the horizontal one. The legs intentionally overlap each
    /// attached segment at the corner-overhang region — those
    /// overlaps are hidden under the existing rects on screen, but
    /// they keep the bridge electrically continuous through the new
    /// corner area.
    static member private ComputeCornerJogs
            (route: Rekolektion.Viz.Core.Routing.Detect.Route)
            (postIdx: int)
            (_adjusts: SlideAdjust list)
            (dx: int64) (dy: int64)
            : Rekolektion.Viz.Core.Rkt.Types.Rectangle list =
        if postIdx < 0 || postIdx >= route.Posts.Length then []
        elif dx = 0L && dy = 0L then []
        else
            let post = route.Posts.[postIdx]
            let newCornerX = post.Position.X + dx
            let newCornerY = post.Position.Y + dy
            let validIdxs =
                post.AttachedSegments
                |> List.filter (fun i ->
                    i >= 0 && i < route.Segments.Length)
            let spineOf i = route.Segments.[i].Spine
            let hIdxs =
                validIdxs
                |> List.filter (fun i ->
                    spineOf i = Rekolektion.Viz.Core.Routing.Detect.Axis.X)
            let vIdxs =
                validIdxs
                |> List.filter (fun i ->
                    spineOf i = Rekolektion.Viz.Core.Routing.Detect.Axis.Y)
            let jogs = ResizeArray<Rekolektion.Viz.Core.Rkt.Types.Rectangle>()
            for hi in hIdxs do
                for vi in vIdxs do
                    let h = route.Segments.[hi]
                    let v = route.Segments.[vi]
                    // H seg centerline Y, V seg centerline X — these
                    // are the original corner coords. The new corner
                    // sits at (newCornerX, newCornerY).
                    let hCenterY = h.Center
                    let vCenterX = v.Center
                    let hHalfY = (h.YMax - h.YMin) / 2L
                    let vHalfX = (v.XMax - v.XMin) / 2L
                    let layer =
                        Rekolektion.Viz.Core.Rkt.OfGds.layerFromGds
                            h.Layer h.DataType
                    // Vertical leg: only when the corner moved in Y.
                    // Y extends past BOTH endpoints by H seg's half-
                    // height so the leg fully overlaps H stub at the
                    // bottom (proper JOG-fix corner) and fully
                    // overlaps the horizontal leg at the top.
                    if dy <> 0L then
                        let yLo = (min hCenterY newCornerY) - hHalfY
                        let yHi = (max hCenterY newCornerY) + hHalfY
                        jogs.Add
                            ({ Layer = layer
                               X1 = newCornerX - vHalfX
                               Y1 = yLo
                               X2 = newCornerX + vHalfX
                               Y2 = yHi
                               Net = None
                               Props =
                                   StackCanvasControl.BridgePropsAt
                                       post.Position.X post.Position.Y
                               Comments = [] }
                             : Rekolektion.Viz.Core.Rkt.Types.Rectangle)
                    // Horizontal leg: only when the corner moved in X.
                    // X extends past BOTH endpoints by V seg's half-
                    // width for the matching JOG-fix overhang at the
                    // V seg meeting and at the corner.
                    if dx <> 0L then
                        let xLo = (min vCenterX newCornerX) - vHalfX
                        let xHi = (max vCenterX newCornerX) + vHalfX
                        jogs.Add
                            ({ Layer = layer
                               X1 = xLo
                               Y1 = newCornerY - hHalfY
                               X2 = xHi
                               Y2 = newCornerY + hHalfY
                               Net = None
                               Props =
                                   StackCanvasControl.BridgePropsAt
                                       post.Position.X post.Position.Y
                               Comments = [] }
                             : Rekolektion.Viz.Core.Rkt.Types.Rectangle)
            jogs |> List.ofSeq

    /// Compute the (mx_x, mx_y, my_x, my_y) endpoint-motion
    /// multipliers for a segment under its SlideAdjust recipe.
    /// `whichEndIsStart`: true for the Start endpoint (X1 or Y1
    /// of the bbox depending on spine), false for End. The
    /// perp-axis coordinate is the segment's centerline, so it
    /// averages the two perp multipliers. In well-formed adjusts
    /// the two perp multipliers are equal (or both zero) — perp
    /// slide moves the whole rect, not just one corner.
    static member private EndpointMultipliers
            (seg: Rekolektion.Viz.Core.Routing.Detect.Segment)
            (adjust: SlideAdjust)
            (whichEndIsStart: bool)
            : int64 * int64 * int64 * int64 =
        match seg.Spine with
        | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
            // Endpoint X = X1 (start) or X2 (end). Endpoint Y =
            // centerline, depends on both Y1 and Y2.
            let mxX, mxY =
                if whichEndIsStart then adjust.Mx1X, adjust.Mx1Y
                else adjust.Mx2X, adjust.Mx2Y
            let myX = (adjust.My1X + adjust.My2X) / 2L
            let myY = (adjust.My1Y + adjust.My2Y) / 2L
            mxX, mxY, myX, myY
        | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
            let mxX = (adjust.Mx1X + adjust.Mx2X) / 2L
            let mxY = (adjust.Mx1Y + adjust.Mx2Y) / 2L
            let myX, myY =
                if whichEndIsStart then adjust.My1X, adjust.My1Y
                else adjust.My2X, adjust.My2Y
            mxX, mxY, myX, myY

    /// Find a same-layer rect anchor at the dragged beam's
    /// `endpoint`. Walks the FlatPolygons array (which has SRef
    /// transforms applied) so anchors INSIDE child cells (a power
    /// rail buried in a FET subcell) are detected just as well
    /// as top-cell rects.
    ///
    /// `topCellName` is the name of the cell containing the
    /// dragged beam. Polys whose source cell IS the top cell
    /// AND whose source index is in `excludeTopIds` are skipped
    /// (the dragged beam itself + perp-neighbors already in the
    /// slide-adjust list — they'd duplicate the extension).
    /// SRef-internal polys are never excluded.
    static member private FindAnchorAt
            (doc: Rekolektion.Viz.Core.Rkt.Types.Document)
            (topCellName: string)
            (beamLayer: int) (beamDataType: int)
            (endpoint: Rekolektion.Viz.Core.Rkt.Types.Point)
            (excludeTopIds: Set<int>)
            (mults: int64 * int64 * int64 * int64)
            (draggedSegHalfPerp: int64)
            : AnchorInfo option =
        let (mxX, mxY, myX, myY) = mults
        let flat = Layout.Flatten.flatten doc
        flat
        |> Array.tryPick (fun fp ->
            if fp.Layer <> beamLayer || fp.DataType <> beamDataType then None
            elif fp.SourceStructure = topCellName
                 && excludeTopIds.Contains fp.SourceIndex then None
            elif fp.Points.Length = 0 then None
            else
                let mutable xMin = System.Int64.MaxValue
                let mutable yMin = System.Int64.MaxValue
                let mutable xMax = System.Int64.MinValue
                let mutable yMax = System.Int64.MinValue
                for p in fp.Points do
                    if p.X < xMin then xMin <- p.X
                    if p.X > xMax then xMax <- p.X
                    if p.Y < yMin then yMin <- p.Y
                    if p.Y > yMax then yMax <- p.Y
                if endpoint.X >= xMin && endpoint.X <= xMax
                   && endpoint.Y >= yMin && endpoint.Y <= yMax then
                    Some { OrigEndpoint = endpoint
                           BboxDbu = (xMin, yMin, xMax, yMax)
                           Layer =
                               Rekolektion.Viz.Core.Rkt.OfGds.layerFromGds
                                   beamLayer beamDataType
                           EndpointMxX = mxX
                           EndpointMxY = mxY
                           EndpointMyX = myX
                           EndpointMyY = myY
                           DraggedSegHalfPerp = draggedSegHalfPerp }
                else None)

    /// Compute the extension rect for a track-slide anchor. The
    /// dragged beam's spine endpoint was at `anchor.OrigEndpoint`
    /// inside `anchor.BboxDbu` at press time; after a slide of
    /// `(dx, dy)` the endpoint shifts on the beam's perpendicular
    /// axis. When the new endpoint lands outside the anchor's
    /// bbox, emit a rect covering the gap on the anchor's layer
    /// (anchor's perp size preserved). Returns None when the new
    /// endpoint still sits inside the anchor or when delta is 0.
    /// Compute the extension rect needed at an anchored endpoint
    /// that has moved past the anchor's bbox. Uses the endpoint's
    /// motion multipliers (captured at press) so this works for
    /// the dragged beam's endpoints AND for far ends of other
    /// segments adjusted in a corner-style post drag. The
    /// extension preserves the anchor's perp size and covers the
    /// gap from the original endpoint to the new one along
    /// whichever axis actually moved.
    static member private ComputeExtensionRect
            (anchor: AnchorInfo)
            (dx: int64) (dy: int64)
            : Rekolektion.Viz.Core.Rkt.Types.Rectangle option =
        let endpointDx = anchor.EndpointMxX * dx + anchor.EndpointMxY * dy
        let endpointDy = anchor.EndpointMyX * dx + anchor.EndpointMyY * dy
        if endpointDx = 0L && endpointDy = 0L then None
        else
            let (aXMin, aYMin, aXMax, aYMax) = anchor.BboxDbu
            let newX = anchor.OrigEndpoint.X + endpointDx
            let newY = anchor.OrigEndpoint.Y + endpointDy
            let stillInside =
                newX >= aXMin && newX <= aXMax
                && newY >= aYMin && newY <= aYMax
            if stillInside then None
            else
                // Extension bridges the gap on whichever axis the
                // endpoint actually moved. Anchor's perp extent on
                // the OTHER axis is preserved so the new rect reads
                // as "more rail."
                // Owner position for the tag: the endpoint that this
                // anchor extension is anchored to — pre-drag location,
                // so a subsequent drag at the SAME endpoint reaps the
                // prior bridge while drags at other corners leave it.
                let bridgeProps =
                    StackCanvasControl.BridgePropsAt
                        anchor.OrigEndpoint.X anchor.OrigEndpoint.Y
                // JOG-fix overhang: the extension's wire-side end is
                // pulled past the new endpoint by the dragged segment's
                // half perp thickness so it lands at the wire's far
                // edge instead of its centerline. Otherwise only the
                // half of the wire facing the anchor connects.
                let half = anchor.DraggedSegHalfPerp
                if endpointDx <> 0L && endpointDy = 0L then
                    let xMin = min anchor.OrigEndpoint.X newX
                    let xMax = max anchor.OrigEndpoint.X newX
                    let xMinExt, xMaxExt =
                        if newX > anchor.OrigEndpoint.X then xMin, xMax + half
                        else xMin - half, xMax
                    Some
                        ({ Layer = anchor.Layer
                           X1 = xMinExt; Y1 = aYMin
                           X2 = xMaxExt; Y2 = aYMax
                           Net = None
                           Props = bridgeProps
                           Comments = [] }
                         : Rekolektion.Viz.Core.Rkt.Types.Rectangle)
                elif endpointDy <> 0L && endpointDx = 0L then
                    let yMin = min anchor.OrigEndpoint.Y newY
                    let yMax = max anchor.OrigEndpoint.Y newY
                    let yMinExt, yMaxExt =
                        if newY > anchor.OrigEndpoint.Y then yMin, yMax + half
                        else yMin - half, yMax
                    Some
                        ({ Layer = anchor.Layer
                           X1 = aXMin; Y1 = yMinExt
                           X2 = aXMax; Y2 = yMaxExt
                           Net = None
                           Props = bridgeProps
                           Comments = [] }
                         : Rekolektion.Viz.Core.Rkt.Types.Rectangle)
                else
                    // Endpoint moved diagonally — anchor extension
                    // would need to be an L-shape. v1: just emit a
                    // bbox covering both axes; clean L-shape is a
                    // follow-up.
                    let xMin = min anchor.OrigEndpoint.X newX
                    let xMax = max anchor.OrigEndpoint.X newX
                    let yMin = min anchor.OrigEndpoint.Y newY
                    let yMax = max anchor.OrigEndpoint.Y newY
                    let xMinExt, xMaxExt =
                        if newX > anchor.OrigEndpoint.X then xMin, xMax + half
                        else xMin - half, xMax
                    let yMinExt, yMaxExt =
                        if newY > anchor.OrigEndpoint.Y then yMin, yMax + half
                        else yMin - half, yMax
                    Some
                        ({ Layer = anchor.Layer
                           X1 = xMinExt; Y1 = yMinExt
                           X2 = xMaxExt; Y2 = yMaxExt
                           Net = None
                           Props = bridgeProps
                           Comments = [] }
                         : Rekolektion.Viz.Core.Rkt.Types.Rectangle)

    /// Hit-test screen point against the rendered post handles for
    /// `route` at `layerZ`. Each post is rendered as a small
    /// square (~0.32 µm side) centered on the post position. Hit
    /// zone projects each post center to screen, snap radius
    /// 12 px. Returns the post index when within range.
    member private this.HitTestPostHandleFor
            (route: Rekolektion.Viz.Core.Routing.Detect.Route)
            (layerZ: float32)
            (screen: Avalonia.Point)
            : int option =
        match this.Library with
        | Some lib ->
            let w = this.Bounds.Width
            let h = this.Bounds.Height
            if w < 1.0 || h < 1.0 then None
            else
                let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                let z = layerZ
                let mvp =
                    Matrix4x4Helpers.buildOrbitMvp
                        yawDeg pitchDeg zoom target extent (w, h)
                let projectToScreen (xUm: float32) (yUm: float32)
                        : (float * float) option =
                    let v =
                        Vector4.Transform(
                            Vector4(xUm, yUm, z, 1.0f),
                            mvp)
                    if v.W <= 1.0e-6f then None
                    else
                        let ndcX = float (v.X / v.W)
                        let ndcY = float (v.Y / v.W)
                        Some ((ndcX + 1.0) * 0.5 * w,
                              (1.0 - ndcY) * 0.5 * h)
                // Snap radius matches the rendered square's actual
                // on-screen size (half-diagonal of the 0.32 µm
                // square) instead of a fixed pixel value. With a
                // fixed 12 px snap, zooming in made the square
                // bigger than the hit zone and the visible corners
                // became dead pixels. Floor at 12 px so the handle
                // stays clickable at low zoom.
                let postHalfUm = 0.16f
                let mutable bestIdx : int option = None
                let mutable bestDist = System.Double.MaxValue
                for i in 0 .. route.Posts.Length - 1 do
                    let p = route.Posts.[i]
                    let cx = float32 (float p.Position.X * umPerDbu)
                    let cy = float32 (float p.Position.Y * umPerDbu)
                    match projectToScreen cx cy with
                    | Some (sx, sy) ->
                        // Project a corner of the visual square; its
                        // screen distance from the center is the
                        // half-diagonal at the current view.
                        let snapPx =
                            match projectToScreen
                                      (cx + postHalfUm) (cy + postHalfUm) with
                            | Some (cornerX, cornerY) ->
                                let cdx = cornerX - sx
                                let cdy = cornerY - sy
                                max 12.0 (sqrt (cdx * cdx + cdy * cdy))
                            | None -> 12.0
                        let ddx = screen.X - sx
                        let ddy = screen.Y - sy
                        let dist = sqrt (ddx * ddx + ddy * ddy)
                        if dist < snapPx && dist < bestDist then
                            bestDist <- dist
                            bestIdx <- Some i
                    | None -> ()
                bestIdx
        | _ -> None

    override this.OnPointerPressed e =
        base.OnPointerPressed e
        let props = e.GetCurrentPoint(this).Properties
        // Edit Routing mode + left button + cursor over a route +
        // press lands on a track handle → start a route slide drag.
        // Suppresses the normal orbit-drag setup so the camera
        // doesn't rotate. Drag math + live preview lands in the
        // next slice; for now this just verifies the press is
        // detected and stops orbit from interfering.
        let editing =
            match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
            | Some (m: Rekolektion.Viz.App.Model.Model.Model) -> m.EditRoutingMode
            | None -> false
        // Try TRACK hit first (more constrained / specific), then
        // POST. Both run only in edit mode on left-button presses.
        let trackHit =
            if editing && props.IsLeftButtonPressed then
                match hoveredRoute with
                | Some r ->
                    this.HitTestTrackHandleFor r hoveredRouteLayerZ
                        (e.GetPosition this)
                | None -> None
            else None
        let postHit =
            if editing && props.IsLeftButtonPressed && trackHit.IsNone then
                match hoveredRoute with
                | Some r ->
                    this.HitTestPostHandleFor r hoveredRouteLayerZ
                        (e.GetPosition this)
                | None -> None
            else None
        let beginSlide
                (kind: SlideKind)
                (route: Rekolektion.Viz.Core.Routing.Detect.Route)
                (lib: Rekolektion.Viz.Core.Rkt.Types.Document)
                (spine: Rekolektion.Viz.Core.Routing.Detect.Axis)
                (adjusts: SlideAdjust list)
                (handleLabel: string)
                (draggedSeg: Rekolektion.Viz.Core.Routing.Detect.Segment option)
                (postIdx: int option) =
            match this.UnprojectAtZ (e.GetPosition this) hoveredRouteLayerZ with
            | None -> ()
            | Some hitDbu ->
                // For TRACK slides, detect rail/strap anchors at
                // each spine endpoint so the commit can emit
                // extensions if the endpoints leave the anchor
                // bbox. Skip rects already being adjusted (perp
                // neighbors at corners — their stretch already
                // preserves the corner).
                // Walk EVERY adjusted segment, not just the dragged
                // beam, and check both of its spine endpoints for
                // an anchor. This makes the cascade case work — if
                // a post drag perp-slides a neighbor and the
                // neighbor's far end was on a rail, we emit an
                // extension to bridge that far end back too.
                // Exclude rects already in the adjust list so the
                // dragged geometry doesn't anchor against itself.
                let excludeIds =
                    adjusts
                    |> List.map (fun a -> a.SourceIdx)
                    |> Set.ofList
                let segBySource =
                    route.Segments
                    |> Array.map (fun s -> s.SourceIndex, s)
                    |> Map.ofArray
                let anchors =
                    adjusts
                    |> List.collect (fun a ->
                        match Map.tryFind a.SourceIdx segBySource with
                        | None -> []
                        | Some seg ->
                            let multsStart =
                                StackCanvasControl.EndpointMultipliers seg a true
                            let multsEnd =
                                StackCanvasControl.EndpointMultipliers seg a false
                            [ seg.Start, multsStart; seg.End, multsEnd ]
                            |> List.choose (fun (endpt, mults) ->
                                let (mxX, mxY, myX, myY) = mults
                                if mxX = 0L && mxY = 0L
                                   && myX = 0L && myY = 0L then
                                    // Endpoint doesn't move under
                                    // the recipe — no anchor work
                                    // needed there.
                                    None
                                else
                                    StackCanvasControl.FindAnchorAt
                                        lib route.Cell seg.Layer seg.DataType
                                        endpt excludeIds mults
                                        (seg.Width / 2L)))
                dragMode <- RouteTrackDrag
                pressedButton <- RouteTrackDrag
                lastPos <- e.GetPosition this
                pressStart <- lastPos
                e.Pointer.Capture this
                this.Focus () |> ignore
                routeSlide <- Some {
                    Cell = route.Cell
                    Kind = kind
                    Spine = spine
                    LayerZ = hoveredRouteLayerZ
                    StartHitDbu = hitDbu
                    Adjusts = adjusts
                    Anchors = anchors
                    Route = Some route
                    PostIdx = postIdx
                    LastDxDbu = 0L
                    LastDyDbu = 0L
                }
                // Seed the speculative document so the renderer
                // immediately paints from it (covers any race
                // between press and the first move tick).
                dragLiveDoc <- Some lib
                dragLiveFlat <- this.FlatPolygons
                Rekolektion.Viz.App.Services.Logger.log "route.tool"
                    {| op = "press"
                       handle = handleLabel
                       cell = route.Cell
                       hitDbu = sprintf "%d,%d" hitDbu.X hitDbu.Y
                       adjusts = adjusts.Length
                       anchors = anchors.Length |}
        match trackHit, postHit, hoveredRoute, this.Library with
        | Some segIdx, _, Some route, Some lib
                when segIdx < route.Segments.Length ->
            let seg = route.Segments.[segIdx]
            let baseAdjusts =
                StackCanvasControl.BuildSlideAdjusts route segIdx
            // Cascade L-corner neighbor trims through every
            // ADJUSTED segment's other posts so the corner-stretch
            // propagates across the whole connected route, not
            // just at the press point.
            let cascaded =
                StackCanvasControl.CascadeNeighborAdjusts route baseAdjusts None
            // Risers (knuckles) sitting on the dragged beam translate
            // with it as rigid bodies — pads + via cuts that bracket
            // the beam centerline within its X range follow the dy.
            let risers =
                StackCanvasControl.BuildRiserAdjusts lib route.Cell seg
            let adjusts = baseAdjusts @ cascaded @ risers
            beginSlide TrackSlide route lib seg.Spine adjusts "track"
                (Some seg) None
        | None, Some postIdx, Some route, Some lib
                when postIdx < route.Posts.Length ->
            // No cascade for PostSlide: with spine-only stretch, no
            // segment translates in its perpendicular axis, so no
            // perpendicular neighbor anywhere in the route graph
            // needs to follow. Cascade stays for TrackSlide where the
            // dragged segment really does translate.
            let adjusts = StackCanvasControl.BuildPostAdjusts route postIdx
            beginSlide PostSlide route lib
                Rekolektion.Viz.Core.Routing.Detect.Axis.X adjusts "post"
                None (Some postIdx)
        | _ ->
            // No track hit (or surrounding state missing) — fall
            // through to the normal orbit / pan dispatch so the
            // canvas stays usable.
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

    /// Routing-relevant drawing layers, ordered TOP-DOWN so the
    /// raycast prefers the upper layer when a click would hit
    /// stacked routing on multiple layers (e.g. met2 over met1).
    /// Datatype 20 = drawing per the layer table.
    static member private RoutingLayerKeys
            : (int * int) array =
        [| 72, 20  // met5
           71, 20  // met4
           70, 20  // met3
           69, 20  // met2
           68, 20  // met1
           67, 20  // li1
        |]

    /// Raycast the cursor against each routing layer's slab and run
    /// the route-detection module at the first hit. Updates
    /// `hoveredRoute` + `hoveredRouteLayerZ` and returns whether the
    /// hover changed (so the caller knows whether to redraw).
    member private this.UpdateRoutingHover (screen: Avalonia.Point)
            : bool =
        match this.Library with
        | None ->
            let changed = hoveredRoute.IsSome
            hoveredRoute <- None
            changed
        | Some lib ->
            let w = this.Bounds.Width
            let h = this.Bounds.Height
            if w < 1.0 || h < 1.0 then
                let changed = hoveredRoute.IsSome
                hoveredRoute <- None
                changed
            else
                let ndcX = float32 (2.0 * screen.X / w - 1.0)
                let ndcY = float32 (1.0 - 2.0 * screen.Y / h)
                let mvp =
                    Matrix4x4Helpers.buildOrbitMvp
                        yawDeg pitchDeg zoom target extent (w, h)
                match Matrix4x4.Invert(mvp) with
                | false, _ ->
                    let changed = hoveredRoute.IsSome
                    hoveredRoute <- None
                    changed
                | true, inv ->
                    let unproj (z: float32) =
                        let v = Vector4(ndcX, ndcY, z, 1.0f)
                        let r = Vector4.Transform(v, inv)
                        Vector3(r.X / r.W, r.Y / r.W, r.Z / r.W)
                    let nearW = unproj -1.0f
                    let farW = unproj 1.0f
                    let rayO = nearW
                    let rayD = Vector3.Normalize(farW - nearW)
                    // Mesh world coords are in µm. Translate the
                    // hit µm-coord to DBU so locateRoute (which keys
                    // on the .rkt's native DBU integer coords) can
                    // match.
                    let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                    let dbuPerUm = 1.0 / umPerDbu
                    let topCellName =
                        match lib.TopCell with
                        | Some n -> n
                        | None ->
                            // Fall back to first cell name if the
                            // Document lacks an explicit `(top …)`.
                            match lib.Cells with
                            | c :: _ -> c.Name
                            | _ -> ""
                    let mutable found : Rekolektion.Viz.Core.Routing.Detect.Route option = None
                    let mutable foundZ : float32 = 0.0f
                    if topCellName <> "" then
                        let doc = lib
                        let toggle = this.Toggle
                        let layerKeys = StackCanvasControl.RoutingLayerKeys
                        let mutable i = 0
                        while found.IsNone && i < layerKeys.Length do
                            let (layerNum, layerDt) = layerKeys.[i]
                            let key = (layerNum, layerDt)
                            if Visibility.isLayerVisible toggle key then
                                match Layout.Layer.bySky130Number layerNum layerDt with
                                | None -> ()
                                | Some layer ->
                                    // Sample many Z planes through
                                    // the slab. At 35° camera pitch
                                    // the projected (X,Y) at the
                                    // top vs bottom of a 0.36 µm
                                    // metal slab differs by ~0.55 µm
                                    // — much wider than a min-width
                                    // wire (0.14 µm met1). With
                                    // only 3 samples (top/mid/bot),
                                    // only a thin Z slice maps to
                                    // the wire's footprint and the
                                    // user sees a "narrow stripe"
                                    // of valid hover. Step through
                                    // the slab in 0.05 µm increments
                                    // so any cursor over the slab's
                                    // visible screen footprint hits
                                    // some interior wire pixel.
                                    let zBot =
                                        float32 (layer.StackZ * Z_EXAGGERATION)
                                    let zTop =
                                        float32 ((layer.StackZ + layer.Thickness)
                                                 * Z_EXAGGERATION)
                                    let stepUm = 0.05f
                                    let nSamples =
                                        max 3 (int (MathF.Ceiling((zTop - zBot) / stepUm)) + 1)
                                    if MathF.Abs(rayD.Z) > 1.0e-6f then
                                        let mutable k = 0
                                        while found.IsNone && k < nSamples do
                                            // Walk top -> bot so the
                                            // first hit is the one
                                            // nearest the camera at
                                            // typical top-down-ish
                                            // angles. Index 0 is top,
                                            // index nSamples-1 is bot.
                                            let frac =
                                                if nSamples = 1 then 0.0f
                                                else float32 k / float32 (nSamples - 1)
                                            let zPlane =
                                                zTop + (zBot - zTop) * frac
                                            let t = (zPlane - rayO.Z) / rayD.Z
                                            if t >= 0.0f then
                                                let px = rayO.X + rayD.X * t
                                                let py = rayO.Y + rayD.Y * t
                                                let hit =
                                                    ({ X = int64 (float px * dbuPerUm)
                                                       Y = int64 (float py * dbuPerUm) }
                                                     : Rekolektion.Viz.Core.Rkt.Types.Point)
                                                match Rekolektion.Viz.Core.Routing.Detect.locateRoute
                                                          doc topCellName layerNum layerDt hit with
                                                | Some r ->
                                                    found <- Some r
                                                    // Sit overlay on the slab top, not above
                                                    // it — depth-test is disabled when we draw
                                                    // it so there's no z-fight to dodge, and
                                                    // floating above the metal reads wrong.
                                                    foundZ <- zTop
                                                | None -> ()
                                            k <- k + 1
                            i <- i + 1
                    let priorRoute = hoveredRoute
                    let priorLayerZ = hoveredRouteLayerZ
                    // Sticky hover: if the raycast missed but we
                    // had a route, check whether the cursor is
                    // still near one of that route's track handles.
                    // The handles render OUTSIDE the wire's metal
                    // bbox (perp-axis bars extend ±0.5 µm), so
                    // moving onto a bar tip would otherwise drop
                    // hover and the press would orbit instead of
                    // capture.
                    let stickyFound, stickyZ =
                        match found, priorRoute with
                        | Some _, _ -> found, foundZ
                        | None, Some r ->
                            // Symmetric sticky for post handles too —
                            // the corner squares extend past the metal
                            // and were dropping hover on the overhang.
                            match this.HitTestTrackHandleFor r priorLayerZ screen with
                            | Some _ -> Some r, priorLayerZ
                            | None ->
                                match this.HitTestPostHandleFor
                                          r priorLayerZ screen with
                                | Some _ -> Some r, priorLayerZ
                                | None -> None, 0.0f
                        | None, None -> None, 0.0f
                    hoveredRoute <- stickyFound
                    hoveredRouteLayerZ <- stickyZ
                    let found = stickyFound
                    let changed =
                        match priorRoute, found with
                        | None, None -> false
                        | Some a, Some b when a.Cell = b.Cell
                                          && a.Segments.Length = b.Segments.Length ->
                            // Cheap "different route" check: the
                            // first segment's source index. Avoids
                            // a deep struct compare per pointer move.
                            (a.Segments.[0].SourceIndex
                             <> b.Segments.[0].SourceIndex)
                            || (a.Segments.[0].Layer <> b.Segments.[0].Layer)
                        | _ -> true
                    if changed then
                        Rekolektion.Viz.App.Services.Logger.trace "route.tool"
                            {| op = "hover"
                               on = found.IsSome
                               segments =
                                   match found with
                                   | Some r -> r.Segments.Length
                                   | None -> 0
                               cell =
                                   match found with
                                   | Some r -> r.Cell
                                   | None -> ""
                               layerZ = float foundZ |}
                    changed

    /// Diagnostic version of the hover hit-test: same raycast +
    /// per-layer detection as `UpdateRoutingHover`, but builds a
    /// JSON string with every intermediate value so an agent can
    /// drive hit-test calibration via MCP without depending on the
    /// OS cursor or a screenshot. Doesn't mutate `hoveredRoute` —
    /// purely a probe.
    member private this.DiagnoseRoutingHoverAt (screen: Avalonia.Point)
            : string =
        let escapeJson (s: string) = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
        let sb = System.Text.StringBuilder()
        let w = this.Bounds.Width
        let h = this.Bounds.Height
        sb.Append (sprintf "{\"screen\":{\"x\":%g,\"y\":%g}," screen.X screen.Y) |> ignore
        sb.Append (sprintf "\"bounds\":{\"w\":%g,\"h\":%g}," w h) |> ignore
        match this.Library with
        | None ->
            sb.Append "\"ok\":false,\"error\":\"no library loaded\"}" |> ignore
            sb.ToString()
        | Some lib ->
            if w < 1.0 || h < 1.0 then
                sb.Append "\"ok\":false,\"error\":\"canvas not sized\"}" |> ignore
                sb.ToString()
            else
                let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                let dbuPerUm = 1.0 / umPerDbu
                sb.Append (sprintf "\"umPerDbu\":%g," umPerDbu) |> ignore
                let topCellName =
                    match lib.TopCell with
                    | Some n -> n
                    | None ->
                        match lib.Cells with
                        | c :: _ -> c.Name
                        | _ -> ""
                sb.Append (sprintf "\"topCellName\":\"%s\"," (escapeJson topCellName)) |> ignore
                let ndcX = float32 (2.0 * screen.X / w - 1.0)
                let ndcY = float32 (1.0 - 2.0 * screen.Y / h)
                let mvp =
                    Matrix4x4Helpers.buildOrbitMvp
                        yawDeg pitchDeg zoom target extent (w, h)
                match Matrix4x4.Invert(mvp) with
                | false, _ ->
                    sb.Append "\"ok\":false,\"error\":\"mvp not invertible\"}" |> ignore
                    sb.ToString()
                | true, inv ->
                    let unproj (z: float32) =
                        let v = Vector4(ndcX, ndcY, z, 1.0f)
                        let r = Vector4.Transform(v, inv)
                        Vector3(r.X / r.W, r.Y / r.W, r.Z / r.W)
                    let nearW = unproj -1.0f
                    let farW = unproj 1.0f
                    let rayO = nearW
                    let rayD = Vector3.Normalize(farW - nearW)
                    sb.Append (sprintf "\"ndc\":{\"x\":%g,\"y\":%g}," ndcX ndcY) |> ignore
                    sb.Append (sprintf "\"rayOrigin\":{\"x\":%g,\"y\":%g,\"z\":%g}," rayO.X rayO.Y rayO.Z) |> ignore
                    sb.Append (sprintf "\"rayDir\":{\"x\":%g,\"y\":%g,\"z\":%g}," rayD.X rayD.Y rayD.Z) |> ignore
                    let doc = lib
                    let toggle = this.Toggle
                    let layerKeys = StackCanvasControl.RoutingLayerKeys
                    sb.Append "\"layers\":[" |> ignore
                    let mutable firstLayer = true
                    for (layerNum, layerDt) in layerKeys do
                        let key = (layerNum, layerDt)
                        let visible = Visibility.isLayerVisible toggle key
                        if not firstLayer then sb.Append "," |> ignore
                        firstLayer <- false
                        sb.Append "{" |> ignore
                        sb.Append (sprintf "\"number\":%d,\"datatype\":%d," layerNum layerDt) |> ignore
                        match Layout.Layer.bySky130Number layerNum layerDt with
                        | None ->
                            sb.Append "\"name\":null,\"visible\":false,\"samples\":[]" |> ignore
                        | Some layer ->
                            sb.Append (sprintf "\"name\":\"%s\",\"visible\":%s,"
                                        (escapeJson layer.Name)
                                        (if visible then "true" else "false")) |> ignore
                            let zBot = float32 (layer.StackZ * Z_EXAGGERATION)
                            let zTop =
                                float32 ((layer.StackZ + layer.Thickness) * Z_EXAGGERATION)
                            let zMid = (zBot + zTop) * 0.5f
                            let samples =
                                [| ("top", zTop); ("mid", zMid); ("bot", zBot) |]
                            sb.Append "\"samples\":[" |> ignore
                            let mutable firstSample = true
                            for (sName, zPlane) in samples do
                                if not firstSample then sb.Append "," |> ignore
                                firstSample <- false
                                if MathF.Abs(rayD.Z) <= 1.0e-6f then
                                    sb.Append (sprintf "{\"plane\":\"%s\",\"z\":%g,\"skipped\":\"ray parallel to z\"}" sName zPlane) |> ignore
                                else
                                    let t = (zPlane - rayO.Z) / rayD.Z
                                    let px = rayO.X + rayD.X * t
                                    let py = rayO.Y + rayD.Y * t
                                    let dbuX = int64 (float px * dbuPerUm)
                                    let dbuY = int64 (float py * dbuPerUm)
                                    let routeRes =
                                        if not visible || topCellName = "" || t < 0.0f then None
                                        else
                                            let hit =
                                                ({ X = dbuX; Y = dbuY }
                                                 : Rekolektion.Viz.Core.Rkt.Types.Point)
                                            Rekolektion.Viz.Core.Routing.Detect.locateRoute
                                                doc topCellName layerNum layerDt hit
                                    let segCount =
                                        match routeRes with
                                        | Some r -> r.Segments.Length
                                        | None -> 0
                                    sb.Append
                                        (sprintf
                                            "{\"plane\":\"%s\",\"z\":%g,\"t\":%g,\"umX\":%g,\"umY\":%g,\"dbuX\":%d,\"dbuY\":%d,\"routeFound\":%s,\"routeSegments\":%d}"
                                            sName zPlane t px py dbuX dbuY
                                            (if routeRes.IsSome then "true" else "false")
                                            segCount) |> ignore
                            sb.Append "]" |> ignore
                        sb.Append "}" |> ignore
                    sb.Append "],\"ok\":true}" |> ignore
                    sb.ToString()

    /// Synthesize a full route-slide gesture without OS pointer
    /// events: run hover detection at `start`, hit-test for a
    /// handle, build the same RouteSlide state OnPointerPressed
    /// would, compute the world-DBU delta from `start` → `end`,
    /// apply it once, and dispatch the same commit Msg release
    /// would. Returns a JSON description for the calling MCP tool.
    /// Used to drive end-to-end edit tests from an agent without a
    /// human at the cursor.
    member private this.SimulateRouteDragAt
            (startX: float) (startY: float)
            (endX: float) (endY: float)
            : string =
        let escapeJson (s: string) =
            s.Replace("\\", "\\\\").Replace("\"", "\\\"")
        let startPt = Avalonia.Point(startX, startY)
        let endPt = Avalonia.Point(endX, endY)
        // Re-run hover detection at the start position so
        // `hoveredRoute` reflects what the user would have seen
        // when they put the cursor over the handle.
        this.UpdateRoutingHover startPt |> ignore
        match hoveredRoute, this.Library with
        | None, _ | _, None ->
            "{\"ok\":false,\"reason\":\"no hovered route at start\"}"
        | Some route, Some lib ->
            let trackHit =
                this.HitTestTrackHandleFor route hoveredRouteLayerZ startPt
            let postHit =
                if trackHit.IsNone then
                    this.HitTestPostHandleFor route hoveredRouteLayerZ startPt
                else None
            let kindStr, adjusts, spine, dragPostIdx =
                match trackHit, postHit with
                | Some segIdx, _ when segIdx < route.Segments.Length ->
                    let seg = route.Segments.[segIdx]
                    let baseAdjusts =
                        StackCanvasControl.BuildSlideAdjusts route segIdx
                    let cascaded =
                        StackCanvasControl.CascadeNeighborAdjusts
                            route baseAdjusts None
                    let risers =
                        StackCanvasControl.BuildRiserAdjusts
                            lib route.Cell seg
                    "track", baseAdjusts @ cascaded @ risers, seg.Spine, None
                | None, Some postIdx when postIdx < route.Posts.Length ->
                    let adjusts =
                        StackCanvasControl.BuildPostAdjusts route postIdx
                    "post",
                    adjusts,
                    Rekolektion.Viz.Core.Routing.Detect.Axis.X,
                    Some postIdx
                | _ ->
                    "none", [], Rekolektion.Viz.Core.Routing.Detect.Axis.X,
                    None
            let excludeIds =
                adjusts |> List.map (fun a -> a.SourceIdx) |> Set.ofList
            let segBySource =
                route.Segments
                |> Array.map (fun s -> s.SourceIndex, s)
                |> Map.ofArray
            let anchors =
                adjusts
                |> List.collect (fun a ->
                    match Map.tryFind a.SourceIdx segBySource with
                    | None -> []
                    | Some seg ->
                        let multsStart =
                            StackCanvasControl.EndpointMultipliers seg a true
                        let multsEnd =
                            StackCanvasControl.EndpointMultipliers seg a false
                        [ seg.Start, multsStart; seg.End, multsEnd ]
                        |> List.choose (fun (endpt, mults) ->
                            let (mxX, mxY, myX, myY) = mults
                            if mxX = 0L && mxY = 0L
                               && myX = 0L && myY = 0L then None
                            else
                                StackCanvasControl.FindAnchorAt
                                    lib route.Cell seg.Layer seg.DataType
                                    endpt excludeIds mults
                                    (seg.Width / 2L)))
            if kindStr = "none" then
                "{\"ok\":false,\"reason\":\"no handle under start point\"}"
            else
                match this.UnprojectAtZ startPt hoveredRouteLayerZ,
                      this.UnprojectAtZ endPt hoveredRouteLayerZ with
                | Some s, Some e ->
                    let rawDx = e.X - s.X
                    let rawDy = e.Y - s.Y
                    let dx', dy' =
                        match kindStr with
                        | "track" ->
                            match spine with
                            | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                                0L, rawDy
                            | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                                rawDx, 0L
                        | _ -> rawDx, rawDy
                    let snapDx = StackCanvasControl.SnapDbu dx'
                    let snapDy = StackCanvasControl.SnapDbu dy'
                    let payload =
                        adjusts
                        |> List.map (fun a ->
                            a.SourceIdx,
                            a.Mx1X, a.Mx1Y, a.My1X, a.My1Y,
                            a.Mx2X, a.Mx2Y, a.My2X, a.My2Y)
                    let anchorExts =
                        anchors
                        |> List.choose (fun a ->
                            StackCanvasControl.ComputeExtensionRect
                                a snapDx snapDy)
                    let cornerJogs =
                        match dragPostIdx with
                        | Some pi ->
                            StackCanvasControl.ComputeCornerJogs
                                route pi adjusts snapDx snapDy
                        | None -> []
                    let extensions = anchorExts @ cornerJogs
                    if snapDx <> 0L || snapDy <> 0L then
                        Rekolektion.Viz.App.Services.AppDispatch.send
                            (Rekolektion.Viz.App.Model.Msg.RouteSlideCommit
                                (route.Cell, snapDx, snapDy, payload, extensions))
                    sprintf
                        "{\"ok\":true,\"handle\":\"%s\",\"cell\":\"%s\",\"adjusts\":%d,\"anchors\":%d,\"extensions\":%d,\"startDbu\":[%d,%d],\"endDbu\":[%d,%d],\"snapDxDbu\":%d,\"snapDyDbu\":%d}"
                        (escapeJson kindStr)
                        (escapeJson route.Cell)
                        adjusts.Length
                        anchors.Length
                        extensions.Length
                        s.X s.Y e.X e.Y snapDx snapDy
                | _ ->
                    "{\"ok\":false,\"reason\":\"unproject failed\"}"

    override this.OnPointerMoved e =
        base.OnPointerMoved e
        // Edit Routing mode: hover-detect the route under the cursor
        // so the GL renderer can outline it. Only run when the user
        // isn't mid-drag (orbit/pan) — the cursor isn't really
        // hovering during a drag, and the per-tick raycast is the
        // most expensive thing this handler does.
        if dragMode = NoDrag then
            let editing =
                match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
                | Some (m: Rekolektion.Viz.App.Model.Model.Model) -> m.EditRoutingMode
                | None -> false
            if editing then
                let p = e.GetPosition this
                if this.UpdateRoutingHover(p) then
                    this.RequestNextFrameRendering()
            elif hoveredRoute.IsSome then
                hoveredRoute <- None
                this.RequestNextFrameRendering()
        match dragMode with
        | NoDrag -> ()
        | RouteTrackDrag ->
            match routeSlide, this.Library with
            | Some slide, Some lib ->
                let p = e.GetPosition this
                lastPos <- p
                match this.UnprojectAtZ p slide.LayerZ with
                | None -> ()
                | Some hitDbu ->
                    let rawDx = hitDbu.X - slide.StartHitDbu.X
                    let rawDy = hitDbu.Y - slide.StartHitDbu.Y
                    // TrackSlide: axis-locked to the perp axis.
                    // PostSlide: free 2D by default; Shift to
                    // ortho-lock (snap to whichever axis the cursor
                    // moved farther on).
                    let shiftHeld =
                        e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)
                    let rawDx', rawDy' =
                        match slide.Kind, slide.Spine with
                        | TrackSlide,
                          Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                            0L, rawDy
                        | TrackSlide,
                          Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                            rawDx, 0L
                        | PostSlide, _ when shiftHeld ->
                            if abs rawDx >= abs rawDy then rawDx, 0L
                            else 0L, rawDy
                        | PostSlide, _ ->
                            rawDx, rawDy
                    let snapDx = StackCanvasControl.SnapDbu rawDx'
                    let snapDy = StackCanvasControl.SnapDbu rawDy'
                    if snapDx <> slide.LastDxDbu || snapDy <> slide.LastDyDbu then
                        slide.LastDxDbu <- snapDx
                        slide.LastDyDbu <- snapDy
                        let docAfterAdjusts =
                            StackCanvasControl.ApplyAdjustsToDoc
                                lib slide.Cell slide.Adjusts snapDx snapDy
                        // For track slides with anchored endpoints,
                        // append any extension rects required to
                        // bridge the gap from each anchor's
                        // original endpoint to where it sits now.
                        let anchorExts =
                            slide.Anchors
                            |> List.choose (fun a ->
                                StackCanvasControl.ComputeExtensionRect
                                    a snapDx snapDy)
                        let cornerJogs =
                            match slide.Route, slide.PostIdx with
                            | Some r, Some pi ->
                                StackCanvasControl.ComputeCornerJogs
                                    r pi slide.Adjusts snapDx snapDy
                            | _ -> []
                        let extensions = anchorExts @ cornerJogs
                        // Reap only bridges whose tag matches this
                        // drag's new bridges (same owning post /
                        // endpoint). Mirrors the commit-time reap in
                        // Update.fs so the live preview stays in sync.
                        let bridgeTagOf
                                (r: Rekolektion.Viz.Core.Rkt.Types.Rectangle)
                                : string option =
                            r.Props
                            |> List.tryPick (fun p ->
                                if p.Key = "viz:bridge" then
                                    match p.Value with
                                    | Rekolektion.Viz.Core.Rkt.Types.PvString s ->
                                        Some s
                                    | Rekolektion.Viz.Core.Rkt.Types.PvAtom s ->
                                        Some s
                                    | _ -> None
                                else None)
                        let newTags =
                            extensions
                            |> List.choose bridgeTagOf
                            |> Set.ofList
                        let docNew =
                            let cells' =
                                docAfterAdjusts.Cells
                                |> List.map (fun c ->
                                    if c.Name <> slide.Cell then c
                                    else
                                        let kept =
                                            c.Elements
                                            |> List.filter (fun el ->
                                                match el with
                                                | Rekolektion.Viz.Core.Rkt.Types.RectEl r ->
                                                    match bridgeTagOf r with
                                                    | Some tag when newTags.Contains tag ->
                                                        false
                                                    | _ -> true
                                                | _ -> true)
                                        let extEls =
                                            extensions
                                            |> List.map
                                                Rekolektion.Viz.Core.Rkt.Types.RectEl
                                        { c with Elements = kept @ extEls })
                            { docAfterAdjusts with Cells = cells' }
                        let flatNew = Layout.Flatten.flatten docNew
                        dragLiveDoc <- Some docNew
                        dragLiveFlat <- flatNew
                        meshDirty <- true
                        this.RequestNextFrameRendering()
                        Rekolektion.Viz.App.Services.Logger.trace "route.tool"
                            {| op = "slide"
                               kind =
                                   match slide.Kind with
                                   | TrackSlide -> "track"
                                   | PostSlide -> "post"
                               dx = snapDx
                               dy = snapDy
                               cell = slide.Cell
                               adjusts = slide.Adjusts.Length
                               extensions = extensions.Length |}
            | _ ->
                lastPos <- e.GetPosition this
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
        let wasRouteDrag  = pressedButton = RouteTrackDrag
        dragMode <- NoDrag
        pressedButton <- NoDrag
        e.Pointer.Capture null
        if wasOrbitClick then
            this.PickAt(release)
        elif wasRouteDrag then
            match routeSlide with
            | Some slide when slide.LastDxDbu <> 0L || slide.LastDyDbu <> 0L ->
                let payload =
                    slide.Adjusts
                    |> List.map (fun a ->
                        a.SourceIdx,
                        a.Mx1X, a.Mx1Y, a.My1X, a.My1Y,
                        a.Mx2X, a.Mx2Y, a.My2X, a.My2Y)
                let anchorExts =
                    slide.Anchors
                    |> List.choose (fun a ->
                        StackCanvasControl.ComputeExtensionRect
                            a slide.LastDxDbu slide.LastDyDbu)
                let cornerJogs =
                    match slide.Route, slide.PostIdx with
                    | Some r, Some pi ->
                        StackCanvasControl.ComputeCornerJogs
                            r pi slide.Adjusts
                            slide.LastDxDbu slide.LastDyDbu
                    | _ -> []
                let extensions = anchorExts @ cornerJogs
                Rekolektion.Viz.App.Services.AppDispatch.send
                    (Rekolektion.Viz.App.Model.Msg.RouteSlideCommit
                        (slide.Cell, slide.LastDxDbu, slide.LastDyDbu,
                         payload, extensions))
                Rekolektion.Viz.App.Services.Logger.log "route.tool"
                    {| op = "release"
                       handle =
                           match slide.Kind with
                           | TrackSlide -> "track"
                           | PostSlide -> "post"
                       travelPx = travel
                       dx = slide.LastDxDbu
                       dy = slide.LastDyDbu
                       committed = true |}
            | _ ->
                Rekolektion.Viz.App.Services.Logger.log "route.tool"
                    {| op = "release"
                       handle = "route"
                       travelPx = travel
                       committed = false |}
            // Clear speculative state — once the model commits the
            // change above, the bound FlatPolygons updates and the
            // renderer flips back to it.
            routeSlide <- None
            dragLiveDoc <- None
            dragLiveFlat <- [||]
            meshDirty <- true
            this.RequestNextFrameRendering()

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
        // Register the routing-hover diagnose callback now that the
        // GL context exists (and therefore the canvas has a valid
        // Bounds + camera state). Out-of-tree code (CommandListener
        // → MCP) calls this to probe hit-test math at a given screen
        // pixel without depending on the OS cursor.
        Rekolektion.Viz.App.Services.AppDispatch.diagnoseRoutingHover <-
            Some (fun (x, y) -> this.DiagnoseRoutingHoverAt(Avalonia.Point(x, y)))
        Rekolektion.Viz.App.Services.AppDispatch.simulateRouteDrag <-
            Some (fun (sx, sy, ex, ey) -> this.SimulateRouteDragAt sx sy ex ey)
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
        // Edit-routing hover outline — same layout, rebuilt every
        // frame the hover changes (cheap: at most a few hundred
        // lines per route).
        hoverVao <- g.GenVertexArray()
        hoverVbo <- g.GenBuffer()

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
            // Speculative geometry during a route slide drag —
            // canvas-side override of the bound FlatPolygons so
            // the in-flight position renders without a round-trip
            // through the Elmish loop. Cleared on release.
            let flat =
                match dragLiveDoc with
                | Some _ when dragLiveFlat.Length > 0 -> dragLiveFlat
                | _ -> this.FlatPolygons
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
                // Tick lengths are fixed in world µm so they look
                // the same on a 1-µm FET and a 100-µm macro. Old
                // rule scaled by `step`, which produced 10-µm ticks
                // on big macros and sub-µm ticks on small cells —
                // both wrong by the user's "ticks shouldn't scale
                // with the bbox" rule.
                let majorTick = 0.3f
                let minorTick = 0.12f
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
                // Fixed world-µm size, same rationale as the major
                // / minor tick lengths above — don't scale with bbox.
                let originSize = 0.2f
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
            // Edit-routing hover overlay: outline every segment in
            // the hovered route. Built from `hoveredRoute` (set by
            // UpdateRoutingHover on every PointerMoved tick when
            // EditRoutingMode is on). Drawn slightly above the
            // layer's top-of-slab z so it doesn't z-fight with the
            // underlying metal.
            // Suppress the gizmo overlay during a slide drag — the
            // hovered route's segment positions are still the
            // pre-drag coords, so painting them while the
            // speculative metal sits at the new position would
            // double-render. The user already has the metal moving
            // visually; the handles return on release. A separate
            // moving "you grabbed this" indicator is drawn below
            // so the user has feedback during the drag.
            let suppressOverlay = (dragMode = RouteTrackDrag)
            // Draw a bright indicator at the cursor's current
            // projected world position so the user can see the
            // handle they're dragging. Uses the same hoverVbo —
            // emit AFTER the per-route overlay block (or in its
            // place when suppressed).
            let drawDragIndicator () =
                match routeSlide, this.Library with
                | Some slide, Some lib when rulerProgram <> 0u ->
                    let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                    let curX = slide.StartHitDbu.X + slide.LastDxDbu
                    let curY = slide.StartHitDbu.Y + slide.LastDyDbu
                    let cx = float32 (float curX * umPerDbu)
                    let cy = float32 (float curY * umPerDbu)
                    let z = slide.LayerZ
                    // Color cues the handle kind. Same palette as
                    // the static post handles so the moving glyph
                    // reads as "the one you grabbed."
                    let r, gC, bC =
                        match slide.Kind with
                        | TrackSlide -> 1.00f, 0.55f, 0.20f   // orange
                        | PostSlide  -> 1.00f, 0.90f, 0.30f   // bright yellow
                    let half = 0.16f
                    let crossHalf = half * 0.35f
                    let verts = ResizeArray<float32>()
                    let pushSeg
                            (x1: float32) (y1: float32)
                            (x2: float32) (y2: float32) =
                        verts.Add x1; verts.Add y1; verts.Add z
                        verts.Add r;  verts.Add gC; verts.Add bC
                        verts.Add x2; verts.Add y2; verts.Add z
                        verts.Add r;  verts.Add gC; verts.Add bC
                    // Outline square + center cross — no fill, so
                    // the user can see the metal under the cursor
                    // while dragging.
                    pushSeg (cx - half) (cy - half) (cx + half) (cy - half)
                    pushSeg (cx + half) (cy - half) (cx + half) (cy + half)
                    pushSeg (cx + half) (cy + half) (cx - half) (cy + half)
                    pushSeg (cx - half) (cy + half) (cx - half) (cy - half)
                    pushSeg (cx - crossHalf) cy (cx + crossHalf) cy
                    pushSeg cx (cy - crossHalf) cx (cy + crossHalf)
                    let arr = verts.ToArray()
                    g.BindVertexArray hoverVao
                    g.BindBuffer(GLEnum.ArrayBuffer, hoverVbo)
                    g.BufferData(
                        GLEnum.ArrayBuffer,
                        ReadOnlySpan<float32>(arr),
                        GLEnum.DynamicDraw)
                    g.UseProgram rulerProgram
                    let stride = uint32 (6 * sizeof<float32>)
                    g.EnableVertexAttribArray 0u
                    g.VertexAttribPointer(0u, 3, GLEnum.Float, false, stride, nativeint 0)
                    g.EnableVertexAttribArray 1u
                    g.VertexAttribPointer(1u, 3, GLEnum.Float, false, stride, nativeint (3 * sizeof<float32>))
                    let loc = g.GetUniformLocation(rulerProgram, "uMVP")
                    let mvpArr = Matrix4x4Helpers.toFloatArray mvp
                    g.UniformMatrix4(loc, 1u, false, ReadOnlySpan<float32>(mvpArr))
                    g.LineWidth 2.0f
                    g.Disable GLEnum.DepthTest
                    g.DrawArrays(GLEnum.Lines, 0, uint32 (arr.Length / 6))
                    g.Enable GLEnum.DepthTest
                | _ -> ()
            match hoveredRoute with
            | None -> drawDragIndicator ()
            | Some _ when suppressOverlay -> drawDragIndicator ()
            | Some route when this.Library.IsSome ->
                let lib = this.Library.Value
                let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                let z = hoveredRouteLayerZ
                // Three overlay parts share the same vertex buffer
                // since they all use rulerProgram (position + RGB,
                // MVP-transformed). Lines first, then triangles —
                // tracked separately so we can switch draw mode.
                let lineVerts = ResizeArray<float32>()
                let triVerts  = ResizeArray<float32>()
                let pushLine
                        (x1: float32) (y1: float32)
                        (x2: float32) (y2: float32)
                        (struct (rR, gG, bB)) =
                    lineVerts.Add x1; lineVerts.Add y1; lineVerts.Add z
                    lineVerts.Add rR; lineVerts.Add gG; lineVerts.Add bB
                    lineVerts.Add x2; lineVerts.Add y2; lineVerts.Add z
                    lineVerts.Add rR; lineVerts.Add gG; lineVerts.Add bB
                let pushTriangle
                        (x1: float32) (y1: float32)
                        (x2: float32) (y2: float32)
                        (x3: float32) (y3: float32)
                        (struct (rR, gG, bB)) =
                    triVerts.Add x1; triVerts.Add y1; triVerts.Add z
                    triVerts.Add rR; triVerts.Add gG; triVerts.Add bB
                    triVerts.Add x2; triVerts.Add y2; triVerts.Add z
                    triVerts.Add rR; triVerts.Add gG; triVerts.Add bB
                    triVerts.Add x3; triVerts.Add y3; triVerts.Add z
                    triVerts.Add rR; triVerts.Add gG; triVerts.Add bB
                // Subtle bbox outline per segment so the hovered route
                // reads as one logical wire even before the user grabs
                // any specific handle. Cyan, thin.
                let outlineColor = struct (0.30f, 0.95f, 1.00f)
                for s in route.Segments do
                    let x1 = float32 (float s.XMin * umPerDbu)
                    let y1 = float32 (float s.YMin * umPerDbu)
                    let x2 = float32 (float s.XMax * umPerDbu)
                    let y2 = float32 (float s.YMax * umPerDbu)
                    pushLine x1 y1 x2 y1 outlineColor
                    pushLine x2 y1 x2 y2 outlineColor
                    pushLine x2 y2 x1 y2 outlineColor
                    pushLine x1 y2 x1 y1 outlineColor
                // Track handles: one bar per segment, perpendicular
                // to the spine, drawn at the segment midpoint. The
                // bar's axis IS the drag axis — pre-constrains the
                // user gesture (no "is up Y or Z?" ambiguity).
                let trackColor = struct (1.00f, 0.55f, 0.20f)   // orange
                let trackHalfLenUm = 0.50f
                let capHalfUm      = 0.10f
                for s in route.Segments do
                    match s.Spine with
                    | Rekolektion.Viz.Core.Routing.Detect.Axis.X ->
                        // Horizontal spine — bar runs along Y (the
                        // drag axis). Midpoint X is the spine center.
                        let midX =
                            float32 ((float s.Start.X + float s.End.X) * 0.5 * umPerDbu)
                        let cy = float32 (float s.Center * umPerDbu)
                        pushLine midX (cy - trackHalfLenUm)
                                 midX (cy + trackHalfLenUm)
                                 trackColor
                        // End caps (small perpendicular ticks) so the
                        // bar reads as a "drag this" affordance, not
                        // as part of the route geometry.
                        pushLine (midX - capHalfUm) (cy - trackHalfLenUm)
                                 (midX + capHalfUm) (cy - trackHalfLenUm)
                                 trackColor
                        pushLine (midX - capHalfUm) (cy + trackHalfLenUm)
                                 (midX + capHalfUm) (cy + trackHalfLenUm)
                                 trackColor
                    | Rekolektion.Viz.Core.Routing.Detect.Axis.Y ->
                        // Vertical spine — bar runs along X.
                        let midY =
                            float32 ((float s.Start.Y + float s.End.Y) * 0.5 * umPerDbu)
                        let cx = float32 (float s.Center * umPerDbu)
                        pushLine (cx - trackHalfLenUm) midY
                                 (cx + trackHalfLenUm) midY
                                 trackColor
                        pushLine (cx - trackHalfLenUm) (midY - capHalfUm)
                                 (cx - trackHalfLenUm) (midY + capHalfUm)
                                 trackColor
                        pushLine (cx + trackHalfLenUm) (midY - capHalfUm)
                                 (cx + trackHalfLenUm) (midY + capHalfUm)
                                 trackColor
                // Post handles: small filled square per post, on the
                // layer plane. Color codes the kind so the user can
                // tell at a glance: terminus / corner / junction.
                // Rendered as an OUTLINE square + small center
                // cross — no fill — so the underlying metal stays
                // visible. Filled gizmos hide the geometry the
                // user is trying to edit.
                let postHalfUm = 0.16f
                let crossHalfUm = postHalfUm * 0.35f
                for p in route.Posts do
                    let cx = float32 (float p.Position.X * umPerDbu)
                    let cy = float32 (float p.Position.Y * umPerDbu)
                    let color =
                        match p.Kind with
                        | Rekolektion.Viz.Core.Routing.Detect.Terminus ->
                            struct (0.95f, 0.85f, 0.20f)   // yellow
                        | Rekolektion.Viz.Core.Routing.Detect.Corner ->
                            struct (1.00f, 0.55f, 0.20f)   // orange
                        | Rekolektion.Viz.Core.Routing.Detect.Junction ->
                            struct (1.00f, 0.30f, 0.85f)   // magenta
                    let x0 = cx - postHalfUm
                    let x1 = cx + postHalfUm
                    let y0 = cy - postHalfUm
                    let y1 = cy + postHalfUm
                    // Outline square (4 edges).
                    pushLine x0 y0 x1 y0 color
                    pushLine x1 y0 x1 y1 color
                    pushLine x1 y1 x0 y1 color
                    pushLine x0 y1 x0 y0 color
                    // Small center cross so the post location reads
                    // even when the square overlaps something busy.
                    pushLine (cx - crossHalfUm) cy (cx + crossHalfUm) cy color
                    pushLine cx (cy - crossHalfUm) cx (cy + crossHalfUm) color
                let lineCount = lineVerts.Count / 6
                let triCount  = triVerts.Count / 6
                if (lineCount > 0 || triCount > 0) && rulerProgram <> 0u then
                    g.UseProgram rulerProgram
                    let loc = g.GetUniformLocation(rulerProgram, "uMVP")
                    let mvpArr = Matrix4x4Helpers.toFloatArray mvp
                    g.UniformMatrix4(loc, 1u, false, ReadOnlySpan<float32>(mvpArr))
                    let stride = uint32 (6 * sizeof<float32>)
                    // Disable depth so handles are never occluded by
                    // metal above them; the layer-plane Z lift is
                    // unreliable across drivers / FBO depth precision.
                    g.Disable GLEnum.DepthTest
                    if lineCount > 0 then
                        let arr = lineVerts.ToArray()
                        g.BindVertexArray hoverVao
                        g.BindBuffer(GLEnum.ArrayBuffer, hoverVbo)
                        g.BufferData(
                            GLEnum.ArrayBuffer,
                            ReadOnlySpan<float32>(arr),
                            GLEnum.DynamicDraw)
                        g.EnableVertexAttribArray 0u
                        g.VertexAttribPointer(0u, 3, GLEnum.Float, false, stride, nativeint 0)
                        g.EnableVertexAttribArray 1u
                        g.VertexAttribPointer(1u, 3, GLEnum.Float, false, stride, nativeint (3 * sizeof<float32>))
                        g.LineWidth 3.0f
                        g.DrawArrays(GLEnum.Lines, 0, uint32 lineCount)
                    if triCount > 0 then
                        let arr = triVerts.ToArray()
                        g.BindVertexArray hoverVao
                        g.BindBuffer(GLEnum.ArrayBuffer, hoverVbo)
                        g.BufferData(
                            GLEnum.ArrayBuffer,
                            ReadOnlySpan<float32>(arr),
                            GLEnum.DynamicDraw)
                        g.EnableVertexAttribArray 0u
                        g.VertexAttribPointer(0u, 3, GLEnum.Float, false, stride, nativeint 0)
                        g.EnableVertexAttribArray 1u
                        g.VertexAttribPointer(1u, 3, GLEnum.Float, false, stride, nativeint (3 * sizeof<float32>))
                        g.DrawArrays(GLEnum.Triangles, 0, uint32 triCount)
                    g.Enable GLEnum.DepthTest
                    Rekolektion.Viz.App.Services.Logger.trace "route.tool"
                        {| op = "handles"
                           segments = route.Segments.Length
                           posts = route.Posts.Length |}
            | Some _ -> ()
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
