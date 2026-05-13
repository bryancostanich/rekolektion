module Rekolektion.Viz.Core.Rkt.OfGds

/// Adapter: convert a `Gds.Types.Library` (the legacy in-memory model)
/// into a `Rkt.Types.Document` (the new canonical model).
///
/// Information added:
/// - Named layers via `Layout.Layer.bySky130Number`. Pairs without a
///   PDK entry land as `Unknown(number, datatype)` — visible, not
///   dropped (per the format's "unknown layers stay visible" rule).
/// - Default PDK = `sky130` and `dbu_nm = 1` (one nanometer per DBU)
///   match the project convention.
///
/// Information not added (intentional, since GDS doesn't carry it):
/// - Net membership per element.
/// - Port flags / direction.
/// - Top-level nets block.
/// - Imports.
///
/// Round-trip plan: `Library -> Document` followed by `Document ->
/// Library` (via `Rkt.ToGds`) is lossless for geometry + cell
/// structure. Layer-name annotations re-resolve via the same lookup
/// table; unknown layers stay unknown both ways.

open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types

let private defaultPdk : string = "sky130"

let layerFromGds (number: int) (datatype: int) : Layer =
    match Layout.Layer.bySky130Number number datatype with
    | Some l -> Named (defaultPdk, l.Name)
    | None -> Unknown (number, datatype)

let private pointFromGds (p: Gds.Types.Point) : Point =
    { X = p.X; Y = p.Y }

let fromBoundary (b: Gds.Types.Boundary) : Element =
    PolyEl {
        Layer = layerFromGds b.Layer b.DataType
        Points = b.Points |> List.map pointFromGds
        Net = None
        Props = []
    }

let fromPath (p: Gds.Types.Path) : Element =
    PathEl {
        Layer = layerFromGds p.Layer p.DataType
        Width = int64 p.Width
        Points = p.Points |> List.map pointFromGds
        Net = None
        Cap = None
        Props = []
    }

let fromSRef (s: Gds.Types.SRef) : Element =
    SRefEl {
        Cell = s.StructureName
        Origin = pointFromGds s.Origin
        Rot = s.Angle
        Mag = s.Mag
        Reflect = s.Reflected
        Props = []
    }

let fromARef (a: Gds.Types.ARef) : Element =
    ARefEl {
        Cell = a.StructureName
        Origin = pointFromGds a.Origin
        Cols = a.Cols
        Rows = a.Rows
        ColPitch = pointFromGds a.ColPitch
        RowPitch = pointFromGds a.RowPitch
        Rot = a.Angle
        Mag = a.Mag
        Reflect = a.Reflected
        Props = []
    }

let fromText (t: Gds.Types.TextLabel) : Element =
    LabelEl {
        Layer = layerFromGds t.Layer t.TextType
        Text = t.Text
        Origin = pointFromGds t.Origin
        Class = None
        Props = []
    }

let fromElement (e: Gds.Types.Element) : Element =
    match e with
    | Gds.Types.Boundary b -> fromBoundary b
    | Gds.Types.Path p -> fromPath p
    | Gds.Types.SRef s -> fromSRef s
    | Gds.Types.ARef a -> fromARef a
    | Gds.Types.Text t -> fromText t

let fromStructure (s: Gds.Types.Structure) : Cell =
    { Name = s.Name
      Elements = s.Elements |> List.map fromElement }

let fromLibrary (lib: Gds.Types.Library) : Document =
    let units : Units =
        // SKY130 convention: DbUnitsInMeters = 1e-9 means 1 nm per
        // DBU, so dbu_nm = 1. Fall back to 1 if the field is unset
        // (legacy GDS readers default to this anyway).
        let dbuNm =
            let nm = lib.DbUnitsInMeters * 1.0e9
            if nm <= 0.0 then 1
            else int (System.Math.Round nm)
        { DbuNm = max 1 dbuNm; UuUm = 1 }
    let topCell =
        match lib.Structures with
        | [] -> None
        | s :: _ -> Some s.Name
    { Version = 1
      Pdk = defaultPdk
      Units = units
      Imports = []
      Nets = []
      Cells = lib.Structures |> List.map fromStructure
      TopCell = topCell }
