module Rekolektion.Viz.Core.Rkt.ToGds

/// Adapter: convert a `Rkt.Types.Document` back to a
/// `Gds.Types.Library` for the legacy in-memory model and GDS export.
///
/// Lossless for: cell hierarchy, geometry (`PolyEl`, `PathEl`,
/// `RectEl`), references (`SRefEl`, `ARefEl`), labels (`LabelEl`).
/// Layer references resolve via the SKY130 layer table; `Unknown(n,d)`
/// pairs land as `(n, d)` verbatim.
///
/// Information dropped (intentional, since GDS doesn't represent it):
/// - Port metadata. `PortEl` becomes a `TextLabel` (the port name) plus
///   the port's geometry as a `Boundary`/`Path` on the port's layer.
///   Direction + flag set survive only via re-parse on the .rkt side.
/// - Per-element net membership.
/// - Top-level `(nets ...)` block.
/// - `(props ...)` element-level and cell-level properties.
///
/// Round-trip plan: `Document -> Library -> Document` survives
/// geometry + hierarchy + labels. Port flags and nets fall back to
/// defaults on re-import. The .rkt save path stays on
/// `Writer.renderCst` (CST round-trip) for editor cases where this
/// loss is unacceptable.

open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types

/// Reverse layer table: `(pdk, name) -> (number, datatype)`. Built
/// once from `Layout.Layer.allDrawing`. Only entries with PDK
/// `"sky130"` participate; other PDKs degrade to `(0, 0)` and warn —
/// out-of-scope at v1.
let private sky130NameTable : Map<string, int * int> =
    Layout.Layer.allDrawing
    |> List.map (fun l -> l.Name, (l.Number, l.DataType))
    |> Map.ofList

/// Resolve a Rkt `Layer` back to a GDS `(number, datatype)` pair.
/// `Unknown(n, d)` returns `(n, d)` verbatim. `Named("sky130", name)`
/// looks up the SKY130 table; misses return `(0, 0)` (silently lost
/// for now — caller logs if it cares).
let layerToGds (layer: Layer) : int * int =
    match layer with
    | Unknown (n, d) -> n, d
    | Named ("sky130", name) ->
        match Map.tryFind name sky130NameTable with
        | Some pair -> pair
        | None -> 0, 0
    | Named (_, _) ->
        // Non-SKY130 PDK names aren't representable in our current
        // table. Land as (0, 0); the polygon still renders via the
        // theme fallback.
        0, 0

let private pointToGds (p: Point) : Gds.Types.Point =
    { X = p.X; Y = p.Y }

let private rectToBoundary
    (layer: Layer) (x1: int64) (y1: int64) (x2: int64) (y2: int64)
    : Gds.Types.Boundary =
    let l, dt = layerToGds layer
    {
        Layer = l
        DataType = dt
        Points = [
            { X = x1; Y = y1 }
            { X = x2; Y = y1 }
            { X = x2; Y = y2 }
            { X = x1; Y = y2 }
            { X = x1; Y = y1 }
        ]
    }

let polyToBoundary (p: Poly) : Gds.Types.Boundary =
    let l, dt = layerToGds p.Layer
    {
        Layer = l
        DataType = dt
        Points = p.Points |> List.map pointToGds
    }

let pathToGds (p: Path) : Gds.Types.Path =
    let l, dt = layerToGds p.Layer
    {
        Layer = l
        DataType = dt
        Width = int p.Width
        Points = p.Points |> List.map pointToGds
    }

let srefToGds (r: SRef) : Gds.Types.SRef =
    {
        StructureName = r.Cell
        Origin = pointToGds r.Origin
        Mag = r.Mag
        Angle = r.Rot
        Reflected = r.Reflect
    }

let arefToGds (r: ARef) : Gds.Types.ARef =
    {
        StructureName = r.Cell
        Origin = pointToGds r.Origin
        Cols = r.Cols
        Rows = r.Rows
        ColPitch = pointToGds r.ColPitch
        RowPitch = pointToGds r.RowPitch
        Mag = r.Mag
        Angle = r.Rot
        Reflected = r.Reflect
    }

let labelToGds (l: Label) : Gds.Types.TextLabel =
    let layerNo, dt = layerToGds l.Layer
    {
        Layer = layerNo
        TextType = dt
        Origin = pointToGds l.Origin
        Text = l.Text
    }

/// Port → list of GDS elements: a name TextLabel anchored at the
/// shape's reference point, plus a Boundary/Path representing the
/// shape geometry.
let portToGds (p: Port) : Gds.Types.Element list =
    let layerNo, dt = layerToGds p.Layer
    let shapeElement, anchor =
        match p.Shape with
        | RectShape (x1, y1, x2, y2) ->
            let cx = (x1 + x2) / 2L
            let cy = (y1 + y2) / 2L
            let b = rectToBoundary p.Layer x1 y1 x2 y2
            Gds.Types.Boundary b, { Gds.Types.X = cx; Gds.Types.Y = cy }
        | PolyShape pts ->
            // Use the first point as the label anchor — matches the
            // convention `MagToLayout.labelToText` uses for rect labels.
            let anchor =
                match pts with
                | first :: _ -> pointToGds first
                | [] -> { Gds.Types.X = 0L; Gds.Types.Y = 0L }
            let b : Gds.Types.Boundary = {
                Layer = layerNo
                DataType = dt
                Points = pts |> List.map pointToGds
            }
            Gds.Types.Boundary b, anchor
    let labelEl : Gds.Types.Element =
        Gds.Types.Text {
            Layer = layerNo
            TextType = dt
            Origin = anchor
            Text = p.Name
        }
    [ shapeElement; labelEl ]

let elementToGds (e: Element) : Gds.Types.Element list =
    match e with
    | PolyEl p -> [ Gds.Types.Boundary (polyToBoundary p) ]
    | PathEl p -> [ Gds.Types.Path (pathToGds p) ]
    | RectEl r -> [ Gds.Types.Boundary (rectToBoundary r.Layer r.X1 r.Y1 r.X2 r.Y2) ]
    | PortEl p -> portToGds p
    | LabelEl l -> [ Gds.Types.Text (labelToGds l) ]
    | SRefEl s -> [ Gds.Types.SRef (srefToGds s) ]
    | ARefEl a -> [ Gds.Types.ARef (arefToGds a) ]
    | PropsEl _ -> []

let cellToStructure (c: Cell) : Gds.Types.Structure =
    { Name = c.Name
      Elements = c.Elements |> List.collect elementToGds }

let toLibrary (doc: Document) : Gds.Types.Library =
    {
        Name = doc.TopCell |> Option.defaultValue "rkt"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = float doc.Units.DbuNm * 1.0e-9
        Structures = doc.Cells |> List.map cellToStructure
    }
