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
    // ASCII "um" — Skia's default SKPaint typeface doesn't carry
    // the MICRO SIGN (U+00B5) glyph and renders tofu.
    sprintf "%.3f um" (float dbu * umPerDbu)

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
    // Two colour schemes: amber for tighten (snuggle-up move),
    // cyan for loosen (move AWAY from a pre-existing violation
    // to fix it). The colour also drives the slot number's
    // circle so the visual answer to "what does clicking this do"
    // is the same wherever the user looks.
    let tightenColor = SKColor(0xFFuy, 0xA0uy, 0x40uy, 0xFFuy)
    let loosenColor  = SKColor(0x40uy, 0xC0uy, 0xFFuy, 0xFFuy)
    use paintLineTighten =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            Color = tightenColor,
            StrokeWidth = 1.5f,
            IsAntialias = true)
    use paintLineLoosen =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            Color = loosenColor,
            StrokeWidth = 1.5f,
            IsAntialias = true)
    use paintNumberBgTighten =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = tightenColor,
            IsAntialias = true)
    use paintNumberBgLoosen =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = loosenColor,
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
        let paintLine = if c.IsLoosen then paintLineLoosen else paintLineTighten
        let paintNumberBg = if c.IsLoosen then paintNumberBgLoosen else paintNumberBgTighten
        // Endpoint geometry:
        //   Tighten: arrow spans the gap between sel's facing
        //     edge and oth's facing edge — exactly what the move
        //     is closing.
        //   Loosen: oth is on the OPPOSITE of the move
        //     direction, so spanning to oth would point the wrong
        //     way. Instead draw the arrow ON THE MOVE PATH: from
        //     sel's leading edge in the move direction to where
        //     it lands after the SlackDbu-sized move.
        let (p1x, p1y), (p2x, p2y) =
            if c.IsLoosen then
                // Concern side = OPPOSITE of move direction.
                // Place the arrow on the sel edge that's facing
                // the violation (where the user expects to "see"
                // the problem) and have it run in the move
                // direction by SlackDbu.
                let (sx1, sy1, sx2, sy2) = c.SelBb
                let yMid = (sy1 + sy2) / 2L
                let xMid = (sx1 + sx2) / 2L
                if c.DirX = 1 then (sx1, yMid), (sx1 + c.SlackDbu, yMid)
                elif c.DirX = -1 then (sx2, yMid), (sx2 - c.SlackDbu, yMid)
                elif c.DirY = 1 then (xMid, sy1), (xMid, sy1 + c.SlackDbu)
                else (xMid, sy2), (xMid, sy2 - c.SlackDbu)
            else
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
            if c.IsLoosen then
                // One-headed arrow at the move-destination end,
                // head pointing in the move direction. signY is
                // inverted relative to "up world is up screen"
                // because drawHead's signY=+1 places the tail
                // BELOW the tip in screen coords (→ head points
                // up).
                if c.DirX = 1 then drawHead s2 -1.0f 0.0f
                elif c.DirX = -1 then drawHead s2 1.0f 0.0f
                elif c.DirY = 1 then drawHead s2 0.0f 1.0f
                else drawHead s2 0.0f -1.0f
            elif c.DirX = 1 then
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
            let label = string c.Slot
            // Number is centered on the circle; +4.5 baseline
            // adjustment because Skia text origin is the
            // baseline, not the center.
            canvas.DrawText(label, mid.X, mid.Y + 4.5f, paintNumber)
            hits.Add {
                Index = c.Slot
                Rect = SKRect(mid.X - radius, mid.Y - radius,
                              mid.X + radius, mid.Y + radius)
            }
            // Caption: tighten shows the closing direction
            // ("gap -> limit"), loosen shows the recovery move
            // ("gap -> limit, fix"). ASCII "->" rather than "→"
            // for the Skia glyph-availability reason "um" vs
            // "µm".
            let caption =
                if c.IsLoosen then
                    sprintf "%d: %s  fix  %s -> %s"
                        c.Slot c.LayerName
                        (formatUm umPerDbu c.GapDbu)
                        (formatUm umPerDbu c.LimitDbu)
                else
                    sprintf "%d: %s  %s -> %s"
                        c.Slot c.LayerName
                        (formatUm umPerDbu c.GapDbu)
                        (formatUm umPerDbu c.LimitDbu)
            let mutable bounds = SKRect()
            paintGapLabel.MeasureText(caption, &bounds) |> ignore
            // Place caption offset from the click target so it
            // doesn't sit underneath. Pick a side based on arrow
            // axis: horizontal arrow → caption above (centered on
            // the circle); vertical arrow → caption to the right
            // (left-aligned just past the circle). `txtX, txtY` is
            // always the caption's TOP-LEFT in screen pixels;
            // bg + DrawText derive from there so wide captions
            // don't slide back over the circle.
            let padX = 4.0f
            let padY = 2.0f
            let txtX, txtY =
                if c.DirX <> 0 then
                    // Horizontal arrow: caption sits above the
                    // circle, centered on its X.
                    mid.X - bounds.Width * 0.5f,
                    mid.Y - radius - padY * 2.0f - bounds.Height
                else
                    // Vertical arrow: caption sits to the right of
                    // the circle, baseline aligned with the circle
                    // center.
                    mid.X + radius + padX * 2.0f,
                    mid.Y - bounds.Height * 0.5f
            let bg =
                SKRect(
                    txtX - padX,
                    txtY - padY,
                    txtX + bounds.Width + padX,
                    txtY + bounds.Height + padY)
            canvas.DrawRect(bg, paintGapBg)
            canvas.DrawText(caption, txtX, txtY + bounds.Height - 1.0f, paintGapLabel)
    hits.ToArray()
