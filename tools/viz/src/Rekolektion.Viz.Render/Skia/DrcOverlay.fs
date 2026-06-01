module Rekolektion.Viz.Render.Skia.DrcOverlay

open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Render.Skia

let private worldToScreen (vb: LayerPainter.ViewBox) (x: float) (y: float) : SKPoint =
    let dx = float (vb.MaxX - vb.MinX) |> max 1.0
    let dy = float (vb.MaxY - vb.MinY) |> max 1.0
    let sx = (x - float vb.MinX) / dx * float vb.PixelW
    let sy = float vb.PixelH - ((y - float vb.MinY) / dy * float vb.PixelH)
    SKPoint(float32 sx, float32 sy)

let private bboxToSkRect (vb: LayerPainter.ViewBox)
                        ((x1, y1, x2, y2): int64 * int64 * int64 * int64)
                        : SKRect =
    let p1 = worldToScreen vb (float x1) (float y1)
    let p2 = worldToScreen vb (float x2) (float y2)
    SKRect(min p1.X p2.X, min p1.Y p2.Y, max p1.X p2.X, max p1.Y p2.Y)

/// Build the label string shown above a violation's bbox. Pure
/// so unit tests can pin the format without a render pipeline.
/// `provenance` is the `RulesetView.Provenance` map; when an
/// entry exists for `v.Rule`, the rule name is annotated with the
/// source file's basename: `met2.2 (overrides/v1.yaml)`.
let formatLabel
        (provenance: Map<string, string>)
        (umPerDbu: float)
        (v: Check.Violation) : string =
    let measuredUm = float v.MeasuredDbu * umPerDbu
    let limitUm = float v.LimitDbu * umPerDbu
    let ruleLabel =
        match Map.tryFind v.Rule provenance with
        | Some src when not (System.String.IsNullOrEmpty src) ->
            sprintf "%s (%s)" v.Rule (System.IO.Path.GetFileName src)
        | _ -> v.Rule
    sprintf "%s: %.3f<%.3f um" ruleLabel measuredUm limitUm

type private Side = | Right | Left | Top | Bottom

let private classifySide
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : Side option =
    let yOverlap = (min ay2 by2) > (max ay1 by1)
    let xOverlap = (min ax2 bx2) > (max ax1 bx1)
    if yOverlap && bx1 >= ax2 then Some Right
    elif yOverlap && bx2 <= ax1 then Some Left
    elif xOverlap && by1 >= ay2 then Some Top
    elif xOverlap && by2 <= ay1 then Some Bottom
    else None

/// Endpoints of an axis-aligned connector line between two bboxes
/// — only valid when the pair is orthogonally facing. The line
/// rides the perpendicular-axis overlap midpoint, so it always
/// reads as a pure horizontal or vertical segment between the
/// nearest edges (same convention the dimension overlay uses).
let private orthEndpoints
        (side: Side)
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : (int64 * int64) * (int64 * int64) =
    match side with
    | Right ->
        let yMid = (max ay1 by1 + min ay2 by2) / 2L
        (ax2, yMid), (bx1, yMid)
    | Left ->
        let yMid = (max ay1 by1 + min ay2 by2) / 2L
        (ax1, yMid), (bx2, yMid)
    | Top ->
        let xMid = (max ax1 bx1 + min ax2 bx2) / 2L
        (xMid, ay2), (xMid, by1)
    | Bottom ->
        let xMid = (max ax1 bx1 + min ax2 bx2) / 2L
        (xMid, ay1), (xMid, by2)

/// Paint every violation as a red outline with a small label
/// showing the rule name and measured/limit gap. Spacing
/// violations connect their two bboxes with a red line so the
/// user sees which pair triggered.
///
/// `provenance` (ADR-0004) lets the label include the source
/// file the rule came from, e.g.
/// `"met2.2 (overrides/v1.yaml): 0.139<0.140 um"`. Pass
/// `Map.empty` when there's no view-derived provenance (legacy
/// callers / `Rules.defaultView`).
///
/// `showLabels = false` paints the violation outlines + connector
/// lines but skips the per-violation rule-name / measurement
/// tooltip text. Useful on dense macros where the label boxes
/// stack into an unreadable wall.
///
/// Hit-test publication. The renderer pushes one entry into
/// `hitsOut` per violation describing the screen-pixel regions
/// the user can click to select it: the outline bbox(es) +
/// the label box (when shown). The canvas reads these on
/// PointerPressed to map a click to the underlying Violation
/// without re-running geometry.

