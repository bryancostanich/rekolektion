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
    { Number =  64; DataType = 18; Name = "dnwell";  Color = rgba 0x60 0x80 0xa0 0xff; StackZ = -0.25; Thickness = 0.20 }
    { Number =  64; DataType = 20; Name = "nwell";   Color = rgba 0xa0 0xc8 0xff 0xff; StackZ = -0.20; Thickness = 0.20 }
    { Number =  65; DataType = 20; Name = "diff";    Color = rgba 0xff 0xd0 0x80 0xff; StackZ = -0.10; Thickness = 0.15 }
    { Number =  65; DataType = 44; Name = "tap";     Color = rgba 0xff 0xd0 0x80 0xff; StackZ = -0.10; Thickness = 0.15 }
    // Poly gets the bright blue the routing metals used to wear —
    // poly is the gate-forming layer and reading distinctly from
    // diff/li1 is more important than reading "warm" or "metallic".
    { Number =  66; DataType = 20; Name = "poly";    Color = rgba 0x40 0x90 0xff 0xff; StackZ =  0.00; Thickness = 0.18 }
    // licon1 = poly/diff contact down to li1. Name `licon1` matches the
    // sky130 stream convention and Python `_layer_map.py`; the Magic
    // `.mag` loader keeps the legacy `licon` name and aliases to this
    // pair via `Mag/LayerMap.fs`.
    { Number =  66; DataType = 44; Name = "licon1";  Color = rgba 0x80 0x80 0x80 0xff; StackZ =  0.05; Thickness = 0.38 }
    // Routing-layer palette — silver/gold tones that read as
    // "metal" instead of the saturated rainbow this used to be.
    // Stacked alternating cool/warm so adjacent layers stay
    // visually distinct without color-coding by function (which
    // never quite worked at six routing layers anyway).
    //   li1   → graphite gray-purple (titanium-nitride local IC)
    //   met1  → aluminum silver
    //   met2  → light gold
    //   met3  → bronze
    //   met4  → copper
    //   met5  → pale brass
    // Vias/contacts keep their dim-gray (they're tiny dots, not
    // wires; competing color would distract from the routing).
    { Number =  67; DataType = 20; Name = "li1";     Color = rgba 0x88 0x82 0x96 0xff; StackZ =  0.43; Thickness = 0.10 }
    { Number =  67; DataType = 44; Name = "mcon";    Color = rgba 0x60 0x60 0x60 0xff; StackZ =  0.53; Thickness = 0.36 }
    { Number =  68; DataType = 20; Name = "met1";    Color = rgba 0xc8 0xc8 0xd4 0xff; StackZ =  0.89; Thickness = 0.36 }
    { Number =  68; DataType = 44; Name = "via";     Color = rgba 0x50 0x50 0x50 0xff; StackZ =  1.25; Thickness = 0.36 }
    { Number =  69; DataType = 20; Name = "met2";    Color = rgba 0xdc 0xc8 0x88 0xff; StackZ =  1.61; Thickness = 0.36 }
    { Number =  69; DataType = 44; Name = "via2";    Color = rgba 0x64 0x64 0x64 0xff; StackZ =  1.97; Thickness = 0.36 }
    { Number =  70; DataType = 20; Name = "met3";    Color = rgba 0xb8 0x8c 0x5c 0xff; StackZ =  2.33; Thickness = 0.36 }
    { Number =  70; DataType = 44; Name = "via3";    Color = rgba 0x46 0x46 0x46 0xff; StackZ =  2.69; Thickness = 0.36 }
    { Number =  71; DataType = 20; Name = "met4";    Color = rgba 0xc8 0x88 0x60 0xff; StackZ =  3.05; Thickness = 0.36 }
    { Number =  71; DataType = 44; Name = "via4";    Color = rgba 0x46 0x46 0x46 0xff; StackZ =  3.41; Thickness = 0.36 }
    { Number =  72; DataType = 20; Name = "met5";    Color = rgba 0xb4 0xa4 0x70 0xff; StackZ =  3.77; Thickness = 0.50 }
    // HV implant + native-threshold markers — required for sky130
    // `_g5v0d10v5_` device families (HV nfet / pfet).  Magic's DRC
    // rules switch from LV to HV variants based on these markers.
    // Without them the writer drops g5v0 device markers and Magic
    // applies LV rules, producing cascading false-positive DRC
    // violations.
    // Sky130 stream names: HVI=75/20, HVTP=78/44, HVNTM=125/20.
    // We carry the Python `_layer_map.py` names (`hvntm`@78/44,
    // `nwell_drawing`@125/20) verbatim so existing primitives keep
    // round-tripping without a rename.  TODO: rename in a follow-up
    // PR after migrating cell_designs/primitives/.
    { Number =  75; DataType = 20; Name = "hvi";     Color = rgba 0xa0 0xff 0xc0 0x40; StackZ =  4.05; Thickness = 0.05 }
    { Number =  78; DataType = 44; Name = "hvntm";   Color = rgba 0xff 0xa0 0xc0 0x40; StackZ =  4.06; Thickness = 0.05 }
    { Number = 125; DataType = 20; Name = "nwell_drawing"; Color = rgba 0xc0 0xa0 0xff 0x40; StackZ = 4.07; Thickness = 0.05 }
    // MIMCAP — used by CIM cells; sits between met3 and met4 in the
    // legacy stackup. Without this, the cap top plate of CIM cells
    // doesn't render in 3D.
    { Number =  89; DataType = 44; Name = "mimcap";  Color = rgba 0xff 0xc8 0x00 0xff; StackZ =  2.50; Thickness = 0.05 }
    // NPC (Nitride Poly Cut) — used over HV gate poly to remove
    // silicidation; otherwise dropped by the GDS writer.
    { Number =  95; DataType = 20; Name = "npc";     Color = rgba 0xff 0xc0 0x80 0x40; StackZ =  4.02; Thickness = 0.05 }
    // CFOM (Cu fill region marker) — fill-density hinting layer.
    // Python `_layer_map.py` calls (122, 16) "cfom_drawing".
    { Number = 122; DataType = 16; Name = "cfom_drawing"; Color = rgba 0x80 0x80 0xa0 0x30; StackZ = 4.08; Thickness = 0.05 }
    // ReRAM body. The sky130_fd_pr_reram PDK uses 201/20 for the
    // ReRAM cell stack; physical position sits between li1 and
    // met1 (post-li1 contact, pre-via). Distinctive purple keeps
    // it visually separate from li1 and met1 colors.
    { Number = 201; DataType = 20; Name = "reram";   Color = rgba 0xc8 0x40 0xff 0xff; StackZ =  0.55; Thickness = 0.30 }
    // Source/drain implant markers — non-physical (process-flag
    // layers), but they're drawn in real .mag/.gds and were
    // silently dropped by the renderer when not in the catalog.
    // Translucent so they don't overpower the silicon underneath.
    //
    // GDS pair-to-name is the sky130 standard ordering (see
    // `sky130B.tech`: `calma NSDM 93 44`, `calma PSDM 94 20`).  The
    // previous entries had these SWAPPED — a P0 round-trip bug that
    // caused every g5v0 FET to write its implant on the *opposite*
    // mask after `Rkt.ToGds`, triggering implant/well DRC cascades.
    { Number =  93; DataType = 44; Name = "nsdm";    Color = rgba 0x80 0xc0 0xff 0x40; StackZ =  4.10; Thickness = 0.05 }
    { Number =  94; DataType = 20; Name = "psdm";    Color = rgba 0xff 0x80 0x80 0x40; StackZ =  4.15; Thickness = 0.05 }
    // Marker (areaid.core) — non-physical, drawn flat as a thin
    // overlay.  Name `areaid_core` matches the Python `_layer_map.py`
    // and the sky130 standard (underscore separator); the Magic
    // `.mag` loader keeps its legacy `areaid.sc` alias in
    // `Mag/LayerMap.fs`.
    { Number =  81; DataType =  2; Name = "areaid_core"; Color = rgba 0xff 0x00 0xff 0x40; StackZ =  4.30; Thickness = 0.05 }
    { Number =  81; DataType = 53; Name = "areaid_lowtapdensity"; Color = rgba 0xff 0x80 0xff 0x30; StackZ = 4.32; Thickness = 0.05 }
    // Stream boundary marker — used by some chip-level GDS exporters.
    { Number = 235; DataType =  4; Name = "boundary"; Color = rgba 0x60 0x60 0x60 0x20; StackZ =  4.38; Thickness = 0.05 }
    // Magic-internal marker layers. Not silicon; Magic uses these
    // for incremental-extract bookkeeping and DRC/extract
    // diagnostics. Distinct key (255, *) so they're toggleable on
    // their own row in the layers panel; AutoFit (2D + 3D) skips
    // them so they don't distort the rendered area size; rendered
    // dim + translucent above met5 so they read as overlays when
    // the user does want to see them.
    { Number = 255; DataType =  0; Name = "magic.checkpaint"; Color = rgba 0x40 0xa0 0xa0 0x30; StackZ =  4.40; Thickness = 0.05 }
    { Number = 255; DataType =  1; Name = "magic.error";      Color = rgba 0xff 0x40 0x40 0x60; StackZ =  4.45; Thickness = 0.05 }
    { Number = 255; DataType =  2; Name = "magic.feedback";   Color = rgba 0xff 0xa0 0x40 0x60; StackZ =  4.50; Thickness = 0.05 }
]

