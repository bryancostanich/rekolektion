module Rekolektion.Viz.Core.Layout.Flatten

open Rekolektion.Viz.Core.Gds.Types

/// One polygon after the GDS hierarchy has been walked and all
/// SRef / ARef transforms applied. World coordinates are in DBU,
/// same units as the original polygon points.
///
/// `SourceStructure` + `SourceIndex` point at the polygon in its
/// ORIGINAL cell (not the top), so sidecar lookups keyed by
/// (structure, index) still work for instances whose source cell
/// has a sidecar entry. For instanced polygons the same source
/// indices repeat — each bitcell instance reports the same source
/// cell and index, just at a different transformed position.
type FlatPolygon = {
    Layer: int
    DataType: int
    Points: Point array
    SourceStructure: string
    SourceIndex: int
}

/// 2D affine in homogenous form: [A B Tx; C D Ty]. Avoids carrying
/// rotation+reflection+scale separately, which compose poorly.
type private Affine = {
    A: float; B: float; Tx: float
    C: float; D: float; Ty: float
}

let private identityXform : Affine =
    { A = 1.0; B = 0.0; Tx = 0.0
      C = 0.0; D = 1.0; Ty = 0.0 }

let private apply (m: Affine) (p: Point) : Point =
    let x = float p.X
    let y = float p.Y
    let xp = m.A * x + m.B * y + m.Tx
    let yp = m.C * x + m.D * y + m.Ty
    { X = int64 (System.Math.Round xp)
      Y = int64 (System.Math.Round yp) }

/// Compose so the result first applies `inner` then `outer`:
///   apply (compose outer inner) p = apply outer (apply inner p)
let private compose (outer: Affine) (inner: Affine) : Affine =
    { A = outer.A * inner.A + outer.B * inner.C
      B = outer.A * inner.B + outer.B * inner.D
      Tx = outer.A * inner.Tx + outer.B * inner.Ty + outer.Tx
      C = outer.C * inner.A + outer.D * inner.C
      D = outer.C * inner.B + outer.D * inner.D
      Ty = outer.C * inner.Tx + outer.D * inner.Ty + outer.Ty }

/// Build the affine for an SRef. GDS convention: apply reflection
/// about X axis FIRST (if Reflected), then uniform scale by Mag,
/// then rotate Angle (CCW), then translate by Origin.
let private fromSref (s: SRef) : Affine =
    let rad = s.Angle * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = s.Mag
    // Without reflect: [mag*cosA, -mag*sinA; mag*sinA, mag*cosA]
    // With reflect (about X):
    //   F_X = [1 0; 0 -1]  then S * F_X = [mag 0; 0 -mag]
    //   R * S * F_X = [mag*cosA, mag*sinA; mag*sinA, -mag*cosA]
    if s.Reflected then
        { A = mag * cosA;  B = mag * sinA
          C = mag * sinA;  D = -mag * cosA
          Tx = float s.Origin.X; Ty = float s.Origin.Y }
    else
        { A = mag * cosA;  B = -mag * sinA
          C = mag * sinA;  D = mag * cosA
          Tx = float s.Origin.X; Ty = float s.Origin.Y }

/// Base affine for an ARef instance (i=0, j=0). Per-instance offset
/// is added to (Tx, Ty) at expansion time.
let private fromArefBase (a: ARef) : Affine =
    let rad = a.Angle * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = a.Mag
    if a.Reflected then
        { A = mag * cosA;  B = mag * sinA
          C = mag * sinA;  D = -mag * cosA
          Tx = float a.Origin.X; Ty = float a.Origin.Y }
    else
        { A = mag * cosA;  B = -mag * sinA
          C = mag * sinA;  D = mag * cosA
          Tx = float a.Origin.X; Ty = float a.Origin.Y }

/// Detect the "top" structure: one that no other structure
/// references via SRef/ARef. If multiple candidates (or none),
/// fall back to the first structure in the file.
let private findTop (lib: Library) : Structure =
    let referenced = System.Collections.Generic.HashSet<string>()
    for s in lib.Structures do
        for el in s.Elements do
            match el with
            | SRef sr -> referenced.Add sr.StructureName |> ignore
            | ARef ar -> referenced.Add ar.StructureName |> ignore
            | _ -> ()
    lib.Structures
    |> List.tryFind (fun s -> not (referenced.Contains s.Name))
    |> Option.defaultWith (fun () -> List.head lib.Structures)

/// Walk the GDS hierarchy starting from the top cell and produce a
/// flat list of polygons with all SRef / ARef transforms applied.
/// O(N) in the total number of polygons after expansion (which can
/// be 100s of thousands for a production SRAM macro).
let flatten (lib: Library) : FlatPolygon array =
    let byName =
        lib.Structures
        |> List.map (fun s -> s.Name, s)
        |> Map.ofList
    let top = findTop lib
    let result = System.Collections.Generic.List<FlatPolygon>()
    let rec walk (struc: Structure) (xform: Affine) =
        struc.Elements
        |> List.iteri (fun idx el ->
            match el with
            | Boundary b ->
                let pts =
                    b.Points
                    |> List.map (apply xform)
                    |> List.toArray
                result.Add {
                    Layer = b.Layer
                    DataType = b.DataType
                    Points = pts
                    SourceStructure = struc.Name
                    SourceIndex = idx }
            | SRef sr ->
                match Map.tryFind sr.StructureName byName with
                | None -> ()
                | Some child ->
                    walk child (compose xform (fromSref sr))
            | ARef ar ->
                match Map.tryFind ar.StructureName byName with
                | None -> ()
                | Some child when ar.Cols > 0 && ar.Rows > 0 ->
                    let baseXform = fromArefBase ar
                    let colStepX = (float ar.ColPitch.X - float ar.Origin.X) / float ar.Cols
                    let colStepY = (float ar.ColPitch.Y - float ar.Origin.Y) / float ar.Cols
                    let rowStepX = (float ar.RowPitch.X - float ar.Origin.X) / float ar.Rows
                    let rowStepY = (float ar.RowPitch.Y - float ar.Origin.Y) / float ar.Rows
                    for r in 0 .. ar.Rows - 1 do
                        for c in 0 .. ar.Cols - 1 do
                            let instXform =
                                { baseXform with
                                    Tx = baseXform.Tx + float c * colStepX + float r * rowStepX
                                    Ty = baseXform.Ty + float c * colStepY + float r * rowStepY }
                            walk child (compose xform instXform)
                | _ -> ()
            | _ -> ())
    walk top identityXform
    result.ToArray()
