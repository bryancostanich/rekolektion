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
                use bgPaint = new SKPaint(Style = SKPaintStyle.Fill, Color = bg)
                canvas.DrawRect(clip, bgPaint)
                use majorPaint =
                    new SKPaint(Style = SKPaintStyle.Stroke,
                                Color = fg, StrokeWidth = 1.0f,
                                IsAntialias = false)
                use minorPaint =
                    new SKPaint(Style = SKPaintStyle.Stroke,
                                Color = fgDim, StrokeWidth = 1.0f,
                                IsAntialias = false)
                use labelPaint =
                    new SKPaint(Style = SKPaintStyle.Fill,
                                Color = fg, IsAntialias = true,
                                TextSize = LabelPx)
                // Baseline: ticks grow DOWN from the bottom edge
                // so the labels sit above them inside the gutter.
                let bottom = h
                for t in ticks do
                    let x = float32 t.PxOffset + 0.5f
                    let len = if t.IsMajor then MajorTickPx else MinorTickPx
                    let paint = if t.IsMajor then majorPaint else minorPaint
                    canvas.DrawLine(x, bottom - len, x, bottom, paint)
                    if t.IsMajor then
                        let label = RulerTicks.formatLabel t.Um majorStepUm
                        // Nudge the label 2 px right of the tick
                        // so it doesn't overlap the tick line. The
                        // 11 px font + 2 px baseline-from-top
                        // keeps the label inside the 18 px gutter.
                        canvas.DrawText(label, x + 2.0f, 11.0f, labelPaint)
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
                use bgPaint = new SKPaint(Style = SKPaintStyle.Fill, Color = bg)
                canvas.DrawRect(clip, bgPaint)
                use majorPaint =
                    new SKPaint(Style = SKPaintStyle.Stroke,
                                Color = fg, StrokeWidth = 1.0f,
                                IsAntialias = false)
                use minorPaint =
                    new SKPaint(Style = SKPaintStyle.Stroke,
                                Color = fgDim, StrokeWidth = 1.0f,
                                IsAntialias = false)
                use labelPaint =
                    new SKPaint(Style = SKPaintStyle.Fill,
                                Color = fg, IsAntialias = true,
                                TextSize = LabelPx)
                let right = w
                for t in ticks do
                    let y = float32 t.PxOffset + 0.5f
                    let len = if t.IsMajor then MajorTickPx else MinorTickPx
                    let paint = if t.IsMajor then majorPaint else minorPaint
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
                        canvas.DrawText(label, 0.0f, 0.0f, labelPaint)
                        canvas.RestoreToCount savedT
                canvas.RestoreToCount saved

// ─────────────────────────────────────────────────────────────
// Controls. Both subscribe to ViewportSync on attach and
// unsubscribe on detach. Each Render reads the current snapshot,
// computes the visible µm range for its axis, builds the tick
// list, and hands off to its Skia draw op.
// ─────────────────────────────────────────────────────────────

/// Shared subscribe/unsubscribe helper so both controls have one
/// place to manage their `ViewportSync` lifetime — easier to
/// audit than two separate subscriptions and matches the symmetry
/// in their render math.
type RulerBase() =
    inherit Control()
    let mutable sub : IDisposable option = None
    /// Avalonia hands us a fresh `e` on every camera change; the
    /// only side-effect we want is to schedule a redraw, since
    /// the new state is read fresh inside `Render`.
    member private this.OnCameraChanged (_snapshot: ViewportSync.Snapshot) =
        Avalonia.Threading.Dispatcher.UIThread.Post (fun () ->
            this.InvalidateVisual())
    override this.OnAttachedToVisualTree(e) =
        base.OnAttachedToVisualTree e
        sub <- Some (ViewportSync.onChanged.Subscribe this.OnCameraChanged)
    override this.OnDetachedFromVisualTree(e) =
        sub |> Option.iter (fun s -> s.Dispose())
        sub <- None
        base.OnDetachedFromVisualTree e

type TopRulerControl() =
    inherit RulerBase()
    override this.Render(context) =
        base.Render context
        let cam = ViewportSync.current()
        let bounds = Rect(0.0, 0.0, this.Bounds.Width, this.Bounds.Height)
        // Transparent fill so Avalonia's hit-test sees the
        // bounds (and the gutter doesn't 'eat' clicks meant for
        // future guideline-drag interactions). Background fill
        // happens inside the Skia draw op.
        context.FillRectangle(Brushes.Transparent, bounds)
        if cam.PixelsPerDbu > 0.0 && bounds.Width > 0.0 then
            let startUm, endUm =
                RulerTicks.gutterRangeUm
                    cam.CenterDbuX cam.PixelsPerDbu bounds.Width cam.DbuNm
            let pxPerUm = RulerTicks.pxPerUm cam.PixelsPerDbu cam.DbuNm
            let majorStep = RulerTicks.pickMajorStepUm pxPerUm MinPxPerMajor
            let ticks = RulerTicks.ticks startUm endUm pxPerUm MinPxPerMajor
            context.Custom(new TopRulerDraw(bounds, ticks, majorStep))

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
