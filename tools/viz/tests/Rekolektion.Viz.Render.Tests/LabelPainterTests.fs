module Rekolektion.Viz.Render.Tests.LabelPainterTests

open SkiaSharp
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Render.Skia

[<Fact>]
let ``Labels paint visible text`` () =
    let doc : Document =
        { emptyDocument with
            Cells = [
                { Name = "top"
                  Comments = []
                  Elements = [
                      LabelEl {
                          Layer = Named ("sky130", "met1")
                          Text = "BL"
                          Origin = { X = 100L; Y = 100L }
                          Class = None
                          Props = []
                          Comments = []
                      }
                  ] }
            ] }
    use surface = SKSurface.Create(SKImageInfo(200, 200))
    surface.Canvas.Clear(SKColors.Black)
    LabelPainter.paint surface.Canvas (200, 200) doc
    use img = surface.Snapshot()
    use pix = img.PeekPixels()
    let mutable found = false
    for y in 0 .. 199 do
        for x in 0 .. 199 do
            let c = pix.GetPixelColor(x, y)
            if c.Red > 200uy && c.Green > 200uy && c.Blue > 200uy then found <- true
    found |> should equal true
