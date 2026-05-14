module Rekolektion.Viz.Core.Drc.Rules

/// Subset of SKY130 design rules ported from
/// `src/rekolektion/tech/sky130.py`. We only port the rules the
/// interactive editor needs for fast in-process DRC at edit time
/// — min-width and min-spacing on the layers most edits touch.
/// Min-enclosure / contact rules land in a later pass.
///
/// Values are in micrometers; the runtime converts to DBU using
/// the active library's `UserUnitsPerDbUnit`.

type LayerRule = {
    /// Display name — also used as the rule prefix when reporting
    /// violations (e.g. "met1.width").
    Layer       : string
    /// SKY130 (gds-layer-number, datatype) so the runtime can
    /// match flattened polygons to their rule entry.
    Number      : int
    DataType    : int
    MinWidthUm  : float
    MinSpacingUm: float
}

/// SKY130 drawing-layer rules covered by the interactive DRC.
/// Numbers match `Rekolektion.Viz.Core.Layout.Layer.allDrawing`.
let allLayerRules : LayerRule list = [
    { Layer = "diff";  Number = 65; DataType = 20
      MinWidthUm = 0.15; MinSpacingUm = 0.27 }
    { Layer = "tap";   Number = 65; DataType = 44
      MinWidthUm = 0.26; MinSpacingUm = 0.27 }
    { Layer = "nwell"; Number = 64; DataType = 20
      MinWidthUm = 0.84; MinSpacingUm = 1.27 }
    { Layer = "poly";  Number = 66; DataType = 20
      MinWidthUm = 0.15; MinSpacingUm = 0.21 }
    { Layer = "li1";   Number = 67; DataType = 20
      MinWidthUm = 0.17; MinSpacingUm = 0.17 }
    { Layer = "met1";  Number = 68; DataType = 20
      MinWidthUm = 0.14; MinSpacingUm = 0.14 }
    { Layer = "met2";  Number = 69; DataType = 20
      MinWidthUm = 0.14; MinSpacingUm = 0.14 }
    { Layer = "met3";  Number = 70; DataType = 20
      MinWidthUm = 0.30; MinSpacingUm = 0.30 }
    { Layer = "met4";  Number = 71; DataType = 20
      MinWidthUm = 0.30; MinSpacingUm = 0.30 }
    { Layer = "met5";  Number = 72; DataType = 20
      MinWidthUm = 1.60; MinSpacingUm = 1.60 }
    // Contact / via cuts also have width + spacing constraints —
    // checked the same way as drawing layers.
    { Layer = "licon1"; Number = 66; DataType = 44
      MinWidthUm = 0.17; MinSpacingUm = 0.17 }
    { Layer = "mcon";  Number = 67; DataType = 44
      MinWidthUm = 0.17; MinSpacingUm = 0.19 }
    { Layer = "via";   Number = 68; DataType = 44
      MinWidthUm = 0.15; MinSpacingUm = 0.17 }
    { Layer = "via2";  Number = 69; DataType = 44
      MinWidthUm = 0.20; MinSpacingUm = 0.20 }
]

let private byKey =
    allLayerRules
    |> List.map (fun r -> (r.Number, r.DataType), r)
    |> Map.ofList

/// Look up rules by (gds-layer, datatype). Returns None for
/// layers we don't track.
let tryFind (number: int) (dataType: int) : LayerRule option =
    Map.tryFind (number, dataType) byKey
