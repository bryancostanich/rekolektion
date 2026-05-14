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
let render
        (canvas: SKCanvas)
        (vb: LayerPainter.ViewBox)
        (umPerDbu: float)
        (violations: Check.Violation array) =
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
    for v in violations do
        let rA = bboxToSkRect vb v.BboxA
        canvas.DrawRect(rA, stroke)
        match v.BboxB with
        | None -> ()
        | Some bb ->
            let rB = bboxToSkRect vb bb
            canvas.DrawRect(rB, stroke)
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
        // Label sits above the first bbox.
        let measuredUm = float v.MeasuredDbu * umPerDbu
        let limitUm = float v.LimitDbu * umPerDbu
        // ASCII "um" — Skia default typeface lacks U+00B5.
        let label = sprintf "%s: %.3f<%.3f um" v.Rule measuredUm limitUm
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
