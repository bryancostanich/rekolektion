module Rekolektion.Viz.App.Canvas2D.GdsCanvasControl

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Platform
open Avalonia.Rendering.SceneGraph
open Avalonia.Skia
open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Render.Skia

type private SkiaDraw(bounds: Rect, lib: Library, flat: FlatPolygon array, toggle: Visibility.ToggleState) =
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
                // canvas here is the WHOLE WINDOW's SkSurface, not a
                // per-control surface — canvas.Clear() would erase
                // tab strip + side panels. Instead clip to our
                // bounds and fill that sub-rect.
                let saved = canvas.Save ()
                let clipRect = SKRect(0.0f, 0.0f, float32 w, float32 h)
                canvas.ClipRect(clipRect, SKClipOperation.Intersect)
                use bg = new SKPaint(Style = SKPaintStyle.Fill, Color = SKColors.Black)
                canvas.DrawRect(clipRect, bg)
                LayerPainter.paint canvas (w, h) flat toggle
                LabelPainter.paint canvas (w, h) lib
                canvas.RestoreToCount saved

type GdsCanvasControl() =
    inherit Control()

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

    override this.OnPropertyChanged(e) =
        base.OnPropertyChanged e
        if e.Property = GdsCanvasControl.LibraryProperty
           || e.Property = GdsCanvasControl.FlatPolygonsProperty
           || e.Property = GdsCanvasControl.ToggleProperty then
            this.InvalidateVisual()

    override this.Render(context) =
        base.Render context
        match this.Library with
        | Some lib ->
            let bounds = Rect(0.0, 0.0, this.Bounds.Width, this.Bounds.Height)
            context.Custom(new SkiaDraw(bounds, lib, this.FlatPolygons, this.Toggle))
        | None -> ()
