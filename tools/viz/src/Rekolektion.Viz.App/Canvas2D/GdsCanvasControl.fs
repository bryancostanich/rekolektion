module Rekolektion.Viz.App.Canvas2D.GdsCanvasControl

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Media
open Avalonia.Platform
open Avalonia.Rendering.SceneGraph
open Avalonia.Skia
open SkiaSharp
open Rekolektion.Viz.Core
// Open Rkt.Types last so `Document`, `Point`, `Cell`, `Element`,
// and the variant cases (`PolyEl`, `RectEl`, …) all resolve to the
// canonical model. `Gds.Types` still stays open for the few places
// that name `Library` (the legacy on-disk type) explicitly.
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Render.Skia

/// Selection rendering payload. `Instances` + `Selected` together
/// drive a thin cyan outline around the resting selection so the
/// user can see what's picked between drags. During an active drag
/// the canvas swaps in a re-flattened library (selected SRefs
/// translated by the live Δ), so no preview-overlay is drawn — the
/// polygons themselves move under the cursor.
type private SelectionOverlay = {
    Instances : Instances.Instance array
    Selected  : Set<int>
    /// True while a SelectionDrag is in flight. While dragging we
    /// suppress the at-rest cyan outline (the bboxes shown are
    /// stale relative to the moved geometry) — the moved polygons
    /// are themselves the indicator.
    Dragging  : bool
    /// When ShowDimensions, the canvas hands per-instance per-layer
    /// per-polygon bboxes to the overlay so it can dim between
    /// individual feature shapes (not just cell bboxes).
    /// Recomputed every frame from the renderLib so arrows track
    /// edits live during a drag.
    ShowDimensions     : bool
    InstancePolyBboxes :
        Map<int, Map<int * int, (int64 * int64 * int64 * int64) array>>
    /// In-process DRC violations from the active library, drawn
    /// as red bbox outlines + connectors. Empty when the toggle
    /// is off.
    Violations : Drc.Check.Violation array
    /// Marquee rectangle in world DBU (xmin,ymin,xmax,ymax) when a
    /// MarqueeDrag is in flight. None at rest. Renderer shows the
    /// rect translucent so the user sees what they're about to
    /// pick up.
    MarqueeWorld : (int64 * int64 * int64 * int64) option
    /// Net routes for the ratline overlay. Empty when no ratlines
    /// are turned on (the per-net set is empty).
    Routes          : Net.Ratlines.NetRoute array
    /// Set of net names to draw ratlines for. Decoupled from
    /// HighlightedNets — the user can light a net's polygons
    /// without showing its ratline and vice versa.
    VisibleRatlines : Set<string>
    /// Tighten mode candidates. Empty when mode is off. The
    /// renderer uses these to draw numbered candidate dim
    /// arrows + click targets; it returns the per-label hit
    /// rects so OnPointerPressed can map a click to an index.
    TightenCandidates : Drc.Check.TightenCandidate array
    /// Picked top-cell polygon (struct name, element index).
    /// Drawn outlined in cyan so the user sees what they
    /// selected. None when nothing is picked.
    SelectedPolygons : Set<string * int>
    /// World-DBU bbox of the single selected polygon (or the
    /// live-resized bbox during a ResizeDrag). When set AND no
    /// drag is happening, the renderer draws 8 resize handles
    /// around it. None when no single poly is selected, when a
    /// drag is in flight that isn't ResizeDrag, or for multi-poly
    /// selection (resize is single-poly only at v1).
    ResizeBbox : (int64 * int64 * int64 * int64) option
    /// Grid dot overlay on/off. When true, the renderer draws
    /// major + minor dots at Config.GridMajorUm / GridMinorUm
    /// spacing, aligned to the doc's bbox bottom-left.
    ShowGrid : bool
    /// Ruler overlay on/off. Independent from ShowGrid. Anchored
    /// at the doc's bbox bottom-left; ticks point outward.
    ShowRuler : bool
}

/// One of eight resize handles around a single selected polygon's
/// bbox. Corners drive both axes; edges drive one. The "anchor" is
/// the corner of the original bbox opposite the dragged handle —
/// it stays fixed during the resize so the rest of the bbox lerps
/// relative to it.
type private ResizeHandle =
    | HNW | HN | HNE
    | HW       | HE
    | HSW | HS | HSE

/// Captured screen-pixel hit-test rect for one resize handle.
/// SkiaDraw publishes these each render; PointerPressed reads them.
type private ResizeHandleHit = {
    Handle : ResizeHandle
    Rect   : SKRect
}

