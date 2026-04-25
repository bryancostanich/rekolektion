module Rekolektion.Viz.Render.Tests.LabelPainterTests

open SkiaSharp
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Render.Skia

[<Fact>]
let ``Labels paint visible text`` () =
    let lib =
        { Name = "x"; UserUnitsPerDbUnit = 0.001; DbUnitsInMeters = 1e-9
          Structures = [{
            Name = "top"
            Elements = [
                Text { Layer = 68; TextType = 5; Origin = { X = 100L; Y = 100L }; Text = "BL" }
            ]
          }] }
    use surface = SKSurface.Create(SKImageInfo(200, 200))
    surface.Canvas.Clear(SKColors.Black)
    LabelPainter.paint surface.Canvas (200, 200) lib
    use img = surface.Snapshot()
    use pix = img.PeekPixels()
    let mutable found = false
    for y in 0 .. 199 do
        for x in 0 .. 199 do
            let c = pix.GetPixelColor(x, y)
            if c.Red > 200uy && c.Green > 200uy && c.Blue > 200uy then found <- true
    found |> should equal true
