/// Viewport ruler controls — thin Skia gutters along the top
/// and left edges of the 2D canvas. Always on (no toggle).
///
/// Pure shells over `Layout.RulerTicks`: the tick math is fully
/// unit-tested in Core; here we just wire camera state from
/// `Services.ViewportSync` into the math, render the resulting
/// ticks via SkiaSharp, and `InvalidateVisual` ourselves whenever
/// the canvas's camera changes.
module Rekolektion.Viz.App.Canvas2D.RulerControls

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Media
open Avalonia.Platform
open Avalonia.Rendering.SceneGraph
open Avalonia.Skia
open SkiaSharp
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.App.Services

/// Pixel width of each ruler gutter. Matches the spacing of
/// Photoshop / Illustrator rulers; small enough to not crowd
/// the canvas at common window sizes.
[<Literal>]
let GutterPx : float = 18.0

/// Minimum pixel pitch between major (labelled) ticks. Smaller
/// values would crowd the labels at high zoom; larger would
/// waste gutter real estate at low zoom. 60 px is the breakpoint
/// where a 3-digit label ("100", "-12") plus a 10 px margin
/// reads cleanly at the 11 px label size we use.
[<Literal>]
let MinPxPerMajor : float = 60.0

/// Background of the gutter. Slightly lighter than the canvas
/// (which is `#0C1018`) so the boundary reads at a glance, but
/// dark enough that the white tick lines pop.
let private bg = SKColor(0x1Auy, 0x1Fuy, 0x28uy, 0xFFuy)

/// Major tick / label colour. Slightly off-white so it doesn't
/// compete with selection highlights inside the canvas.
let private fg = SKColor(0xD0uy, 0xD4uy, 0xDEuy, 0xFFuy)

/// Minor tick colour — dimmer so the major ticks read first.
let private fgDim = SKColor(0x68uy, 0x6Cuy, 0x76uy, 0xFFuy)

[<Literal>]
let private LabelPx : float32 = 11.0f

[<Literal>]
let private MajorTickPx : float32 = 8.0f

[<Literal>]
let private MinorTickPx : float32 = 4.0f

// ─────────────────────────────────────────────────────────────
// Cached Skia paints. Each `SKPaint` wraps a native handle;
// `new SKPaint(...)` constructor + GC dispose round-trip the
// unmanaged heap. At 60 Hz pan that was 4 paints * 2 rulers =
// 8 native allocs per frame; hoisting to module scope drops
// it to 4 one-time allocs per process — leak-of-a-kind but
// bounded.
//
// `lazy` (not direct `let`) because module-level eager init
// fires before Avalonia / SkiaSharp bootstraps — `new SKPaint`
// at that point crashes the app under runDesktop. Lazy
// initialization defers each paint to first .Value access,
// which only happens inside the draw op's `Render` (well after
// Skia is up).
//
// SKPaint isn't thread-safe; the Avalonia render thread is the
// only writer/reader, so the single shared instance is fine.
// ─────────────────────────────────────────────────────────────

let private bgPaint : Lazy<SKPaint> =
    lazy (new SKPaint(Style = SKPaintStyle.Fill, Color = bg))

let private majorPaint : Lazy<SKPaint> =
    lazy (new SKPaint(Style = SKPaintStyle.Stroke,
                      Color = fg, StrokeWidth = 1.0f,
                      IsAntialias = false))

let private minorPaint : Lazy<SKPaint> =
    lazy (new SKPaint(Style = SKPaintStyle.Stroke,
                      Color = fgDim, StrokeWidth = 1.0f,
                      IsAntialias = false))

let private labelPaint : Lazy<SKPaint> =
    lazy (new SKPaint(Style = SKPaintStyle.Fill,
                      Color = fg, IsAntialias = true,
                      TextSize = LabelPx))

// ─────────────────────────────────────────────────────────────
// Skia draw ops — one per ruler axis. Each owns its bounds, the
// tick list it should draw, and the major-step (for label
// formatting). Stateless beyond the constructor args so Avalonia
// can replay them across renders.
// ─────────────────────────────────────────────────────────────

