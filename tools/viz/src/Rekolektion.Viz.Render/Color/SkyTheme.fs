module Rekolektion.Viz.Render.Color.SkyTheme

open SkiaSharp
open Rekolektion.Viz.Core.Layout

let private toSkColor (c: Layer.ColorRgba) =
    SKColor(c.R, c.G, c.B, c.A)

let fillFor (layerName: string) : SKColor =
    Layer.allDrawing
    |> List.tryFind (fun l -> l.Name = layerName)
    |> Option.map (fun l -> toSkColor l.Color)
    |> Option.defaultValue (SKColor(byte 0xCC, byte 0xCC, byte 0xCC, byte 0x80))

let strokeFor (layerName: string) : SKColor =
    let f = fillFor layerName
    let darken (v: byte) = if v < 64uy then 0uy else v - 64uy
    SKColor(darken f.Red, darken f.Green, darken f.Blue, byte 0xff)
