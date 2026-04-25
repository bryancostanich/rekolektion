module Rekolektion.Viz.Render.Tests.LayerPainterTests

open SkiaSharp
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core
open Rekolektion.Viz.Render.Skia

let private rect x y w h = [ {X=x;Y=y}; {X=x+w;Y=y}; {X=x+w;Y=y+h}; {X=x;Y=y+h}; {X=x;Y=y} ]

let private singleBoundaryLib (layer: int) (datatype: int) =
    { Name = "x"; UserUnitsPerDbUnit = 0.001; DbUnitsInMeters = 1e-9
      Structures = [{
        Name = "top"
        Elements = [
          Boundary { Layer = layer; DataType = datatype; Points = rect 0L 0L 1000L 1000L }
        ]
      }] }

[<Fact>]
let ``Paint a single met2 polygon and check non-empty pixels`` () =
    let lib = singleBoundaryLib 69 20  // met2
    use surface = SKSurface.Create(SKImageInfo(200, 200))
    let canvas = surface.Canvas
    canvas.Clear(SKColors.Black)
    LayerPainter.paint canvas (200, 200) lib Visibility.empty
    use img = surface.Snapshot()
    use pix = img.PeekPixels()
    let centerColor = pix.GetPixelColor(100, 100)
    (int centerColor.Red + int centerColor.Green + int centerColor.Blue) |> should be (greaterThan 0)

[<Fact>]
let ``Paint with met2 hidden produces black canvas`` () =
    let lib = singleBoundaryLib 69 20
    let hidden = Visibility.empty |> Visibility.toggleLayer (69, 20) false
    use surface = SKSurface.Create(SKImageInfo(50, 50))
    let canvas = surface.Canvas
    canvas.Clear(SKColors.Black)
    LayerPainter.paint canvas (50, 50) lib hidden
    use img = surface.Snapshot()
    use pix = img.PeekPixels()
    let c = pix.GetPixelColor(25, 25)
    (int c.Red + int c.Green + int c.Blue) |> should equal 0
