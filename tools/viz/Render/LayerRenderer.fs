/// GDS to per-layer PNG renderer using SkiaSharp.
/// Replicates the exact behavior of Python render_cell.py for validation.
module Viz.Render.LayerRenderer

open System.IO
open SkiaSharp
open Viz.Gds.Types
open Viz.Render.LayerMap

/// Convert layout coordinate (nm) to pixel coordinate.
/// Y is flipped (layout Y-up → image Y-down).
let private toPx
    (originX: int) (originY: int)
    (imgH: int) (scale: float) (margin: int)
    (x: int) (y: int) : SKPoint =
    let px = float32 (float (x - originX) * scale) + float32 margin
    let py = float32 imgH - (float32 (float (y - originY) * scale) + float32 margin)
    SKPoint(px, py)

/// Group boundaries by (layer, datatype).
let private groupByLayer (elements: GdsElement list) : Map<(int * int), GdsBoundary list> =
    elements
    |> List.choose (fun e ->
        match e with
        | Boundary b -> Some b
        | _ -> None)
    |> List.groupBy (fun b -> (b.Layer, b.Datatype))
    |> Map.ofList

/// Draw a polygon (list of GdsPoints) onto a canvas.
let private drawPolygon
    (canvas: SKCanvas) (fillPaint: SKPaint) (outlinePaint: SKPaint)
    (toPixel: int -> int -> SKPoint) (points: GdsPoint list) =
    if points.Length >= 3 then
        use path = new SKPath()
        let first = toPixel (int points.[0].X) (int points.[0].Y)
        path.MoveTo(first)
        for i in 1 .. points.Length - 1 do
            let pt = toPixel (int points.[i].X) (int points.[i].Y)
            path.LineTo(pt)
        path.Close()
        canvas.DrawPath(path, fillPaint)
        canvas.DrawPath(path, outlinePaint)

/// Create an SKColor from our Color type.
let private toSkColor (c: Color) = SKColor(c.R, c.G, c.B, c.A)

