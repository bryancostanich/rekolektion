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
    /// Net routes for the ratline overlay. Empty when neither
    /// `ShowRatlines` is on nor a highlight is active.
    Routes        : Net.Ratlines.NetRoute array
    HighlightNet  : string option
    ShowRatlines  : bool
    /// Tighten mode candidates. Empty when mode is off. The
    /// renderer uses these to draw numbered candidate dim
    /// arrows + click targets; it returns the per-label hit
    /// rects so OnPointerPressed can map a click to an index.
    TightenCandidates : Drc.Check.TightenCandidate array
    /// Picked top-cell polygon (struct name, element index).
    /// Drawn outlined in cyan so the user sees what they
    /// selected. None when nothing is picked.
    SelectedPolygons : Set<string * int>
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
                      tightenHitsOut: TightenOverlay.LabelHit array ref) =
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
                    use stroke = new SKPaint(
                                    Style = SKPaintStyle.Stroke,
                                    Color = SKColor(0x00uy, 0xFFuy, 0xFFuy, 0xFFuy),
                                    StrokeWidth = 1.5f,
                                    IsAntialias = true)
                    for inst in overlay.Instances do
                        if overlay.Selected.Contains inst.Index then
                            canvas.DrawRect(bboxRect inst.BBox, stroke)

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
                                canvas.DrawPath(path, pSel)
                                path.Dispose()
                            | _ -> ()
                        | _ -> ()

                if overlay.Violations.Length > 0 then
                    DrcOverlay.render canvas vb (float lib.Units.DbuNm * 1.0e-3) overlay.Violations

                if overlay.Routes.Length > 0 then
                    RatlineOverlay.render canvas vb
                        overlay.Routes overlay.HighlightNet overlay.ShowRatlines

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
                canvas.RestoreToCount saved