/// Screen-pixel hit region for a clickable DRC violation
/// element (label box or outline bbox).
type DrcHit = {
    /// Index into the `violations` array passed to `render`.
    /// The canvas uses this to resolve the actual Violation
    /// record from its current overlay snapshot.
    Index : int
    /// Screen-space rect in pixels (origin top-left).
    Rect  : SKRect
    /// "label" for the tooltip-text box, "bbox" for either
    /// outline rect. Diagnostic / ordering hint; the canvas
    /// doesn't need to interpret it.
    Kind  : string
}

let render
        (canvas: SKCanvas)
        (vb: LayerPainter.ViewBox)
        (umPerDbu: float)
        (provenance: Map<string, string>)
        (showLabels: bool)
        (hitsOut: System.Collections.Generic.List<DrcHit>)
        (violations: Check.Violation array) =
    hitsOut.Clear()
    if violations.Length = 0 then () else
    use stroke =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            Color = SKColor(0xFFuy, 0x40uy, 0x40uy, 0xFFuy),
            StrokeWidth = 2.0f,
            IsAntialias = true)
    use connector =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            Color = SKColor(0xFFuy, 0x40uy, 0x40uy, 0xC0uy),
            StrokeWidth = 1.5f,
            IsAntialias = true,
            PathEffect =
                SKPathEffect.CreateDash([| 4.0f; 3.0f |], 0.0f))
    use textBg =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColor(0xFFuy, 0x20uy, 0x20uy, 0xC0uy),
            IsAntialias = true)
    use text =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 11.0f)
    for i in 0 .. violations.Length - 1 do
        let v = violations.[i]
        let rA = bboxToSkRect vb v.BboxA
        canvas.DrawRect(rA, stroke)
        hitsOut.Add { Index = i; Rect = rA; Kind = "bbox" }
        match v.BboxB with
        | None -> ()
        | Some bb ->
            let rB = bboxToSkRect vb bb
            canvas.DrawRect(rB, stroke)
            hitsOut.Add { Index = i; Rect = rB; Kind = "bbox" }
            // Connector follows the same orthogonal nearest-edge
            // path the dimension overlay uses — pure horizontal or
            // vertical between the facing edges, no diagonal
            // center-to-center lines.
            match classifySide v.BboxA bb with
            | Some side ->
                let (p1x, p1y), (p2x, p2y) = orthEndpoints side v.BboxA bb
                let s1 = worldToScreen vb (float p1x) (float p1y)
                let s2 = worldToScreen vb (float p2x) (float p2y)
                canvas.DrawLine(s1, s2, connector)
            | None ->
                // Should not happen — checkInterInstance only
                // emits orthogonally-facing pairs — but degrade
                // gracefully without a diagonal scribble.
                ()
        // Label sits above the first bbox. Skipped wholesale when
        // showLabels is off — the user wants the violation
        // highlights without the tooltip-text noise.
        if showLabels then
            let label = formatLabel provenance umPerDbu v
            let mutable bounds = SKRect()
            text.MeasureText(label, &bounds) |> ignore
            let padX = 4.0f
            let padY = 2.0f
            let lx = rA.Left
            let ly = rA.Top - padY * 2.0f - bounds.Height
            let bgRect =
                SKRect(
                    lx - padX,
                    ly - padY,
                    lx + bounds.Width + padX,
                    ly + bounds.Height + padY)
            canvas.DrawRect(bgRect, textBg)
            canvas.DrawText(label, lx, ly + bounds.Height - 1.0f, text)
            hitsOut.Add { Index = i; Rect = bgRect; Kind = "label" }
