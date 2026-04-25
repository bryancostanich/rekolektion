module Rekolektion.Viz.Render.Skia.LayerPainter

open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Render.Color

type ViewBox = {
    MinX: int64; MinY: int64
    MaxX: int64; MaxY: int64
    PixelW: int; PixelH: int
}

let private boundsOf (lib: Library) : (int64 * int64 * int64 * int64) =
    let allPts =
        lib.Structures
        |> List.collect (fun s ->
            s.Elements
            |> List.collect (function
                | Boundary b -> b.Points
                | Path p -> p.Points
                | _ -> []))
    match allPts with
    | [] -> (0L, 0L, 1L, 1L)
    | _ ->
        let xs = allPts |> List.map (fun p -> p.X)
        let ys = allPts |> List.map (fun p -> p.Y)
        List.min xs, List.min ys, List.max xs, List.max ys

let private project (vb: ViewBox) (p: Point) : SKPoint =
    let dx = float (vb.MaxX - vb.MinX) |> max 1.0
    let dy = float (vb.MaxY - vb.MinY) |> max 1.0
    let x = float (p.X - vb.MinX) / dx * float vb.PixelW
    let y = float vb.PixelH - (float (p.Y - vb.MinY) / dy * float vb.PixelH)
    SKPoint(float32 x, float32 y)

/// Paint every boundary in the library, layer-ordered by stack Z so
/// upper metal sits on top of lower metal. Honors ToggleState.Layers.
/// Net-aware dimming is handled in the App layer (which annotates
/// each polygon with its net before calling); for now this is a
/// layer-only painter.
let paint (canvas: SKCanvas) (size: int * int) (lib: Library) (toggle: Visibility.ToggleState) : unit =
    let (w, h) = size
    let (xmin, ymin, xmax, ymax) = boundsOf lib
    let vb = { MinX = xmin; MinY = ymin; MaxX = xmax; MaxY = ymax; PixelW = w; PixelH = h }

    let byLayer =
        lib.Structures
        |> List.collect (fun s ->
            s.Elements
            |> List.choose (function Boundary b -> Some b | _ -> None))
        |> List.groupBy (fun b -> b.Layer, b.DataType)

    let zOf (key: int * int) =
        Layout.Layer.bySky130Number (fst key) (snd key)
        |> Option.map (fun l -> l.StackZ)
        |> Option.defaultValue 100.0
    let ordered = byLayer |> List.sortBy (fun (k, _) -> zOf k)

    use fill = new SKPaint(Style = SKPaintStyle.Fill, IsAntialias = true)
    use stroke = new SKPaint(Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 0.5f)

    for (key, boundaries) in ordered do
        if Visibility.isLayerVisible toggle key then
            match Layout.Layer.bySky130Number (fst key) (snd key) with
            | None -> ()  // unknown layer — skip
            | Some layer ->
                fill.Color <- SkyTheme.fillFor layer.Name
                stroke.Color <- SkyTheme.strokeFor layer.Name
                for b in boundaries do
                    use path = new SKPath()
                    match b.Points with
                    | [] -> ()
                    | first :: rest ->
                        path.MoveTo(project vb first)
                        for pt in rest do path.LineTo(project vb pt)
                        path.Close()
                        canvas.DrawPath(path, fill)
                        canvas.DrawPath(path, stroke)
