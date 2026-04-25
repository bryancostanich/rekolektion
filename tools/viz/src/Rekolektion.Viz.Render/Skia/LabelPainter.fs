module Rekolektion.Viz.Render.Skia.LabelPainter

open SkiaSharp
open Rekolektion.Viz.Core.Gds.Types

let private bounds (lib: Library) =
    let allPts =
        lib.Structures
        |> List.collect (fun s ->
            s.Elements |> List.collect (function
                | Boundary b -> b.Points
                | Path p -> p.Points
                | Text t -> [t.Origin]
                | _ -> []))
    match allPts with
    | [] -> 0L, 0L, 1L, 1L
    | _ ->
        let xs = allPts |> List.map (fun p -> p.X)
        let ys = allPts |> List.map (fun p -> p.Y)
        List.min xs, List.min ys, List.max xs, List.max ys

let paint (canvas: SKCanvas) (size: int * int) (lib: Library) : unit =
    let (w, h) = size
    let (xmin, ymin, xmax, ymax) = bounds lib
    let dx = float (xmax - xmin) |> max 1.0
    let dy = float (ymax - ymin) |> max 1.0
    use paint = new SKPaint(Color = SKColors.White, IsAntialias = true, TextSize = 11.0f, IsStroke = false)
    for s in lib.Structures do
        for el in s.Elements do
            match el with
            | Text t ->
                let x = float (t.Origin.X - xmin) / dx * float w
                let y = float h - (float (t.Origin.Y - ymin) / dy * float h)
                canvas.DrawText(t.Text, float32 x, float32 y, paint)
            | _ -> ()
