module Rekolektion.Viz.Core.Layout.Flatten

open Rekolektion.Viz.Core.Rkt.Types

/// One polygon after the hierarchy has been walked and all SRef /
/// ARef transforms applied. World coordinates are in DBU, same units
/// as the original polygon points.
///
/// `SourceStructure` + `SourceIndex` point at the polygon in its
/// ORIGINAL cell (not the top), so sidecar lookups keyed by
/// (cell, index) still work for instances whose source cell has a
/// sidecar entry. Layer / DataType land as the canonical GDS pair so
/// the renderer's existing layer-z lookup keeps working without
/// change; resolution goes through `Rkt.ToGds.layerToGds`.
type FlatPolygon = {
    Layer: int
    DataType: int
    Points: Point array
    SourceStructure: string
    SourceIndex: int
}

/// One label after the hierarchy has been walked. `Origin` is in
/// flat (top-cell) DBU coordinates so spatial tests against
/// `FlatPolygon.Points` are in the same frame regardless of which
/// sub-cell defined the label.
type FlatLabel = {
    Layer: int
    TextType: int
    Origin: Point
    Text: string
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
/// then rotate Rot (CCW), then translate by Origin.
let private fromSref (s: SRef) : Affine =
    let rad = s.Rot * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = s.Mag
    if s.Reflect then
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
    let rad = a.Rot * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = a.Mag
    if a.Reflect then
        { A = mag * cosA;  B = mag * sinA
          C = mag * sinA;  D = -mag * cosA
          Tx = float a.Origin.X; Ty = float a.Origin.Y }
    else
        { A = mag * cosA;  B = -mag * sinA
          C = mag * sinA;  D = mag * cosA
          Tx = float a.Origin.X; Ty = float a.Origin.Y }

let private layerPair (layer: Layer) : int * int =
    Rekolektion.Viz.Core.Rkt.ToGds.layerToGds layer

/// Detect the "top" cell: one that no other cell references via
/// SRef/ARef. If multiple candidates (or none), fall back to the
/// first cell in the document.
let private findTop (doc: Document) : Cell =
    let referenced = System.Collections.Generic.HashSet<string>()
    for c in doc.Cells do
        for el in c.Elements do
            match el with
            | SRefEl s -> referenced.Add s.Cell |> ignore
            | ARefEl a -> referenced.Add a.Cell |> ignore
            | _ -> ()
    doc.Cells
    |> List.tryFind (fun c -> not (referenced.Contains c.Name))
    |> Option.defaultWith (fun () -> List.head doc.Cells)

let private rectPoints
    (x1: int64) (y1: int64) (x2: int64) (y2: int64) : Point list =
    [ { X = x1; Y = y1 }
      { X = x2; Y = y1 }
      { X = x2; Y = y2 }
      { X = x1; Y = y2 }
      { X = x1; Y = y1 } ]

/// Walk the hierarchy starting from the top cell and produce a flat
/// list of polygons with all SRef / ARef transforms applied. O(N) in
/// the total number of polygons after expansion (which can be
/// 100s of thousands for a production SRAM macro).
let flatten (doc: Document) : FlatPolygon array =
    if List.isEmpty doc.Cells then [||]
    else
        let byName = doc.Cells |> List.map (fun c -> c.Name, c) |> Map.ofList
        let top = findTop doc
        let result = System.Collections.Generic.List<FlatPolygon>()
        let rec walk (cell: Cell) (xform: Affine) =
            cell.Elements
            |> List.iteri (fun idx el ->
                match el with
                | PolyEl p ->
                    let pts =
                        p.Points
                        |> List.map (apply xform)
                        |> List.toArray
                    let n, d = layerPair p.Layer
                    result.Add {
                        Layer = n
                        DataType = d
                        Points = pts
                        SourceStructure = cell.Name
                        SourceIndex = idx }
                | RectEl r ->
                    let pts =
                        rectPoints r.X1 r.Y1 r.X2 r.Y2
                        |> List.map (apply xform)
                        |> List.toArray
                    let n, d = layerPair r.Layer
                    result.Add {
                        Layer = n
                        DataType = d
                        Points = pts
                        SourceStructure = cell.Name
                        SourceIndex = idx }
                | SRefEl sr ->
                    match Map.tryFind sr.Cell byName with
                    | None -> ()
                    | Some child ->
                        walk child (compose xform (fromSref sr))
                | ARefEl ar ->
                    match Map.tryFind ar.Cell byName with
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

/// Same hierarchy walk as `flatten`, but emits Label elements with
/// origins transformed into the top-cell coordinate frame. Used by
/// net highlighting so a label authored in a sub-cell still matches
/// flat polygons by spatial position.
let flattenLabels (doc: Document) : FlatLabel array =
    if List.isEmpty doc.Cells then [||]
    else
        let byName = doc.Cells |> List.map (fun c -> c.Name, c) |> Map.ofList
        let top = findTop doc
        let result = System.Collections.Generic.List<FlatLabel>()
        let rec walk (cell: Cell) (xform: Affine) =
            for el in cell.Elements do
                match el with
                | LabelEl l ->
                    let n, d = layerPair l.Layer
                    result.Add {
                        Layer = n
                        TextType = d
                        Origin = apply xform l.Origin
                        Text = l.Text }
                | SRefEl sr ->
                    match Map.tryFind sr.Cell byName with
                    | None -> ()
                    | Some child ->
                        walk child (compose xform (fromSref sr))
                | ARefEl ar ->
                    match Map.tryFind ar.Cell byName with
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
                | _ -> ()
        walk top identityXform
        result.ToArray()

/// Same as `flattenLabels`, but each label is tagged with the index
/// of the top-cell element it descends from (None for labels
/// authored directly in the top cell). Lets the ratline renderer
/// group labels by top-instance so per-net pin centroids can be
/// computed per cell.
let flattenLabelsTagged (doc: Document) : (int option * FlatLabel) array =
    if List.isEmpty doc.Cells then [||]
    else
        let byName = doc.Cells |> List.map (fun c -> c.Name, c) |> Map.ofList
        let top = findTop doc
        let result = System.Collections.Generic.List<int option * FlatLabel>()
        let rec walk (topIdx: int option) (cell: Cell) (xform: Affine) =
            for el in cell.Elements do
                match el with
                | LabelEl l ->
                    let n, d = layerPair l.Layer
                    let fl : FlatLabel = {
                        Layer = n
                        TextType = d
                        Origin = apply xform l.Origin
                        Text = l.Text }
                    result.Add((topIdx, fl))
                | SRefEl sr ->
                    match Map.tryFind sr.Cell byName with
                    | None -> ()
                    | Some child ->
                        walk topIdx child (compose xform (fromSref sr))
                | ARefEl ar ->
                    match Map.tryFind ar.Cell byName with
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
                                walk topIdx child (compose xform instXform)
                    | _ -> ()
                | _ -> ()
        // At the top cell, tag each child element with its own index
        // BEFORE descending. Sub-recursive walks inherit that tag.
        top.Elements
        |> List.iteri (fun idx el ->
            match el with
            | LabelEl l ->
                let n, d = layerPair l.Layer
                let fl : FlatLabel = {
                    Layer = n
                    TextType = d
                    Origin = l.Origin
                    Text = l.Text }
                result.Add((None, fl))
            | SRefEl sr ->
                match Map.tryFind sr.Cell byName with
                | None -> ()
                | Some child ->
                    walk (Some idx) child (fromSref sr)
            | ARefEl ar ->
                match Map.tryFind ar.Cell byName with
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
                            walk (Some idx) child (compose identityXform instXform)
                | _ -> ()
            | _ -> ())
        result.ToArray()

/// Flatten just one top-level SRef's subtree. The returned polygons
/// are in world coordinates with the SRef's own transform composed
/// in, identical to what `flatten` produces for that branch — the
/// difference is the result is tagged-by-instance so dimension
/// overlay / DRC can compare instances pairwise without re-walking
/// the whole hierarchy. Returns an empty array if `topInstanceIdx`
/// doesn't point to a top-level SRef whose target cell is present.
let flattenInstance (doc: Document) (topInstanceIdx: int) : FlatPolygon array =
    if List.isEmpty doc.Cells then [||]
    else
        let byName = doc.Cells |> List.map (fun c -> c.Name, c) |> Map.ofList
        let top = findTop doc
        let topSref =
            top.Elements
            |> List.indexed
            |> List.tryPick (fun (idx, el) ->
                if idx = topInstanceIdx then
                    match el with
                    | SRefEl sr -> Some sr
                    | _ -> None
                else None)
        match topSref with
        | None -> [||]
        | Some sr ->
            match Map.tryFind sr.Cell byName with
            | None -> [||]
            | Some childCell ->
                let result = System.Collections.Generic.List<FlatPolygon>()
                let rec walk (cell: Cell) (xform: Affine) =
                    cell.Elements
                    |> List.iteri (fun idx el ->
                        match el with
                        | PolyEl p ->
                            let pts =
                                p.Points
                                |> List.map (apply xform)
                                |> List.toArray
                            let n, d = layerPair p.Layer
                            result.Add {
                                Layer = n
                                DataType = d
                                Points = pts
                                SourceStructure = cell.Name
                                SourceIndex = idx }
                        | RectEl r ->
                            let pts =
                                rectPoints r.X1 r.Y1 r.X2 r.Y2
                                |> List.map (apply xform)
                                |> List.toArray
                            let n, d = layerPair r.Layer
                            result.Add {
                                Layer = n
                                DataType = d
                                Points = pts
                                SourceStructure = cell.Name
                                SourceIndex = idx }
                        | SRefEl sr2 ->
                            match Map.tryFind sr2.Cell byName with
                            | None -> ()
                            | Some child ->
                                walk child (compose xform (fromSref sr2))
                        | ARefEl ar ->
                            match Map.tryFind ar.Cell byName with
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
                walk childCell (fromSref sr)
                result.ToArray()

/// Top-cell-direct paint only — every PolyEl/RectEl/PathEl
/// authored at the top of the doc, without walking SRefs/ARefs.
/// Returned in flat (top-cell) DBU coords (no transform applied
/// since these elements ARE the top frame). Used by Tighten so
/// non-instance geometry like power straps participates as a
/// neighbor for tighten-toward.
let flattenTopCellDirect (doc: Document) : FlatPolygon array =
    if List.isEmpty doc.Cells then [||]
    else
        let top = findTop doc
        let result = System.Collections.Generic.List<FlatPolygon>()
        top.Elements
        |> List.iteri (fun idx el ->
            match el with
            | PolyEl p ->
                let n, d = layerPair p.Layer
                result.Add {
                    Layer = n
                    DataType = d
                    Points = p.Points |> List.toArray
                    SourceStructure = top.Name
                    SourceIndex = idx }
            | RectEl r ->
                let n, d = layerPair r.Layer
                let pts = rectPoints r.X1 r.Y1 r.X2 r.Y2 |> List.toArray
                result.Add {
                    Layer = n
                    DataType = d
                    Points = pts
                    SourceStructure = top.Name
                    SourceIndex = idx }
            | PathEl p ->
                let n, d = layerPair p.Layer
                result.Add {
                    Layer = n
                    DataType = d
                    Points = p.Points |> List.toArray
                    SourceStructure = top.Name
                    SourceIndex = idx }
            | _ -> ())
        result.ToArray()
