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
open Rekolektion.Viz.Core.Gds.Types
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
}

/// Skia draw operation that takes an explicit ViewBox so the canvas
/// can drive pan/zoom externally.
type private SkiaDraw(bounds: Rect,
                      lib: Library,
                      flat: FlatPolygon array,
                      vb: LayerPainter.ViewBox,
                      toggle: Visibility.ToggleState,
                      overlay: SelectionOverlay) =
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
                LayerPainter.paintIn canvas vb lib flat toggle
                LabelPainter.paintIn canvas vb lib toggle

                // Resting selection: thin cyan outline around each
                // selected instance's bbox so the user can see what's
                // picked. Suppressed during drag — the polygons
                // themselves are moving and the at-rest bbox would
                // lie at the pre-drag position (wrong + distracting).
                if not overlay.Dragging
                   && overlay.Selected.Count > 0
                   && overlay.Instances.Length > 0 then
                    let scaleX =
                        if vb.MaxX = vb.MinX then 1.0
                        else float vb.PixelW / float (vb.MaxX - vb.MinX)
                    let scaleY =
                        if vb.MaxY = vb.MinY then 1.0
                        else float vb.PixelH / float (vb.MaxY - vb.MinY)
                    use stroke = new SKPaint(
                                    Style = SKPaintStyle.Stroke,
                                    Color = SKColor(0x00uy, 0xFFuy, 0xFFuy, 0xFFuy),
                                    StrokeWidth = 1.5f,
                                    IsAntialias = true)
                    for inst in overlay.Instances do
                        if overlay.Selected.Contains inst.Index then
                            let (x1, y1, x2, y2) = inst.BBox
                            let sx1 = (float x1 - float vb.MinX) * scaleX |> float32
                            let sx2 = (float x2 - float vb.MinX) * scaleX |> float32
                            let sy1 = float vb.PixelH - (float y1 - float vb.MinY) * scaleY |> float32
                            let sy2 = float vb.PixelH - (float y2 - float vb.MinY) * scaleY |> float32
                            let r = SKRect(min sx1 sx2, min sy1 sy2, max sx1 sx2, max sy1 sy2)
                            canvas.DrawRect(r, stroke)
                canvas.RestoreToCount saved

type private DragKind = NoDrag | PanDrag | SelectionDrag

