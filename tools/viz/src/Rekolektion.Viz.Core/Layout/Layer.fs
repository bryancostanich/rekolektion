module Rekolektion.Viz.Core.Layout.Layer

type ColorRgba = { R: byte; G: byte; B: byte; A: byte }

type Layer = {
    Number   : int       // GDS layer number
    DataType : int       // GDS datatype (20 for drawing in SKY130)
    Name     : string
    Color    : ColorRgba
    StackZ   : float     // bottom of layer in 3D extrusion (μm)
    Thickness: float     // extrusion thickness (μm)
}

let private rgba r g b a = { R = byte r; G = byte g; B = byte b; A = byte a }

/// SKY130 drawing layers we care about for SRAM viz. Z heights are
/// approximate process-stack values (top-of-substrate to top-of-met5)
/// in μm. Colors loosely follow Magic's default theme so the viz feels
/// familiar to anyone who has used Magic. Layer numbers from
/// SKY130 PDK (sky130_fd_pr) — verify against the PDK's tech file
/// before trusting in production.
let allDrawing : Layer list = [
    { Number =  64; DataType = 20; Name = "nwell";   Color = rgba 0x66 0x66 0x66 0xff; StackZ = -0.50; Thickness = 0.30 }
    { Number =  65; DataType = 20; Name = "diff";    Color = rgba 0x48 0x84 0x48 0xff; StackZ = -0.20; Thickness = 0.15 }
    { Number =  66; DataType = 20; Name = "poly";    Color = rgba 0xa4 0x44 0x44 0xff; StackZ =  0.05; Thickness = 0.18 }
    { Number =  67; DataType = 20; Name = "li1";     Color = rgba 0xc8 0x84 0x44 0xff; StackZ =  0.40; Thickness = 0.10 }
    { Number =  68; DataType = 20; Name = "met1";    Color = rgba 0x48 0x88 0xaa 0xff; StackZ =  0.65; Thickness = 0.36 }
    { Number =  69; DataType = 20; Name = "met2";    Color = rgba 0x55 0xaa 0x88 0xff; StackZ =  1.20; Thickness = 0.36 }
    { Number =  70; DataType = 20; Name = "met3";    Color = rgba 0x33 0xaa 0xaa 0xff; StackZ =  1.78; Thickness = 0.85 }
    { Number =  71; DataType = 20; Name = "met4";    Color = rgba 0xaa 0x88 0xaa 0xff; StackZ =  2.78; Thickness = 0.85 }
    { Number =  72; DataType = 20; Name = "met5";    Color = rgba 0xbb 0xbb 0x66 0xff; StackZ =  3.78; Thickness = 1.26 }
    // Vias / contacts (data type 44 in SKY130)
    { Number =  66; DataType = 44; Name = "licon";   Color = rgba 0xff 0xff 0xff 0x60; StackZ =  0.23; Thickness = 0.17 }
    { Number =  67; DataType = 44; Name = "mcon";    Color = rgba 0xff 0xff 0xff 0x60; StackZ =  0.50; Thickness = 0.15 }
    { Number =  68; DataType = 44; Name = "via";     Color = rgba 0xff 0xff 0xff 0x60; StackZ =  1.01; Thickness = 0.19 }
    { Number =  69; DataType = 44; Name = "via2";    Color = rgba 0xff 0xff 0xff 0x60; StackZ =  1.56; Thickness = 0.22 }
    { Number =  70; DataType = 44; Name = "via3";    Color = rgba 0xff 0xff 0xff 0x60; StackZ =  2.63; Thickness = 0.15 }
    { Number =  71; DataType = 44; Name = "via4";    Color = rgba 0xff 0xff 0xff 0x60; StackZ =  3.63; Thickness = 0.15 }
    // Marker
    { Number =  81; DataType =  2; Name = "areaid.sc"; Color = rgba 0xff 0x00 0xff 0x40; StackZ =  4.00; Thickness = 0.05 }
]

let private byKey =
    allDrawing |> List.map (fun l -> (l.Number, l.DataType), l) |> Map.ofList

let bySky130Number (number: int) (dataType: int) : Layer option =
    Map.tryFind (number, dataType) byKey
