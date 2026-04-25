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
open Rekolektion.Viz.Render.Skia

type private SkiaDraw(bounds: Rect, lib: Library, toggle: Visibility.ToggleState) =
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
                canvas.Clear(SKColors.Black)
                LayerPainter.paint canvas (w, h) lib toggle
                LabelPainter.paint canvas (w, h) lib

type GdsCanvasControl() =
    inherit Control()

    static member val LibraryProperty : StyledProperty<Library option> =
        AvaloniaProperty.Register<GdsCanvasControl, Library option>("Library", None)
        with get
    static member val ToggleProperty : StyledProperty<Visibility.ToggleState> =
        AvaloniaProperty.Register<GdsCanvasControl, Visibility.ToggleState>("Toggle", Visibility.empty)
        with get

    member this.Library
        with get() : Library option = this.GetValue(GdsCanvasControl.LibraryProperty)
        and set(v: Library option) = this.SetValue(GdsCanvasControl.LibraryProperty, v) |> ignore

    member this.Toggle
        with get() : Visibility.ToggleState = this.GetValue(GdsCanvasControl.ToggleProperty)
        and set(v: Visibility.ToggleState) = this.SetValue(GdsCanvasControl.ToggleProperty, v) |> ignore

    override this.OnPropertyChanged(e) =
        base.OnPropertyChanged e
        if e.Property = GdsCanvasControl.LibraryProperty || e.Property = GdsCanvasControl.ToggleProperty then
            this.InvalidateVisual()

    override this.Render(context) =
        base.Render context
        match this.Library with
        | Some lib ->
            let bounds = Rect(0.0, 0.0, this.Bounds.Width, this.Bounds.Height)
            context.Custom(new SkiaDraw(bounds, lib, this.Toggle))
        | None -> ()
