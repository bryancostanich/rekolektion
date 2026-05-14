module Rekolektion.Viz.Render.Skia.TightenOverlay

open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Render.Skia

/// Hit-test region for one numbered Tighten label. The canvas
/// stores these between renders so OnPointerPressed can map a
/// click back to the candidate index.
type LabelHit = {
    Index : int          // 1-based, matches the visible label
    Rect  : SKRect       // screen pixels
}

let private worldToScreen (vb: LayerPainter.ViewBox) (x: float) (y: float) : SKPoint =
    let dx = float (vb.MaxX - vb.MinX) |> max 1.0
    let dy = float (vb.MaxY - vb.MinY) |> max 1.0
    let sx = (x - float vb.MinX) / dx * float vb.PixelW
    let sy = float vb.PixelH - ((y - float vb.MinY) / dy * float vb.PixelH)
    SKPoint(float32 sx, float32 sy)

/// Endpoints of an axis-aligned arrow between two bboxes —
/// rides the perpendicular-axis overlap midpoint so the line is
/// pure horizontal / vertical between the facing edges.
let private orthEndpoints
        (dirX: int) (dirY: int)
        ((sx1, sy1, sx2, sy2): int64 * int64 * int64 * int64)
        ((ox1, oy1, ox2, oy2): int64 * int64 * int64 * int64)
        : (int64 * int64) * (int64 * int64) =
    if dirX = 1 then
        let yMid = (max sy1 oy1 + min sy2 oy2) / 2L
        (sx2, yMid), (ox1, yMid)
    elif dirX = -1 then
        let yMid = (max sy1 oy1 + min sy2 oy2) / 2L
        (sx1, yMid), (ox2, yMid)
    elif dirY = 1 then
        let xMid = (max sx1 ox1 + min sx2 ox2) / 2L
        (xMid, sy2), (xMid, oy1)
    else
        let xMid = (max sx1 ox1 + min sx2 ox2) / 2L
        (xMid, sy1), (xMid, oy2)

let private formatUm (umPerDbu: float) (dbu: int64) : string =
    sprintf "%.3f µm" (float dbu * umPerDbu)

/// Draw the numbered candidate dim arrows for Tighten mode.
/// Returns the click-hit-test rects so the canvas can map a
/// later mouse click to a candidate index.
let render
        (canvas: SKCanvas)
        (vb: LayerPainter.ViewBox)
        (umPerDbu: float)
        (candidates: Check.TightenCandidate array)
        : LabelHit array =
    if candidates.Length = 0 then [||]
    else
    use paintLine =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            // Tighten amber, slightly different shade so it doesn't
            // mistake itself for the regular dimension overlay.
            Color = SKColor(0xFFuy, 0xA0uy, 0x40uy, 0xFFuy),
            StrokeWidth = 1.5f,
            IsAntialias = true)
    use paintNumberBg =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            // Bright filled circle behind the index — clickable.
            Color = SKColor(0xFFuy, 0xA0uy, 0x40uy, 0xFFuy),
            IsAntialias = true)
    use paintNumberStroke =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            Color = SKColors.White,
            StrokeWidth = 1.0f,
            IsAntialias = true)
    use paintNumber =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColors.Black,
            IsAntialias = true,
            TextSize = 13.0f,
            FakeBoldText = true,
            TextAlign = SKTextAlign.Center)
    use paintGapLabel =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 11.0f)
    use paintGapBg =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColor(0x00uy, 0x00uy, 0x00uy, 0xC0uy),
            IsAntialias = true)
    let hits = System.Collections.Generic.List<LabelHit>()
    for idx0 in 0 .. candidates.Length - 1 do
        let c = candidates.[idx0]
        let (p1x, p1y), (p2x, p2y) =
            orthEndpoints c.DirX c.DirY c.SelBb c.OthBb
        let s1 = worldToScreen vb (float p1x) (float p1y)
        let s2 = worldToScreen vb (float p2x) (float p2y)
        // Skip degenerate (sub-pixel) segments — clicking them
        // would be impossible.
        let lenPx =
            let dx = float (s2.X - s1.X)
            let dy = float (s2.Y - s1.Y)
            sqrt (dx * dx + dy * dy)
        if lenPx >= 6.0 then
            canvas.DrawLine(s1, s2, paintLine)
            // Arrow heads.
            let head = 6.0f
            let drawHead (tip: SKPoint) (signX: float32) (signY: float32) =
                let path = new SKPath()
                if c.DirX <> 0 then
                    path.MoveTo tip
                    path.LineTo (SKPoint(tip.X + signX * head, tip.Y - head * 0.5f))
                    path.LineTo (SKPoint(tip.X + signX * head, tip.Y + head * 0.5f))
                else
                    path.MoveTo tip
                    path.LineTo (SKPoint(tip.X - head * 0.5f, tip.Y + signY * head))
                    path.LineTo (SKPoint(tip.X + head * 0.5f, tip.Y + signY * head))
                path.Close()
                canvas.DrawPath(path, paintLine)
                path.Dispose()
            if c.DirX = 1 then
                drawHead s1 1.0f 0.0f
                drawHead s2 -1.0f 0.0f
            elif c.DirX = -1 then
                drawHead s1 -1.0f 0.0f
                drawHead s2 1.0f 0.0f
            elif c.DirY = 1 then
                drawHead s1 0.0f 1.0f
                drawHead s2 0.0f -1.0f
            else
                drawHead s1 0.0f -1.0f
                drawHead s2 0.0f 1.0f
            // Numbered click target: filled circle at the
            // midpoint of the arrow.
            let mid = SKPoint((s1.X + s2.X) * 0.5f, (s1.Y + s2.Y) * 0.5f)
            let radius = 11.0f
            canvas.DrawCircle(mid.X, mid.Y, radius, paintNumberBg)
            canvas.DrawCircle(mid.X, mid.Y, radius, paintNumberStroke)
            let label = string (idx0 + 1)
            // Number is centered on the circle; +4.5 baseline
            // adjustment because Skia text origin is the
            // baseline, not the center.
            canvas.DrawText(label, mid.X, mid.Y + 4.5f, paintNumber)
            hits.Add {
                Index = idx0 + 1
                Rect = SKRect(mid.X - radius, mid.Y - radius,
                              mid.X + radius, mid.Y + radius)
            }
            // Caption: "<n>: <layer> · gap → limit µm"
            let caption =
                sprintf "%d: %s  %s → %s"
                    (idx0 + 1) c.LayerName
                    (formatUm umPerDbu c.GapDbu)
                    (formatUm umPerDbu c.LimitDbu)
            let mutable bounds = SKRect()
            paintGapLabel.MeasureText(caption, &bounds) |> ignore
            // Place caption offset from the click target so it
            // doesn't sit underneath. Pick a side based on arrow
            // axis: horizontal arrow → caption above; vertical →
            // caption to the right.
            let padX = 4.0f
            let padY = 2.0f
            let cx, cy =
                if c.DirX <> 0 then
                    mid.X, mid.Y - radius - padY * 2.0f - bounds.Height
                else
                    mid.X + radius + padX * 2.0f, mid.Y - bounds.Height * 0.5f
            let bg =
                SKRect(
                    cx - bounds.Width * 0.5f - padX,
                    cy - padY,
                    cx + bounds.Width * 0.5f + padX,
                    cy + bounds.Height + padY)
            canvas.DrawRect(bg, paintGapBg)
            canvas.DrawText(caption, cx - bounds.Width * 0.5f, cy + bounds.Height - 1.0f, paintGapLabel)
    hits.ToArray()