type private DragKind =
    | NoDrag
    | PanDrag
    | SelectionDrag
    | MarqueeDrag
    | PolygonDrag

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
    static member val ShowDrcProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("ShowDrc", false)
        with get
    static member val ShowRatlinesProperty : StyledProperty<bool> =
        AvaloniaProperty.Register<GdsCanvasControl, bool>("ShowRatlines", false)
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

    member this.ShowRatlines
        with get() : bool = this.GetValue(GdsCanvasControl.ShowRatlinesProperty)
        and set(v: bool) = this.SetValue(GdsCanvasControl.ShowRatlinesProperty, v) |> ignore

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
             || e.Property = GdsCanvasControl.ShowRatlinesProperty
             || e.Property = GdsCanvasControl.TightenModeProperty
             || e.Property = GdsCanvasControl.SelectedPolygonsProperty then
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
                dragStartWorldX <- wx
                dragStartWorldY <- wy
                dragLiveDeltaDbu <- 0L, 0L
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
                    dragStartWorldX <- wx
                    dragStartWorldY <- wy
                    dragLiveDeltaDbu <- 0L, 0L
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

    override this.OnPointerMoved e =
        base.OnPointerMoved e
        match dragKind with
        | NoDrag -> ()
        | MarqueeDrag ->
            let p = e.GetPosition this
            let wx, wy = this.ScreenToWorld p
            marqueeWorldEnd <-
                int64 (System.Math.Round wx),
                int64 (System.Math.Round wy)
            this.InvalidateVisual()
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
            // Shift held → ortho-lock. Whichever axis has the
            // larger absolute Δ since drag-start wins, the other
            // is forced to zero. Re-evaluated every move so the
            // user can flip the dominant axis by reversing
            // direction; the moment they release Shift, free
            // motion resumes.
            let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
            let dxRaw, dyRaw =
                if shift then
                    if abs dxRaw >= abs dyRaw then dxRaw, 0L
                    else 0L, dyRaw
                else dxRaw, dyRaw
            // Snap the Δ to the SKY130 5 nm grid using the active
            // library's DBU scale. Without snapping the drag preview
            // (and final commit) drift fractionally as the cursor
            // moves below the per-pixel-DBU resolution.
            let dxSnap, dySnap =
                match this.Library with
                | Some lib ->
                    Snap.snapDeltaDbu lib.Units Snap.sky130MfgGridNm dxRaw dyRaw
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
                    dragLiveFlat <- Layout.Flatten.flatten lib
                | None ->
                    dragLiveLib <- None
                    dragLiveFlat <- [||]
                this.InvalidateVisual()
            lastPos <- p
        | PolygonDrag ->
            let p = e.GetPosition this
            let wx, wy = this.ScreenToWorld p
            let dxRaw = int64 (System.Math.Round (wx - dragStartWorldX))
            let dyRaw = int64 (System.Math.Round (wy - dragStartWorldY))
            let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
            let dxRaw, dyRaw =
                if shift then
                    if abs dxRaw >= abs dyRaw then dxRaw, 0L
                    else 0L, dyRaw
                else dxRaw, dyRaw
            let dxSnap, dySnap =
                match this.Library with
                | Some lib ->
                    Snap.snapDeltaDbu lib.Units Snap.sky130MfgGridNm dxRaw dyRaw
                | None -> dxRaw, dyRaw
            if (dxSnap, dySnap) <> dragLiveDeltaDbu then
                dragLiveDeltaDbu <- dxSnap, dySnap
                // Fast path: skip the library rebuild and the
                // hierarchical re-flatten that the instance-drag
                // path runs. The selection is top-cell polygons
                // only — no SRef transforms to recompose — so we
                // can transform the existing FlatPolygon array
                // directly. O(N_polys) per move, no allocation
                // beyond the shifted points.
                match this.Library with
                | Some lib ->
                    let sel = this.SelectedPolygons
                    let flat0 = this.FlatPolygons
                    let flat' =
                        flat0
                        |> Array.map (fun fp ->
                            if sel.Contains (fp.SourceStructure, fp.SourceIndex) then
                                { fp with
                                    Points =
                                        fp.Points
                                        |> Array.map (fun p ->
                                            { X = p.X + dxSnap
                                              Y = p.Y + dySnap }) }
                            else fp)
                    dragLiveLib <- Some lib
                    dragLiveFlat <- flat'
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
        | PolygonDrag when (dx <> 0L || dy <> 0L) ->
            let h = this.MovePolygonsHandler
            let sel = this.SelectedPolygons
            if not (isNull h) && not sel.IsEmpty then
                h.Invoke(sel, dx, dy)
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
                dragKind = SelectionDrag || dragKind = PolygonDrag
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
            // label walk unless either ShowRatlines or a net
            // highlight is active. A single highlighted net always
            // gets its own ratline regardless of the global toggle.
            let highlightNet = this.Toggle.HighlightNet
            let routes =
                if this.ShowRatlines || highlightNet.IsSome then
                    // Ratlines now takes Rkt.Document; convert at boundary.
                    Net.Ratlines.compute (renderLib)
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
                    let otherPolys =
                        this.Instances
                        |> Array.filter (fun i -> not (this.InstanceSelection.Contains i.Index))
                        |> Array.collect (fun i ->
                            Layout.Flatten.flattenInstance (renderLib) i.Index)
                    Drc.Check.tightenCandidates
                        renderLib.Units
                        selectedPolys otherPolys
                else
                    [||]
            let overlay : SelectionOverlay =
                { Instances = this.Instances
                  Selected  = this.InstanceSelection
                  Dragging  = dragging
                  ShowDimensions = this.ShowDimensions
                  InstancePolyBboxes = instPolyBboxes
                  Violations = violations
                  MarqueeWorld = marquee
                  Routes = routes
                  HighlightNet = highlightNet
                  ShowRatlines = this.ShowRatlines
                  TightenCandidates = tightenCands
                  SelectedPolygons = this.SelectedPolygons }
            context.Custom(new SkiaDraw(bounds, renderLib, renderFlat, vb, this.Toggle, overlay, tightenHits))
        | None ->
            // Closing the active tab leaves None for Library; without
            // an explicit fill the prior frame's polygons stay
            // painted on the shared SkSurface ('canvas closed but
            // view still shows the cell' bug).
            context.FillRectangle(Avalonia.Media.Brushes.Black, bounds)
