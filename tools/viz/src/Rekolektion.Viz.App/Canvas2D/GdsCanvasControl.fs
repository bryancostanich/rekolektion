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
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Render.Skia

/// Skia draw operation that takes an explicit ViewBox so the canvas
/// can drive pan/zoom externally.
type private SkiaDraw(bounds: Rect, lib: Library, flat: FlatPolygon array, vb: LayerPainter.ViewBox, toggle: Visibility.ToggleState) =
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
                LayerPainter.paintIn canvas vb flat toggle
                LabelPainter.paint canvas (w, h) lib
                canvas.RestoreToCount saved

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
    let mutable panning : bool = false
    let mutable lastPos : Avalonia.Point = Avalonia.Point()

    static member val LibraryProperty : StyledProperty<Library option> =
        AvaloniaProperty.Register<GdsCanvasControl, Library option>("Library", None)
        with get
    static member val FlatPolygonsProperty : StyledProperty<FlatPolygon array> =
        AvaloniaProperty.Register<GdsCanvasControl, FlatPolygon array>("FlatPolygons", [||])
        with get
    static member val ToggleProperty : StyledProperty<Visibility.ToggleState> =
        AvaloniaProperty.Register<GdsCanvasControl, Visibility.ToggleState>("Toggle", Visibility.empty)
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

    override this.OnPropertyChanged(e) =
        base.OnPropertyChanged e
        if e.Property = GdsCanvasControl.FlatPolygonsProperty
           || e.Property = GdsCanvasControl.LibraryProperty then
            // New macro → re-fit on next render
            hasFitted <- false
            this.InvalidateVisual()
        elif e.Property = GdsCanvasControl.ToggleProperty then
            this.InvalidateVisual()

    // ---- Pointer-driven pan + wheel zoom ----

    override this.OnPointerPressed e =
        base.OnPointerPressed e
        let props = e.GetCurrentPoint(this).Properties
        if props.IsLeftButtonPressed
           || props.IsMiddleButtonPressed
           || props.IsRightButtonPressed then
            panning <- true
            lastPos <- e.GetPosition this
            e.Pointer.Capture this
            this.Focus () |> ignore

    override this.OnPointerMoved e =
        base.OnPointerMoved e
        if panning then
            let p = e.GetPosition this
            let dxPx = p.X - lastPos.X
            let dyPx = p.Y - lastPos.Y
            // Move the world center opposite to the cursor drag so
            // geometry under the cursor follows the cursor. Y flip
            // because screen Y grows downward but world Y grows up.
            let scale = max pixelsPerDbu 0.0001
            centerX <- centerX - dxPx / scale
            centerY <- centerY + dyPx / scale
            lastPos <- p
            this.InvalidateVisual()

    override this.OnPointerReleased e =
        base.OnPointerReleased e
        panning <- false
        e.Pointer.Capture null

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
        match this.Library with
        | Some lib ->
            if not hasFitted then this.AutoFit ()
            let bounds = Rect(0.0, 0.0, this.Bounds.Width, this.Bounds.Height)
            let vb = this.MakeViewBox ()
            context.Custom(new SkiaDraw(bounds, lib, this.FlatPolygons, vb, this.Toggle))
        | None -> ()
