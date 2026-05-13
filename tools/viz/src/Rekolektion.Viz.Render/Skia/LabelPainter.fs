module Rekolektion.Viz.Render.Skia.LabelPainter

open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types

let private layerPair (layer: Layer) : int * int =
    Rekolektion.Viz.Core.Rkt.ToGds.layerToGds layer

let private bounds (doc: Document) =
    let allPts =
        doc.Cells
        |> List.collect (fun c ->
            c.Elements
            |> List.collect (function
                | PolyEl p -> p.Points
                | PathEl p -> p.Points
                | LabelEl l -> [l.Origin]
                | RectEl r ->
                    [ { X = r.X1; Y = r.Y1 }
                      { X = r.X2; Y = r.Y2 } ]
                | _ -> []))
    match allPts with
    | [] -> 0L, 0L, 1L, 1L
    | _ ->
        let xs = allPts |> List.map (fun p -> p.X)
        let ys = allPts |> List.map (fun p -> p.Y)
        List.min xs, List.min ys, List.max xs, List.max ys

/// Paint into a caller-supplied ViewBox so labels share the same
/// world-to-screen projection as `LayerPainter.paintIn` — i.e.
/// they pan and zoom with the geometry instead of being baked
/// into the canvas-fit rectangle. `toggle` filters labels by
/// (Layer, TextType) the same way polygons are filtered, so
/// hiding e.g. li1.label in the layer panel also drops Q / WL /
/// MWL labels from the canvas.
let paintIn
        (canvas: SKCanvas)
        (vb: LayerPainter.ViewBox)
        (doc: Document)
        (toggle: Visibility.ToggleState)
        : unit =
    let dx = float (vb.MaxX - vb.MinX) |> max 1.0
    let dy = float (vb.MaxY - vb.MinY) |> max 1.0
    use normal = new SKPaint(Color = SKColors.White, IsAntialias = true, TextSize = 11.0f, IsStroke = false)
    use highlight = new SKPaint(Color = SKColor(0xffuy, 0xe0uy, 0x40uy, 0xffuy), IsAntialias = true, TextSize = 12.0f, IsStroke = false)
    use dimmed = new SKPaint(Color = SKColor(0xffuy, 0xffuy, 0xffuy, 0x40uy), IsAntialias = true, TextSize = 11.0f, IsStroke = false)
    for c in doc.Cells do
        for el in c.Elements do
            match el with
            | LabelEl l ->
                let key = layerPair l.Layer
                if Visibility.isLayerVisible toggle key then
                    let x = float (l.Origin.X - vb.MinX) / dx * float vb.PixelW
                    let y = float vb.PixelH - (float (l.Origin.Y - vb.MinY) / dy * float vb.PixelH)
                    let p =
                        match toggle.HighlightNet with
                        | Some name when name = l.Text -> highlight
                        | Some _ -> dimmed
                        | None -> normal
                    canvas.DrawText(l.Text, float32 x, float32 y, p)
            | _ -> ()

/// Auto-fit variant: ViewBox derived from polygon + label bbox.
/// Kept for callers that paint a one-off canvas-fit rendering
/// (e.g. the headless render CLI).
let paint (canvas: SKCanvas) (size: int * int) (doc: Document) : unit =
    let (w, h) = size
    let (xmin, ymin, xmax, ymax) = bounds doc
    let vb : LayerPainter.ViewBox = {
        MinX = xmin; MinY = ymin
        MaxX = xmax; MaxY = ymax
        PixelW = w;  PixelH = h
    }
    paintIn canvas vb doc Visibility.empty