/// True when the layer is a Magic-internal marker (checkpaint,
/// error, feedback). Callers use this to skip non-physical
/// layers when computing the cell's render bbox so that the
/// camera frames silicon, not bookkeeping rectangles.
let isNonPhysical (layerNumber: int) (_dataType: int) : bool =
    layerNumber = 255

let private byKey =
    allDrawing |> List.map (fun l -> (l.Number, l.DataType), l) |> Map.ofList

/// Legacy `(number, datatype)` pairs that the SKY130 stream map
/// doesn't list but that appear in foundry-shipped cells (chiefly
/// the `sky130_fd_pr_reram` library). Magic translates them at
/// gds-read time via cifinput rules; we mirror the translation
/// here so the same `Rkt.Document` lands regardless of which
/// number the source file used.
///
/// Each entry points at the canonical `(number, datatype)` in
/// `byKey`. The reverse map (used by `Rkt.ToGds`) intentionally
/// ignores this table, so a load-then-export round-trip rewrites
/// legacy pairs to their canonical sky130 equivalents.
let private legacyAliases : ((int * int) * (int * int)) list = [
    (6, 0),    (65, 20)    // diff.drawing
    (6, 251),  (65, 20)    // diff.pin (merged into drawing)
    (7, 0),    (66, 44)    // licon (poly contact)
    (8, 0),    (68, 20)    // met1.drawing
    (8, 251),  (68, 20)    // met1.pin (merged into drawing)
    (40, 0),   (201, 20)   // reram body
]

let private aliasMap : Map<int * int, Layer> =
    legacyAliases
    |> List.choose (fun (alias, canonical) ->
        Map.tryFind canonical byKey
        |> Option.map (fun layer -> alias, layer))
    |> Map.ofList

let bySky130Number (number: int) (dataType: int) : Layer option =
    match Map.tryFind (number, dataType) byKey with
    | Some l -> Some l
    | None -> Map.tryFind (number, dataType) aliasMap
