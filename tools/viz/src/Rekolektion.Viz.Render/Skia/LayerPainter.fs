module Rekolektion.Viz.Render.Skia.LayerPainter

open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Render.Color

type ViewBox = {
    MinX: int64; MinY: int64
    MaxX: int64; MaxY: int64
    PixelW: int; PixelH: int
}

let private boundsOfFlat (polys: FlatPolygon array) : (int64 * int64 * int64 * int64) =
    if polys.Length = 0 then (0L, 0L, 1L, 1L)
    else
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for p in polys do
            for pt in p.Points do
                if pt.X < xMin then xMin <- pt.X
                if pt.X > xMax then xMax <- pt.X
                if pt.Y < yMin then yMin <- pt.Y
                if pt.Y > yMax then yMax <- pt.Y
        if xMin > xMax then (0L, 0L, 1L, 1L)
        else (xMin, yMin, xMax, yMax)

let private project (vb: ViewBox) (p: Point) : SKPoint =
    let dx = float (vb.MaxX - vb.MinX) |> max 1.0
    let dy = float (vb.MaxY - vb.MinY) |> max 1.0
    let x = float (p.X - vb.MinX) / dx * float vb.PixelW
    let y = float vb.PixelH - (float (p.Y - vb.MinY) / dy * float vb.PixelH)
    SKPoint(float32 x, float32 y)

/// Paint every flattened polygon, layer-ordered by stack Z so upper
/// metal sits on top of lower metal. Honors ToggleState.Layers.
/// Iterates `flat` (post-hierarchy expansion), so a hierarchical
/// macro renders its full content (e.g. SRAM bitcell array) instead
/// of just the top cell's polygons.
///
/// `vb` defines the world-coordinate window that maps to the canvas
/// pixel rectangle. Callers compute `vb` from current pan + zoom
/// state and pass it in; for auto-fit, use `paint` (no `_vb` arg).
let paintIn (canvas: SKCanvas) (vb: ViewBox) (flat: FlatPolygon array) (toggle: Visibility.ToggleState) : unit =
    // Group polys by layer key for layer-ordered draw. Faster to
    // group once than to sort each polygon's draw call.
    let byLayer =
        flat
        |> Array.groupBy (fun p -> p.Layer, p.DataType)

    let zOf (key: int * int) =
        Layout.Layer.bySky130Number (fst key) (snd key)
        |> Option.map (fun l -> l.StackZ)
        |> Option.defaultValue 100.0
    let ordered = byLayer |> Array.sortBy (fun (k, _) -> zOf k)

    use fill = new SKPaint(Style = SKPaintStyle.Fill, IsAntialias = true)
    use stroke = new SKPaint(Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 0.5f)

    for (key, polys) in ordered do
        if Visibility.isLayerVisible toggle key then
            match Layout.Layer.bySky130Number (fst key) (snd key) with
            | None -> ()
            | Some layer ->
                fill.Color <- SkyTheme.fillFor layer.Name
                stroke.Color <- SkyTheme.strokeFor layer.Name
                for poly in polys do
                    if poly.Points.Length >= 3 then
                        use path = new SKPath()
                        path.MoveTo(project vb poly.Points.[0])
                        for i in 1 .. poly.Points.Length - 1 do
                            path.LineTo(project vb poly.Points.[i])
                        path.Close()
                        canvas.DrawPath(path, fill)
                        canvas.DrawPath(path, stroke)

/// Auto-fit variant: ViewBox derived from polygon bbox.
let paint (canvas: SKCanvas) (size: int * int) (flat: FlatPolygon array) (toggle: Visibility.ToggleState) : unit =
    let (w, h) = size
    let (xmin, ymin, xmax, ymax) = boundsOfFlat flat
    let vb = { MinX = xmin; MinY = ymin; MaxX = xmax; MaxY = ymax; PixelW = w; PixelH = h }
    paintIn canvas vb flat toggle

/// Compute the bbox of the flat polygons in world DBU coordinates.
let bboxOf (flat: FlatPolygon array) : (int64 * int64 * int64 * int64) =
    boundsOfFlat flat