type GdsCanvasControl() =
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
    let mutable dragStartWorldX : float = 0.0
    let mutable dragStartWorldY : float = 0.0
    let mutable dragLiveDeltaDbu : int64 * int64 = 0L, 0L
    // Speculative re-flatten cached during an in-flight selection
    // drag: every time the snapped Δ changes, we copy the active
    // Library, translate the selected SRef origins, and re-flatten.
    // The Render path uses these instead of the bound FlatPolygons
    // so the moved geometry — not a ghost outline — tracks the
    // cursor. None when no drag is active.
    let mutable dragLiveLib : Library option = None
    let mutable dragLiveFlat : FlatPolygon array = [||]

    // Make the control focusable so ESC (clear selection) lands
    // here. Setting Focusable from the instance ctor triggers
    // OnPropertyChanged during F# type init, which recursively
    // dereferences the static StyledProperty fields and crashes
    // with FailInit. Override the metadata default instead — that
    // runs in the static ctor before any instance exists.
    static do
        Avalonia.Input.InputElement.FocusableProperty.OverrideDefaultValue<GdsCanvasControl>(true)

    static member val LibraryProperty : StyledProperty<Library option> =
        AvaloniaProperty.Register<GdsCanvasControl, Library option>("Library", None)
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

    member this.Library
        with get() : Library option = this.GetValue(GdsCanvasControl.LibraryProperty)
        and set(v: Library option) = this.SetValue(GdsCanvasControl.LibraryProperty, v) |> ignore

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
        if e.Property = GdsCanvasControl.FlatPolygonsProperty
           || e.Property = GdsCanvasControl.LibraryProperty then
            // New macro → re-fit on next render. Drag in flight (if
            // any) is also stale at this point — a different macro is
            // about to render, so any accumulated Δ no longer maps to
            // the new geometry.
            hasFitted <- false
            dragKind <- NoDrag
            dragLiveDeltaDbu <- 0L, 0L
            dragLiveLib <- None
            dragLiveFlat <- [||]
            this.InvalidateVisual()
        elif e.Property = GdsCanvasControl.ToggleProperty
             || e.Property = GdsCanvasControl.InstancesProperty
             || e.Property = GdsCanvasControl.InstanceSelectionProperty then
            this.InvalidateVisual()

    // ---- Pointer-driven select / drag / pan + wheel zoom ----

    override this.OnPointerPressed e =
        base.OnPointerPressed e
        let props = e.GetCurrentPoint(this).Properties
        let p = e.GetPosition this
        lastPos <- p
        e.Pointer.Capture this
        this.Focus () |> ignore

        if props.IsMiddleButtonPressed || props.IsRightButtonPressed then
            // Middle / right → pan, regardless of geometry beneath.
            dragKind <- PanDrag
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
                // Topmost in declaration order = last array element.
                let target = hit.[hit.Length - 1]
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
                dragStartWorldX <- wx
                dragStartWorldY <- wy
                dragLiveDeltaDbu <- 0L, 0L
                dragKind <- if next.IsEmpty then PanDrag else SelectionDrag
            else
                // Empty space → clear any prior selection and pan.
                if not this.InstanceSelection.IsEmpty then
                    let h = this.ClearInstanceSelectionHandler
                    if not (isNull h) then h.Invoke ()
                dragKind <- PanDrag

    override this.OnPointerMoved e =
        base.OnPointerMoved e
        match dragKind with
        | NoDrag -> ()
        | PanDrag ->
            let p = e.GetPosition this
            let dxPx = p.X - lastPos.X
            let dyPx = p.Y - lastPos.Y
            let scale = max pixelsPerDbu 0.0001
            centerX <- centerX - dxPx / scale
            centerY <- centerY + dyPx / scale
            lastPos <- p
            this.InvalidateVisual()
        | SelectionDrag ->
            let p = e.GetPosition this
            let wx, wy = this.ScreenToWorld p
            let dxRaw = int64 (System.Math.Round (wx - dragStartWorldX))
            let dyRaw = int64 (System.Math.Round (wy - dragStartWorldY))
            // Snap the Δ to the SKY130 5 nm grid using the active
            // library's DBU scale. Without snapping the drag preview
            // (and final commit) drift fractionally as the cursor
            // moves below the per-pixel-DBU resolution.
            let dxSnap, dySnap =
                match this.Library with
                | Some lib ->
                    Snap.snapDeltaDbu lib Snap.sky130MfgGridNm dxRaw dyRaw
                | None ->
                    dxRaw, dyRaw
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
                    let lib' =
                        Instances.translateSelection
                            lib this.InstanceSelection dxSnap dySnap
                    dragLiveLib <- Some lib'
                    dragLiveFlat <- Layout.Flatten.flatten lib'
                | None ->
                    dragLiveLib <- None
                    dragLiveFlat <- [||]
                this.InvalidateVisual()
            lastPos <- p

    override this.OnPointerReleased e =
        base.OnPointerReleased e
        let kind = dragKind
        let dx, dy = dragLiveDeltaDbu
        dragKind <- NoDrag
        dragLiveDeltaDbu <- 0L, 0L
        dragLiveLib <- None
        dragLiveFlat <- [||]
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
            let dragging = (dragKind = SelectionDrag)
            let overlay : SelectionOverlay =
                { Instances = this.Instances
                  Selected  = this.InstanceSelection
                  Dragging  = dragging }
            // While a drag is in flight, render the speculatively
            // translated Library + FlatPolygons so the moved
            // geometry tracks the cursor. The bound props haven't
            // changed yet — we only commit on release.
            let renderLib, renderFlat =
                match dragLiveLib with
                | Some live when dragging -> live, dragLiveFlat
                | _ -> lib, this.FlatPolygons
            context.Custom(new SkiaDraw(bounds, renderLib, renderFlat, vb, this.Toggle, overlay))
        | None ->
            // Closing the active tab leaves None for Library; without
            // an explicit fill the prior frame's polygons stay
            // painted on the shared SkSurface ('canvas closed but
            // view still shows the cell' bug).
            context.FillRectangle(Avalonia.Media.Brushes.Black, bounds)