/// Render per-layer PNGs and a composite, matching Python render_cell.py behavior.
let render (gdsPath: string) (outputDir: string) (scale: float) : unit =
    let lib = Viz.Gds.Reader.readGds gdsPath

    // Use the last structure (top-level cell is typically last in GDS)
    let cell =
        match lib.Structures with
        | [] -> failwith "No structures in GDS file"
        | structs -> structs |> List.last

    printfn "Rendering: %s" cell.Name

    let layerGroups = groupByLayer cell.Elements

    // Compute bounding box from all boundary points
    let allPoints =
        cell.Elements
        |> List.collect (fun e ->
            match e with
            | Boundary b -> b.Points
            | Path p -> p.Points
            | _ -> [])

    if allPoints.IsEmpty then
        printfn "No geometry to render"
    else

    let minX = allPoints |> List.map (fun p -> int p.X) |> List.min
    let maxX = allPoints |> List.map (fun p -> int p.X) |> List.max
    let minY = allPoints |> List.map (fun p -> int p.Y) |> List.min
    let maxY = allPoints |> List.map (fun p -> int p.Y) |> List.max

    let w = float (maxX - minX)
    let h = float (maxY - minY)

    // Scale: pixels per nm. Python uses "scale" as pixels per um.
    // With nm coordinates, we need scale/1000.
    let pxPerNm = scale / 1000.0
    let margin = 20

    let imgW = int (w * pxPerNm) + 2 * margin
    let imgH = int (h * pxPerNm) + 2 * margin

    let toPixel = toPx minX minY imgH pxPerNm margin

    Directory.CreateDirectory(outputDir) |> ignore

    let mutable renderCount = 0

    // Render each layer individually
    for layerKey in renderOrder do
        match layerGroups |> Map.tryFind layerKey with
        | None -> ()
        | Some boundaries ->
            match sky130Layers |> Map.tryFind layerKey with
            | None -> ()
            | Some style ->
                let info = new SKImageInfo(imgW, imgH, SKColorType.Rgba8888, SKAlphaType.Premul)
                use surface = SKSurface.Create(info)
                let canvas = surface.Canvas
                canvas.Clear(SKColor(0uy, 0uy, 0uy, 255uy))

                // Draw cell boundary outline
                use boundaryPaint = new SKPaint(
                    Style = SKPaintStyle.Stroke,
                    Color = SKColor(60uy, 60uy, 60uy, 128uy),
                    StrokeWidth = 1.0f,
                    IsAntialias = true)
                let c1 = toPixel minX minY
                let c2 = toPixel maxX minY
                let c3 = toPixel maxX maxY
                let c4 = toPixel minX maxY
                use cellPath = new SKPath()
                cellPath.MoveTo(c1)
                cellPath.LineTo(c2)
                cellPath.LineTo(c3)
                cellPath.LineTo(c4)
                cellPath.Close()
                canvas.DrawPath(cellPath, boundaryPaint)

                // Draw layer polygons
                let color = toSkColor style.Color
                use fillPaint = new SKPaint(
                    Style = SKPaintStyle.Fill,
                    Color = color,
                    IsAntialias = true)
                use outlinePaint = new SKPaint(
                    Style = SKPaintStyle.Stroke,
                    Color = SKColor(255uy, 255uy, 255uy, 100uy),
                    StrokeWidth = 1.0f,
                    IsAntialias = true)

                for b in boundaries do
                    drawPolygon canvas fillPaint outlinePaint toPixel b.Points

                // Save PNG
                use image = surface.Snapshot()
                use data = image.Encode(SKEncodedImageFormat.Png, 100)
                let path = Path.Combine(outputDir, $"layer_{style.Name}.png")
                use file = File.Create(path)
                data.SaveTo(file)
                renderCount <- renderCount + 1
                printfn "  %s: %d polygons" style.Name boundaries.Length

    // Composite: all layers overlaid with alpha compositing
    let compositeInfo = new SKImageInfo(imgW, imgH, SKColorType.Rgba8888, SKAlphaType.Premul)
    use compositeSurface = SKSurface.Create(compositeInfo)
    let compositeCanvas = compositeSurface.Canvas
    compositeCanvas.Clear(SKColor(20uy, 20uy, 30uy, 255uy))

    // Cell boundary
    use boundaryPaint = new SKPaint(
        Style = SKPaintStyle.Stroke,
        Color = SKColor(80uy, 80uy, 80uy, 128uy),
        StrokeWidth = 1.0f,
        IsAntialias = true)
    let c1 = toPixel minX minY
    let c2 = toPixel maxX minY
    let c3 = toPixel maxX maxY
    let c4 = toPixel minX maxY
    use cellPath = new SKPath()
    cellPath.MoveTo(c1)
    cellPath.LineTo(c2)
    cellPath.LineTo(c3)
    cellPath.LineTo(c4)
    cellPath.Close()
    compositeCanvas.DrawPath(cellPath, boundaryPaint)

    // Draw each layer
    for layerKey in renderOrder do
        match layerGroups |> Map.tryFind layerKey with
        | None -> ()
        | Some boundaries ->
            match sky130Layers |> Map.tryFind layerKey with
            | None -> ()
            | Some style ->
                let color = toSkColor style.Color
                let outlineAlpha = min 255 (int style.Color.A + 40)
                use fillPaint = new SKPaint(
                    Style = SKPaintStyle.Fill,
                    Color = color,
                    IsAntialias = true)
                use outlinePaint = new SKPaint(
                    Style = SKPaintStyle.Stroke,
                    Color = SKColor(style.Color.R, style.Color.G, style.Color.B, byte outlineAlpha),
                    StrokeWidth = 1.0f,
                    IsAntialias = true)
                for b in boundaries do
                    drawPolygon compositeCanvas fillPaint outlinePaint toPixel b.Points

    use compositeImage = compositeSurface.Snapshot()
    use compositeData = compositeImage.Encode(SKEncodedImageFormat.Png, 100)
    let compositePath = Path.Combine(outputDir, "composite.png")
    use compositeFile = File.Create(compositePath)
    compositeData.SaveTo(compositeFile)
    renderCount <- renderCount + 1

    printfn "Generated %d images in %s" renderCount outputDir
