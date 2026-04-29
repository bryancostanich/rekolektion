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

/// SKY130 drawing layers we care about for SRAM viz. Z heights and
/// colors mirror the legacy `tools/viz/Mesh/MeshGenerator.fs` GLB
/// stackup so the live 3D canvas matches what the user sees when
/// opening exported GLB files in MeshLab / Preview / Blender.
///
/// Stackup uses cosmetic uniform 0.36 µm thickness for metals and
/// vias rather than physical SKY130 spec (where vias are thinner
/// than metals). The user's expectation is "match the GLB output";
/// physical accuracy is a non-goal for visualization.
let allDrawing : Layer list = [
    { Number =  64; DataType = 20; Name = "nwell";   Color = rgba 0xa0 0xc8 0xff 0xff; StackZ = -0.20; Thickness = 0.20 }
    { Number =  65; DataType = 20; Name = "diff";    Color = rgba 0xff 0xd0 0x80 0xff; StackZ = -0.10; Thickness = 0.15 }
    { Number =  65; DataType = 44; Name = "tap";     Color = rgba 0xff 0xd0 0x80 0xff; StackZ = -0.10; Thickness = 0.15 }
    { Number =  66; DataType = 20; Name = "poly";    Color = rgba 0xff 0x40 0x40 0xff; StackZ =  0.00; Thickness = 0.18 }
    { Number =  66; DataType = 44; Name = "licon";   Color = rgba 0x80 0x80 0x80 0xff; StackZ =  0.05; Thickness = 0.38 }
    { Number =  67; DataType = 20; Name = "li1";     Color = rgba 0xc0 0x80 0xff 0xff; StackZ =  0.43; Thickness = 0.10 }
    { Number =  67; DataType = 44; Name = "mcon";    Color = rgba 0x60 0x60 0x60 0xff; StackZ =  0.53; Thickness = 0.36 }
    { Number =  68; DataType = 20; Name = "met1";    Color = rgba 0x40 0x90 0xff 0xff; StackZ =  0.89; Thickness = 0.36 }
    { Number =  68; DataType = 44; Name = "via";     Color = rgba 0x50 0x50 0x50 0xff; StackZ =  1.25; Thickness = 0.36 }
    { Number =  69; DataType = 20; Name = "met2";    Color = rgba 0x40 0xff 0x90 0xff; StackZ =  1.61; Thickness = 0.36 }
    { Number =  69; DataType = 44; Name = "via2";    Color = rgba 0x64 0x64 0x64 0xff; StackZ =  1.97; Thickness = 0.36 }
    { Number =  70; DataType = 20; Name = "met3";    Color = rgba 0xff 0xa0 0x40 0xff; StackZ =  2.33; Thickness = 0.36 }
    { Number =  70; DataType = 44; Name = "via3";    Color = rgba 0x46 0x46 0x46 0xff; StackZ =  2.69; Thickness = 0.36 }
    { Number =  71; DataType = 20; Name = "met4";    Color = rgba 0xff 0xff 0x40 0xff; StackZ =  3.05; Thickness = 0.36 }
    { Number =  71; DataType = 44; Name = "via4";    Color = rgba 0x46 0x46 0x46 0xff; StackZ =  3.41; Thickness = 0.36 }
    { Number =  72; DataType = 20; Name = "met5";    Color = rgba 0xbb 0xbb 0x66 0xff; StackZ =  3.77; Thickness = 0.50 }
    // MIMCAP — used by CIM cells; sits between met3 and met4 in the
    // legacy stackup. Without this, the cap top plate of CIM cells
    // doesn't render in 3D.
    { Number =  89; DataType = 44; Name = "mimcap";  Color = rgba 0xff 0xc8 0x00 0xff; StackZ =  2.50; Thickness = 0.05 }
    // Marker (areaid.sc) — non-physical, drawn flat as a thin overlay.
    { Number =  81; DataType =  2; Name = "areaid.sc"; Color = rgba 0xff 0x00 0xff 0x40; StackZ =  4.30; Thickness = 0.05 }
]

let private byKey =
    allDrawing |> List.map (fun l -> (l.Number, l.DataType), l) |> Map.ofList

let bySky130Number (number: int) (dataType: int) : Layer option =
    Map.tryFind (number, dataType) byKey
