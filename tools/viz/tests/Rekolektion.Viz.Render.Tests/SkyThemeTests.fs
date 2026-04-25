module Rekolektion.Viz.Render.Tests.SkyThemeTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Render.Color

[<Fact>]
let ``SkyTheme.fillFor returns a Skia color`` () =
    let c = SkyTheme.fillFor "met2"
    c.Alpha |> should be (greaterThan 0uy)

[<Fact>]
let ``SkyTheme.strokeFor is darker than fillFor`` () =
    let f = SkyTheme.fillFor "met2"
    let s = SkyTheme.strokeFor "met2"
    let lum (c: SkiaSharp.SKColor) = int c.Red + int c.Green + int c.Blue
    lum s |> should be (lessThan (lum f))
