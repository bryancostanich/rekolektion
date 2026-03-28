/// SKY130 GDS layer/datatype → rendering properties.
/// Colors and layer order match the Python render_cell.py for validation.
module Viz.Render.LayerMap

/// RGBA color tuple.
type Color = { R: byte; G: byte; B: byte; A: byte }

/// Rendering properties for a GDS layer.
type LayerStyle = {
    Name: string
    Color: Color
}

/// Create a Color from RGBA values.
let rgba r g b a = { R = r; G = g; B = b; A = a }

/// SKY130 layer map: (layer, datatype) → style.
/// Exact match with Python LAYER_COLORS for pixel-diff validation.
let sky130Layers : Map<(int * int), LayerStyle> =
    [
        (64, 20), { Name = "nwell"; Color = rgba 160uy 200uy 255uy 80uy }
        (65, 20), { Name = "diff";  Color = rgba 255uy 208uy 128uy 200uy }
        (65, 44), { Name = "tap";   Color = rgba 255uy 208uy 128uy 200uy }
        (66, 20), { Name = "poly";  Color = rgba 255uy 64uy  64uy  220uy }
        (66, 44), { Name = "licon"; Color = rgba 128uy 128uy 128uy 240uy }
        (67, 20), { Name = "li1";   Color = rgba 192uy 128uy 255uy 200uy }
        (67, 44), { Name = "mcon";  Color = rgba 96uy  96uy  96uy  240uy }
        (68, 20), { Name = "met1";  Color = rgba 64uy  144uy 255uy 200uy }
        (68, 44), { Name = "via";   Color = rgba 80uy  80uy  80uy  240uy }
        (69, 20), { Name = "met2";  Color = rgba 64uy  255uy 144uy 200uy }
        (93, 44), { Name = "nsdm";  Color = rgba 255uy 255uy 128uy 60uy }
        (94, 20), { Name = "psdm";  Color = rgba 255uy 128uy 255uy 60uy }
    ]
    |> Map.ofList

/// Render order (bottom to top), matching Python RENDER_LAYERS.
let renderOrder : (int * int) list = [
    (64, 20); (65, 20); (65, 44); (93, 44); (94, 20)
    (66, 20); (66, 44); (67, 20); (67, 44)
    (68, 20); (68, 44); (69, 20)
]

/// Look up style for a layer/datatype pair.
let tryGetStyle (layer: int) (datatype: int) : LayerStyle option =
    sky130Layers |> Map.tryFind (layer, datatype)