type private TopRulerDraw(bounds: Rect, ticks: RulerTicks.Tick list, majorStepUm: float) =
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
                let saved = canvas.Save()
                let w = float32 bounds.Width
                let h = float32 bounds.Height
                let clip = SKRect(0.0f, 0.0f, w, h)
                canvas.ClipRect(clip, SKClipOperation.Intersect)
                let bg' = bgPaint.Value
                let major' = majorPaint.Value
                let minor' = minorPaint.Value
                let label' = labelPaint.Value
                canvas.DrawRect(clip, bg')
                // Baseline: ticks grow DOWN from the bottom edge
                // so the labels sit above them inside the gutter.
                let bottom = h
                for t in ticks do
                    let x = float32 t.PxOffset + 0.5f
                    let len = if t.IsMajor then MajorTickPx else MinorTickPx
                    let paint = if t.IsMajor then major' else minor'
                    canvas.DrawLine(x, bottom - len, x, bottom, paint)
                    if t.IsMajor then
                        let label = RulerTicks.formatLabel t.Um majorStepUm
                        // Nudge the label 2 px right of the tick
                        // so it doesn't overlap the tick line. The
                        // 11 px font + 2 px baseline-from-top
                        // keeps the label inside the 18 px gutter.
                        canvas.DrawText(label, x + 2.0f, 11.0f, label')
                canvas.RestoreToCount saved

type private LeftRulerDraw(bounds: Rect, ticks: RulerTicks.Tick list, majorStepUm: float) =
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
                let saved = canvas.Save()
                let w = float32 bounds.Width
                let h = float32 bounds.Height
                let clip = SKRect(0.0f, 0.0f, w, h)
                canvas.ClipRect(clip, SKClipOperation.Intersect)
                let bg' = bgPaint.Value
                let major' = majorPaint.Value
                let minor' = minorPaint.Value
                let label' = labelPaint.Value
                canvas.DrawRect(clip, bg')
                let right = w
                for t in ticks do
                    let y = float32 t.PxOffset + 0.5f
                    let len = if t.IsMajor then MajorTickPx else MinorTickPx
                    let paint = if t.IsMajor then major' else minor'
                    canvas.DrawLine(right - len, y, right, y, paint)
                    if t.IsMajor then
                        let label = RulerTicks.formatLabel t.Um majorStepUm
                        // Rotate the label 90° CCW so it reads
                        // bottom-to-top along the left gutter.
                        // Anchor point is the tick's Y, then
                        // translate so the rotated baseline sits
                        // 2 px left of the tick line.
                        let savedT = canvas.Save()
                        canvas.Translate(11.0f, y - 2.0f)
                        canvas.RotateDegrees(-90.0f)
                        canvas.DrawText(label, 0.0f, 0.0f, label')
                        canvas.RestoreToCount savedT
                canvas.RestoreToCount saved

// ─────────────────────────────────────────────────────────────
// Controls. Both subscribe to ViewportSync on attach and
// unsubscribe on detach. Each Render reads the current snapshot,
// computes the visible µm range for its axis, builds the tick
// list, and hands off to its Skia draw op.
// ─────────────────────────────────────────────────────────────

/// Snap a world DBU coord to the active camera's grid step.
/// No-op when snap is disabled or the step is sub-unit.
let private snapCoord (cam: ViewportSync.Snapshot) (v: float) : float =
    if not cam.SnapEnabled || cam.SnapStepDbu <= 1L then v
    else
        let step = float cam.SnapStepDbu
        System.Math.Round(v / step) * step

/// Convert a screen Y inside the canvas's local pixel space to
/// a world DBU Y. Y is up in world but down on screen, hence the
/// `(half - canvasY)` flip.
let private canvasPxYToWorldDbu (cam: ViewportSync.Snapshot) (canvasY: float) : float =
    if cam.PixelsPerDbu <= 0.0 then 0.0
    else
        let half = cam.PixelH / 2.0
        cam.CenterDbuY + (half - canvasY) / cam.PixelsPerDbu

/// Convert a screen X inside the canvas's local pixel space to
/// a world DBU X. X is up-positive in both world and screen.
let private canvasPxXToWorldDbu (cam: ViewportSync.Snapshot) (canvasX: float) : float =
    if cam.PixelsPerDbu <= 0.0 then 0.0
    else
        let half = cam.PixelW / 2.0
        cam.CenterDbuX + (canvasX - half) / cam.PixelsPerDbu

/// Shared subscribe/unsubscribe helper so both controls have one
/// place to manage their `ViewportSync` + `GuidesService`
/// lifetime — easier to audit than four separate subscriptions
/// and matches the symmetry in the controls' render math.
type RulerBase() =
    inherit Control()
    let mutable camSub    : IDisposable option = None
    let mutable guideSub  : IDisposable option = None
    /// Schedule a redraw on every camera or guides change.
    ///
    /// **Must Post, not call directly.** `ViewportSync.update`
    /// fires from `GdsCanvasControl.PushViewportSync`, which is
    /// called from inside the canvas's `Render` pass. Avalonia
    /// throws `InvalidOperationException: Visual was invalidated
    /// during the render pass` if any control's
    /// `InvalidateVisual` runs there. The Dispatcher.Post defers
    /// the invalidation to the next message-loop iteration —
    /// after the current render finishes — which is the only
    /// safe time to mark a control dirty. (Tried a direct call
    /// once; viz crashed instantly on startup. Saved future-self
    /// the time.)
    member private this.OnExternalChanged () =
        Avalonia.Threading.Dispatcher.UIThread.Post
            (fun () -> this.InvalidateVisual())
    override this.OnAttachedToVisualTree(e) =
        base.OnAttachedToVisualTree e
        camSub   <- Some (ViewportSync.onChanged.Subscribe   (fun _ -> this.OnExternalChanged()))
        guideSub <- Some (GuidesService.onChanged.Subscribe  (fun _ -> this.OnExternalChanged()))
    override this.OnDetachedFromVisualTree(e) =
        camSub   |> Option.iter (fun s -> s.Dispose())
        guideSub |> Option.iter (fun s -> s.Dispose())
        camSub   <- None
        guideSub <- None
        base.OnDetachedFromVisualTree e

type TopRulerControl() =
    inherit RulerBase()
    override this.Render(context) =
        base.Render context
        let cam = ViewportSync.current()
        let bounds = Rect(0.0, 0.0, this.Bounds.Width, this.Bounds.Height)
        // Transparent fill so Avalonia's hit-test sees the
        // bounds and routes our PointerPressed correctly. Skia
        // op below paints the actual gutter background.
        context.FillRectangle(Brushes.Transparent, bounds)
        if cam.PixelsPerDbu > 0.0 && bounds.Width > 0.0 then
            let startUm, endUm =
                RulerTicks.gutterRangeUm
                    cam.CenterDbuX cam.PixelsPerDbu bounds.Width cam.DbuNm
            let pxPerUm = RulerTicks.pxPerUm cam.PixelsPerDbu cam.DbuNm
            let majorStep = RulerTicks.pickMajorStepUm pxPerUm MinPxPerMajor
            let ticks = RulerTicks.ticks startUm endUm pxPerUm MinPxPerMajor
            context.Custom(new TopRulerDraw(bounds, ticks, majorStep))

    /// Press anywhere on the top ruler starts a new HORIZONTAL
    /// guide drag. The cursor's Y coord at press defines the
    /// guide's initial world Y; PointerMoved updates it; Release
    /// decides commit (cursor in canvas) vs discard (cursor still
    /// on ruler).
    override this.OnPointerPressed(e) =
        base.OnPointerPressed e
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then
            e.Pointer.Capture this
            let cam = ViewportSync.current()
            let rulerY = e.GetPosition(this).Y
            let canvasY = rulerY - GutterPx
            let worldDbu = canvasPxYToWorldDbu cam canvasY |> snapCoord cam
            GuidesService.startDrag Guides.Horizontal (int64 worldDbu) None
            e.Handled <- true

    override this.OnPointerMoved(e) =
        base.OnPointerMoved e
        match (GuidesService.current()).Drag with
        | Some d when d.MovingId.IsNone
                       && d.Orientation = Guides.Horizontal ->
            let cam = ViewportSync.current()
            let rulerY = e.GetPosition(this).Y
            let canvasY = rulerY - GutterPx
            let worldDbu = canvasPxYToWorldDbu cam canvasY |> snapCoord cam
            GuidesService.updateDrag (int64 worldDbu)
        | _ -> ()

    override this.OnPointerReleased(e) =
        base.OnPointerReleased e
        match (GuidesService.current()).Drag with
        | Some d when d.MovingId.IsNone
                       && d.Orientation = Guides.Horizontal ->
            e.Pointer.Capture null
            let rulerY = e.GetPosition(this).Y
            if rulerY >= GutterPx then
                // Released inside the canvas area → commit.
                GuidesService.commitDrag ()
            else
                // Released still on the ruler → discard.
                GuidesService.cancelDrag ()
        | _ -> ()

type LeftRulerControl() =
    inherit RulerBase()
    override this.Render(context) =
        base.Render context
        let cam = ViewportSync.current()
        let bounds = Rect(0.0, 0.0, this.Bounds.Width, this.Bounds.Height)
        context.FillRectangle(Brushes.Transparent, bounds)
        if cam.PixelsPerDbu > 0.0 && bounds.Height > 0.0 then
            // World-Y axis: pixels increase DOWN, but world µm
            // increases UP, so the gutter's start (top edge) is
            // centerY + half-span, and end (bottom) is
            // centerY - half-span. Pre-negate by walking centerY
            // backwards through `gutterRangeUm`.
            let startUm, endUm =
                RulerTicks.gutterRangeUm
                    -cam.CenterDbuY cam.PixelsPerDbu bounds.Height cam.DbuNm
            let pxPerUm = RulerTicks.pxPerUm cam.PixelsPerDbu cam.DbuNm
            let majorStep = RulerTicks.pickMajorStepUm pxPerUm MinPxPerMajor
            let ticks = RulerTicks.ticks startUm endUm pxPerUm MinPxPerMajor
            // Flip the labels back to their natural sign (we
            // walked through gutterRangeUm with -centerY) so the
            // labels read "world µm Y", not "negated".
            let ticks =
                ticks
                |> List.map (fun t -> { t with Um = -t.Um })
            context.Custom(new LeftRulerDraw(bounds, ticks, majorStep))

    /// Press anywhere on the left ruler starts a new VERTICAL
    /// guide drag. Mirror image of TopRulerControl — see comments
    /// there for the press / move / release contract.
    override this.OnPointerPressed(e) =
        base.OnPointerPressed e
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then
            e.Pointer.Capture this
            let cam = ViewportSync.current()
            let rulerX = e.GetPosition(this).X
            let canvasX = rulerX - GutterPx
            let worldDbu = canvasPxXToWorldDbu cam canvasX |> snapCoord cam
            GuidesService.startDrag Guides.Vertical (int64 worldDbu) None
            e.Handled <- true

    override this.OnPointerMoved(e) =
        base.OnPointerMoved e
        match (GuidesService.current()).Drag with
        | Some d when d.MovingId.IsNone
                       && d.Orientation = Guides.Vertical ->
            let cam = ViewportSync.current()
            let rulerX = e.GetPosition(this).X
            let canvasX = rulerX - GutterPx
            let worldDbu = canvasPxXToWorldDbu cam canvasX |> snapCoord cam
            GuidesService.updateDrag (int64 worldDbu)
        | _ -> ()

    override this.OnPointerReleased(e) =
        base.OnPointerReleased e
        match (GuidesService.current()).Drag with
        | Some d when d.MovingId.IsNone
                       && d.Orientation = Guides.Vertical ->
            e.Pointer.Capture null
            let rulerX = e.GetPosition(this).X
            if rulerX >= GutterPx then GuidesService.commitDrag ()
            else GuidesService.cancelDrag ()
        | _ -> ()
