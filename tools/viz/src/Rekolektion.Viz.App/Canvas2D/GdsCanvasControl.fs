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
    /// Net names whose ratline overlay is currently selected — the
    /// renderer paints them in a brighter / thicker style.
    SelectedRatlines : Set<string>
    /// Tighten mode candidates. Empty when mode is off. The
    /// renderer uses these to draw numbered candidate dim
    /// arrows + click targets; it returns the per-label hit
    /// rects so OnPointerPressed can map a click to an index.
    TightenCandidates : Drc.Check.TightenCandidate array
    /// Picked top-cell polygon (struct name, element index).
    /// Drawn outlined in cyan so the user sees what they
    /// selected. None when nothing is picked.
    SelectedPolygons : Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>
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
    /// Layout label text rendering. When false, the LabelPainter
    /// pass is skipped — net names and port markers stay in the
    /// model but don't render. Default true.
    ShowLabels : bool
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
                      resizeHitsOut: ResizeHandleHit array ref,
                      draftRoute: Routing.Draft.DraftRoute option,
                      routeLiveViolations: Drc.Check.Violation array,
                      drcProvenance: Map<string, string>,
                      hoveredSnapTarget: Routing.Snap.SnapTarget option,
                      segmentDrag: Routing.SegmentDrag.DragState option,
                      // Doc reference, so the segment-drag overlay can
                      // project the new geometry without reaching into
                      // the canvas. None when no library is loaded.
                      segmentDragDoc: Document option,
                      debugOverlay: bool,
                      netMap: Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>,
                      flatPolygonsForDebug: FlatPolygon array) =
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
                use bg = new SKPaint(Style = SKPaintStyle.Fill, Color = SKColor(0x0Cuy, 0x10uy, 0x18uy, 0xFFuy))
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
                    // Dot screen-pixel spacing — drop a pass when dots
                    // would crowd closer than a few diameters apart.
                    // Below ~10 px minor / ~6 px major the lattice
                    // reads as fog instead of a grid; the 3 px we
                    // used to pick was barely above the dot radius
                    // and produced exactly that clutter.
                    let minorPx = float minorDbu * gsX
                    let drawMinor = minorPx >= 14.0
                    let majorPx = float majorDbu * gsX
                    let drawMajor = majorPx >= 9.0
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
                if overlay.ShowLabels then
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
                    for pk in overlay.SelectedPolygons do
                        let sname = pk.Cell
                        let idx = pk.Index
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
                    DrcOverlay.render canvas vb
                        (float lib.Units.DbuNm * 1.0e-3)
                        drcProvenance overlay.Violations

                if overlay.Routes.Length > 0 then
                    RatlineOverlay.render canvas vb
                        overlay.Routes overlay.VisibleRatlines
                        overlay.SelectedRatlines

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
                        // Ruler labels scale with world zoom — height
                        // fixed in µm, converted to pixels via the
                        // current screen-per-µm ratio so the labels
                        // shrink with the geometry on zoom-out and
                        // grow on zoom-in. Floor at 6 px so the
                        // labels stay legible at extreme zoom-out
                        // instead of vanishing entirely.
                        let pxPerUm = gsX / umPerDbu
                        let labelHeightUm = 0.35
                        let labelPx =
                            max 6.0 (labelHeightUm * pxPerUm) |> float32
                        use labelPaint =
                            new SKPaint(
                                Style = SKPaintStyle.Fill,
                                Color = SKColors.White,
                                IsAntialias = true,
                                TextSize = labelPx)
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
                                // Offset scales with label height so the
                                // baseline drops below the tick by ~one
                                // glyph height regardless of zoom.
                                canvas.DrawText(label, sx + 2.0f, origSy + tickLen + labelPx + 1.0f, labelPaint)
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
                                // X offset scales with label width
                                // estimate (≈ char-width × digit-count);
                                // Y nudge scales with height so the
                                // text sits centered on the tick.
                                let approxCharW = labelPx * 0.55f
                                let approxW = approxCharW * float32 label.Length
                                canvas.DrawText(label, origSx - tickLen - approxW - 2.0f, sy + labelPx * 0.4f, labelPaint)

                // ADR-0002 draft route overlay — paint LAST so the
                // in-flight wire reads on top of every other layer.
                // Fixed segments solid amber; tentative segments same
                // hue at reduced alpha so the user sees what is and
                // isn't committed yet.
                match draftRoute with
                | None -> ()
                | Some r ->
                    let dxWorld = float (vb.MaxX - vb.MinX) |> max 1.0
                    let dyWorld = float (vb.MaxY - vb.MinY) |> max 1.0
                    let pxPerDbuX = float vb.PixelW / dxWorld
                    let pxPerDbuY = float vb.PixelH / dyWorld
                    let toScr (xDbu: int64) (yDbu: int64) : float32 * float32 =
                        let sx = (float (xDbu - vb.MinX)) * pxPerDbuX |> float32
                        let sy =
                            float vb.PixelH
                            - (float (yDbu - vb.MinY)) * pxPerDbuY
                            |> float32
                        sx, sy
                    use fixedFill =
                        new SKPaint(
                            Style = SKPaintStyle.Fill,
                            IsAntialias = true,
                            Color = SKColor(0xE8uy, 0x99uy, 0x1Cuy, 0xCCuy))
                    use tentFill =
                        new SKPaint(
                            Style = SKPaintStyle.Fill,
                            IsAntialias = true,
                            Color = SKColor(0xE8uy, 0x99uy, 0x1Cuy, 0x80uy))
                    use outline =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            IsAntialias = true,
                            StrokeWidth = 1.0f,
                            Color = SKColor(0xFFuy, 0xC8uy, 0x50uy, 0xFFuy))
                    let paintSeg (paint: SKPaint) (seg: Routing.Draft.DraftSegment) =
                        let (x1s, y1s) = toScr seg.X1 seg.Y1
                        let (x2s, y2s) = toScr seg.X2 seg.Y2
                        let l = min x1s x2s
                        let r' = max x1s x2s
                        let t = min y1s y2s
                        let b = max y1s y2s
                        let rect = SKRect(l, t, r', b)
                        canvas.DrawRect(rect, paint)
                        canvas.DrawRect(rect, outline)
                    for seg in Routing.Draft.fixedSegments r do
                        paintSeg fixedFill seg
                    for seg in Routing.Draft.tentativeSegments r do
                        paintSeg tentFill seg

                // ADR-0003 — live DRC violation overlay. Paints each
                // violation bbox as a bright red outline on top of
                // everything else so they read at a glance against
                // the draft route + cell geometry. Empty array on
                // the fast path (no draft, or draft is clean).
                if routeLiveViolations.Length > 0 then
                    let dxWorld = float (vb.MaxX - vb.MinX) |> max 1.0
                    let dyWorld = float (vb.MaxY - vb.MinY) |> max 1.0
                    let pxPerDbuX = float vb.PixelW / dxWorld
                    let pxPerDbuY = float vb.PixelH / dyWorld
                    let toScrV (xDbu: int64) (yDbu: int64) : float32 * float32 =
                        let sx = (float (xDbu - vb.MinX)) * pxPerDbuX |> float32
                        let sy =
                            float vb.PixelH
                            - (float (yDbu - vb.MinY)) * pxPerDbuY
                            |> float32
                        sx, sy
                    use vOutline =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            IsAntialias = true,
                            StrokeWidth = 1.5f,
                            Color = SKColor(0xFFuy, 0x40uy, 0x40uy, 0xFFuy))
                    let paintBbox (b: int64 * int64 * int64 * int64) =
                        let (x1, y1, x2, y2) = b
                        let (sx1, sy1) = toScrV x1 y1
                        let (sx2, sy2) = toScrV x2 y2
                        let l = min sx1 sx2
                        let r' = max sx1 sx2
                        let t = min sy1 sy2
                        let b' = max sy1 sy2
                        canvas.DrawRect(SKRect(l, t, r', b'), vOutline)
                    for v in routeLiveViolations do
                        paintBbox v.BboxA
                        match v.BboxB with
                        | Some b -> paintBbox b
                        | None -> ()

                // Wire-mode snap-target hint — small circle at the
                // pin centroid the cursor is hovering over. Tells
                // the user "a wire CAN start (or end) here" before
                // they click. Painted last so it always reads on top.
                match hoveredSnapTarget with
                | None -> ()
                | Some t ->
                    let dxWorld = float (vb.MaxX - vb.MinX) |> max 1.0
                    let dyWorld = float (vb.MaxY - vb.MinY) |> max 1.0
                    let pxPerDbuX = float vb.PixelW / dxWorld
                    let pxPerDbuY = float vb.PixelH / dyWorld
                    let sx =
                        (float (t.X - vb.MinX)) * pxPerDbuX |> float32
                    let sy =
                        float vb.PixelH
                        - (float (t.Y - vb.MinY)) * pxPerDbuY
                        |> float32
                    use snapStroke =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            IsAntialias = true,
                            StrokeWidth = 2.0f,
                            Color = SKColor(0x4Cuy, 0xFFuy, 0xA0uy, 0xFFuy))
                    use snapFill =
                        new SKPaint(
                            Style = SKPaintStyle.Fill,
                            IsAntialias = true,
                            Color = SKColor(0x4Cuy, 0xFFuy, 0xA0uy, 0x33uy))
                    canvas.DrawCircle(sx, sy, 7.0f, snapFill)
                    canvas.DrawCircle(sx, sy, 7.0f, snapStroke)

                // route_editing_plan.md v1.1 — segment-drag preview.
                // Paint the projected wire (under the new
                // perpendicular delta) as a saturated outline on top
                // of the existing geometry. The original rects
                // underneath stay visible as a ghost — on commit the
                // document swap removes them and the projected geom
                // takes their place as ordinary RectEls.
                match segmentDrag, segmentDragDoc with
                | Some s, Some doc when s.Delta <> 0L ->
                    let projected = Routing.SegmentDrag.projectGeometry s doc
                    let dxWorld = float (vb.MaxX - vb.MinX) |> max 1.0
                    let dyWorld = float (vb.MaxY - vb.MinY) |> max 1.0
                    let pxPerDbuX = float vb.PixelW / dxWorld
                    let pxPerDbuY = float vb.PixelH / dyWorld
                    let toScrSD (xDbu: int64) (yDbu: int64) : float32 * float32 =
                        let sx = (float (xDbu - vb.MinX)) * pxPerDbuX |> float32
                        let sy =
                            float vb.PixelH
                            - (float (yDbu - vb.MinY)) * pxPerDbuY
                            |> float32
                        sx, sy
                    use sdFill =
                        new SKPaint(
                            Style = SKPaintStyle.Fill,
                            IsAntialias = false,
                            Color = SKColor(0xFFuy, 0xE0uy, 0x40uy, 0x60uy))
                    use sdOutline =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            IsAntialias = true,
                            StrokeWidth = 1.5f,
                            Color = SKColor(0xFFuy, 0xE0uy, 0x40uy, 0xFFuy))
                    for r in projected do
                        let (sx1, sy1) = toScrSD r.X1 r.Y1
                        let (sx2, sy2) = toScrSD r.X2 r.Y2
                        let l = min sx1 sx2
                        let r' = max sx1 sx2
                        let t = min sy1 sy2
                        let b = max sy1 sy2
                        canvas.DrawRect(SKRect(l, t, r', b), sdFill)
                        canvas.DrawRect(SKRect(l, t, r', b), sdOutline)
                | _ -> ()

                // Walkaround debug overlay (O key). When on AND a
                // draft is active, paint every obstacle bbox the
                // walkaround considers blocked for the draft's
                // (layer, startNet). Lets the user verify a "clear
                // path" really is clear in the obstacle set.
                match debugOverlay, draftRoute with
                | true, Some d ->
                    let layerKey : Routing.Obstacles.LayerKey =
                        { Number = fst d.Layer; DataType = snd d.Layer }
                    let netIdx = Routing.Obstacles.buildNetIndex netMap
                    let oSet =
                        Routing.Obstacles.obstacleSet
                            layerKey d.StartNet netIdx flatPolygonsForDebug
                    let obstaclePolys = Routing.Obstacles.polygonsOf oSet
                    let dxWorld = float (vb.MaxX - vb.MinX) |> max 1.0
                    let dyWorld = float (vb.MaxY - vb.MinY) |> max 1.0
                    let pxPerDbuX = float vb.PixelW / dxWorld
                    let pxPerDbuY = float vb.PixelH / dyWorld
                    let toScrDbg (xDbu : int64) (yDbu : int64) : float32 * float32 =
                        let sx = (float (xDbu - vb.MinX)) * pxPerDbuX |> float32
                        let sy =
                            float vb.PixelH
                            - (float (yDbu - vb.MinY)) * pxPerDbuY
                            |> float32
                        sx, sy
                    use obFill =
                        new SKPaint(
                            Style = SKPaintStyle.Fill,
                            IsAntialias = false,
                            Color = SKColor(0xFFuy, 0x00uy, 0xFFuy, 0x40uy))
                    use obStroke =
                        new SKPaint(
                            Style = SKPaintStyle.Stroke,
                            IsAntialias = true,
                            StrokeWidth = 1.0f,
                            Color = SKColor(0xFFuy, 0x00uy, 0xFFuy, 0xFFuy))
                    for fp in obstaclePolys do
                        let mutable xMin = System.Int64.MaxValue
                        let mutable yMin = System.Int64.MaxValue
                        let mutable xMax = System.Int64.MinValue
                        let mutable yMax = System.Int64.MinValue
                        for pt in fp.Points do
                            if pt.X < xMin then xMin <- pt.X
                            if pt.X > xMax then xMax <- pt.X
                            if pt.Y < yMin then yMin <- pt.Y
                            if pt.Y > yMax then yMax <- pt.Y
                        let (sx1, sy1) = toScrDbg xMin yMin
                        let (sx2, sy2) = toScrDbg xMax yMax
                        let l = min sx1 sx2
                        let r = max sx1 sx2
                        let t = min sy1 sy2
                        let b = max sy1 sy2
                        canvas.DrawRect(SKRect(l, t, r, b), obFill)
                        canvas.DrawRect(SKRect(l, t, r, b), obStroke)
                    // Corner-node rendering DROPPED: building the
                    // visibility graph for ~700 obstacles is O(N³)
                    // ≈ 400M ops on the UI thread per render frame.
                    // It froze the first draft frame for seconds.
                    // Obstacles + chosen path are the load-bearing
                    // diagnostic; nodes are nice-to-have and need
                    // either UI-thread offloading or a cached
                    // prebuilt graph borrowed from the BG walkaround
                    // before they come back.
                    //
                    // Last computed path (from Draft.Auto if any)
                    // — paint as bright connected lines so user can
                    // see what the search settled on.
                    match d.Auto with
                    | [] -> ()
                    | corners ->
                        use pathPaint =
                            new SKPaint(
                                Style = SKPaintStyle.Stroke,
                                IsAntialias = true,
                                StrokeWidth = 2.5f,
                                Color = SKColor(0x00uy, 0xFFuy, 0x80uy, 0xFFuy))
                        let pts =
                            match List.tryLast d.Points with
                            | Some last -> last :: corners
                            | None -> corners
                        let pts =
                            match d.Cursor with
                            | Some c -> pts @ [c]
                            | None -> pts
                        let arr = pts |> List.toArray
                        for i in 0 .. arr.Length - 2 do
                            let (x1, y1) = arr.[i]
                            let (x2, y2) = arr.[i + 1]
                            let (sx1, sy1) = toScrDbg x1 y1
                            let (sx2, sy2) = toScrDbg x2 y2
                            canvas.DrawLine(sx1, sy1, sx2, sy2, pathPaint)
                | _ -> ()

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
    // DRC cache for fast drag updates. `cachedDrcFlat` is the
    // FlatPolygons reference (by identity) that produced
    // `cachedDrcViolations`. If the static FlatPolygons hasn't
    // changed identity, the cache is reusable. During a drag,
    // we compute fresh DRC only for the moving area and merge
    // with the cached violations from the rest of the design.
    //
    // Implant tags are cached separately because they're
    // expensive to compute (O(N×M) bbox AND of every polygon
    // against every implant marker) and don't change unless
    // the underlying flat changes.
    //
    // Cache invariant: if `cachedDrcFlat` references the same
    // array as `this.FlatPolygons`, the cached violations and
    // tags are valid. Any property change that produces a new
    // FlatPolygons array invalidates the cache automatically
    // by identity mismatch.
    let mutable cachedDrcFlat : FlatPolygon array = [||]
    let mutable cachedDrcViolations : Drc.Check.Violation array = [||]
    let mutable cachedDrcImplantTags : Drc.Implant.ImplantTags array = [||]
    let mutable cachedDrcDisabled : Set<string> = Set.empty
    /// ADR-0003 — violations from the live DRC pass against the
    /// current draft route. Recomputed in OnPropertyChanged when
    /// DraftRoute changes; consumed by SkiaDraw to paint red
    /// outlines on offending bboxes.
    let mutable cachedRouteLiveViolations : Drc.Check.Violation array = [||]
    /// Currently-hovered snap target while in wire mode. When set,
    /// the renderer paints a small circle at this world point so
    /// the user sees where a wire CAN start (or where the next
    /// fix-segment click will land). Updated on every pointer move
    /// when RoutingMode || DraftRoute is active; cleared otherwise.
    let mutable hoveredSnapTarget : Routing.Snap.SnapTarget option = None
    /// Spatial index over the active macro's `FlatPolygons`, built
    /// once per geometry change and reused by `runLiveWithIndex`
    /// across mouse moves. `cachedCellIndexFor` tracks the
    /// FlatPolygons array we built against so we can detect when
    /// to rebuild via reference equality (the array gets a new
    /// reference on every edit / re-flatten).
    let mutable cachedCellIndex : Spatial.UniformGrid.Index option = None
    let mutable cachedCellIndexFor : FlatPolygon array = [||]
    /// Cached snap targets for the active macro. `OnPointerMoved`
    /// was rebuilding these on EVERY frame (flattenLabels + per-
    /// label linear scan over FlatPolygons), which made wire-mode
    /// hover lag on big cells. Built once on geometry change,
    /// reused on every move until FlatPolygons identity flips.
    /// Invalidation contract: see `tools/viz/docs/routing_caches.md`.
    let mutable cachedSnapTargets : Routing.Snap.SnapTarget array = [||]
    let mutable cachedSnapTargetsFor : FlatPolygon array = [||]
    /// Cached cell↔cell cross-net overlap violations. Recomputed
    /// only when FlatPolygons OR the NetMap changes — the
    /// existing draft↔cell pass inside `runLiveWithIndex` only
    /// fires during an active draft, so post-commit cell overlaps
    /// would disappear from the live overlay without this cache.
    /// O(N²) inside `cellCrossNetOverlaps`, hence the cache.
    let mutable cachedCellCrossNet : Drc.Check.Violation array = [||]
    let mutable cachedCellCrossNetFlatFor : FlatPolygon array = [||]
    let mutable cachedCellCrossNetNetsFor : Map<string, Sidecar.Types.NetEntry> = Map.empty
    /// Sample counter for the per-pointer-move timing log — sampled
    /// every 30 frames so the log doesn't churn under continuous
    /// mouse motion.
    let mutable pointerMoveCount : int = 0
    /// Background-task state for the live DRC compute. See
    /// `Routing.LiveDrc` — owns the monotonic version counter and
    /// staleness-drop semantics so the UI thread never blocks on
    /// the 1.4–1.5 s recompute.
    let liveDrcState : Routing.LiveDrc.State<Drc.Check.Violation array> =
        Routing.LiveDrc.create [||]
    /// ADR-0006 — background-task state for the walk-around router.
    /// Same staleness-drop pattern as `liveDrcState`; the `Latest`
    /// slot holds the most recently accepted corner list.
    let walkAroundState : Routing.LiveDrc.State<(int64 * int64) list> =
        Routing.LiveDrc.create []
    // ADR-0006 — graph build is region-bounded by (start, cursor)
    // and runs INSIDE the background compute, so the cache that
    // used to sit here at module scope was hit ~0% in practice and
    // got dropped. Build per move; sub-millisecond on real cells.
    /// Snapshot of the last-rendered ratline routes. Computed in
    /// SkiaDraw and stashed here so PointerPressed can hit-test
    /// ratline edges (selection) without re-running the flood-fill.
    let mutable lastRoutes : Net.Ratlines.NetRoute array = [||]
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
    static member val ShowLabelsProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("ShowLabels", true)
        with get
    static member val SnapEnabledProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("SnapEnabled", false)
        with get
    /// Walkaround debug overlay (O key). When true AND a draft is
    /// active, the canvas paints obstacle bboxes the walkaround
    /// currently sees.
    static member val DebugOverlayProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("DebugOverlay", false)
        with get
    static member val ShowDrcProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("ShowDrc", false)
        with get
    /// Magic-compatible rule names the user has silenced. Passed
    /// through to Drc.Check.checkWithToggles so violations of
    /// listed rules don't render. Plumbing only — no UI yet; the
    /// MCP command listener writes this set when a user disables
    /// a rule via tooling.
    static member val DisabledDrcRulesProperty : StyledProperty<Set<string>> =
        AvaloniaProperty.Register<GdsCanvasControl, Set<string>>(
            "DisabledDrcRules", Set.empty)
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
            : StyledProperty<Action<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>>(
            "PolygonPickedHandler", null)
        with get
    /// Currently picked top-cell polygons: set of (struct name,
    /// element index). Drives the highlight outline. Empty when
    /// nothing is picked. Multi-select supported via shift-click.
    static member val SelectedPolygonsProperty
            : StyledProperty<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>> =
        AvaloniaProperty.Register<GdsCanvasControl, Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>>(
            "SelectedPolygons", Set.empty)
        with get
    /// Replace the polygon selection (used by shift-click extend
    /// and marquee bulk-pick).
    static member val SetPolygonSelectionHandlerProperty
            : StyledProperty<Action<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>>>(
            "SetPolygonSelectionHandler", null)
        with get
    /// Set of net names whose ratline overlay is selected. Drives
    /// the distinct ratline render.
    static member val SelectedRatlinesProperty : StyledProperty<Set<string>> =
        AvaloniaProperty.Register<GdsCanvasControl, Set<string>>(
            "SelectedRatlines", Set.empty)
        with get
    /// Replace the selected-ratline set after a ratline click.
    static member val SetSelectedRatlinesHandlerProperty
            : StyledProperty<Action<Set<string>>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<Set<string>>>(
            "SetSelectedRatlinesHandler", null)
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
            : StyledProperty<Action<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>, int64, int64>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>, int64, int64>>(
            "MovePolygonsHandler", null)
        with get
    /// Clear the polygon Selection (Esc / empty marquee).
    static member val ClearPolygonSelectionHandlerProperty
            : StyledProperty<Action> =
        AvaloniaProperty.Register<GdsCanvasControl, Action>(
            "ClearPolygonSelectionHandler", null)
        with get

    // ---- ADR-0002 interactive routing tool -----------------------------
    /// When true, left-click on the canvas starts/extends a draft
    /// route on the active layer. When false, clicks fall through
    /// to normal selection.
    static member val RoutingModeProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("RoutingMode", false)
        with get
    /// Current in-flight draft route, or None when nothing is being
    /// drawn. Drives the canvas overlay and click semantics.
    static member val DraftRouteProperty
            : StyledProperty<Routing.Draft.DraftRoute option> =
        AvaloniaProperty.Register<GdsCanvasControl, Routing.Draft.DraftRoute option>(
            "DraftRoute", None)
        with get
    /// In-flight perpendicular segment drag (route_editing_plan.md
    /// v1.1). Set when the canvas detects mouse-down over a wire
    /// rect in idle state; cleared on commit / cancel. Renderer
    /// projects the new geometry from this state.
    static member val SegmentDragProperty
            : StyledProperty<Routing.SegmentDrag.DragState option> =
        AvaloniaProperty.Register<GdsCanvasControl, Routing.SegmentDrag.DragState option>(
            "SegmentDrag", None)
        with get
    /// Active edit layer for the routing tool (and other future
    /// edit ops). None = no layer focused; routing-mode clicks then
    /// do nothing rather than guessing.
    static member val ActiveLayerProperty
            : StyledProperty<Visibility.LayerKey option> =
        AvaloniaProperty.Register<GdsCanvasControl, Visibility.LayerKey option>(
            "ActiveLayer", None)
        with get
    /// Dispatched on left-click when RoutingMode is on and no draft
    /// is in flight. Args: layer, width, x, y (all in DBU).
    static member val StartRouteHandlerProperty
            : StyledProperty<Action<Visibility.LayerKey, int64, string, int64, int64>> =
        AvaloniaProperty.Register<GdsCanvasControl,
                                  Action<Visibility.LayerKey, int64, string, int64, int64>>(
            "StartRouteHandler", null)
        with get
    /// ADR-0006 — invoked from the walk-around background dispatch
    /// with the latest corner sequence. Update arm calls
    /// `Draft.setAuto corners` so the tentative polyline re-renders
    /// through the auto-jog path.
    static member val RouteAutoComputedHandlerProperty
            : StyledProperty<Action<(int64 * int64) list>> =
        AvaloniaProperty.Register<GdsCanvasControl,
                                  Action<(int64 * int64) list>>(
            "RouteAutoComputedHandler", null)
        with get
    /// ADR-0006 — net membership map driving the walk-around
    /// obstacle classification (same map Macro.Nets carries).
    static member val NetMapProperty
            : StyledProperty<Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>> =
        AvaloniaProperty.Register<GdsCanvasControl,
                                  Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>>(
            "NetMap", Map.empty)
        with get
    /// Dispatched on every mouse-move when a draft is in flight.
    /// Args: x, y in DBU.
    static member val RouteMouseMoveHandlerProperty
            : StyledProperty<Action<int64, int64>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<int64, int64>>(
            "RouteMouseMoveHandler", null)
        with get
    /// Dispatched on left-click when a draft is in flight — commits
    /// the tentative L as a fixed corner.
    static member val RouteFixSegmentHandlerProperty
            : StyledProperty<Action> =
        AvaloniaProperty.Register<GdsCanvasControl, Action>(
            "RouteFixSegmentHandler", null)
        with get
    /// Dispatched on right-click when a draft is in flight — commits
    /// the whole route into the cell as one undo step.
    static member val RouteFinishHandlerProperty
            : StyledProperty<Action> =
        AvaloniaProperty.Register<GdsCanvasControl, Action>(
            "RouteFinishHandler", null)
        with get
    /// Dispatched on mouse-down over a routing-layer rect (idle
    /// state). Args: wireId?, cellName, segIdx, rect, pickupX,
    /// pickupY, shift modifier. WireId is None for rects authored
    /// without a wire tag (pre-WireId or hand-edited geometry);
    /// the commit path allocates a fresh WireId for the new
    /// rects. Shift state is captured at mouse-down so a
    /// click-without-drag commit knows whether to replace the
    /// selection (no shift) or toggle the picked wire (shift).
    static member val SegmentDragStartHandlerProperty
            : StyledProperty<Action<int option, string, int, Rekolektion.Viz.Core.Rkt.Types.Rectangle, int64, int64, bool>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<int option, string, int, Rekolektion.Viz.Core.Rkt.Types.Rectangle, int64, int64, bool>>(
            "SegmentDragStartHandler", null)
        with get
    /// Dispatched on every mouse-move while a segment drag is active.
    /// Args: x, y in DBU.
    static member val SegmentDragMoveHandlerProperty
            : StyledProperty<Action<int64, int64>> =
        AvaloniaProperty.Register<GdsCanvasControl, Action<int64, int64>>(
            "SegmentDragMoveHandler", null)
        with get
    /// Dispatched on mouse-up — commit the drag as one undo step.
    static member val SegmentDragCommitHandlerProperty
            : StyledProperty<Action> =
        AvaloniaProperty.Register<GdsCanvasControl, Action>(
            "SegmentDragCommitHandler", null)
        with get
    /// Dispatched on Esc / pointer-cancel — drop without commit.
    static member val SegmentDragCancelHandlerProperty
            : StyledProperty<Action> =
        AvaloniaProperty.Register<GdsCanvasControl, Action>(
            "SegmentDragCancelHandler", null)
        with get
    /// ADR-0004 — effective DRC ruleset (rules + per-rule provenance).
    /// Flows from Model.DrcView via AppView. The canvas reads this
    /// for every DRC call (live route DRC + commit DRC overlays).
    static member val DrcViewProperty
            : StyledProperty<Drc.Rules.RulesetView> =
        AvaloniaProperty.Register<GdsCanvasControl, Drc.Rules.RulesetView>(
            "DrcView", Drc.Rules.defaultView)
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

    member this.DebugOverlay
        with get() : bool = this.GetValue(GdsCanvasControl.DebugOverlayProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.DebugOverlayProperty, v) |> ignore

    member this.DisabledDrcRules
        with get() : Set<string> = this.GetValue(GdsCanvasControl.DisabledDrcRulesProperty)
        and set(v: Set<string>) = this.SetValue(GdsCanvasControl.DisabledDrcRulesProperty, v) |> ignore

    member this.ShowGrid
        with get() : bool = this.GetValue(GdsCanvasControl.ShowGridProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.ShowGridProperty, v) |> ignore

    member this.ShowRuler
        with get() : bool = this.GetValue(GdsCanvasControl.ShowRulerProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.ShowRulerProperty, v) |> ignore

    member this.ShowLabels
        with get() : bool = this.GetValue(GdsCanvasControl.ShowLabelsProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.ShowLabelsProperty, v) |> ignore

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
        with get() : Action<Rekolektion.Viz.Core.Layout.Flatten.PolyKey> =
            this.GetValue(GdsCanvasControl.PolygonPickedHandlerProperty)
        and set(v: Action<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>) =
            this.SetValue(GdsCanvasControl.PolygonPickedHandlerProperty, v) |> ignore

    member this.SelectedPolygons
        with get() : Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey> =
            this.GetValue(GdsCanvasControl.SelectedPolygonsProperty)
        and set(v: Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>) =
            this.SetValue(GdsCanvasControl.SelectedPolygonsProperty, v) |> ignore

    member this.SetPolygonSelectionHandler
        with get() : Action<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>> =
            this.GetValue(GdsCanvasControl.SetPolygonSelectionHandlerProperty)
        and set(v: Action<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>>) =
            this.SetValue(GdsCanvasControl.SetPolygonSelectionHandlerProperty, v) |> ignore

    member this.SelectedRatlines
        with get() : Set<string> =
            this.GetValue(GdsCanvasControl.SelectedRatlinesProperty)
        and set(v: Set<string>) =
            this.SetValue(GdsCanvasControl.SelectedRatlinesProperty, v) |> ignore

    member this.SetSelectedRatlinesHandler
        with get() : Action<Set<string>> =
            this.GetValue(GdsCanvasControl.SetSelectedRatlinesHandlerProperty)
        and set(v: Action<Set<string>>) =
            this.SetValue(GdsCanvasControl.SetSelectedRatlinesHandlerProperty, v) |> ignore

    member this.ResizePolygonHandler
        with get() : Action<string, int, int64, int64, int64, int64> =
            this.GetValue(GdsCanvasControl.ResizePolygonHandlerProperty)
        and set(v: Action<string, int, int64, int64, int64, int64>) =
            this.SetValue(GdsCanvasControl.ResizePolygonHandlerProperty, v) |> ignore

    member this.MovePolygonsHandler
        with get() : Action<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>, int64, int64> =
            this.GetValue(GdsCanvasControl.MovePolygonsHandlerProperty)
        and set(v: Action<Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>, int64, int64>) =
            this.SetValue(GdsCanvasControl.MovePolygonsHandlerProperty, v) |> ignore

    member this.ClearPolygonSelectionHandler
        with get() : Action =
            this.GetValue(GdsCanvasControl.ClearPolygonSelectionHandlerProperty)
        and set(v: Action) =
            this.SetValue(GdsCanvasControl.ClearPolygonSelectionHandlerProperty, v) |> ignore

    // ---- ADR-0002 routing accessors ------------------------------------
    member this.RoutingMode
        with get() : bool = this.GetValue(GdsCanvasControl.RoutingModeProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.RoutingModeProperty, v) |> ignore

    member this.DraftRoute
        with get() : Routing.Draft.DraftRoute option =
            this.GetValue(GdsCanvasControl.DraftRouteProperty)
        and set(v: Routing.Draft.DraftRoute option) =
            this.SetValue(GdsCanvasControl.DraftRouteProperty, v) |> ignore

    member this.SegmentDrag
        with get() : Routing.SegmentDrag.DragState option =
            this.GetValue(GdsCanvasControl.SegmentDragProperty)
        and set(v: Routing.SegmentDrag.DragState option) =
            this.SetValue(GdsCanvasControl.SegmentDragProperty, v) |> ignore

    member this.SegmentDragStartHandler
        with get() : Action<int option, string, int, Rekolektion.Viz.Core.Rkt.Types.Rectangle, int64, int64, bool> =
            this.GetValue(GdsCanvasControl.SegmentDragStartHandlerProperty)
        and set(v: Action<int option, string, int, Rekolektion.Viz.Core.Rkt.Types.Rectangle, int64, int64, bool>) =
            this.SetValue(GdsCanvasControl.SegmentDragStartHandlerProperty, v) |> ignore

    member this.SegmentDragMoveHandler
        with get() : Action<int64, int64> =
            this.GetValue(GdsCanvasControl.SegmentDragMoveHandlerProperty)
        and set(v: Action<int64, int64>) =
            this.SetValue(GdsCanvasControl.SegmentDragMoveHandlerProperty, v) |> ignore

    member this.SegmentDragCommitHandler
        with get() : Action =
            this.GetValue(GdsCanvasControl.SegmentDragCommitHandlerProperty)
        and set(v: Action) =
            this.SetValue(GdsCanvasControl.SegmentDragCommitHandlerProperty, v) |> ignore

    member this.SegmentDragCancelHandler
        with get() : Action =
            this.GetValue(GdsCanvasControl.SegmentDragCancelHandlerProperty)
        and set(v: Action) =
            this.SetValue(GdsCanvasControl.SegmentDragCancelHandlerProperty, v) |> ignore

    member this.ActiveLayer
        with get() : Visibility.LayerKey option =
            this.GetValue(GdsCanvasControl.ActiveLayerProperty)
        and set(v: Visibility.LayerKey option) =
            this.SetValue(GdsCanvasControl.ActiveLayerProperty, v) |> ignore

    member this.StartRouteHandler
        with get() : Action<Visibility.LayerKey, int64, string, int64, int64> =
            this.GetValue(GdsCanvasControl.StartRouteHandlerProperty)
        and set(v: Action<Visibility.LayerKey, int64, string, int64, int64>) =
            this.SetValue(GdsCanvasControl.StartRouteHandlerProperty, v) |> ignore

    member this.RouteAutoComputedHandler
        with get() : Action<(int64 * int64) list> =
            this.GetValue(GdsCanvasControl.RouteAutoComputedHandlerProperty)
        and set(v: Action<(int64 * int64) list>) =
            this.SetValue(GdsCanvasControl.RouteAutoComputedHandlerProperty, v) |> ignore

    member this.NetMap
        with get() : Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry> =
            this.GetValue(GdsCanvasControl.NetMapProperty)
        and set(v: Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>) =
            this.SetValue(GdsCanvasControl.NetMapProperty, v) |> ignore

    member this.RouteMouseMoveHandler
        with get() : Action<int64, int64> =
            this.GetValue(GdsCanvasControl.RouteMouseMoveHandlerProperty)
        and set(v: Action<int64, int64>) =
            this.SetValue(GdsCanvasControl.RouteMouseMoveHandlerProperty, v) |> ignore

    member this.RouteFixSegmentHandler
        with get() : Action =
            this.GetValue(GdsCanvasControl.RouteFixSegmentHandlerProperty)
        and set(v: Action) =
            this.SetValue(GdsCanvasControl.RouteFixSegmentHandlerProperty, v) |> ignore

    member this.RouteFinishHandler
        with get() : Action =
            this.GetValue(GdsCanvasControl.RouteFinishHandlerProperty)
        and set(v: Action) =
            this.SetValue(GdsCanvasControl.RouteFinishHandlerProperty, v) |> ignore

    member this.DrcView
        with get() : Drc.Rules.RulesetView =
            this.GetValue(GdsCanvasControl.DrcViewProperty)
        and set(v: Drc.Rules.RulesetView) =
            this.SetValue(GdsCanvasControl.DrcViewProperty, v) |> ignore

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
    /// Lazy + cached snap-target lookup for wire-mode hover & click.
    /// Rebuilds only when `this.FlatPolygons` is replaced (edit /
    /// re-flatten); subsequent calls return the same array.
    member private this.SnapTargets () : Routing.Snap.SnapTarget array =
        if obj.ReferenceEquals(cachedSnapTargetsFor, this.FlatPolygons)
           && cachedSnapTargets.Length > 0 then
            cachedSnapTargets
        else
            match this.Library with
            | None ->
                cachedSnapTargets <- [||]
                cachedSnapTargetsFor <- this.FlatPolygons
                cachedSnapTargets
            | Some doc ->
                let labels = Layout.Flatten.flattenLabels doc
                let targets =
                    Routing.Snap.buildTargets labels this.FlatPolygons
                cachedSnapTargets <- targets
                cachedSnapTargetsFor <- this.FlatPolygons
                targets

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
        // PROBE — confirm NetMap actually propagates through the
        // attr binding. Walkaround diagnostics show netNameCount=0
        // even when the macro has 22 nets loaded.
        if e.Property = GdsCanvasControl.NetMapProperty then
            Rekolektion.Viz.App.Services.Logger.log "canvas.netmap.changed"
                {| count = this.NetMap.Count |}
        // ADR-0002 — flip the cursor to a crosshair when the wire
        // tool is armed so the user gets immediate visual feedback
        // that clicks now anchor a route. Restored to the platform
        // default when routing mode is off.
        if e.Property = GdsCanvasControl.RoutingModeProperty then
            this.Cursor <-
                if this.RoutingMode then
                    new Cursor(StandardCursorType.Cross)
                else
                    Cursor.Default
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
             || e.Property = GdsCanvasControl.DebugOverlayProperty
             || e.Property = GdsCanvasControl.DisabledDrcRulesProperty
             || e.Property = GdsCanvasControl.VisibleRatlinesProperty
             || e.Property = GdsCanvasControl.TightenModeProperty
             || e.Property = GdsCanvasControl.SelectedPolygonsProperty
             || e.Property = GdsCanvasControl.ShowGridProperty
             || e.Property = GdsCanvasControl.ShowRulerProperty
             || e.Property = GdsCanvasControl.ShowLabelsProperty
             || e.Property = GdsCanvasControl.SelectedRatlinesProperty
             || e.Property = GdsCanvasControl.SnapEnabledProperty
             || e.Property = GdsCanvasControl.DraftRouteProperty
             || e.Property = GdsCanvasControl.SegmentDragProperty
             || e.Property = GdsCanvasControl.RoutingModeProperty
             || e.Property = GdsCanvasControl.ActiveLayerProperty
             || e.Property = GdsCanvasControl.DrcViewProperty then
            // Geometry / overlay state changed — re-render but
            // KEEP the existing pan/zoom so editing operations
            // (Tighten, drag, rotate, mirror) don't snap the
            // camera away from the user's working view.
            //
            // ADR-0003 — recompute live DRC against the draft
            // whenever the route or the cell flat changes. The cost
            // is proportional to (cell rects + draft rects); for
            // typical macros it stays under a frame at 60 fps.
            if e.Property = GdsCanvasControl.DraftRouteProperty
               || e.Property = GdsCanvasControl.FlatPolygonsProperty
               || e.Property = GdsCanvasControl.DisabledDrcRulesProperty then
                // Live DRC: kicked off on a thread-pool task by
                // `Routing.LiveDrc.schedule` so per-frame UI cost
                // stays at ~0. Older results (overtaken by a newer
                // schedule) are dropped via the version counter
                // inside the module.
                let snapshotDraft = this.DraftRoute
                let snapshotFlat  = this.FlatPolygons
                let snapshotView  = this.DrcView
                let snapshotDisabled = this.DisabledDrcRules
                let snapshotNets = this.NetMap
                let snapshotStartNet =
                    snapshotDraft
                    |> Option.bind (fun d ->
                        if d.StartNet = "" then None else Some d.StartNet)
                let snapshotUnits =
                    match this.Library with
                    | Some doc -> doc.Units
                    | None -> { DbuNm = 1; UuUm = 1 }
                let triggerName =
                    if e.Property = GdsCanvasControl.DraftRouteProperty then "DraftRoute"
                    elif e.Property = GdsCanvasControl.FlatPolygonsProperty then "FlatPolygons"
                    else "DisabledDrcRules"
                // Rebuild the spatial index on the calling thread
                // when geometry changed — it's bounded by the
                // flat-array size and must be ready before any
                // background task uses it. Cached for subsequent
                // mouse-move recomputes.
                let mutable indexBuilt = false
                let cellIndex =
                    if obj.ReferenceEquals(cachedCellIndexFor, snapshotFlat)
                       && cachedCellIndex.IsSome then
                        cachedCellIndex.Value
                    else
                        indexBuilt <- true
                        let bboxes =
                            snapshotFlat
                            |> Array.map (fun p ->
                                let mutable xMin = System.Int64.MaxValue
                                let mutable yMin = System.Int64.MaxValue
                                let mutable xMax = System.Int64.MinValue
                                let mutable yMax = System.Int64.MinValue
                                for pt in p.Points do
                                    if pt.X < xMin then xMin <- pt.X
                                    if pt.X > xMax then xMax <- pt.X
                                    if pt.Y < yMin then yMin <- pt.Y
                                    if pt.Y > yMax then yMax <- pt.Y
                                (xMin, yMin, xMax, yMax))
                        let cs = Spatial.UniformGrid.suggestCellSize bboxes
                        let idx = Spatial.UniformGrid.build cs bboxes
                        cachedCellIndex <- Some idx
                        cachedCellIndexFor <- snapshotFlat
                        idx
                let swDrc = System.Diagnostics.Stopwatch()
                let phaseTimings = Drc.Check.newPhaseTimings ()
                // Refresh the cell↔cell cross-net cache when
                // FlatPolygons or NetMap reference flips. Holds
                // the violations that the draft↔cell pass would
                // also catch if a draft existed — without this,
                // those violations disappear the instant the user
                // commits the route ("DRC disappeared on commit").
                let cellCrossNetCached =
                    if obj.ReferenceEquals(cachedCellCrossNetFlatFor, snapshotFlat)
                       && obj.ReferenceEquals(cachedCellCrossNetNetsFor, snapshotNets) then
                        cachedCellCrossNet
                    else
                        let r =
                            try Drc.Check.cellCrossNetOverlaps snapshotFlat snapshotNets
                            with _ -> [||]
                        cachedCellCrossNet <- r
                        cachedCellCrossNetFlatFor <- snapshotFlat
                        cachedCellCrossNetNetsFor <- snapshotNets
                        r
                let compute () =
                    swDrc.Restart()
                    let r =
                        try
                            // No draft → still run DRC with an
                            // empty draftFlat so committed geometry
                            // (the just-finished wire, prior wires,
                            // hand-edited rects) keeps its standing
                            // violations visible. The earlier
                            // `| None -> [||]` short-circuit cleared
                            // every red box the instant the user
                            // finished a route — looked like "DRC
                            // disappeared on commit."
                            let draftFlat =
                                match snapshotDraft with
                                | Some r ->
                                    Routing.Draft.allSegments r
                                    |> Routing.Draft.toFlatPolygons
                                | None -> [||]
                            let liveResult =
                                Drc.Check.runLiveWithIndexTimed snapshotView
                                    snapshotUnits snapshotFlat cellIndex
                                    draftFlat snapshotNets snapshotStartNet
                                    snapshotDisabled phaseTimings
                            // Append the cell↔cell cross-net overlaps
                            // so committed wires keep their short-
                            // detection markers after commit.
                            Array.append liveResult cellCrossNetCached
                        with _ -> [||]
                    swDrc.Stop()
                    r
                let postBack (action : unit -> unit) =
                    Avalonia.Threading.Dispatcher.UIThread.Post(System.Action(action))
                let onAccept (result : Drc.Check.Violation array) =
                    cachedRouteLiveViolations <- result
                    this.InvalidateVisual()
                    if swDrc.ElapsedMilliseconds >= 2L
                       || pointerMoveCount % 30 = 0 then
                        Rekolektion.Viz.App.Services.Logger.log "drc.live.recompute"
                            {| triggerProp = triggerName
                               ms = swDrc.ElapsedMilliseconds
                               indexBuilt = indexBuilt
                               async = true
                               violations = result.Length
                               regionMs = phaseTimings.RegionFilterMs
                               tagMs    = phaseTimings.TagAllMs
                               stdMs    = phaseTimings.StandardMs
                               netMs    = phaseTimings.NetIndexMs
                               overlapMs = phaseTimings.OverlapMs
                               regionPolys = phaseTimings.RegionFilterCount
                               combinedPolys = phaseTimings.CombinedCount |}
                Routing.LiveDrc.schedule liveDrcState compute postBack onAccept
                |> ignore
            // ADR-0006 — walk-around dispatch. Same background-task
            // pattern as live DRC; results land via the
            // RouteAutoComputedHandler which the Update arm wires to
            // `Draft.setAuto`.
            if (e.Property = GdsCanvasControl.DraftRouteProperty
                || e.Property = GdsCanvasControl.FlatPolygonsProperty
                || e.Property = GdsCanvasControl.NetMapProperty
                || e.Property = GdsCanvasControl.ActiveLayerProperty)
               && this.DraftRoute.IsSome
               // LabelFlood derives nets asynchronously and can take
               // 60+ seconds on dense macros. Running walkaround
               // against an empty NetMap treats every polygon as
               // foreign — the wire takes catastrophic detours to
               // avoid every li1 polygon on the macro. Defer until
               // NetsLoaded populates the map. Logged so the user
               // can see why the auto-jog is silent.
               && this.NetMap.Count > 0 then
                let draft = this.DraftRoute.Value
                match draft.Cursor with
                | None -> ()       // no cursor yet, nothing to route to
                | Some (cx, cy) ->
                    // Last fixed point — walk-around runs from there.
                    let lastPt =
                        match List.tryLast draft.Points with
                        | Some pt -> pt
                        | None    -> (cx, cy)
                    // Skip the trivial walkaround when cursor equals
                    // the last fixed point (typically fires once at
                    // StartRoute when the snap target IS the start
                    // and the cursor lands there). The first user
                    // cursor movement bumps the dispatch, no cold-
                    // build cycle wasted on a zero-length search.
                    if lastPt = (cx, cy) then () else
                    let layerKey : Routing.Obstacles.LayerKey =
                        { Number = fst draft.Layer; DataType = snd draft.Layer }
                    // Clearance = wire_half_width + min_spacing. The
                    // wire's centerline must stay at least this far
                    // from any foreign obstacle so its outer edge
                    // clears the spacing rule. The visibility graph
                    // expands every obstacle by this clearance; the
                    // per-edge-type exemption in
                    // `VisibilityGraph.shortestPath` lets pin-tight
                    // starts/goals escape their neighbours.
                    let units =
                        match this.Library with
                        | Some d -> d.Units
                        | None -> { DbuNm = 1; UuUm = 1 }
                    let spacing =
                        Routing.Pads.spacingFor this.DrcView units draft.Layer
                        |> Option.defaultValue 0L
                    let clearance = max 0L (draft.Width / 2L + spacing)
                    let key : Routing.WalkAround.BuildKey =
                        { Layer = layerKey
                          StartNet = draft.StartNet
                          Clearance = clearance
                          FlatPolyRef = this.FlatPolygons
                          NetMapRef = this.NetMap }
                    let startPt : Routing.VisibilityGraph.Pt =
                        { X = fst lastPt; Y = snd lastPt }
                    let cursorPt : Routing.VisibilityGraph.Pt =
                        { X = cx; Y = cy }
                    // Region-bound the obstacle set to a bbox around
                    // (start, cursor) expanded by the manhattan
                    // distance. Region-bounding is an optimization;
                    // `routeAdaptive` retries with a larger region on
                    // noPath so the search preserves "noPath means no
                    // path exists in the full macro." Initial margin
                    // is tight so the common case stays sub-ms.
                    let dxAbs = abs (cursorPt.X - startPt.X)
                    let dyAbs = abs (cursorPt.Y - startPt.Y)
                    let initialMargin = max (dxAbs + dyAbs) (clearance * 4L)
                    // Macro bounds — region won't grow past this.
                    // Cached by FlatPolygons reference identity in
                    // WalkAround.macroBoundsOf; the array doesn't
                    // change between cursor moves on the same macro,
                    // so this collapses to a dict lookup after the
                    // first dispatch. Empty flat → fallback bbox
                    // around (start, cursor).
                    let macroBounds : Routing.WalkAround.MacroBounds =
                        match Routing.WalkAround.macroBoundsOf this.FlatPolygons with
                        | Some b -> b
                        | None ->
                            { XMin = startPt.X; YMin = startPt.Y
                              XMax = cursorPt.X; YMax = cursorPt.Y }
                    let cb = this.RouteAutoComputedHandler
                    // Convert the user's locked DraftPosture into
                    // a walk-around posture preference so the BG
                    // corner placement matches what the user is
                    // mouse-drawing. NoPreference falls back to
                    // the geometric dy>dx rule.
                    let preferred =
                        match draft.Posture with
                        | _ when not draft.PostureLocked ->
                            Routing.VisibilityGraph.NoPreference
                        | Routing.Draft.HorizontalFirst ->
                            Routing.VisibilityGraph.PreferHFirst
                        | Routing.Draft.VerticalFirst ->
                            Routing.VisibilityGraph.PreferVFirst
                    let compute () : (int64 * int64) list =
                        let swBuild = System.Diagnostics.Stopwatch.StartNew()
                        let emptyGraph () = Routing.VisibilityGraph.build 0L [||]
                        let adaptive =
                            try
                                Routing.WalkAround.routeAdaptive
                                    preferred
                                    key startPt cursorPt
                                    initialMargin macroBounds 3
                            with _ ->
                                { Path = None
                                  FinalRegion =
                                      { XMin = 0L; YMin = 0L
                                        XMax = 0L; YMax = 0L }
                                  Graph = emptyGraph ()
                                  Expansions = 0 }
                        swBuild.Stop()
                        // Reuse the graph that routeAdaptive already
                        // built for FinalRegion. Calling
                        // buildGraphInRegion a second time here cost
                        // ~50% of the per-frame compute on dense
                        // macros.
                        let graph = adaptive.Graph
                        let swSearch = System.Diagnostics.Stopwatch.StartNew()
                        let mutable searchOutcome = "unknown"
                        let result =
                            match adaptive.Path with
                            | None ->
                                searchOutcome <- "noPath"
                                []
                            | Some nodes ->
                                match nodes with
                                | _ :: rest ->
                                    let corners =
                                        rest
                                        |> List.rev
                                        |> (fun xs -> match xs with _ :: t -> t | [] -> [])
                                        |> List.rev
                                        |> List.map (fun pt -> pt.X, pt.Y)
                                    searchOutcome <-
                                        if corners.IsEmpty then "trivialStraight"
                                        else "jogged"
                                    corners
                                | [] ->
                                    searchOutcome <- "emptyNodes"
                                    []
                        swSearch.Stop()
                        // Containment check: which (if any) obstacle's
                        // interior contains start/cursor. Strict
                        // inequality matches the manhattan-visibility
                        // test in VisibilityGraph — a point exactly on
                        // a bbox edge does NOT count as inside.
                        let inside (pt : Routing.VisibilityGraph.Pt) (b : Routing.VisibilityGraph.Bbox) =
                            pt.X > b.XMin && pt.X < b.XMax
                            && pt.Y > b.YMin && pt.Y < b.YMax
                        let mutable startInIdx = -1
                        let mutable cursorInIdx = -1
                        for i in 0 .. graph.Obstacles.Length - 1 do
                            if startInIdx < 0 && inside startPt graph.Obstacles.[i] then
                                startInIdx <- i
                            if cursorInIdx < 0 && inside cursorPt graph.Obstacles.[i] then
                                cursorInIdx <- i
                        // How many polygons does the start net actually
                        // own? Zero means LabelFlood / sidecar didn't
                        // include the labeled-pin polygon under net
                        // `startNet`, so `netOf` falls back to None
                        // and the defensive "unknown → foreign" rule
                        // makes the user's OWN start patch an obstacle.
                        let startNetClaimed =
                            match Map.tryFind key.StartNet key.NetMapRef with
                            | Some entry -> entry.Polygons.Length
                            | None -> -1
                        let netNameCount = key.NetMapRef.Count
                        // Path validation. Every walkaround result the
                        // search returns gets checked against the same
                        // obstacle set the search used. Two passes:
                        //   • expanded — DRC clearance violations
                        //     (includes pre-existing endpoint-in-margin
                        //     situations the wire can't avoid).
                        //   • silicon (expanded shrunk back by clearance)
                        //     — actual electrical shorts. Any non-zero
                        //     here is a search bug.
                        // Logged separately so a regression shows up
                        // immediately in the live log.
                        let validationPath =
                            match adaptive.Path with
                            | Some nodes -> nodes
                            | None -> []
                        // Skip the trivial cases: a single-point path
                        // (start == cursor) has no segments to test,
                        // and a noPath result has nothing returned at
                        // all. Otherwise emit a log line every time —
                        // counts are zero when the path is clean, so
                        // the entry confirms the check ran and the
                        // path was validated.
                        if validationPath.Length >= 2 then
                            let expandedViols =
                                Routing.PathCheck.crossings
                                    validationPath graph.Obstacles
                            let silicon =
                                Routing.PathCheck.shrinkByClearance
                                    key.Clearance graph.Obstacles
                            let siliconViols =
                                Routing.PathCheck.crossings
                                    validationPath silicon
                            let fmtViol (v : Routing.PathCheck.Crossing) =
                                let (a, b) = v.Segment
                                sprintf
                                    "(%d,%d)→(%d,%d)|obs%d|(%d,%d,%d,%d)"
                                    a.X a.Y b.X b.Y v.ObstacleIndex
                                    v.Obstacle.XMin v.Obstacle.YMin
                                    v.Obstacle.XMax v.Obstacle.YMax
                            let expSummary =
                                expandedViols |> List.map fmtViol
                                |> String.concat " ; "
                            let silSummary =
                                siliconViols |> List.map fmtViol
                                |> String.concat " ; "
                            Rekolektion.Viz.App.Services.Logger.log
                                "walkaround.path_check"
                                {| expanded = expandedViols.Length
                                   silicon = siliconViols.Length
                                   expandedDetails = expSummary
                                   siliconDetails = silSummary
                                   pathLen = validationPath.Length
                                   startX = startPt.X; startY = startPt.Y
                                   cursorX = cursorPt.X; cursorY = cursorPt.Y |}
                        // Log EVERY walkaround compute so post-mortem
                        // can see exactly what the algorithm returned
                        // for each cursor position. Previously gated
                        // (every 30 frames + slow), which hid the
                        // path the wire actually rendered during fast
                        // drags.
                        if true then
                            // Emit corner coordinates so the diagnostic
                            // log shows WHERE the path bends — not just
                            // count. Without coords we can't tell a
                            // detour-style jog from a degenerate
                            // same-as-L-bend corner.
                            let cornerCoords =
                                result
                                |> List.map (fun (x, y) -> sprintf "(%d,%d)" x y)
                                |> String.concat " "
                            // Snapshot of which flat polys on the
                            // routing layer near the cursor are
                            // claimed by startNet vs left foreign,
                            // PLUS the foreign polys' bboxes — so
                            // the log shows exactly which "obstacles"
                            // the walkaround sees in the wire's
                            // path. Direct evidence for the
                            // "over-classification" hypothesis.
                            let nearbyClassification =
                                let cellFlat = key.FlatPolyRef
                                let netIdx = Routing.Obstacles.buildNetIndex key.NetMapRef
                                let region : Routing.Obstacles.Region =
                                    { XMin = min startPt.X cursorPt.X - 1000L
                                      YMin = min startPt.Y cursorPt.Y - 1000L
                                      XMax = max startPt.X cursorPt.X + 1000L
                                      YMax = max startPt.Y cursorPt.Y + 1000L }
                                let mutable ours = 0
                                let mutable foreign = 0
                                let foreignBoxes = System.Collections.Generic.List<string>()
                                let oursBoxes = System.Collections.Generic.List<string>()
                                for fp in cellFlat do
                                    if fp.Layer = key.Layer.Number
                                       && fp.DataType = key.Layer.DataType then
                                        let (xMin, yMin, xMax, yMax) =
                                            let mutable a = System.Int64.MaxValue
                                            let mutable b = System.Int64.MaxValue
                                            let mutable c = System.Int64.MinValue
                                            let mutable d = System.Int64.MinValue
                                            for pt in fp.Points do
                                                if pt.X < a then a <- pt.X
                                                if pt.X > c then c <- pt.X
                                                if pt.Y < b then b <- pt.Y
                                                if pt.Y > d then d <- pt.Y
                                            a, b, c, d
                                        if not (xMax < region.XMin
                                                || xMin > region.XMax
                                                || yMax < region.YMin
                                                || yMin > region.YMax) then
                                            let bbox = sprintf "(%d,%d,%d,%d)" xMin yMin xMax yMax
                                            if Routing.Obstacles.isOurs netIdx key.StartNet fp then  // seed-aware classifier
                                                ours <- ours + 1
                                                if oursBoxes.Count < 12 then oursBoxes.Add bbox
                                            else
                                                foreign <- foreign + 1
                                                if foreignBoxes.Count < 12 then foreignBoxes.Add bbox
                                ours, foreign,
                                String.concat " " oursBoxes,
                                String.concat " " foreignBoxes
                            let (nOurs, nForeign, oursBoxes, foreignBoxes) = nearbyClassification
                            Rekolektion.Viz.App.Services.Logger.log "walkaround"
                                {| buildMs = swBuild.ElapsedMilliseconds
                                   searchMs = swSearch.ElapsedMilliseconds
                                   layer = sprintf "%d/%d" key.Layer.Number key.Layer.DataType
                                   nearbyOurs = nOurs
                                   nearbyForeign = nForeign
                                   nearbyOursBboxes = oursBoxes
                                   nearbyForeignBboxes = foreignBoxes
                                   obstacles = graph.Obstacles.Length
                                   nodes = graph.Nodes.Length
                                   corners = result.Length
                                   cornerCoords = cornerCoords
                                   outcome = searchOutcome
                                   expansions = adaptive.Expansions
                                   startNet = key.StartNet
                                   startNetClaimed = startNetClaimed
                                   netNameCount = netNameCount
                                   startX = startPt.X
                                   startY = startPt.Y
                                   cursorX = cursorPt.X
                                   cursorY = cursorPt.Y
                                   startInside = startInIdx
                                   cursorInside = cursorInIdx |}
                        result
                    let postBack (action : unit -> unit) =
                        Avalonia.Threading.Dispatcher.UIThread.Post(System.Action(action))
                    let onAccept (corners : (int64 * int64) list) =
                        if not (isNull cb) then cb.Invoke(corners)
                    Routing.LiveDrc.schedule walkAroundState compute postBack onAccept
                    |> ignore
            this.InvalidateVisual()

    // ---- Pointer-driven select / drag / pan + wheel zoom ----

    override this.OnPointerPressed e =
        base.OnPointerPressed e
        let props = e.GetCurrentPoint(this).Properties
        let p = e.GetPosition this
        lastPos <- p
        e.Pointer.Capture this
        this.Focus () |> ignore

        // route_editing_plan.md v1.1 — segment drag. Left-click in
        // IDLE state (not routing-armed, no draft in flight, no
        // other drag) that lands on a wire-tagged rect picks up
        // the segment for perpendicular drag. Hit-test runs BEFORE
        // the routing dispatch so a wire pickup wins over an empty
        // routing click.
        let inIdleClick =
            props.IsLeftButtonPressed
            && not this.RoutingMode
            && (this.DraftRoute).IsNone
            && (this.SegmentDrag).IsNone
        if inIdleClick then
            let (wxIdle, wyIdle) = this.ScreenToWorld p
            match this.Library with
            | Some doc ->
                match Routing.Wire.findSegmentAt (int64 wxIdle) (int64 wyIdle) doc with
                | Some (wireIdOpt, cellName, idx, rect) ->
                    let cb = this.SegmentDragStartHandler
                    let shiftHeld =
                        e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)
                    if not (isNull cb) then
                        cb.Invoke(wireIdOpt, cellName, idx, rect,
                                  int64 wxIdle, int64 wyIdle, shiftHeld)
                    e.Handled <- true
                | None -> ()
            | None -> ()
        if e.Handled then () else

        // ADR-0002 interactive routing tool. Click semantics live in
        // Routing.Pointer.decideAction so the dispatch matrix can be
        // unit-tested without an Avalonia harness.
        let draft = this.DraftRoute
        let (wx, wy) = this.ScreenToWorld p
        // Snap to nearest labeled pin centroid within ~20 px. The
        // Option tells us whether the cursor was actually on a
        // target — used below to gate StartRoute (no free-air
        // starts: a wire that doesn't begin at a pin would never
        // connect to anything).
        let snapTargetOpt =
            if this.RoutingMode || draft.IsSome then
                // During an active draft, restrict snap targets to
                // pins on the SAME net the route started on. A click
                // on a foreign-net pin would commit a cross-net
                // short via `Pointer.Finish`; filtering at this
                // layer makes the foreign pin invisible to the snap
                // pass, so `decideAction` gets `onSnapTarget = false`
                // and falls through to FixSegment (free-space corner)
                // instead.
                let targets =
                    let raw = this.SnapTargets ()
                    match draft with
                    | Some d -> Routing.Snap.forStartNet d.StartNet raw
                    | None -> raw
                if targets.Length = 0 then None
                else
                    let radiusDbu =
                        int64 (20.0 / max pixelsPerDbu 0.0001)
                    Routing.Snap.nearest targets (int64 wx, int64 wy) radiusDbu
            else None
        let (snapX, snapY) =
            match snapTargetOpt with
            | Some t -> t.X, t.Y
            | None -> int64 wx, int64 wy
        // Default wire width pulled from the active view's per-layer
        // Width rule (e.g. 140 nm for met1, 300 nm for met3). Pads
        // widen the endpoints separately via `Routing.Pads`.
        let defaultLayer = (68, 20)
        let defaultWidth =
            let units =
                match this.Library with
                | Some d -> d.Units
                | None -> { DbuNm = 1; UuUm = 1 }
            let layerForWidth =
                this.ActiveLayer |> Option.defaultValue defaultLayer
            Routing.Pads.wireWidthFor this.DrcView units layerForWidth
            |> Option.defaultValue 140L
        let action =
            Routing.Pointer.decideAction
                this.RoutingMode draft this.ActiveLayer
                props.IsLeftButtonPressed props.IsRightButtonPressed
                defaultLayer defaultWidth
                (snapX, snapY)
                snapTargetOpt.IsSome
        // Snap-required-to-start: if Pointer would StartRoute but
        // the click missed every snap target, refuse. Stops users
        // from anchoring wires in free space where they can't
        // possibly connect to anything.
        let action =
            match action with
            | Routing.Pointer.StartRoute _ when snapTargetOpt.IsNone ->
                Routing.Pointer.Ignore
            | other -> other
        match action with
        | Routing.Pointer.StartRoute (layer, width, x, y) ->
            let cb = this.StartRouteHandler
            // Net comes from the snap target the user clicked. The
            // snap-required-to-start gate above guarantees this is
            // Some when StartRoute fires; defensive default to ""
            // (walk-around treats it as unknown → route around all).
            let startNet =
                match snapTargetOpt with
                | Some t -> t.Net
                | None -> ""
            if not (isNull cb) then cb.Invoke(layer, width, startNet, x, y)
            e.Handled <- true
        | Routing.Pointer.FixSegment ->
            let cb = this.RouteFixSegmentHandler
            if not (isNull cb) then cb.Invoke()
            e.Handled <- true
        | Routing.Pointer.Finish ->
            // Single pipeline: trust the BG walkaround's most-recent
            // `DraftRoute.Auto`. No more click-time synchronous
            // routeAdaptive — that was a second pipeline running
            // parallel to the BG one and producing a different
            // answer than what the user was looking at when they
            // clicked. The BG walkaround runs on every cursor frame
            // and posts its corners through `RouteAutoComputed`
            // before this Finish ever fires.
            //
            // Same-net pin click (enforced by the snap-target
            // filter above) expresses commit intent; path
            // cleanliness is a separate, later pass (segment
            // drag / vertex edit per `route_editing_plan.md`).
            // See `feedback_endpoint_over_path.md`.
            let cbFinish = this.RouteFinishHandler
            if not (isNull cbFinish) then cbFinish.Invoke()
            e.Handled <- true
        | Routing.Pointer.Ignore -> ()
        // Block non-routing LEFT-clicks while the wire tool is armed
        // (no accidental instance drag / polygon move / marquee /
        // resize). Middle-click (pan) and right-click (finish or
        // pan-while-route-inactive) pass through to the pan handler
        // below — panning around the macro is essential during wire
        // routing.
        if this.RoutingMode && props.IsLeftButtonPressed && not e.Handled then
            e.Handled <- true
        // Tighten mode: a left click on a numbered label commits
        // that candidate. Other clicks are swallowed so the user
        // doesn't accidentally pan, marquee, or change selection
        // while choosing a tighten direction.
        if not e.Handled && this.TightenMode && props.IsLeftButtonPressed then
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
        elif not e.Handled && (props.IsMiddleButtonPressed || props.IsRightButtonPressed) then
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
        elif not e.Handled
             && props.IsLeftButtonPressed
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
            let pk = this.SelectedPolygons.MinimumElement
            let sname = pk.Cell
            let idx = pk.Index
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
        elif not e.Handled && props.IsLeftButtonPressed then
            // Left button: hit-test the selectable instances. If we
            // hit something, start (or extend) selection + prep a
            // selection-drag. If we hit empty space, clear the
            // selection and start a pan.
            let wx, wy = this.ScreenToWorld p
            let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
            // Ratline hit-test FIRST so the user can click an MST
            // edge that visually overlaps polygon geometry. Tolerance
            // is in world DBU corresponding to ~6 px on screen, so
            // the click target tracks the visual line width.
            let ratlineHit : string option =
                if lastRoutes.Length = 0 then None
                else
                    let tolPx = 6.0
                    let dbuPerPxEst =
                        if pixelsPerDbu > 1e-9 then 1.0 / pixelsPerDbu
                        else 0.0
                    let tolDbuF = tolPx * dbuPerPxEst
                    let tolDbu = max 1L (int64 tolDbuF)
                    let cx = int64 (System.Math.Round wx)
                    let cy = int64 (System.Math.Round wy)
                    let visibleSet = this.VisibleRatlines
                    let segDist
                            (ax: int64) (ay: int64)
                            (bx: int64) (by: int64)
                            : int64 =
                        let dx = bx - ax
                        let dy = by - ay
                        let len2 = dx * dx + dy * dy
                        if len2 = 0L then
                            let px = cx - ax
                            let py = cy - ay
                            int64 (sqrt (float (px * px + py * py)))
                        else
                            let t =
                                float ((cx - ax) * dx + (cy - ay) * dy)
                                / float len2
                            let tc = max 0.0 (min 1.0 t)
                            let qx = float ax + tc * float dx
                            let qy = float ay + tc * float dy
                            let ddx = float cx - qx
                            let ddy = float cy - qy
                            int64 (sqrt (ddx * ddx + ddy * ddy))
                    let mutable best : string option = None
                    let mutable bestD = System.Int64.MaxValue
                    for route in lastRoutes do
                        if visibleSet.Contains route.Name then
                            for edge in route.Mst do
                                if edge.From >= 0
                                   && edge.From < route.Pins.Length
                                   && edge.To >= 0
                                   && edge.To < route.Pins.Length then
                                    let a = route.Pins.[edge.From].Position
                                    let b = route.Pins.[edge.To].Position
                                    let d = segDist a.X a.Y b.X b.Y
                                    if d <= tolDbu && d < bestD then
                                        bestD <- d
                                        best <- Some route.Name
                    best
            match ratlineHit with
            | Some name ->
                let prior = this.SelectedRatlines
                let next =
                    if shift then
                        if prior.Contains name then prior.Remove name
                        else prior.Add name
                    else Set.singleton name
                let h = this.SetSelectedRatlinesHandler
                if not (isNull h) then h.Invoke next
                // Swallow — ratline click shouldn't initiate pan or
                // polygon-selection drag.
                ()
            | None ->
            let hit =
                Instances.hitTest this.Instances (int64 (System.Math.Round wx)) (int64 (System.Math.Round wy))
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
                    let target : Rekolektion.Viz.Core.Layout.Flatten.PolyKey =
                        { Cell = sname; Index = idx; TopInstance = None }
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
                                    let kk : Rekolektion.Viz.Core.Layout.Flatten.PolyKey =
                                        { Cell = c.Name; Index = i; TopInstance = None }
                                    if next.Contains kk then
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
            let selKeys =
                sel
                |> Set.map (fun (pk: Rekolektion.Viz.Core.Layout.Flatten.PolyKey) ->
                    pk.Cell, pk.Index)
            for c in doc.Cells do
                c.Elements
                |> List.iteri (fun i el ->
                    if selKeys.Contains (c.Name, i) then
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
            (doc: Document) (sel: Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>)
            (dx: int64) (dy: int64) : Document =
        let perCell =
            sel
            |> Set.toList
            |> List.groupBy (fun (pk: Rekolektion.Viz.Core.Layout.Flatten.PolyKey) -> pk.Cell)
            |> List.map (fun (s, items) ->
                s, items |> List.map (fun pk -> pk.Index) |> Set.ofList)
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
                    let selTuples =
                        this.SelectedPolygons
                        |> Set.map (fun (pk: Rekolektion.Viz.Core.Layout.Flatten.PolyKey) ->
                            pk.Cell, pk.Index)
                    let lib' =
                        Instances.translateSelectionsWithLabels
                            lib this.InstanceSelection selTuples
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
                        let polySelKeys =
                            polySel
                            |> Set.map (fun (pk: Rekolektion.Viz.Core.Layout.Flatten.PolyKey) ->
                                pk.Cell, pk.Index)
                        let flat' =
                            flat0
                            |> Array.map (fun fp ->
                                if polySelKeys.Contains (fp.SourceStructure, fp.SourceIndex) then
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
                        let polySelTuples =
                            polySel
                            |> Set.map (fun (pk: Rekolektion.Viz.Core.Layout.Flatten.PolyKey) ->
                                pk.Cell, pk.Index)
                        let lib' =
                            Instances.translateSelectionsWithLabels
                                lib instSel polySelTuples dxSnap dySnap
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
        // route_editing_plan.md v1.1 — segment drag move. While a
        // drag is in flight, every mouse-move feeds
        // SegmentDragMove. The perpendicular constraint is applied
        // inside `SegmentDrag.setCursor` (off-axis motion is
        // ignored), so the canvas just forwards raw cursor coords.
        if (this.SegmentDrag).IsSome then
            let (wxDrag, wyDrag) = this.ScreenToWorld p
            let cb = this.SegmentDragMoveHandler
            if not (isNull cb) then cb.Invoke(int64 wxDrag, int64 wyDrag)
            this.InvalidateVisual()
        // ADR-0002 — when a draft is in flight, every move feeds
        // RouteMouseMove so the tentative L tracks the cursor.
        // Snap the cursor to a nearby labeled pin centroid so the
        // tentative L lands on the pin when the mouse is close —
        // makes "draw straight line from pin A to pin B" actually
        // land on B instead of a few DBU off-axis.
        //
        // Also stash the hovered snap target whenever the wire tool
        // is active so the renderer can paint a hint circle at
        // valid start/end points before the user clicks.
        if this.RoutingMode || (this.DraftRoute).IsSome then
            let swTotal = System.Diagnostics.Stopwatch.StartNew()
            let swScreen = System.Diagnostics.Stopwatch.StartNew()
            let (wx, wy) = this.ScreenToWorld p
            swScreen.Stop()
            let swTargets = System.Diagnostics.Stopwatch.StartNew()
            // Same filter as the press path (above): hide foreign-net
            // pins from the hover/snap pass while a draft is active
            // so the user sees only valid termination targets glow.
            let targets =
                let raw = this.SnapTargets ()
                match this.DraftRoute with
                | Some d -> Routing.Snap.forStartNet d.StartNet raw
                | None -> raw
            let snapCacheHit =
                obj.ReferenceEquals(cachedSnapTargetsFor, this.FlatPolygons)
            swTargets.Stop()
            let swNearest = System.Diagnostics.Stopwatch.StartNew()
            let target =
                if targets.Length = 0 then None
                else
                    let radiusDbu =
                        int64 (20.0 / max pixelsPerDbu 0.0001)
                    Routing.Snap.nearest targets (int64 wx, int64 wy) radiusDbu
            swNearest.Stop()
            // Only invalidate the canvas when the hover state
            // actually changes — bouncing the mouse over empty
            // space shouldn't churn frames.
            let changed =
                match hoveredSnapTarget, target with
                | None, None -> false
                | Some a, Some b -> a.X <> b.X || a.Y <> b.Y
                | _ -> true
            hoveredSnapTarget <- target
            let swInvalidate = System.Diagnostics.Stopwatch.StartNew()
            if changed then this.InvalidateVisual()
            swInvalidate.Stop()
            // Mouse-move dispatch (only when actually routing).
            let swDispatch = System.Diagnostics.Stopwatch.StartNew()
            if (this.DraftRoute).IsSome then
                let cb = this.RouteMouseMoveHandler
                if not (isNull cb) then
                    let (sx, sy) =
                        match target with
                        | Some t -> t.X, t.Y
                        | None -> int64 wx, int64 wy
                    cb.Invoke(sx, sy)
            swDispatch.Stop()
            swTotal.Stop()
            // Sample log: only emit when total >= 2 ms OR every 30
            // frames so the log doesn't churn but lag spikes surface.
            pointerMoveCount <- pointerMoveCount + 1
            if swTotal.ElapsedMilliseconds >= 2L
               || pointerMoveCount % 30 = 0 then
                Rekolektion.Viz.App.Services.Logger.log "pointer.move"
                    {| totalMs       = swTotal.ElapsedMilliseconds
                       screenToWorldMs = swScreen.ElapsedMilliseconds
                       snapTargetsMs = swTargets.ElapsedMilliseconds
                       snapTargetsCacheHit = snapCacheHit
                       snapTargetsCount = targets.Length
                       nearestMs     = swNearest.ElapsedMilliseconds
                       invalidateMs  = swInvalidate.ElapsedMilliseconds
                       dispatchMs    = swDispatch.ElapsedMilliseconds
                       draftActive   = (this.DraftRoute).IsSome
                       sampleFrame   = pointerMoveCount |}
        else
            if hoveredSnapTarget.IsSome then
                hoveredSnapTarget <- None
                this.InvalidateVisual()
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
        // route_editing_plan.md v1.1 — segment drag commit. Left
        // release while a segment drag is in flight commits the
        // projected geometry as one undo step. Wins over the
        // selection / pan release paths below.
        if (this.SegmentDrag).IsSome then
            let cb = this.SegmentDragCommitHandler
            if not (isNull cb) then cb.Invoke()
            this.InvalidateVisual()
            e.Handled <- true
        if e.Handled then () else
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
                    if not this.SelectedRatlines.IsEmpty then
                        let h = this.SetSelectedRatlinesHandler
                        if not (isNull h) then h.Invoke Set.empty
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
                                        if bboxFits bb then
                                            Some
                                                ({ Cell = top.Name; Index = i; TopInstance = None }
                                                 : Rekolektion.Viz.Core.Layout.Flatten.PolyKey)
                                        else None)
                                | PathEl p ->
                                    polyBbox p.Points
                                    |> Option.bind (fun bb ->
                                        if bboxFits bb then
                                            Some
                                                ({ Cell = top.Name; Index = i; TopInstance = None }
                                                 : Rekolektion.Viz.Core.Layout.Flatten.PolyKey)
                                        else None)
                                | RectEl r ->
                                    let bb =
                                        (min r.X1 r.X2, min r.Y1 r.Y2,
                                         max r.X1 r.X2, max r.Y1 r.Y2)
                                    if bboxFits bb then
                                            Some
                                                ({ Cell = top.Name; Index = i; TopInstance = None }
                                                 : Rekolektion.Viz.Core.Layout.Flatten.PolyKey)
                                        else None
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
            if not this.SelectedRatlines.IsEmpty then
                let h = this.SetSelectedRatlinesHandler
                if not (isNull h) then
                    h.Invoke Set.empty
                    e.Handled <- true
        | _ -> ()

    override this.OnPointerWheelChanged e =
        base.OnPointerWheelChanged e
        // Zoom about the pointer position so the world point under
        // the cursor stays put.
        // Delta-aware exponential zoom — mirrors the 3D wheel
        // handler. A flat 1.15× per event ignored the trackpad's
        // smaller fractional deltas (gestures registered as many
        // tiny 1.15× steps) and required spinning the wheel forever
        // to make a visible change at high zoom. exp(Delta.Y * 0.4)
        // gives ~1.5× per click-wheel tick and accumulates trackpad
        // gestures smoothly.
        let factor = System.Math.Exp(e.Delta.Y * 0.4)
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
                    // Two complementary passes:
                    //
                    // (1) checkWithToggles on the TOP CELL DIRECT
                    //     polygons — catches everything the user
                    //     authored at this level (width, spacing,
                    //     min-area, enclosure, endcap, cross-layer
                    //     spacing). Excluding SRef-internal polys
                    //     keeps the canvas from drowning in
                    //     foundry-COREID-waivered intra-primitive
                    //     errors the user can't fix without
                    //     editing the primitive itself.
                    //
                    // (2) checkInterInstance — orthogonal-only
                    //     spacing across SRefs. Same filter as
                    //     the dimension overlay so the canvas
                    //     isn't a hairball of diagonal arrows for
                    //     shapes that don't share an axis.
                    //
                    // The two passes don't overlap: (1) checks
                    // intra-top-cell only; (2) checks across
                    // instances. Concatenation is the merge.
                    // Two-tier DRC: full pass on static state
                    // (cached across frames), incremental pass
                    // for the moving region during a drag.
                    //
                    // Non-drag render: cache invariant holds
                    // (this.FlatPolygons identity matches
                    // cachedDrcFlat) → return cache. Otherwise
                    // recompute full, refresh cache.
                    //
                    // Drag render: keep cached violations
                    // OUTSIDE the moving region (they can't
                    // change — none of their polys moved); run
                    // fresh DRC on polys inside the moving
                    // region + halo (drag-affected area). Concat.
                    let disabled = this.DisabledDrcRules
                    let staticFlat = this.FlatPolygons
                    // Refresh cache if the static flat changed
                    // identity OR the disabled-rules set changed.
                    let cacheValid =
                        obj.ReferenceEquals(cachedDrcFlat, staticFlat)
                        && cachedDrcDisabled = disabled
                    if not cacheValid then
                        let tags = Drc.Implant.tagAll staticFlat
                        let vs =
                            Drc.Check.checkWithToggles
                                this.DrcView
                                lib.Units staticFlat tags disabled
                        cachedDrcFlat <- staticFlat
                        cachedDrcImplantTags <- tags
                        cachedDrcViolations <- vs
                        cachedDrcDisabled <- disabled
                    let drcDragActive =
                        dragLiveLib.IsSome
                        && (dragKind = PolygonDrag
                            || dragKind = SelectionDrag
                            || (match dragKind with
                                | ResizeDrag _ -> true
                                | _ -> false))
                    if not drcDragActive then
                        // Static state: cache is authoritative.
                        cachedDrcViolations
                    else
                        // Drag in flight: compute moving region
                        // bbox = union of (selected SRef bboxes
                        // pre-move) ∪ (post-move = + delta).
                        // Halo by 1270 DBU (the max rule limit
                        // — nwell.2a) so spacing violations with
                        // stationary neighbors near the boundary
                        // are caught.
                        let dx, dy = dragLiveDeltaDbu
                        let halo = 1270L
                        // Build a "drag-affected area" — covers
                        // pre-move + post-move positions plus
                        // halo. Drop selected polys' static
                        // bboxes; add (static + delta) for the
                        // post-move position. Union them all.
                        let selBboxes =
                            this.Instances
                            |> Array.filter (fun i ->
                                this.InstanceSelection.Contains i.Index)
                            |> Array.map (fun i ->
                                let (x1, y1, x2, y2) = i.BBox
                                let postX1 = x1 + dx
                                let postY1 = y1 + dy
                                let postX2 = x2 + dx
                                let postY2 = y2 + dy
                                (min x1 postX1) - halo,
                                (min y1 postY1) - halo,
                                (max x2 postX2) + halo,
                                (max y2 postY2) + halo)
                        if selBboxes.Length = 0 then
                            // No instance selection (e.g. polygon
                            // drag without instance context).
                            // Fall back to full recompute on the
                            // live state — slow but correct.
                            let tags = Drc.Implant.tagAll renderFlat
                            Drc.Check.checkWithToggles
                                this.DrcView
                                renderLib.Units renderFlat tags disabled
                        else
                            // Affected = union bbox of all
                            // selected instances' areas.
                            let aBb =
                                let init =
                                    System.Int64.MaxValue, System.Int64.MaxValue,
                                    System.Int64.MinValue, System.Int64.MinValue
                                selBboxes
                                |> Array.fold (fun (axMn, ayMn, axMx, ayMx)
                                                   (bxMn, byMn, bxMx, byMx) ->
                                    min axMn bxMn, min ayMn byMn,
                                    max axMx bxMx, max ayMx byMx) init
                            let (ax1, ay1, ax2, ay2) = aBb
                            let overlapsAffected
                                    ((px1, py1, px2, py2): int64*int64*int64*int64) =
                                px1 < ax2 && ax1 < px2
                                && py1 < ay2 && ay1 < py2
                            // Cached violations OUTSIDE affected
                            // area stay valid; drop those inside.
                            let kept =
                                cachedDrcViolations
                                |> Array.filter (fun v ->
                                    let bb =
                                        match v.BboxB with
                                        | None ->
                                            v.BboxA
                                        | Some b ->
                                            let (x1, y1, x2, y2) = v.BboxA
                                            let (bx1, by1, bx2, by2) = b
                                            min x1 bx1, min y1 by1,
                                            max x2 bx2, max y2 by2
                                    not (overlapsAffected bb))
                            // Filter renderFlat to polys in the
                            // affected area.
                            let polyBb (p: FlatPolygon) =
                                let mutable xMn = System.Int64.MaxValue
                                let mutable yMn = System.Int64.MaxValue
                                let mutable xMx = System.Int64.MinValue
                                let mutable yMx = System.Int64.MinValue
                                for pt in p.Points do
                                    if pt.X < xMn then xMn <- pt.X
                                    if pt.X > xMx then xMx <- pt.X
                                    if pt.Y < yMn then yMn <- pt.Y
                                    if pt.Y > yMx then yMx <- pt.Y
                                xMn, yMn, xMx, yMx
                            let smallFlat =
                                renderFlat
                                |> Array.filter (fun p ->
                                    overlapsAffected (polyBb p))
                            let smallTags =
                                Drc.Implant.tagAll smallFlat
                            let fresh =
                                Drc.Check.checkWithToggles
                                    this.DrcView
                                    renderLib.Units smallFlat smallTags disabled
                            Array.append kept fresh
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
            // Stash for ratline hit-test on next PointerPressed —
            // avoids re-running compute when the user just wants to
            // click a visible MST edge to identify its net.
            lastRoutes <- routes
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
                        let pk = this.SelectedPolygons.MinimumElement
                        let sname = pk.Cell
                        let idx = pk.Index
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
                  SelectedRatlines = this.SelectedRatlines
                  TightenCandidates = tightenCands
                  SelectedPolygons = this.SelectedPolygons
                  ResizeBbox = resizeBbox
                  ShowGrid = this.ShowGrid
                  ShowRuler = this.ShowRuler
                  ShowLabels = this.ShowLabels }
            // Route-live overlay shows when EITHER the DRC button
            // is on OR a draft is in flight. While drawing, the
            // user needs to see what their wire is creating
            // regardless of the DRC toggle. After the draft ends,
            // the toggle reasserts — turn DRC off and the
            // post-commit red boxes disappear cleanly.
            let routeLiveViolations' =
                if this.ShowDrc || (this.DraftRoute).IsSome
                then cachedRouteLiveViolations
                else [||]
            context.Custom(new SkiaDraw(bounds, renderLib, renderFlat, vb, this.Toggle, overlay, tightenHits, resizeHandleHits, this.DraftRoute, routeLiveViolations', this.DrcView.Provenance, hoveredSnapTarget, this.SegmentDrag, this.Library, this.DebugOverlay, this.NetMap, this.FlatPolygons))
        | None ->
            // Closing the active tab leaves None for Library; without
            // an explicit fill the prior frame's polygons stay
            // painted on the shared SkSurface ('canvas closed but
            // view still shows the cell' bug).
            context.FillRectangle(SolidColorBrush(Color.FromArgb(0xFFuy, 0x0Cuy, 0x10uy, 0x18uy)), bounds)
