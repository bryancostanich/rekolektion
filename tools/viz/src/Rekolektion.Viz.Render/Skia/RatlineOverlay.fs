module Rekolektion.Viz.Render.Skia.RatlineOverlay

open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Net
open Rekolektion.Viz.Render.Skia

let private worldToScreen (vb: LayerPainter.ViewBox) (x: float) (y: float) : SKPoint =
    let dx = float (vb.MaxX - vb.MinX) |> max 1.0
    let dy = float (vb.MaxY - vb.MinY) |> max 1.0
    let sx = (x - float vb.MinX) / dx * float vb.PixelW
    let sy = float vb.PixelH - ((y - float vb.MinY) / dy * float vb.PixelH)
    SKPoint(float32 sx, float32 sy)

/// Draw a single net's pairwise pin-to-pin ratlines, plus a
/// label-bearing endpoint mark. Pairwise (n choose 2) is fine for
/// the typical signal nets — a few hops between instances. For
/// power rails with many fans-out the caller should either skip
/// or pre-cluster.
let private drawRoute
        (canvas: SKCanvas)
        (vb: LayerPainter.ViewBox)
        (paintLine: SKPaint)
        (paintNode: SKPaint)
        (paintText: SKPaint)
        (paintTextBg: SKPaint)
        (route: Ratlines.NetRoute) =
    let pts =
        route.Pins
        |> Array.map (fun p ->
            worldToScreen vb (float p.Position.X) (float p.Position.Y))
    // Pairwise line: connect every pair of endpoints. For 2 pins
    // this is 1 line; for n pins it's n(n-1)/2. Capped lightly
    // by relying on the caller to filter mega-fanout nets.
    for i in 0 .. pts.Length - 1 do
        for j in i + 1 .. pts.Length - 1 do
            canvas.DrawLine(pts.[i], pts.[j], paintLine)
    // Pin marker: small filled circle so the user sees the
    // endpoint as well as the line.
    for p in pts do
        canvas.DrawCircle(p.X, p.Y, 3.0f, paintNode)
    // Label the first endpoint with the net name so dense
    // overlays stay readable.
    if pts.Length > 0 then
        let mutable bounds = SKRect()
        paintText.MeasureText(route.Name, &bounds) |> ignore
        let p0 = pts.[0]
        let lx = p0.X + 6.0f
        let ly = p0.Y - 4.0f
        let padX = 3.0f
        let padY = 1.0f
        let bg =
            SKRect(
                lx - padX,
                ly - bounds.Height - padY,
                lx + bounds.Width + padX,
                ly + padY)
        canvas.DrawRect(bg, paintTextBg)
        canvas.DrawText(route.Name, lx, ly, paintText)

/// `visibleNets` is the explicit set of net names whose ratlines
/// should render. The renderer no longer cares about poly-highlight
/// state — ratline visibility is decoupled from it. Empty set =
/// nothing drawn (early return). `lib.UserUnitsPerDbUnit` isn't
/// needed here (we render in pixel/world space directly).
let render
        (canvas: SKCanvas)
        (vb: LayerPainter.ViewBox)
        (routes: Ratlines.NetRoute array)
        (visibleNets: Set<string>) =
    if routes.Length = 0 || visibleNets.IsEmpty then () else
    let visible =
        routes |> Array.filter (fun r -> visibleNets.Contains r.Name)
    if visible.Length = 0 then () else
    use paintLine =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            Color = SKColor(0xFFuy, 0xC8uy, 0x40uy, 0xE0uy),  // amber
            StrokeWidth = 1.0f,
            IsAntialias = true)
    use paintNode =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColor(0xFFuy, 0xE8uy, 0x80uy, 0xFFuy),
            IsAntialias = true)
    use paintText =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 11.0f)
    use paintTextBg =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColor(0x00uy, 0x00uy, 0x00uy, 0xB0uy),
            IsAntialias = true)
    for r in visible do
        drawRoute canvas vb paintLine paintNode paintText paintTextBg r