/// Skia draw operation that takes an explicit ViewBox so the canvas
/// can drive pan/zoom externally. `tightenHitsOut` is published
/// each render with the per-label click target rects (in screen
/// pixels) so the canvas's pointer handler can map a click to a
/// Tighten candidate index. Empty when not in Tighten mode.
type private SkiaDraw(bounds: Rect,
                      lib: Document,
                      flat: FlatPolygon array,
                      vb: LayerPainter.ViewBox,
                      toggle: Visibility.ToggleState,
                      overlay: SelectionOverlay,
                      tightenHitsOut: TightenOverlay.LabelHit array ref,
                      resizeHitsOut: ResizeHandleHit array ref) =
    interface ICustomDrawOperation with
        member _.Bounds = bounds
        member _.Equals(_: ICustomDrawOperation) = false
        member _.HitTest _ = false
        member _.Dispose() = ()
        member _.Render(context) =
            let leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()
            if not (isNull leaseFeature) then
                use lease = leaseFeature.Lease()
                let canvas = lease.SkCanvas
                let w = int bounds.Width
                let h = int bounds.Height
                // canvas here is the WHOLE WINDOW's SkSurface — clip
                // + fill our sub-rect so we don't wipe the tab strip
                // or panels.
                let saved = canvas.Save ()
                let clipRect = SKRect(0.0f, 0.0f, float32 w, float32 h)
                canvas.ClipRect(clipRect, SKClipOperation.Intersect)
                use bg = new SKPaint(Style = SKPaintStyle.Fill, Color = SKColors.Black)
                canvas.DrawRect(clipRect, bg)

                // Compute the flat-geometry bbox once — both the
                // grid (dots align to this corner) and the ruler
                // (axes anchored at this corner) need it. When the
                // doc has no geometry, the grid still draws but
                // falls back to world (0,0) alignment.
                let mutable bxMinFlat = System.Int64.MaxValue
                let mutable byMinFlat = System.Int64.MaxValue
                let mutable bxMaxFlat = System.Int64.MinValue
                let mutable byMaxFlat = System.Int64.MinValue
                for fp in flat do
                    for pt in fp.Points do
                        if pt.X < bxMinFlat then bxMinFlat <- pt.X
                        if pt.X > bxMaxFlat then bxMaxFlat <- pt.X
                        if pt.Y < byMinFlat then byMinFlat <- pt.Y
                        if pt.Y > byMaxFlat then byMaxFlat <- pt.Y
                let hasFlat = bxMaxFlat > bxMinFlat && byMaxFlat > byMinFlat
                let originXDbu = if hasFlat then bxMinFlat else 0L
                let originYDbu = if hasFlat then byMinFlat else 0L

                // Grid dots: drawn between the bg fill and the
                // geometry so they sit behind everything but on top
                // of black. Aligned to the ruler origin (bbox
                // bottom-left) so dots fall exactly on the ruler
                // tick positions.
                if overlay.ShowGrid then
                    let gsX =
                        if vb.MaxX = vb.MinX then 1.0
                        else float vb.PixelW / float (vb.MaxX - vb.MinX)
                    let gsY =
                        if vb.MaxY = vb.MinY then 1.0
                        else float vb.PixelH / float (vb.MaxY - vb.MinY)
                    // World µm per DBU from the document.
                    let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                    let majorDbu =
                        max 1L (int64 (Rekolektion.Viz.App.Services.Config.current.GridMajorUm / umPerDbu))
                    let minorDbu =
                        max 1L (int64 (Rekolektion.Viz.App.Services.Config.current.GridMinorUm / umPerDbu))
                    // Dot screen-pixel spacing — skip the pass when
                    // dots would crowd to < 3 px apart, which is
                    // mush at high zoom-out.
                    let minorPx = float minorDbu * gsX
                    let drawMinor = minorPx >= 3.0
                    let majorPx = float majorDbu * gsX
                    let drawMajor = majorPx >= 3.0
                    let wxToScr (wx: int64) =
                        (float wx - float vb.MinX) * gsX |> float32
                    let wyToScr (wy: int64) =
                        float vb.PixelH - (float wy - float vb.MinY) * gsY |> float32
                    // First grid coordinate at-or-after `lo`, aligned
                    // to `origin + k*step` for integer k. Lets the
                    // dot lattice land on origin instead of world 0.
                    let firstAlignedAtOrAbove (lo: int64) (origin: int64) (step: int64) =
                        let d = lo - origin
                        let k =
                            if d <= 0L then -((-d) / step)
                            else (d + step - 1L) / step
                        origin + k * step
                    if drawMinor then
                        use minorPaint =
                            new SKPaint(
                                Style = SKPaintStyle.Fill,
                                Color = SKColor(0x90uy, 0x90uy, 0x90uy, 0xE0uy),
                                IsAntialias = true)
                        let xStart = firstAlignedAtOrAbove vb.MinX originXDbu minorDbu
                        let yStart = firstAlignedAtOrAbove vb.MinY originYDbu minorDbu
                        let mutable wx = xStart
                        while wx <= vb.MaxX do
                            let sx = wxToScr wx
                            let mutable wy = yStart
                            while wy <= vb.MaxY do
                                // Skip minors that coincide with a
                                // major — the major pass draws over
                                // them and keeps the visual clean.
                                // Major alignment is also relative
                                // to origin.
                                if ((wx - originXDbu) % majorDbu <> 0L)
                                   || ((wy - originYDbu) % majorDbu <> 0L) then
                                    let sy = wyToScr wy
                                    canvas.DrawCircle(sx, sy, 1.0f, minorPaint)
                                wy <- wy + minorDbu
                            wx <- wx + minorDbu
                    if drawMajor then
                        use majorPaint =
                            new SKPaint(
                                Style = SKPaintStyle.Fill,
                                Color = SKColor(0xE0uy, 0xE0uy, 0xE0uy, 0xFFuy),
                                IsAntialias = true)
                        let xStart = firstAlignedAtOrAbove vb.MinX originXDbu majorDbu
                        let yStart = firstAlignedAtOrAbove vb.MinY originYDbu majorDbu
                        let mutable wx = xStart
                        while wx <= vb.MaxX do
                            let sx = wxToScr wx
                            let mutable wy = yStart
                            while wy <= vb.MaxY do
                                let sy = wyToScr wy
                                canvas.DrawCircle(sx, sy, 1.6f, majorPaint)
                                wy <- wy + majorDbu
                            wx <- wx + majorDbu

                LayerPainter.paintIn canvas vb lib flat toggle
                LabelPainter.paintIn canvas vb lib toggle

                let scaleX =
                    if vb.MaxX = vb.MinX then 1.0
                    else float vb.PixelW / float (vb.MaxX - vb.MinX)
                let scaleY =
                    if vb.MaxY = vb.MinY then 1.0
                    else float vb.PixelH / float (vb.MaxY - vb.MinY)
                let bboxRect (x1, y1, x2, y2) =
                    let sx1 = (float x1 - float vb.MinX) * scaleX |> float32
                    let sx2 = (float x2 - float vb.MinX) * scaleX |> float32
                    let sy1 = float vb.PixelH - (float y1 - float vb.MinY) * scaleY |> float32
                    let sy2 = float vb.PixelH - (float y2 - float vb.MinY) * scaleY |> float32
                    SKRect(min sx1 sx2, min sy1 sy2, max sx1 sx2, max sy1 sy2)

                // Cell bbox outlines (dotted) — every top-level
                // instance gets a faint dotted rectangle so cells are
                // visible at a glance even when their own polys are
                // hidden by layer toggles.
                if overlay.Instances.Length > 0 then
                    use cellStroke =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            Color = SKColor(0xC0uy, 0xC0uy, 0xC0uy, 0xA0uy),
                            StrokeWidth = 1.0f,
                            IsAntialias = true,
                            PathEffect = SKPathEffect.CreateDash([| 1.5f; 3.0f |], 0.0f))
                    for inst in overlay.Instances do
                        canvas.DrawRect(bboxRect inst.BBox, cellStroke)

                // Top-cell (whole-GDS) bbox — union of every flat
                // polygon. Drawn dashed yellow so the die outline is
                // visible even when zoomed out and instance bboxes
                // crowd the view.
                if flat.Length > 0 then
                    let mutable xMin = System.Int64.MaxValue
                    let mutable yMin = System.Int64.MaxValue
                    let mutable xMax = System.Int64.MinValue
                    let mutable yMax = System.Int64.MinValue
                    for fp in flat do
                        for p in fp.Points do
                            if p.X < xMin then xMin <- p.X
                            if p.X > xMax then xMax <- p.X
                            if p.Y < yMin then yMin <- p.Y
                            if p.Y > yMax then yMax <- p.Y
                    if xMax >= xMin && yMax >= yMin then
                        use topStroke =
                            new SKPaint(
                                Style = SKPaintStyle.Stroke,
                                Color = SKColor(0xFFuy, 0xD0uy, 0x40uy, 0xE0uy),
                                StrokeWidth = 1.5f,
                                IsAntialias = true,
                                PathEffect = SKPathEffect.CreateDash([| 6.0f; 4.0f |], 0.0f))
                        canvas.DrawRect(bboxRect (xMin, yMin, xMax, yMax), topStroke)

                // Resting selection: thin cyan outline around each
                // selected instance's bbox so the user can see what's
                // picked. Suppressed during drag — the polygons
                // themselves are moving and the at-rest bbox would
                // lie at the pre-drag position (wrong + distracting).
                if not overlay.Dragging
                   && overlay.Selected.Count > 0
                   && overlay.Instances.Length > 0 then
                    // Soft outer halo so the selection stays
                    // legible when a DRC red outline (or any other
                    // overlay) sits on top of the same edge. Drawn
                    // first so the crisp cyan line lands on top.
                    use halo = new SKPaint(
                                    Style = SKPaintStyle.Stroke,
                                    Color = SKColor(0x00uy, 0xFFuy, 0xFFuy, 0xC0uy),
                                    StrokeWidth = 7.0f,
                                    IsAntialias = true,
                                    MaskFilter =
                                        SKMaskFilter.CreateBlur(
                                            SKBlurStyle.Normal, 3.5f))
                    use stroke = new SKPaint(
                                    Style = SKPaintStyle.Stroke,
                                    Color = SKColor(0x00uy, 0xFFuy, 0xFFuy, 0xFFuy),
                                    StrokeWidth = 1.5f,
                                    IsAntialias = true)
                    for inst in overlay.Instances do
                        if overlay.Selected.Contains inst.Index then
                            let r = bboxRect inst.BBox
                            canvas.DrawRect(r, halo)
                            canvas.DrawRect(r, stroke)

                if overlay.ShowDimensions
                   && overlay.Selected.Count > 0
                   && overlay.Instances.Length > 0 then
                    DimensionOverlay.render
                        canvas vb lib
                        overlay.Instances overlay.Selected
                        overlay.InstancePolyBboxes
                        DimensionOverlay.defaultSettings
                // Polygon selection outlines. Cyan stroke tracing
                // each picked element's edges so the user sees what
                // the click(s) landed on. Suppressed during drag —
                // the outlines would stick at pre-drag positions
                // (the source-of-truth Library hasn't been mutated
                // yet) while the live FlatPolygons preview shows
                // the moved geometry, so the two don't agree.
                if not overlay.SelectedPolygons.IsEmpty
                   && not overlay.Dragging then
                    let scaleX =
                        if vb.MaxX = vb.MinX then 1.0
                        else float vb.PixelW / float (vb.MaxX - vb.MinX)
                    let scaleY =
                        if vb.MaxY = vb.MinY then 1.0
                        else float vb.PixelH / float (vb.MaxY - vb.MinY)
                    // Soft halo first so the crisp 2 px stroke
                    // lands on top — keeps the selection readable
                    // when DRC's red outline shares the edge.
                    use pHalo =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            Color = SKColor(0x00uy, 0xFFuy, 0xFFuy, 0xC0uy),
                            StrokeWidth = 7.0f,
                            IsAntialias = true,
                            MaskFilter =
                                SKMaskFilter.CreateBlur(
                                    SKBlurStyle.Normal, 3.5f))
                    use pSel =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            Color = SKColor(0x00uy, 0xFFuy, 0xFFuy, 0xFFuy),
                            StrokeWidth = 2.0f,
                            IsAntialias = true)
                    let toScreen (pt: Point) =
                        let sx = (float pt.X - float vb.MinX) * scaleX |> float32
                        let sy = float vb.PixelH - (float pt.Y - float vb.MinY) * scaleY |> float32
                        SKPoint(sx, sy)
                    let structByName =
                        lib.Cells
                        |> List.map (fun s -> s.Name, s)
                        |> Map.ofList
                    for (sname, idx) in overlay.SelectedPolygons do
                        match Map.tryFind sname structByName with
                        | Some s when idx >= 0 && idx < s.Elements.Length ->
                            let pts =
                                match s.Elements.[idx] with
                                | PolyEl p -> Some p.Points
                                | PathEl p -> Some p.Points
                                | RectEl r ->
                                    Some [
                                        { X = r.X1; Y = r.Y1 }
                                        { X = r.X2; Y = r.Y1 }
                                        { X = r.X2; Y = r.Y2 }
                                        { X = r.X1; Y = r.Y2 }
                                        { X = r.X1; Y = r.Y1 }
                                    ]
                                | _ -> None
                            match pts with
                            | Some points when points.Length > 0 ->
                                let path = new SKPath()
                                let first = toScreen points.[0]
                                path.MoveTo first
                                for i in 1 .. points.Length - 1 do
                                    path.LineTo (toScreen points.[i])
                                path.Close()
                                canvas.DrawPath(path, pHalo)
                                canvas.DrawPath(path, pSel)
                                path.Dispose()
                            | _ -> ()
                        | _ -> ()

                if overlay.Violations.Length > 0 then
                    DrcOverlay.render canvas vb (float lib.Units.DbuNm * 1.0e-3) overlay.Violations

                if overlay.Routes.Length > 0 then
                    RatlineOverlay.render canvas vb
                        overlay.Routes overlay.VisibleRatlines

                // Tighten mode: numbered candidate dim arrows
                // sit on top of all the other overlays. Capture
                // the per-label hit rects so the canvas's
                // pointer handler can dispatch CommitTighten on
                // click.
                if overlay.TightenCandidates.Length > 0 then
                    let hits =
                        TightenOverlay.render
                            canvas vb (float lib.Units.DbuNm * 1.0e-3)
                            overlay.TightenCandidates
                    tightenHitsOut := hits
                else
                    tightenHitsOut := [||]

                // Resize handles: 8 squares around the single
                // selected polygon's bbox (4 corners + 4 edge
                // midpoints). Drawn after the selection outline so
                // they sit on top. Hidden during any drag because
                // the bbox would be stale relative to the live
                // geometry — except for the ResizeDrag itself,
                // where `overlay.ResizeBbox` already reflects the
                // in-flight bbox. Publishes per-handle hit-test
                // rects for the canvas's PointerPressed.
                match overlay.ResizeBbox with
                | Some (rxMin, ryMin, rxMax, ryMax) ->
                    let sX =
                        if vb.MaxX = vb.MinX then 1.0
                        else float vb.PixelW / float (vb.MaxX - vb.MinX)
                    let sY =
                        if vb.MaxY = vb.MinY then 1.0
                        else float vb.PixelH / float (vb.MaxY - vb.MinY)
                    let wxToScr (wx: int64) =
                        (float wx - float vb.MinX) * sX |> float32
                    let wyToScr (wy: int64) =
                        float vb.PixelH - (float wy - float vb.MinY) * sY |> float32
                    // Screen-pixel bbox. World Y grows upward, screen
                    // Y grows downward — world ymax maps to screen
                    // ymin and vice versa.
                    let sxMin = wxToScr rxMin
                    let sxMax = wxToScr rxMax
                    let syMin = wyToScr ryMax
                    let syMax = wyToScr ryMin
                    let midX = (sxMin + sxMax) * 0.5f
                    let midY = (syMin + syMax) * 0.5f
                    let half = 4.0f
                    use fill =
                        new SKPaint(
                            Style = SKPaintStyle.Fill,
                            Color = SKColors.White,
                            IsAntialias = true)
                    use stroke =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            Color = SKColor(0x00uy, 0xFFuy, 0xFFuy, 0xFFuy),
                            StrokeWidth = 1.5f,
                            IsAntialias = true)
                    let hits = System.Collections.Generic.List<ResizeHandleHit>()
                    let drawHandle (handle: ResizeHandle) (cx: float32) (cy: float32) =
                        let r = SKRect(cx - half, cy - half, cx + half, cy + half)
                        canvas.DrawRect(r, fill)
                        canvas.DrawRect(r, stroke)
                        hits.Add { Handle = handle; Rect = r }
                    drawHandle HNW  sxMin syMin
                    drawHandle HN   midX  syMin
                    drawHandle HNE  sxMax syMin
                    drawHandle HW   sxMin midY
                    drawHandle HE   sxMax midY
                    drawHandle HSW  sxMin syMax
                    drawHandle HS   midX  syMax
                    drawHandle HSE  sxMax syMax
                    resizeHitsOut := hits.ToArray()
                | None ->
                    resizeHitsOut := [||]

                match overlay.MarqueeWorld with
                | Some (mx1, my1, mx2, my2) ->
                    let scaleX =
                        if vb.MaxX = vb.MinX then 1.0
                        else float vb.PixelW / float (vb.MaxX - vb.MinX)
                    let scaleY =
                        if vb.MaxY = vb.MinY then 1.0
                        else float vb.PixelH / float (vb.MaxY - vb.MinY)
                    let toScreen (x: int64, y: int64) =
                        let sx = (float x - float vb.MinX) * scaleX |> float32
                        let sy = float vb.PixelH - (float y - float vb.MinY) * scaleY |> float32
                        sx, sy
                    let (sx1, sy1) = toScreen (mx1, my1)
                    let (sx2, sy2) = toScreen (mx2, my2)
                    let r = SKRect(min sx1 sx2, min sy1 sy2, max sx1 sx2, max sy1 sy2)
                    // CAD convention: blue solid for left→right
                    // (enclose-only); green dashed for right→left
                    // (touch-select).
                    let enclose = mx2 >= mx1
                    let fillColor =
                        if enclose then SKColor(0x40uy, 0x80uy, 0xFFuy, 0x22uy)
                        else SKColor(0x40uy, 0xFFuy, 0x80uy, 0x22uy)
                    let strokeColor =
                        if enclose then SKColor(0x40uy, 0x80uy, 0xFFuy, 0xFFuy)
                        else SKColor(0x40uy, 0xFFuy, 0x80uy, 0xFFuy)
                    use mFill =
                        new SKPaint(
                            Style = SKPaintStyle.Fill,
                            Color = fillColor,
                            IsAntialias = true)
                    use mStroke =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            Color = strokeColor,
                            StrokeWidth = 1.0f,
                            IsAntialias = true)
                    if not enclose then
                        mStroke.PathEffect <- SKPathEffect.CreateDash([| 4.0f; 3.0f |], 0.0f)
                    canvas.DrawRect(r, mFill)
                    canvas.DrawRect(r, mStroke)
                | None -> ()

                // Origin ruler: axes anchored at the FLAT bbox's
                // bottom-left corner (not world 0,0). Matches the
                // 3D canvas — the user reads tick values as offsets
                // from the cell's lower-left, which is what they
                // care about for cell-level dimensions. Ticks
                // extend OUTWARD ONLY (down from the X axis, left
                // from the Y axis) so the axes don't visually
                // pollute the cell interior. Tick hierarchy in
                // µm offsets from origin:
                //   sub-tick : every 0.1 in 0..10  (shortest)
                //   minor    : every 1   in 0..10  (medium)
                //   major    : every 5   for the rest (longest)
                if overlay.ShowRuler && hasFlat then
                    let bxMin, byMin = bxMinFlat, byMinFlat
                    let bxMax, byMax = bxMaxFlat, byMaxFlat
                    do
                        let gsX =
                            if vb.MaxX = vb.MinX then 1.0
                            else float vb.PixelW / float (vb.MaxX - vb.MinX)
                        let gsY =
                            if vb.MaxY = vb.MinY then 1.0
                            else float vb.PixelH / float (vb.MaxY - vb.MinY)
                        let umPerDbu = float lib.Units.DbuNm * 1.0e-3
                        let wxToScr (wx: int64) =
                            (float wx - float vb.MinX) * gsX |> float32
                        let wyToScr (wy: int64) =
                            float vb.PixelH - (float wy - float vb.MinY) * gsY |> float32
                        // Origin = bbox bottom-left in world DBU.
                        let origSx = wxToScr bxMin
                        let origSy = wyToScr byMin
                        let xEndSx = wxToScr bxMax
                        let yEndSy = wyToScr byMax
                        let xColor = SKColor(0xFFuy, 0x80uy, 0x80uy, 0xE0uy)
                        let yColor = SKColor(0x80uy, 0xFFuy, 0x80uy, 0xE0uy)
                        use axisX =
                            new SKPaint(
                                Style = SKPaintStyle.Stroke,
                                Color = xColor,
                                StrokeWidth = 1.0f,
                                IsAntialias = true)
                        use axisY =
                            new SKPaint(
                                Style = SKPaintStyle.Stroke,
                                Color = yColor,
                                StrokeWidth = 1.0f,
                                IsAntialias = true)
                        use tickPaint =
                            new SKPaint(
                                Style = SKPaintStyle.Stroke,
                                Color = SKColor(0xE0uy, 0xE0uy, 0xE0uy, 0xE0uy),
                                StrokeWidth = 1.0f,
                                IsAntialias = true)
                        use labelPaint =
                            new SKPaint(
                                Style = SKPaintStyle.Fill,
                                Color = SKColors.White,
                                IsAntialias = true,
                                TextSize = 10.0f)
                        // X spine along bbox bottom edge.
                        canvas.DrawLine(
                            SKPoint(origSx, origSy),
                            SKPoint(xEndSx, origSy),
                            axisX)
                        // Y spine along bbox left edge.
                        canvas.DrawLine(
                            SKPoint(origSx, origSy),
                            SKPoint(origSx, yEndSy),
                            axisY)
                        // Tick µm positions ALONG an axis, expressed
                        // as offsets from the bbox corner (i.e. 0
                        // µm at the corner, growing toward the
                        // opposite edge). Minor every 1 µm in
                        // 0..10, major every 5 µm thereafter.
                        // Sub-ticks: every 0.1 µm in the 0..10 µm
                        // range. Skip integer-µm positions (those
                        // are the next-bigger tick rank).
                        let subTickUmsAlong (extentDbu: int64) : float seq =
                            seq {
                                let extentUm = float extentDbu * umPerDbu
                                let cap = min extentUm 10.0
                                // Integer step 1..99 in 0.1 µm units
                                // dodges floating-point drift that
                                // breaks the "skip integers" test
                                // when we go straight to floats.
                                let mutable i = 1
                                let upper = int (cap * 10.0 + 1e-6)
                                while i <= upper do
                                    if i % 10 <> 0 then
                                        yield float i * 0.1
                                    i <- i + 1
                            }
                        // Whole-µm ticks + labels along the full
                        // extent. Sub-ticks (0.1 µm) still only
                        // appear in 0..10 µm because they'd be
                        // visually crowded across a large bbox; the
                        // 1-µm ticks scale fine even at hundreds of
                        // micrometers.
                        let tickUmsAlong (extentDbu: int64) : float seq =
                            seq {
                                let extentUm = float extentDbu * umPerDbu
                                let mutable t = 0.0
                                while t <= extentUm + 1e-6 do
                                    yield t
                                    t <- t + 1.0
                            }
                        let tickLen = 10.0f
                        let subTickLen = 5.0f
                        let xExtent = bxMax - bxMin
                        let yExtent = byMax - byMin
                        // X-axis sub-ticks first so the larger
                        // minor/major lines draw over them where
                        // they coincide (clean visual).
                        for um in subTickUmsAlong xExtent do
                            let wx = bxMin + int64 (um / umPerDbu)
                            let sx = wxToScr wx
                            canvas.DrawLine(
                                SKPoint(sx, origSy),
                                SKPoint(sx, origSy + subTickLen),
                                tickPaint)
                        // X-axis minor + major ticks + labels.
                        for um in tickUmsAlong xExtent do
                            let wx = bxMin + int64 (um / umPerDbu)
                            let sx = wxToScr wx
                            canvas.DrawLine(
                                SKPoint(sx, origSy),
                                SKPoint(sx, origSy + tickLen),
                                tickPaint)
                            if um > 1e-6 then
                                let label = sprintf "%.0f" um
                                canvas.DrawText(label, sx + 2.0f, origSy + tickLen + 11.0f, labelPaint)
                        // Y-axis sub-ticks.
                        for um in subTickUmsAlong yExtent do
                            let wy = byMin + int64 (um / umPerDbu)
                            let sy = wyToScr wy
                            canvas.DrawLine(
                                SKPoint(origSx - subTickLen, sy),
                                SKPoint(origSx, sy),
                                tickPaint)
                        // Y-axis minor + major ticks + labels.
                        for um in tickUmsAlong yExtent do
                            let wy = byMin + int64 (um / umPerDbu)
                            let sy = wyToScr wy
                            canvas.DrawLine(
                                SKPoint(origSx - tickLen, sy),
                                SKPoint(origSx, sy),
                                tickPaint)
                            if um > 1e-6 then
                                let label = sprintf "%.0f" um
                                canvas.DrawText(label, origSx - tickLen - 18.0f, sy + 4.0f, labelPaint)

                canvas.RestoreToCount saved

type private DragKind =
    | NoDrag
    | PanDrag
    | SelectionDrag
    | MarqueeDrag
    | PolygonDrag
    | ResizeDrag of handle: ResizeHandle * structure: string * index: int

type GdsCanvasControl() as this =
    inherit Control()

    // 2D view state. `centerX/Y` is the world DBU point at the
    // canvas's screen center; `pixelsPerDbu` is the on-screen
    // scale. Auto-fit on FlatPolygons change; user pan (drag) and
    // zoom (wheel) modify these directly.
    let mutable centerX : float = 0.0
    let mutable centerY : float = 0.0
    let mutable pixelsPerDbu : float = 1.0
    let mutable hasFitted : bool = false

    // Pointer interaction state. `dragKind` distinguishes a
    // selection-drag (left button on geometry, may translate the
    // selection) from a pan-drag (middle/right, or left on empty
    // space). `dragLiveDeltaDbu` is the grid-snapped Δ accumulated
    // since pointer-press; we re-render with it so the user sees
    // a live ghost of the moving selection without committing the
    // edit through the model on every mouse-move tick.
    let mutable dragKind : DragKind = NoDrag
    let mutable lastPos : Avalonia.Point = Avalonia.Point()
    // Last modifier-key state captured from a PointerMoved. The
    // auto-pan timer fires WITHOUT an event arg, so it can't read
    // KeyModifiers from the source event; it pulls from here.
    let mutable lastModifiers : KeyModifiers = KeyModifiers.None
    // Auto-pan ticker. Drives the edge-of-viewport pan + drag
    // advance while the cursor sits in the edge band, including
    // when the user is HOLDING the cursor still. Started by
    // OnPointerMoved when the cursor enters the band; stopped by
    // its own Tick handler when the band-or-drag condition no
    // longer holds, and by OnPointerReleased when the drag ends.
    // 33 ms = ~30 fps; combined with the maxRatePx in
    // AutoPanIfNearEdge, that's ~120 px/sec at saturation —
    // steerable, not jarring.
    let autoPanTimer =
        let t = Avalonia.Threading.DispatcherTimer()
        t.Interval <- System.TimeSpan.FromMilliseconds(33.0)
        t
    do autoPanTimer.Tick.Add(fun _ -> this.OnAutoPanTick ())
    // Resting centroid of the selection at the moment a drag
    // armed. Used so move snaps the SELECTION'S CENTROID to the
    // user grid — not the cursor delta. A user grabbing a cell
    // by its corner expects the cell's center to land on grid
    // intersections, not "wherever the cursor lands plus rounding."
    let mutable dragStartCentroidX : int64 = 0L
    let mutable dragStartCentroidY : int64 = 0L
    let mutable dragStartWorldX : float = 0.0
    let mutable dragStartWorldY : float = 0.0
    let mutable dragLiveDeltaDbu : int64 * int64 = 0L, 0L
    // Speculative re-flatten cached during an in-flight selection
    // drag: every time the snapped Δ changes, we copy the active
    // Library, translate the selected SRef origins, and re-flatten.
    // The Render path uses these instead of the bound FlatPolygons
    // so the moved geometry — not a ghost outline — tracks the
    // cursor. None when no drag is active.
    let mutable dragLiveLib : Document option = None
    let mutable dragLiveFlat : FlatPolygon array = [||]
    // Tighten-mode state. `tightenHits` is overwritten by SkiaDraw
    // each render with the per-label click targets so
    // OnPointerPressed can map a click to a candidate index. The
    // commit handler dispatches `CommitTighten i` and the model
    // exits mode.
    let tightenHits : TightenOverlay.LabelHit array ref = ref [||]
    // Marquee select state. World-DBU corners, both updated in
    // OnPointerMoved. Render shows a translucent rect; on release
    // we select every instance whose bbox intersects this rect.
    // `marqueeAdditive` records the Shift modifier at press time
    // so the marquee acts as "add to selection" instead of
    // replace.
    let mutable marqueeWorldStart : (int64 * int64) = 0L, 0L
    let mutable marqueeWorldEnd   : (int64 * int64) = 0L, 0L
    let mutable marqueeAdditive   : bool = false

    // Resize state. `resizeStartBbox` is the selected poly's bbox
    // at the moment ResizeDrag armed; `resizeLiveBbox` is the
    // snapped current bbox during the drag. The renderer reads
    // `resizeLiveBbox` to draw moved handles and dragLiveFlat to
    // draw the in-flight scaled polygon. `resizeHandleHits` is
    // overwritten each render by SkiaDraw with the screen-pixel
    // rects so PointerPressed can map a click to a handle.
    let mutable resizeStartBbox : int64 * int64 * int64 * int64 = 0L, 0L, 0L, 0L
    let mutable resizeLiveBbox  : int64 * int64 * int64 * int64 = 0L, 0L, 0L, 0L
    let resizeHandleHits : ResizeHandleHit array ref = ref [||]

    // Make the control focusable so ESC (clear selection) lands
    // here. Setting Focusable from the instance ctor triggers
    // OnPropertyChanged during F# type init, which recursively
    // dereferences the static StyledProperty fields and crashes
    // with FailInit. Override the metadata default instead — that
    // runs in the static ctor before any instance exists.
    static do
        Avalonia.Input.InputElement.FocusableProperty.OverrideDefaultValue<GdsCanvasControl>(true)

    static member val LibraryProperty : StyledProperty<Document option> =
        AvaloniaProperty.Register<GdsCanvasControl, Document option>("Library", None)
        with get
    /// Path of the active macro. Changes ONLY on new-file load
    /// (or rename), not on every edit. The canvas uses this as
    /// the auto-fit trigger so geometry edits (drag, Tighten,
    /// rotate, mirror) don't reset the user's pan/zoom.
    static member val MacroPathProperty : StyledProperty<string option> =
        AvaloniaProperty.Register<GdsCanvasControl, string option>("MacroPath", None)
        with get
    static member val FlatPolygonsProperty : StyledProperty<FlatPolygon array> =
        AvaloniaProperty.Register<GdsCanvasControl, FlatPolygon array>("FlatPolygons", [||])
        with get
    static member val ToggleProperty : StyledProperty<Visibility.ToggleState> =
        AvaloniaProperty.Register<GdsCanvasControl, Visibility.ToggleState>("Toggle", Visibility.empty)
        with get
    static member val InstancesProperty : StyledProperty<Instances.Instance array> =
        AvaloniaProperty.Register<GdsCanvasControl, Instances.Instance array>("Instances", [||])
        with get
    static member val InstanceSelectionProperty : StyledProperty<Set<int>> =
        AvaloniaProperty.Register<GdsCanvasControl, Set<int>>("InstanceSelection", Set.empty)
        with get
    static member val SetInstanceSelectionHandlerProperty
            : StyledProperty<Action<Set<int>>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<Set<int>>>(
            "SetInstanceSelectionHandler", null)
        with get
    static member val ClearInstanceSelectionHandlerProperty
            : StyledProperty<Action> =
        AvaloniaProperty.Register<GdsCanvasControl, Action>(
            "ClearInstanceSelectionHandler", null)
        with get
    static member val MoveSelectionHandlerProperty
            : StyledProperty<Action<int64, int64>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<int64, int64>>(
            "MoveSelectionHandler", null)
        with get
    static member val ShowDimensionsProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("ShowDimensions", false)
        with get
    static member val ToggleDimensionsHandlerProperty : StyledProperty<Action> =
        AvaloniaProperty.Register<GdsCanvasControl, Action>(
            "ToggleDimensionsHandler", null)
        with get
    static member val ShowGridProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("ShowGrid", false)
        with get
    static member val ShowRulerProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("ShowRuler", false)
        with get
    static member val SnapEnabledProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("SnapEnabled", false)
        with get
    static member val ShowDrcProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("ShowDrc", false)
        with get
    /// Set of net names whose ratlines are drawn. Replaces the old
    /// boolean ShowRatlines — the master "all on/off" toggle now
    /// flips this set between full and empty in the Update layer.
    static member val VisibleRatlinesProperty : StyledProperty<Set<string>> =
        AvaloniaProperty.Register<GdsCanvasControl, Set<string>>(
            "VisibleRatlines", Set.empty)
        with get
    static member val TightenModeProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("TightenMode", false)
        with get
    /// Dispatched when the user clicks a numbered Tighten label.
    /// The Action arg is the 1-based candidate index.
    static member val CommitTightenHandlerProperty
            : StyledProperty<Action<int>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<int>>(
            "CommitTightenHandler", null)
        with get
    /// Polygon-pick callback. The host wires this to dispatch
    /// `PolygonPicked (struct, index)`. Action(structure, index).
    /// Null = no-op listener.
    static member val PolygonPickedHandlerProperty
            : StyledProperty<Action<string, int>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<string, int>>(
            "PolygonPickedHandler", null)
        with get
    /// Currently picked top-cell polygons: set of (struct name,
    /// element index). Drives the highlight outline. Empty when
    /// nothing is picked. Multi-select supported via shift-click.
    static member val SelectedPolygonsProperty
            : StyledProperty<Set<string * int>> =
        AvaloniaProperty.Register<GdsCanvasControl, Set<string * int>>(
            "SelectedPolygons", Set.empty)
        with get
    /// Replace the polygon selection (used by shift-click extend
    /// and marquee bulk-pick).
    static member val SetPolygonSelectionHandlerProperty
            : StyledProperty<Action<Set<string * int>>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<Set<string * int>>>(
            "SetPolygonSelectionHandler", null)
        with get
    /// Translate the entire polygon selection by Δ DBU.
    /// Dispatched when a resize handle commit lands. Args:
    /// (structure, elementIndex, newXMin, newYMin, newXMax, newYMax).
    /// Update applies the bbox-scale to the element's points
    /// (PolyEl) or replaces its coords (RectEl); see Update.fs.
    static member val ResizePolygonHandlerProperty
            : StyledProperty<Action<string, int, int64, int64, int64, int64>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<string, int, int64, int64, int64, int64>>(
            "ResizePolygonHandler", null)
        with get
    static member val MovePolygonsHandlerProperty
            : StyledProperty<Action<Set<string * int>, int64, int64>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<Set<string * int>, int64, int64>>(
            "MovePolygonsHandler", null)
        with get
    /// Clear the polygon Selection (Esc / empty marquee).
    static member val ClearPolygonSelectionHandlerProperty
            : StyledProperty<Action> =
        AvaloniaProperty.Register<GdsCanvasControl, Action>(
            "ClearPolygonSelectionHandler", null)
        with get

    member this.Library
        with get() : Document option = this.GetValue(GdsCanvasControl.LibraryProperty)
        and set(v: Document option) = this.SetValue(GdsCanvasControl.LibraryProperty, v) |> ignore

    member this.MacroPath
        with get() : string option = this.GetValue(GdsCanvasControl.MacroPathProperty)
        and set(v: string option) = this.SetValue(GdsCanvasControl.MacroPathProperty, v) |> ignore

    member this.FlatPolygons
        with get() : FlatPolygon array = this.GetValue(GdsCanvasControl.FlatPolygonsProperty)
        and set(v: FlatPolygon array) = this.SetValue(GdsCanvasControl.FlatPolygonsProperty, v) |> ignore

    member this.Toggle
        with get() : Visibility.ToggleState = this.GetValue(GdsCanvasControl.ToggleProperty)
        and set(v: Visibility.ToggleState) = this.SetValue(GdsCanvasControl.ToggleProperty, v) |> ignore

    member this.Instances
        with get() : Instances.Instance array = this.GetValue(GdsCanvasControl.InstancesProperty)
        and set(v: Instances.Instance array) = this.SetValue(GdsCanvasControl.InstancesProperty, v) |> ignore

    member this.InstanceSelection
        with get() : Set<int> = this.GetValue(GdsCanvasControl.InstanceSelectionProperty)
        and set(v: Set<int>) = this.SetValue(GdsCanvasControl.InstanceSelectionProperty, v) |> ignore

    member this.SetInstanceSelectionHandler
        with get() : Action<Set<int>> =
            this.GetValue(GdsCanvasControl.SetInstanceSelectionHandlerProperty)
        and set(v: Action<Set<int>>) =
            this.SetValue(GdsCanvasControl.SetInstanceSelectionHandlerProperty, v) |> ignore

    member this.ClearInstanceSelectionHandler
        with get() : Action =
            this.GetValue(GdsCanvasControl.ClearInstanceSelectionHandlerProperty)
        and set(v: Action) =
            this.SetValue(GdsCanvasControl.ClearInstanceSelectionHandlerProperty, v) |> ignore

    member this.MoveSelectionHandler
        with get() : Action<int64, int64> =
            this.GetValue(GdsCanvasControl.MoveSelectionHandlerProperty)
        and set(v: Action<int64, int64>) =
            this.SetValue(GdsCanvasControl.MoveSelectionHandlerProperty, v) |> ignore

    member this.ShowDimensions
        with get() : bool = this.GetValue(GdsCanvasControl.ShowDimensionsProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.ShowDimensionsProperty, v) |> ignore

    member this.ToggleDimensionsHandler
        with get() : Action =
            this.GetValue(GdsCanvasControl.ToggleDimensionsHandlerProperty)
        and set(v: Action) =
            this.SetValue(GdsCanvasControl.ToggleDimensionsHandlerProperty, v) |> ignore

    member this.ShowDrc
        with get() : bool = this.GetValue(GdsCanvasControl.ShowDrcProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.ShowDrcProperty, v) |> ignore

    member this.ShowGrid
        with get() : bool = this.GetValue(GdsCanvasControl.ShowGridProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.ShowGridProperty, v) |> ignore

    member this.ShowRuler
        with get() : bool = this.GetValue(GdsCanvasControl.ShowRulerProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.ShowRulerProperty, v) |> ignore

    member this.SnapEnabled
        with get() : bool = this.GetValue(GdsCanvasControl.SnapEnabledProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.SnapEnabledProperty, v) |> ignore

    member this.VisibleRatlines
        with get() : Set<string> = this.GetValue(GdsCanvasControl.VisibleRatlinesProperty)
        and set(v: Set<string>) = this.SetValue(GdsCanvasControl.VisibleRatlinesProperty, v) |> ignore

    member this.TightenMode
        with get() : bool = this.GetValue(GdsCanvasControl.TightenModeProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.TightenModeProperty, v) |> ignore

    member this.CommitTightenHandler
        with get() : Action<int> =
            this.GetValue(GdsCanvasControl.CommitTightenHandlerProperty)
        and set(v: Action<int>) =
            this.SetValue(GdsCanvasControl.CommitTightenHandlerProperty, v) |> ignore

    member this.PolygonPickedHandler
        with get() : Action<string, int> =
            this.GetValue(GdsCanvasControl.PolygonPickedHandlerProperty)
        and set(v: Action<string, int>) =
            this.SetValue(GdsCanvasControl.PolygonPickedHandlerProperty, v) |> ignore

    member this.SelectedPolygons
        with get() : Set<string * int> =
            this.GetValue(GdsCanvasControl.SelectedPolygonsProperty)
        and set(v: Set<string * int>) =
            this.SetValue(GdsCanvasControl.SelectedPolygonsProperty, v) |> ignore

    member this.SetPolygonSelectionHandler
        with get() : Action<Set<string * int>> =
            this.GetValue(GdsCanvasControl.SetPolygonSelectionHandlerProperty)
        and set(v: Action<Set<string * int>>) =
            this.SetValue(GdsCanvasControl.SetPolygonSelectionHandlerProperty, v) |> ignore

    member this.ResizePolygonHandler
        with get() : Action<string, int, int64, int64, int64, int64> =
            this.GetValue(GdsCanvasControl.ResizePolygonHandlerProperty)
        and set(v: Action<string, int, int64, int64, int64, int64>) =
            this.SetValue(GdsCanvasControl.ResizePolygonHandlerProperty, v) |> ignore

    member this.MovePolygonsHandler
        with get() : Action<Set<string * int>, int64, int64> =
            this.GetValue(GdsCanvasControl.MovePolygonsHandlerProperty)
        and set(v: Action<Set<string * int>, int64, int64>) =
            this.SetValue(GdsCanvasControl.MovePolygonsHandlerProperty, v) |> ignore

    member this.ClearPolygonSelectionHandler
        with get() : Action =
            this.GetValue(GdsCanvasControl.ClearPolygonSelectionHandlerProperty)
        and set(v: Action) =
            this.SetValue(GdsCanvasControl.ClearPolygonSelectionHandlerProperty, v) |> ignore

    override _.MeasureOverride(constraint': Size) : Size =
        let w =
            if System.Double.IsInfinity constraint'.Width then 200.0
            else constraint'.Width
        let h =
            if System.Double.IsInfinity constraint'.Height then 200.0
            else constraint'.Height
        Size(w, h)

    /// Auto-fit centerX/Y + scale so the bbox of `flat` fills the
    /// current canvas with a small margin. Called once when
    /// FlatPolygons is first assigned; user pan/zoom takes over
    /// after that until a new file is loaded.
    member private this.AutoFit () =
        let flat = this.FlatPolygons
        if flat.Length = 0 then ()
        else
            let (xmin, ymin, xmax, ymax) = LayerPainter.bboxOf flat
            let cw = max this.Bounds.Width 1.0
            let ch = max this.Bounds.Height 1.0
            let dxDbu = float (xmax - xmin) |> max 1.0
            let dyDbu = float (ymax - ymin) |> max 1.0
            let pxX = cw / dxDbu
            let pxY = ch / dyDbu
            pixelsPerDbu <- min pxX pxY * 0.95
            centerX <- float (xmin + xmax) * 0.5
            centerY <- float (ymin + ymax) * 0.5
            hasFitted <- true

    /// Build the ViewBox the painter draws into, derived from the
    /// current center+scale and canvas pixel size.
    member private this.MakeViewBox () : LayerPainter.ViewBox =
        let w = max (int this.Bounds.Width) 1
        let h = max (int this.Bounds.Height) 1
        let halfDxDbu = float w / 2.0 / max pixelsPerDbu 0.0001
        let halfDyDbu = float h / 2.0 / max pixelsPerDbu 0.0001
        { LayerPainter.ViewBox.MinX = int64 (centerX - halfDxDbu)
          MinY = int64 (centerY - halfDyDbu)
          MaxX = int64 (centerX + halfDxDbu)
          MaxY = int64 (centerY + halfDyDbu)
          PixelW = w
          PixelH = h }

    /// Convert a screen-pixel point on this control into world DBU.
    /// Y flips because Avalonia screen Y grows down but world Y
    /// grows up (matches the existing wheel-zoom math).
    member private this.ScreenToWorld (p: Avalonia.Point) : float * float =
        let cw = max this.Bounds.Width 1.0
        let ch = max this.Bounds.Height 1.0
        let scale = max pixelsPerDbu 0.0001
        let wx = centerX + (p.X - cw / 2.0) / scale
        let wy = centerY - (p.Y - ch / 2.0) / scale
        wx, wy

    override this.OnPropertyChanged(e) =
        base.OnPropertyChanged e
        if e.Property = GdsCanvasControl.MacroPathProperty then
            // Path changed → genuinely new file or rename to a
            // different file. Reset auto-fit so the camera frames
            // the new geometry. Cancel any in-flight drag too —
            // its Δ doesn't apply to the new macro.
            hasFitted <- false
            dragKind <- NoDrag
            dragLiveDeltaDbu <- 0L, 0L
            dragLiveLib <- None
            dragLiveFlat <- [||]
            this.InvalidateVisual()
        elif e.Property = GdsCanvasControl.FlatPolygonsProperty
             || e.Property = GdsCanvasControl.LibraryProperty
             || e.Property = GdsCanvasControl.ToggleProperty
             || e.Property = GdsCanvasControl.InstancesProperty
             || e.Property = GdsCanvasControl.InstanceSelectionProperty
             || e.Property = GdsCanvasControl.ShowDimensionsProperty
             || e.Property = GdsCanvasControl.ShowDrcProperty
             || e.Property = GdsCanvasControl.VisibleRatlinesProperty
             || e.Property = GdsCanvasControl.TightenModeProperty
             || e.Property = GdsCanvasControl.SelectedPolygonsProperty
             || e.Property = GdsCanvasControl.ShowGridProperty
             || e.Property = GdsCanvasControl.ShowRulerProperty
             || e.Property = GdsCanvasControl.SnapEnabledProperty then
            // Geometry / overlay state changed — re-render but
            // KEEP the existing pan/zoom so editing operations
            // (Tighten, drag, rotate, mirror) don't snap the
            // camera away from the user's working view.
            this.InvalidateVisual()

    // ---- Pointer-driven select / drag / pan + wheel zoom ----

    override this.OnPointerPressed e =
        base.OnPointerPressed e
        let props = e.GetCurrentPoint(this).Properties
        let p = e.GetPosition this
        lastPos <- p
        e.Pointer.Capture this
        this.Focus () |> ignore

        // Tighten mode: a left click on a numbered label commits
        // that candidate. Other clicks are swallowed so the user
        // doesn't accidentally pan, marquee, or change selection
        // while choosing a tighten direction.
        if this.TightenMode && props.IsLeftButtonPressed then
            let hits = !tightenHits
            let pxF = float32 p.X
            let pyF = float32 p.Y
            let pick =
                hits
                |> Array.tryFind (fun h ->
                    pxF >= h.Rect.Left && pxF <= h.Rect.Right
                    && pyF >= h.Rect.Top && pyF <= h.Rect.Bottom)
            match pick with
            | Some h ->
                let cb = this.CommitTightenHandler
                if not (isNull cb) then cb.Invoke h.Index
            | None -> ()
            // Swallow regardless — left-click in tighten mode
            // shouldn't initiate pan / marquee / selection.
            ()
        elif props.IsMiddleButtonPressed || props.IsRightButtonPressed then
            // Middle / right while a left-button drag is already in
            // flight → no dragKind change. PointerMoved checks the
            // live button state and routes to pan handling. Just
            // reset `lastPos` so the first Move-tick computes its
            // delta from this press point.
            let dragInFlight =
                match dragKind with
                | SelectionDrag | PolygonDrag -> true
                | ResizeDrag _ -> true
                | _ -> false
            if dragInFlight then
                lastPos <- p
            else
                dragKind <- PanDrag
        elif props.IsLeftButtonPressed
             && this.SelectedPolygons.Count = 1
             && (let handles = !resizeHandleHits
                 let pxF, pyF = float32 p.X, float32 p.Y
                 handles
                 |> Array.tryFind (fun h ->
                     pxF >= h.Rect.Left && pxF <= h.Rect.Right
                     && pyF >= h.Rect.Top && pyF <= h.Rect.Bottom)).IsSome then
            // Click on a resize handle for the single selected
            // polygon. Hit-test takes priority over instance /
            // polygon selection so handles sitting over geometry
            // still grab the drag.
            let handles = !resizeHandleHits
            let pxF, pyF = float32 p.X, float32 p.Y
            let hit =
                handles
                |> Array.find (fun h ->
                    pxF >= h.Rect.Left && pxF <= h.Rect.Right
                    && pyF >= h.Rect.Top && pyF <= h.Rect.Bottom)
            let (sname, idx) = this.SelectedPolygons.MinimumElement
            // Snapshot the resting bbox so the move handler can
            // compute the in-flight bbox from cursor + anchor.
            let startBbox =
                match this.Library with
                | Some lib ->
                    lib.Cells
                    |> List.tryFind (fun c -> c.Name = sname)
                    |> Option.bind (fun c ->
                        if idx < 0 || idx >= c.Elements.Length then None
                        else
                            match c.Elements.[idx] with
                            | PolyEl pp when not pp.Points.IsEmpty ->
                                let mutable xMin = System.Int64.MaxValue
                                let mutable yMin = System.Int64.MaxValue
                                let mutable xMax = System.Int64.MinValue
                                let mutable yMax = System.Int64.MinValue
                                for pt in pp.Points do
                                    if pt.X < xMin then xMin <- pt.X
                                    if pt.X > xMax then xMax <- pt.X
                                    if pt.Y < yMin then yMin <- pt.Y
                                    if pt.Y > yMax then yMax <- pt.Y
                                Some (xMin, yMin, xMax, yMax)
                            | RectEl r ->
                                let xMin, xMax =
                                    if r.X1 <= r.X2 then r.X1, r.X2 else r.X2, r.X1
                                let yMin, yMax =
                                    if r.Y1 <= r.Y2 then r.Y1, r.Y2 else r.Y2, r.Y1
                                Some (xMin, yMin, xMax, yMax)
                            | _ -> None)
                | None -> None
            match startBbox with
            | Some bb ->
                resizeStartBbox <- bb
                resizeLiveBbox <- bb
                dragKind <- ResizeDrag (hit.Handle, sname, idx)
            | None ->
                // Resize-able element vanished between render and
                // press — treat as no-op, fall through to nothing.
                ()
        elif props.IsLeftButtonPressed then
            // Left button: hit-test the selectable instances. If we
            // hit something, start (or extend) selection + prep a
            // selection-drag. If we hit empty space, clear the
            // selection and start a pan.
            let wx, wy = this.ScreenToWorld p
            let hit =
                Instances.hitTest this.Instances (int64 (System.Math.Round wx)) (int64 (System.Math.Round wy))
            let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
            if hit.Length > 0 then
                // Front-most under the cursor = the SMALLEST
                // bbox containing the click. When a small cell
                // (e.g. ReRAM stack) sits inside a larger cell's
                // bbox (e.g. nfet), the user wants to grab the
                // small one — declaration order picks the larger
                // outer cell instead and makes the inner cell
                // unselectable.
                let bboxArea (i: Instances.Instance) =
                    let (x1, y1, x2, y2) = i.BBox
                    (x2 - x1) * (y2 - y1)
                let target =
                    hit |> Array.minBy bboxArea
                let prior = this.InstanceSelection
                let next =
                    if shift then
                        if prior.Contains target.Index then
                            // Shift-click an already-selected instance
                            // toggles it OFF — symmetric with most
                            // multi-select UIs.
                            prior.Remove target.Index
                        else
                            prior.Add target.Index
                    elif prior.Contains target.Index then
                        // Click on an already-selected member without
                        // shift: keep the selection so a drag moves
                        // the whole group.
                        prior
                    else
                        Set.singleton target.Index
                if next <> prior then
                    let h = this.SetInstanceSelectionHandler
                    if not (isNull h) then h.Invoke next
                // No-shift click on a NEW item should also clear
                // the OTHER selection (polys), matching standard
                // CAD selection semantics. Shift-click EXTENDS,
                // never clears. Clicking on an already-selected
                // member also doesn't clear (the user intends to
                // grab the existing group).
                if not shift && not (prior.Contains target.Index)
                   && not this.SelectedPolygons.IsEmpty then
                    let h = this.ClearPolygonSelectionHandler
                    if not (isNull h) then h.Invoke ()
                dragStartWorldX <- wx
                dragStartWorldY <- wy
                dragLiveDeltaDbu <- 0L, 0L
                // Resting centroid of the selection's bbox union;
                // centroid-snap rebuilds the snapped delta against
                // this on every move so the cell center lands on
                // grid.
                let bboxes =
                    this.Instances
                    |> Array.filter (fun i -> next.Contains i.Index)
                    |> Array.map (fun i -> i.BBox)
                let cx, cy = this.CentroidOfBboxes bboxes
                dragStartCentroidX <- cx
                dragStartCentroidY <- cy
                dragKind <- if next.IsEmpty then PanDrag else SelectionDrag
            else
                // No instance hit → fall back to top-cell
                // polygon pick. Direct met / licon / etc. paint
                // in the top cell (not inside an SRef) is
                // selectable here — sets `Selection` so the
                // inspector shows the polygon's layer and net.
                let polyPick =
                    match this.Library with
                    | Some lib ->
                        let referenced =
                            System.Collections.Generic.HashSet<string>()
                        for c in lib.Cells do
                            for el in c.Elements do
                                match el with
                                | SRefEl sr -> referenced.Add sr.Cell |> ignore
                                | ARefEl ar -> referenced.Add ar.Cell |> ignore
                                | _ -> ()
                        let topOpt =
                            lib.Cells
                            |> List.tryFind (fun c -> not (referenced.Contains c.Name))
                            |> Option.orElseWith (fun () ->
                                lib.Cells |> List.tryHead)
                        topOpt
                        |> Option.bind (fun top ->
                            let pt : Point =
                                { X = int64 (System.Math.Round wx)
                                  Y = int64 (System.Math.Round wy) }
                            Layout.Picking.pickBoundary pt top.Elements
                            |> Option.map (fun (idx, _) -> top.Name, idx))
                    | None -> None
                match polyPick with
                | Some (sname, idx) ->
                    // Compute the new selection set with shift /
                    // already-selected semantics (same logic as
                    // instance click above), then dispatch via
                    // SetPolygonSelection. The drag operates on
                    // the resulting set.
                    let prior = this.SelectedPolygons
                    let target = (sname, idx)
                    let next =
                        if shift then
                            if prior.Contains target then prior.Remove target
                            else prior.Add target
                        elif prior.Contains target then prior
                        else Set.singleton target
                    if next <> prior then
                        let h = this.SetPolygonSelectionHandler
                        if not (isNull h) then h.Invoke next
                    // No-shift click on a NEW polygon clears the
                    // OTHER selection (instances). Mirrors the
                    // instance click path above.
                    if not shift && not (prior.Contains target)
                       && not this.InstanceSelection.IsEmpty then
                        let h = this.ClearInstanceSelectionHandler
                        if not (isNull h) then h.Invoke ()
                    dragStartWorldX <- wx
                    dragStartWorldY <- wy
                    dragLiveDeltaDbu <- 0L, 0L
                    // Capture the new selection's centroid for
                    // centroid-snap. We compute against `next`
                    // rather than the bound SelectedPolygons
                    // because the dispatch above hasn't propagated
                    // through the model yet.
                    let cx, cy =
                        match this.Library with
                        | Some lib ->
                            // Inline computation against `next` so
                            // the stale SelectedPolygons isn't
                            // consulted.
                            let bboxes = ResizeArray<int64 * int64 * int64 * int64>()
                            for c in lib.Cells do
                                c.Elements
                                |> List.iteri (fun i el ->
                                    if next.Contains (c.Name, i) then
                                        match el with
                                        | PolyEl pp when not pp.Points.IsEmpty ->
                                            let mutable xMin = System.Int64.MaxValue
                                            let mutable yMin = System.Int64.MaxValue
                                            let mutable xMax = System.Int64.MinValue
                                            let mutable yMax = System.Int64.MinValue
                                            for pt in pp.Points do
                                                if pt.X < xMin then xMin <- pt.X
                                                if pt.X > xMax then xMax <- pt.X
                                                if pt.Y < yMin then yMin <- pt.Y
                                                if pt.Y > yMax then yMax <- pt.Y
                                            bboxes.Add (xMin, yMin, xMax, yMax)
                                        | RectEl r ->
                                            let xMin, xMax =
                                                if r.X1 <= r.X2 then r.X1, r.X2 else r.X2, r.X1
                                            let yMin, yMax =
                                                if r.Y1 <= r.Y2 then r.Y1, r.Y2 else r.Y2, r.Y1
                                            bboxes.Add (xMin, yMin, xMax, yMax)
                                        | _ -> ())
                            this.CentroidOfBboxes (bboxes :> seq<_>)
                        | None -> 0L, 0L
                    dragStartCentroidX <- cx
                    dragStartCentroidY <- cy
                    dragKind <- if next.IsEmpty then PanDrag else PolygonDrag
                | None ->
                    // Empty space → start a marquee. Shift extends
                    // the existing selection; bare click replaces it
                    // (we DON'T clear yet — that happens at release
                    // if the marquee captures nothing). Pan stays on
                    // middle / right button.
                    marqueeAdditive <- shift
                    let mxi = int64 (System.Math.Round wx)
                    let myi = int64 (System.Math.Round wy)
                    marqueeWorldStart <- mxi, myi
                    marqueeWorldEnd   <- mxi, myi
                    dragKind <- MarqueeDrag

    /// Pick the snap step (DBU) for the current snap state. When
    /// SnapEnabled is on, returns the user grid (Config default or
    /// alt). When off, returns 0 so the caller skips snapping
    /// (effectively raw 1-DBU resolution). The legacy SKY130 5 nm
    /// mfg-grid path is gone — the user explicitly asked for
    /// "replaces sky snap".
    /// Edge-of-viewport auto-pan: when the cursor sits within
    /// `edgePx` of any canvas edge during a left-button drag,
    /// nudge the camera toward the edge so the user can drag past
    /// the visible region without lifting the button. Capped at a
    /// deliberately slow rate so the pan stays steerable.
    member private this.AutoPanIfNearEdge (p: Avalonia.Point) : unit =
        let bw = this.Bounds.Width
        let bh = this.Bounds.Height
        if bw <= 0.0 || bh <= 0.0 then () else
        let edgePx = 24.0
        // Pixels-per-tick at full saturation — kept low so the
        // pan stays steerable. With the cursor inside the canvas
        // edge, the linear ramp tops out at maxRatePx. With the
        // cursor PAST the edge the ramp saturates at 1.0 (was
        // letting it run away into negative-distance territory).
        let maxRatePx = 4.0
        let speedFactor (dist: double) : double =
            // Clamp at the edge — when the cursor goes PAST the
            // canvas (negative dist) we'd otherwise read >1.0 and
            // get a runaway pan rate.
            if dist <= 0.0 then 1.0
            elif dist >= edgePx then 0.0
            else (edgePx - dist) / edgePx
        let leftSpeed   = speedFactor p.X
        let rightSpeed  = speedFactor (bw - p.X)
        let topSpeed    = speedFactor p.Y
        let bottomSpeed = speedFactor (bh - p.Y)
        let dxPx =
            if leftSpeed > 0.0 then -leftSpeed * maxRatePx
            elif rightSpeed > 0.0 then rightSpeed * maxRatePx
            else 0.0
        let dyPx =
            if topSpeed > 0.0 then -topSpeed * maxRatePx
            elif bottomSpeed > 0.0 then bottomSpeed * maxRatePx
            else 0.0
        if dxPx <> 0.0 || dyPx <> 0.0 then
            let scale = max pixelsPerDbu 0.0001
            // Sign opposite the middle-button overlay: auto-pan
            // pushes the camera TOWARD the edge (so the world
            // point under the cursor moves toward the camera's new
            // center). The dragKind move handler downstream then
            // computes a larger `wx - dragStartWorldX` and the
            // selection drags toward the edge — exactly the wanted
            // effect.
            centerX <- centerX + dxPx / scale
            centerY <- centerY - dyPx / scale

    member private this.SnapStepDbu (lib: Document) (altHeld: bool) : int64 =
        if not this.SnapEnabled then 0L
        else
            let umPerDbu = float lib.Units.DbuNm * 1.0e-3
            let stepUm =
                if altHeld then Rekolektion.Viz.App.Services.Config.current.SnapAltUm
                else Rekolektion.Viz.App.Services.Config.current.SnapDefaultUm
            max 0L (int64 (stepUm / umPerDbu))

    /// Snap (dx, dy) DBU delta to the current grid step. No-op when
    /// SnapEnabled is off or when the raw delta is already zero
    /// (the latter is critical — without the zero guard, simply
    /// clicking a cell whose centroid sits off-grid would snap it
    /// to the nearest grid point even though the user never
    /// dragged).
    member private this.SnapDelta (lib: Document) (altHeld: bool) (dx: int64) (dy: int64)
            : int64 * int64 =
        if dx = 0L && dy = 0L then 0L, 0L
        else
            let step = this.SnapStepDbu lib altHeld
            if step <= 1L then dx, dy
            else
                let snapCoord (v: int64) =
                    let q = if v >= 0L then (v + step / 2L) / step else (v - step / 2L) / step
                    q * step
                snapCoord dx, snapCoord dy

    /// Snap an absolute world-DBU point to the current grid step.
    /// Used by resize where the cursor's coord IS the new bbox edge.
    member private this.SnapPoint (lib: Document) (altHeld: bool) (x: int64) (y: int64)
            : int64 * int64 =
        this.SnapDelta lib altHeld x y

    /// Centroid-relative delta snap. The selection's start
    /// centroid is `(cx0, cy0)`; the raw cursor delta is
    /// `(dx, dy)`. We project the new centroid `(cx0+dx, cy0+dy)`
    /// onto the grid, then back out the delta that gets us there.
    /// Result: every commit lands the selection's centroid on a
    /// grid intersection. No-op when SnapEnabled is off OR when
    /// the raw delta is zero — without the zero guard, selecting
    /// a cell whose centroid is off-grid would auto-snap on
    /// release even when the user never dragged.
    member private this.SnapDeltaCentroid
            (lib: Document) (altHeld: bool)
            (cx0: int64) (cy0: int64)
            (dx: int64) (dy: int64) : int64 * int64 =
        if dx = 0L && dy = 0L then 0L, 0L
        else
            let step = this.SnapStepDbu lib altHeld
            if step <= 1L then dx, dy
            else
                let snapCoord (v: int64) =
                    let q = if v >= 0L then (v + step / 2L) / step else (v - step / 2L) / step
                    q * step
                let snappedCx = snapCoord (cx0 + dx)
                let snappedCy = snapCoord (cy0 + dy)
                snappedCx - cx0, snappedCy - cy0

    /// Bbox-center centroid of a set of `(int64*int64*int64*int64)`
    /// bboxes. Returns (0, 0) for an empty seq.
    member private _.CentroidOfBboxes (boxes: (int64 * int64 * int64 * int64) seq) : int64 * int64 =
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        let mutable any = false
        for (a, b, c, d) in boxes do
            any <- true
            if a < xMin then xMin <- a
            if b < yMin then yMin <- b
            if c > xMax then xMax <- c
            if d > yMax then yMax <- d
        if any then (xMin + xMax) / 2L, (yMin + yMax) / 2L
        else 0L, 0L

    /// Centroid of selected polygons in the active library — used
    /// at PolygonDrag press time to seed the centroid-snap math.
    member private this.SelectedPolyCentroid (doc: Document) : int64 * int64 =
        let sel = this.SelectedPolygons
        if sel.IsEmpty then 0L, 0L
        else
            let bboxes = ResizeArray<int64 * int64 * int64 * int64>()
            for c in doc.Cells do
                c.Elements
                |> List.iteri (fun i el ->
                    if sel.Contains (c.Name, i) then
                        match el with
                        | PolyEl p when not p.Points.IsEmpty ->
                            let mutable xMin = System.Int64.MaxValue
                            let mutable yMin = System.Int64.MaxValue
                            let mutable xMax = System.Int64.MinValue
                            let mutable yMax = System.Int64.MinValue
                            for pt in p.Points do
                                if pt.X < xMin then xMin <- pt.X
                                if pt.X > xMax then xMax <- pt.X
                                if pt.Y < yMin then yMin <- pt.Y
                                if pt.Y > yMax then yMax <- pt.Y
                            bboxes.Add (xMin, yMin, xMax, yMax)
                        | RectEl r ->
                            let xMin, xMax =
                                if r.X1 <= r.X2 then r.X1, r.X2 else r.X2, r.X1
                            let yMin, yMax =
                                if r.Y1 <= r.Y2 then r.Y1, r.Y2 else r.Y2, r.Y1
                            bboxes.Add (xMin, yMin, xMax, yMax)
                        | _ -> ())
            this.CentroidOfBboxes (bboxes :> seq<_>)

    /// Live-translate every polygon in `sel` by (dx, dy) in DBU.
    /// Returns a new Document with those polygons shifted — used by
    /// the in-flight PolygonDrag preview so the moved shapes track
    /// the cursor before the model commit lands.
    member private _.LibWithPolygonsShifted
            (doc: Document) (sel: Set<string * int>)
            (dx: int64) (dy: int64) : Document =
        let perCell =
            sel
            |> Set.toList
            |> List.groupBy fst
            |> List.map (fun (s, items) -> s, items |> List.map snd |> Set.ofList)
            |> Map.ofList
        let translatePoly (pts: Point list) =
            pts |> List.map (fun p -> { X = p.X + dx; Y = p.Y + dy })
        let updated =
            doc.Cells
            |> List.map (fun c ->
                match Map.tryFind c.Name perCell with
                | None -> c
                | Some indices ->
                    let elems' =
                        c.Elements
                        |> List.mapi (fun i el ->
                            if not (indices.Contains i) then el
                            else
                                match el with
                                | PolyEl p ->
                                    PolyEl { p with Points = translatePoly p.Points }
                                | PathEl p ->
                                    PathEl { p with Points = translatePoly p.Points }
                                | RectEl r ->
                                    RectEl
                                        { r with
                                            X1 = r.X1 + dx; Y1 = r.Y1 + dy
                                            X2 = r.X2 + dx; Y2 = r.Y2 + dy }
                                | other -> other)
                    { c with Elements = elems' })
        { doc with Cells = updated }

    /// Per-dragKind move-event handler body, abstracted from the
    /// PointerEventArgs so both real PointerMoved events AND the
    /// auto-pan timer can drive it. `pos` is the current cursor
    /// position; `modifiers` is the current keyboard modifier
    /// state (Shift / Alt). The timer pulls these from `lastPos`
    /// + `lastModifiers` (cached on every real move).
    member private this.HandleDragMove
            (pos: Avalonia.Point)
            (modifiers: KeyModifiers)
            : unit =
        match dragKind with
        | NoDrag -> ()
        | MarqueeDrag ->
            let p = pos
            let wx, wy = this.ScreenToWorld p
            marqueeWorldEnd <-
                int64 (System.Math.Round wx),
                int64 (System.Math.Round wy)
            this.InvalidateVisual()
        | PanDrag ->
            let p = pos
            let dxPx = p.X - lastPos.X
            let dyPx = p.Y - lastPos.Y
            let scale = max pixelsPerDbu 0.0001
            centerX <- centerX - dxPx / scale
            centerY <- centerY + dyPx / scale
            lastPos <- p
            this.InvalidateVisual()
        | SelectionDrag ->
            let p = pos
            let wx, wy = this.ScreenToWorld p
            let dxRaw = int64 (System.Math.Round (wx - dragStartWorldX))
            let dyRaw = int64 (System.Math.Round (wy - dragStartWorldY))
            let shift = modifiers.HasFlag KeyModifiers.Shift
            let alt = modifiers.HasFlag KeyModifiers.Alt
            let dxRaw, dyRaw =
                if shift then
                    if abs dxRaw >= abs dyRaw then dxRaw, 0L
                    else 0L, dyRaw
                else dxRaw, dyRaw
            // User-grid snap when SnapEnabled is on (Config
            // default; Alt picks the finer step). Off → raw delta.
            let dxSnap, dySnap =
                match this.Library with
                | Some lib -> this.SnapDeltaCentroid lib alt dragStartCentroidX dragStartCentroidY dxRaw dyRaw
                | None -> dxRaw, dyRaw
            if (dxSnap, dySnap) <> dragLiveDeltaDbu then
                dragLiveDeltaDbu <- dxSnap, dySnap
                // Re-flatten on every visible Δ change so the moved
                // geometry tracks the cursor. For small files (the
                // P0 test case is two SRefs) this is microseconds;
                // for production-scale macros we'd swap to an
                // incremental "translate just the selected SRef
                // subtree's polygons" path, but P0 doesn't need it.
                match this.Library with
                | Some lib ->
                    // Translate SRefs (with anchored labels) AND
                    // any selected polys (with their anchored
                    // labels) in one composed pass. Same code path
                    // the Update commit uses, so post-release
                    // matches mid-drag.
                    let lib' =
                        Instances.translateSelectionsWithLabels
                            lib this.InstanceSelection this.SelectedPolygons
                            dxSnap dySnap
                    dragLiveLib <- Some lib'
                    dragLiveFlat <- Layout.Flatten.flatten lib'
                | None ->
                    dragLiveLib <- None
                    dragLiveFlat <- [||]
                this.InvalidateVisual()
            lastPos <- p
        | PolygonDrag ->
            let p = pos
            let wx, wy = this.ScreenToWorld p
            let dxRaw = int64 (System.Math.Round (wx - dragStartWorldX))
            let dyRaw = int64 (System.Math.Round (wy - dragStartWorldY))
            let shift = modifiers.HasFlag KeyModifiers.Shift
            let alt = modifiers.HasFlag KeyModifiers.Alt
            let dxRaw, dyRaw =
                if shift then
                    if abs dxRaw >= abs dyRaw then dxRaw, 0L
                    else 0L, dyRaw
                else dxRaw, dyRaw
            let dxSnap, dySnap =
                match this.Library with
                | Some lib -> this.SnapDeltaCentroid lib alt dragStartCentroidX dragStartCentroidY dxRaw dyRaw
                | None -> dxRaw, dyRaw
            if (dxSnap, dySnap) <> dragLiveDeltaDbu then
                dragLiveDeltaDbu <- dxSnap, dySnap
                match this.Library with
                | Some lib ->
                    let polySel = this.SelectedPolygons
                    let instSel = this.InstanceSelection
                    if instSel.IsEmpty then
                        // Fast path: only polys are selected. Skip
                        // the library rebuild and the hierarchical
                        // re-flatten — no SRef transforms to
                        // recompose. O(N_polys) per move tick.
                        let flat0 = this.FlatPolygons
                        let flat' =
                            flat0
                            |> Array.map (fun fp ->
                                if polySel.Contains (fp.SourceStructure, fp.SourceIndex) then
                                    { fp with
                                        Points =
                                            fp.Points
                                            |> Array.map (fun p ->
                                                { X = p.X + dxSnap
                                                  Y = p.Y + dySnap }) }
                                else fp)
                        dragLiveLib <- Some lib
                        dragLiveFlat <- flat'
                    else
                        // Mixed selection: instances are also moving.
                        // Re-flatten via the unified helper so SRefs
                        // and polys (each with anchored labels)
                        // shift together.
                        let lib' =
                            Instances.translateSelectionsWithLabels
                                lib instSel polySel dxSnap dySnap
                        dragLiveLib <- Some lib'
                        dragLiveFlat <- Layout.Flatten.flatten lib'
                | None ->
                    dragLiveLib <- None
                    dragLiveFlat <- [||]
                this.InvalidateVisual()
            lastPos <- p
        | ResizeDrag (handle, sname, idx) ->
            let p = pos
            let wx, wy = this.ScreenToWorld p
            let (sxMin0, syMin0, sxMax0, syMax0) = resizeStartBbox
            // Snap the cursor's world coord to the user grid (Alt
            // = finer step). When SnapEnabled is off the cursor
            // lands at raw DBU.
            let alt = modifiers.HasFlag KeyModifiers.Alt
            let (cx, cy) =
                let rx = int64 (System.Math.Round wx)
                let ry = int64 (System.Math.Round wy)
                match this.Library with
                | Some lib -> this.SnapPoint lib alt rx ry
                | None -> rx, ry
            // Compute the new bbox per handle: corner handles
            // anchor at the opposite corner; edge handles anchor
            // at the opposite edge (the unaffected axis keeps the
            // original extents).
            // Per-handle bbox mutation. World Y grows upward — "N"
            // = high Y (yMax), "S" = low Y (yMin). Each handle
            // changes one or two of the four bbox edges; the
            // others stay at their start values (= anchor).
            let newBboxUnclamped =
                match handle with
                | HNW -> (cx,     syMin0, sxMax0, cy)        // NW corner: xMin + yMax
                | HN  -> (sxMin0, syMin0, sxMax0, cy)        // top edge: yMax
                | HNE -> (sxMin0, syMin0, cx,     cy)        // NE corner: xMax + yMax
                | HW  -> (cx,     syMin0, sxMax0, syMax0)    // left edge: xMin
                | HE  -> (sxMin0, syMin0, cx,     syMax0)    // right edge: xMax
                | HSW -> (cx,     cy,     sxMax0, syMax0)    // SW corner: xMin + yMin
                | HS  -> (sxMin0, cy,     sxMax0, syMax0)    // bottom edge: yMin
                | HSE -> (sxMin0, cy,     cx,     syMax0)    // SE corner: xMax + yMin
            // Normalize so xMin <= xMax, yMin <= yMax (allow user
            // to drag past the opposite edge — flipping a bbox is
            // valid; we just present its sorted form).
            let nxMin, nxMax =
                let a, b =
                    let (x0, _, x1, _) = newBboxUnclamped
                    x0, x1
                min a b, max a b
            let nyMin, nyMax =
                let a, b =
                    let (_, y0, _, y1) = newBboxUnclamped
                    y0, y1
                min a b, max a b
            // Aspect-ratio lock: Shift + corner handle. Pick the
            // axis with the smaller proportional change, scale the
            // other to match the original aspect.
            let shift = modifiers.HasFlag KeyModifiers.Shift
            let isCorner =
                match handle with HNW | HNE | HSW | HSE -> true | _ -> false
            let (finalXMin, finalYMin, finalXMax, finalYMax) =
                if shift && isCorner then
                    let oldW = sxMax0 - sxMin0
                    let oldH = syMax0 - syMin0
                    if oldW <= 0L || oldH <= 0L then nxMin, nyMin, nxMax, nyMax
                    else
                        let newW = nxMax - nxMin
                        let newH = nyMax - nyMin
                        // Compare W/H to oldW/oldH; clamp the
                        // larger so newW * oldH = newH * oldW.
                        if int64 newW * int64 oldH > int64 newH * int64 oldW then
                            // Width is wider than aspect-preserved
                            // value; trim width toward the anchor.
                            let targetW = newH * oldW / oldH
                            match handle with
                            | HNW | HSW -> nxMax - targetW, nyMin, nxMax, nyMax
                            | HNE | HSE -> nxMin, nyMin, nxMin + targetW, nyMax
                            | _ -> nxMin, nyMin, nxMax, nyMax
                        else
                            // Trim height toward the anchor edge.
                            // N-handles (HNW/HNE) anchor at yMin
                            // (south); height grows up from yMin.
                            // S-handles anchor at yMax; height
                            // grows down from yMax.
                            let targetH = newW * oldH / oldW
                            match handle with
                            | HNW | HNE -> nxMin, nyMin, nxMax, nyMin + targetH
                            | HSW | HSE -> nxMin, nyMax - targetH, nxMax, nyMax
                            | _ -> nxMin, nyMin, nxMax, nyMax
                else
                    nxMin, nyMin, nxMax, nyMax
            let newBbox = (finalXMin, finalYMin, finalXMax, finalYMax)
            if newBbox <> resizeLiveBbox then
                resizeLiveBbox <- newBbox
                // Build the live geometry by scaling the original
                // element's points / coords from start-bbox to
                // new-bbox. The renderer reads dragLiveLib /
                // dragLiveFlat.
                match this.Library with
                | Some lib ->
                    let oldW = max 1L (sxMax0 - sxMin0)
                    let oldH = max 1L (syMax0 - syMin0)
                    let newW = finalXMax - finalXMin
                    let newH = finalYMax - finalYMin
                    let lerpX (x: int64) =
                        finalXMin + (x - sxMin0) * newW / oldW
                    let lerpY (y: int64) =
                        finalYMin + (y - syMin0) * newH / oldH
                    let updatedCells =
                        lib.Cells
                        |> List.map (fun c ->
                            if c.Name <> sname then c
                            else
                                let elems' =
                                    c.Elements
                                    |> List.mapi (fun i el ->
                                        if i <> idx then el
                                        else
                                            match el with
                                            | PolyEl pp ->
                                                let pts =
                                                    pp.Points
                                                    |> List.map (fun pt ->
                                                        { X = lerpX pt.X; Y = lerpY pt.Y })
                                                PolyEl { pp with Points = pts }
                                            | RectEl r ->
                                                RectEl
                                                    { r with
                                                        X1 = finalXMin; Y1 = finalYMin
                                                        X2 = finalXMax; Y2 = finalYMax }
                                            | other -> other)
                                { c with Elements = elems' })
                    let lib' = { lib with Cells = updatedCells }
                    dragLiveLib <- Some lib'
                    dragLiveFlat <- Layout.Flatten.flatten lib'
                | None ->
                    dragLiveLib <- None
                    dragLiveFlat <- [||]
                this.InvalidateVisual()
            lastPos <- p

    /// True when `p` is inside the auto-pan edge band along ANY of
    /// the four canvas edges. Used by both the move handler (start
    /// timer) and the timer tick (keep panning).
    member private this.CursorInEdgeBand (p: Avalonia.Point) : bool =
        let edgePx = 24.0
        let bw = this.Bounds.Width
        let bh = this.Bounds.Height
        bw > 0.0 && bh > 0.0
        && (p.X <= edgePx || (bw - p.X) <= edgePx
            || p.Y <= edgePx || (bh - p.Y) <= edgePx)

    /// Auto-pan timer tick. Fires while the cursor is in the edge
    /// band during a drag — including when the user is holding the
    /// cursor still. Pans the camera + advances the dragKind move
    /// handler against `lastPos` so the dragged geometry follows
    /// the camera. Stops itself when the band-or-drag condition no
    /// longer holds.
    member private this.OnAutoPanTick () : unit =
        let dragInFlight =
            match dragKind with
            | SelectionDrag | PolygonDrag | MarqueeDrag -> true
            | ResizeDrag _ -> true
            | _ -> false
        if not dragInFlight then
            autoPanTimer.Stop()
        elif not (this.CursorInEdgeBand lastPos) then
            autoPanTimer.Stop()
        else
            this.AutoPanIfNearEdge lastPos
            this.HandleDragMove lastPos lastModifiers

    override this.OnPointerMoved e =
        base.OnPointerMoved e
        let props = e.GetCurrentPoint(this).Properties
        let p = e.GetPosition this
        // Capture the prior cursor position BEFORE any handler
        // updates `lastPos`. The middle-pan branch needs this to
        // compute its screen delta (we rebind `lastPos = p` only
        // after the pan math).
        let prevPos = lastPos
        lastModifiers <- e.KeyModifiers
        let middleOrRightHeld =
            props.IsMiddleButtonPressed || props.IsRightButtonPressed
        let dragInFlight =
            match dragKind with
            | SelectionDrag | PolygonDrag | MarqueeDrag -> true
            | ResizeDrag _ -> true
            | _ -> false
        if middleOrRightHeld && dragInFlight then
            // Manual pan-overlay: middle/right held during a left-
            // button drag. Pan camera, skip dragKind handler.
            // Auto-pan timer (if running) yields — manual pan
            // takes precedence.
            autoPanTimer.Stop()
            let dxPx = p.X - prevPos.X
            let dyPx = p.Y - prevPos.Y
            let scale = max pixelsPerDbu 0.0001
            centerX <- centerX - dxPx / scale
            centerY <- centerY + dyPx / scale
            lastPos <- p
            this.InvalidateVisual()
        else
            // The auto-pan timer is the SOLE source of edge-band
            // pan. Doing AutoPanIfNearEdge per move event AND on
            // the timer would double the rate when the mouse is
            // moving. The dragKind handler runs every move so the
            // dragged geometry tracks the cursor under user input;
            // when the cursor enters the band, we hand pan over to
            // the timer and fire one tick immediately so there's
            // no perceptible pause.
            this.HandleDragMove p e.KeyModifiers
            // Some HandleDragMove branches don't update lastPos
            // (NoDrag, MarqueeDrag). Ensure the timer sees the
            // current cursor position regardless.
            lastPos <- p
            if dragInFlight && this.CursorInEdgeBand p then
                if not autoPanTimer.IsEnabled then
                    autoPanTimer.Start()
                    // Prime the first pan immediately so the user
                    // doesn't see a 33 ms dead zone on entry.
                    this.OnAutoPanTick ()
            elif autoPanTimer.IsEnabled then
                autoPanTimer.Stop()

    override this.OnPointerReleased e =
        base.OnPointerReleased e
        // Middle / right released while left is still held → the
        // user finished the pan-overlay; leave the drag armed.
        // Reset `lastPos` so the next move-tick doesn't compute a
        // stale delta and pan again.
        let props = e.GetCurrentPoint(this).Properties
        let dragInFlight =
            match dragKind with
            | SelectionDrag | PolygonDrag -> true
            | ResizeDrag _ -> true
            | _ -> false
        if dragInFlight && props.IsLeftButtonPressed then
            lastPos <- e.GetPosition this
        else
        // Drag itself is ending (left released, OR a pure pan
        // dragKind ending). Reset state + commit if we had a
        // left-button drag in flight. Stop the auto-pan timer too —
        // no drag → nothing to advance.
        autoPanTimer.Stop()
        let kind = dragKind
        let dx, dy = dragLiveDeltaDbu
        // Capture resize state before resetting; the commit branch
        // below reads them via the locals so the reset can happen
        // unconditionally.
        let startBb = resizeStartBbox
        let liveBb  = resizeLiveBbox
        let zero = 0L, 0L, 0L, 0L
        dragKind <- NoDrag
        dragLiveDeltaDbu <- 0L, 0L
        dragLiveLib <- None
        dragLiveFlat <- [||]
        resizeStartBbox <- zero
        resizeLiveBbox <- zero
        e.Pointer.Capture null
        match kind with
        | SelectionDrag when dx <> 0L || dy <> 0L ->
            // Commit the snapped Δ through the model. The Update
            // handler mutates the active macro's Library + recomputes
            // FlatPolygons / TopInstances; the new bboxes flow back
            // here through the styled properties and replace our
            // speculative re-flatten on the next Render.
            let h = this.MoveSelectionHandler
            if not (isNull h) then h.Invoke(dx, dy)
            this.InvalidateVisual()
        | PolygonDrag when (dx <> 0L || dy <> 0L) ->
            let h = this.MovePolygonsHandler
            let sel = this.SelectedPolygons
            if not (isNull h) && not sel.IsEmpty then
                h.Invoke(sel, dx, dy)
            this.InvalidateVisual()
        | ResizeDrag (_, sname, idx) when liveBb <> startBb ->
            let (rxMin, ryMin, rxMax, ryMax) = liveBb
            // Refuse a degenerate result — if the user dragged
            // through the opposite edge and the new bbox collapsed,
            // the resize is a no-op (we don't want to wipe the
            // poly off the layout).
            if rxMax > rxMin && ryMax > ryMin then
                let h = this.ResizePolygonHandler
                if not (isNull h) then
                    h.Invoke(sname, idx, rxMin, ryMin, rxMax, ryMax)
            this.InvalidateVisual()
        | MarqueeDrag ->
            let (x1, y1) = marqueeWorldStart
            let (x2, y2) = marqueeWorldEnd
            let mxMin, myMin = min x1 x2, min y1 y2
            let mxMax, myMax = max x1 x2, max y1 y2
            // Sub-pixel marquee = effectively a click on empty
            // space. Treat as "clear selection" to match the
            // pre-marquee behaviour.
            let degenerate =
                (mxMax - mxMin) < 1L && (myMax - myMin) < 1L
            if degenerate then
                if not marqueeAdditive then
                    if not this.InstanceSelection.IsEmpty then
                        let h = this.ClearInstanceSelectionHandler
                        if not (isNull h) then h.Invoke ()
                    if not this.SelectedPolygons.IsEmpty then
                        let h = this.ClearPolygonSelectionHandler
                        if not (isNull h) then h.Invoke ()
            else
                // CAD convention: drag left→right = enclose-only
                // (bbox must lie fully inside marquee); drag
                // right→left = touch-select (any intersection).
                let mode = Marquee.modeOfDirection x1 x2
                let marqueeRect = (mxMin, myMin, mxMax, myMax)
                let bboxFits = Marquee.bboxFits mode marqueeRect
                let hits =
                    this.Instances
                    |> Array.filter (fun i -> bboxFits i.BBox)
                    |> Array.map (fun i -> i.Index)
                    |> Set.ofArray
                let next =
                    if marqueeAdditive then
                        Set.union this.InstanceSelection hits
                    else
                        hits
                let h = this.SetInstanceSelectionHandler
                if not (isNull h) then h.Invoke next

                // Also pick top-cell polygons (Boundary / Path)
                // whose own bbox passes the same enclose/touch test.
                // The top cell is the one not referenced by any
                // SRef/ARef in the library.
                match this.Library with
                | Some lib ->
                    let referenced =
                        System.Collections.Generic.HashSet<string>()
                    for c in lib.Cells do
                        for el in c.Elements do
                            match el with
                            | SRefEl sr -> referenced.Add sr.Cell |> ignore
                            | ARefEl ar -> referenced.Add ar.Cell |> ignore
                            | _ -> ()
                    let topOpt =
                        lib.Cells
                        |> List.tryFind (fun c -> not (referenced.Contains c.Name))
                        |> Option.orElseWith (fun () ->
                            lib.Cells |> List.tryHead)
                    match topOpt with
                    | None -> ()
                    | Some top ->
                        let polyBbox (pts: Point list) =
                            let mutable minX = System.Int64.MaxValue
                            let mutable minY = System.Int64.MaxValue
                            let mutable maxX = System.Int64.MinValue
                            let mutable maxY = System.Int64.MinValue
                            for p in pts do
                                if p.X < minX then minX <- p.X
                                if p.X > maxX then maxX <- p.X
                                if p.Y < minY then minY <- p.Y
                                if p.Y > maxY then maxY <- p.Y
                            if minX > maxX then None
                            else Some (minX, minY, maxX, maxY)
                        let polyHits =
                            top.Elements
                            |> List.mapi (fun i el -> i, el)
                            |> List.choose (fun (i, el) ->
                                match el with
                                | PolyEl p ->
                                    polyBbox p.Points
                                    |> Option.bind (fun bb ->
                                        if bboxFits bb then Some (top.Name, i) else None)
                                | PathEl p ->
                                    polyBbox p.Points
                                    |> Option.bind (fun bb ->
                                        if bboxFits bb then Some (top.Name, i) else None)
                                | RectEl r ->
                                    let bb =
                                        (min r.X1 r.X2, min r.Y1 r.Y2,
                                         max r.X1 r.X2, max r.Y1 r.Y2)
                                    if bboxFits bb then Some (top.Name, i) else None
                                | _ -> None)
                            |> Set.ofList
                        let nextPoly =
                            if marqueeAdditive then
                                Set.union this.SelectedPolygons polyHits
                            else
                                polyHits
                        if nextPoly <> this.SelectedPolygons then
                            let h = this.SetPolygonSelectionHandler
                            if not (isNull h) then h.Invoke nextPoly
                | None -> ()
            // Reset the marquee state so the overlay clears.
            marqueeWorldStart <- 0L, 0L
            marqueeWorldEnd <- 0L, 0L
            marqueeAdditive <- false
            this.InvalidateVisual()
        | _ ->
            this.InvalidateVisual()

    override this.OnKeyDown e =
        base.OnKeyDown e
        match e.Key with
        | Key.Escape ->
            if not this.InstanceSelection.IsEmpty then
                let h = this.ClearInstanceSelectionHandler
                if not (isNull h) then
                    h.Invoke ()
                    e.Handled <- true
            if not this.SelectedPolygons.IsEmpty then
                let h = this.ClearPolygonSelectionHandler
                if not (isNull h) then
                    h.Invoke ()
                    e.Handled <- true
        | _ -> ()

    override this.OnPointerWheelChanged e =
        base.OnPointerWheelChanged e
        // Zoom about the pointer position so the world point under
        // the cursor stays put.
        let factor = if e.Delta.Y > 0.0 then 1.15 else 1.0 / 1.15
        let p = e.GetPosition this
        let cw = max this.Bounds.Width 1.0
        let ch = max this.Bounds.Height 1.0
        let scale = max pixelsPerDbu 0.0001
        let wx = centerX + (p.X - cw / 2.0) / scale
        let wy = centerY - (p.Y - ch / 2.0) / scale
        pixelsPerDbu <- pixelsPerDbu * factor
        let newScale = max pixelsPerDbu 0.0001
        centerX <- wx - (p.X - cw / 2.0) / newScale
        centerY <- wy + (p.Y - ch / 2.0) / newScale
        this.InvalidateVisual()

    override this.Render(context) =
        base.Render context
        let bounds = Rect(0.0, 0.0, this.Bounds.Width, this.Bounds.Height)
        // A transparent fill is required for Avalonia's hit-test to
        // treat this control's bounds as clickable. context.Custom
        // draws via Skia on a separate path that the hit-test layer
        // doesn't see, so without this fill PointerPressed / wheel
        // events fall through and pan + zoom appear broken even
        // though all the math is in place.
        context.FillRectangle(Avalonia.Media.Brushes.Transparent, bounds)
        match this.Library with
        | Some lib ->
            if not hasFitted then this.AutoFit ()
            let vb = this.MakeViewBox ()
            let dragging =
                match dragKind with
                | SelectionDrag | PolygonDrag -> true
                | ResizeDrag _ -> true
                | _ -> false
            // While a drag is in flight, render the speculatively
            // translated Library + FlatPolygons so the moved
            // geometry tracks the cursor. The bound props haven't
            // changed yet — we only commit on release.
            let renderLib, renderFlat =
                match dragLiveLib with
                | Some live when dragging -> live, dragLiveFlat
                | _ -> lib, this.FlatPolygons
            // Per-instance per-layer bboxes for the dimension
            // overlay. Only computed when the overlay is on AND
            // there's a selection — keeps the at-rest render path
            // free of layer-walk cost. Recomputed every frame so
            // the arrows track the speculative library during a
            // drag.
            let instPolyBboxes =
                if this.ShowDimensions && not this.InstanceSelection.IsEmpty then
                    Instances.layerPolyBboxesByInstance renderLib
                else
                    Map.empty
            let violations =
                if this.ShowDrc then
                    // Inter-instance only: DRCs entirely inside
                    // one SRef are not editable from here (the
                    // user can't reshape an SRef's polygons), so
                    // we drop them. checkInterInstance also uses
                    // the same orthogonal-only filter as the
                    // dimension overlay so the canvas isn't a
                    // hairball of diagonal violations.
                    let perInstance =
                        this.Instances
                        |> Array.map (fun inst ->
                            inst.Index,
                            Layout.Flatten.flattenInstance (renderLib) inst.Index)
                        |> Map.ofArray
                    Drc.Check.checkInterInstance
                        renderLib.Units perInstance
                else
                    [||]
            let marquee =
                if dragKind = MarqueeDrag then
                    let (x1, y1) = marqueeWorldStart
                    let (x2, y2) = marqueeWorldEnd
                    Some (min x1 x2, min y1 y2, max x1 x2, max y1 y2)
                else None
            // Ratlines: skip the (potentially expensive) per-net
            // label walk unless at least one net's ratline is on.
            // The visible-ratline set is fully decoupled from the
            // polygon highlight set — turning on a highlight no
            // longer auto-shows ratlines.
            let visibleRatlines = this.VisibleRatlines
            let routes =
                if not visibleRatlines.IsEmpty then
                    Net.Ratlines.compute renderLib renderFlat
                else [||]
            // Tighten-mode candidates: per-cardinal binding pair
            // for the current selection vs. every other top
            // instance. Empty when not in mode.
            let tightenCands =
                if this.TightenMode && not this.InstanceSelection.IsEmpty then
                    let selectedPolys =
                        this.Instances
                        |> Array.filter (fun i -> this.InstanceSelection.Contains i.Index)
                        |> Array.collect (fun i ->
                            Layout.Flatten.flattenInstance (renderLib) i.Index)
                    // Other-instance polys + top-cell direct paint
                    // (rectangles / polygons authored at the top
                    // level, not inside an SRef — power straps and
                    // hand-routed wires). Without the top-cell pass
                    // the user could only Tighten a cell against
                    // other cells, never against a parent-painted
                    // strap, which is the common case for
                    // hand-laid analog blocks.
                    let otherInstancePolys =
                        this.Instances
                        |> Array.filter (fun i -> not (this.InstanceSelection.Contains i.Index))
                        |> Array.collect (fun i ->
                            Layout.Flatten.flattenInstance (renderLib) i.Index)
                    let topCellDirectPolys =
                        Layout.Flatten.flattenTopCellDirect renderLib
                    let otherPolys =
                        Array.append otherInstancePolys topCellDirectPolys
                    Drc.Check.tightenCandidates
                        renderLib.Units
                        selectedPolys otherPolys
                else
                    [||]
            // Cell-bbox outlines track the live render library, not the
            // resting model. During a SelectionDrag the speculative
            // `renderLib` has the moved SRefs; re-enumerating against
            // it keeps the dotted cell outlines glued to the geometry
            // instead of lagging at the pre-drag positions.
            let overlayInstances =
                if dragging && dragKind = SelectionDrag then
                    Instances.enumerate renderLib
                else
                    this.Instances
            // Resize handles render only for a single-poly
            // selection. Bbox is either the live in-flight bbox
            // (during ResizeDrag) or the resting bbox computed
            // from the selected element's points. Other drag
            // kinds suppress the handles because their geometry is
            // mid-translate and the handles would lag.
            let resizeBbox =
                let canResize =
                    not this.TightenMode
                    && this.SelectedPolygons.Count = 1
                    && (not dragging || (match dragKind with ResizeDrag _ -> true | _ -> false))
                if not canResize then None
                else
                    match dragKind with
                    | ResizeDrag _ -> Some resizeLiveBbox
                    | _ ->
                        let (sname, idx) = this.SelectedPolygons.MinimumElement
                        renderLib.Cells
                        |> List.tryFind (fun c -> c.Name = sname)
                        |> Option.bind (fun c ->
                            if idx < 0 || idx >= c.Elements.Length then None
                            else
                                match c.Elements.[idx] with
                                | PolyEl p when not p.Points.IsEmpty ->
                                    let mutable xMin = System.Int64.MaxValue
                                    let mutable yMin = System.Int64.MaxValue
                                    let mutable xMax = System.Int64.MinValue
                                    let mutable yMax = System.Int64.MinValue
                                    for pt in p.Points do
                                        if pt.X < xMin then xMin <- pt.X
                                        if pt.X > xMax then xMax <- pt.X
                                        if pt.Y < yMin then yMin <- pt.Y
                                        if pt.Y > yMax then yMax <- pt.Y
                                    if xMax > xMin && yMax > yMin then
                                        Some (xMin, yMin, xMax, yMax)
                                    else None
                                | RectEl r ->
                                    let xMin, xMax =
                                        if r.X1 <= r.X2 then r.X1, r.X2 else r.X2, r.X1
                                    let yMin, yMax =
                                        if r.Y1 <= r.Y2 then r.Y1, r.Y2 else r.Y2, r.Y1
                                    if xMax > xMin && yMax > yMin then
                                        Some (xMin, yMin, xMax, yMax)
                                    else None
                                | _ -> None)
            let overlay : SelectionOverlay =
                { Instances = overlayInstances
                  Selected  = this.InstanceSelection
                  Dragging  = dragging
                  ShowDimensions = this.ShowDimensions
                  InstancePolyBboxes = instPolyBboxes
                  Violations = violations
                  MarqueeWorld = marquee
                  Routes = routes
                  VisibleRatlines = visibleRatlines
                  TightenCandidates = tightenCands
                  SelectedPolygons = this.SelectedPolygons
                  ResizeBbox = resizeBbox
                  ShowGrid = this.ShowGrid
                  ShowRuler = this.ShowRuler }
            context.Custom(new SkiaDraw(bounds, renderLib, renderFlat, vb, this.Toggle, overlay, tightenHits, resizeHandleHits))
        | None ->
            // Closing the active tab leaves None for Library; without
            // an explicit fill the prior frame's polygons stay
            // painted on the shared SkSurface ('canvas closed but
            // view still shows the cell' bug).
            context.FillRectangle(Avalonia.Media.Brushes.Black, bounds)
