module Rekolektion.Viz.Core.Net.LabelFlood

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.Core.Layout.Picking

/// Polygon entry used during flood-fill. Layer key is the canonical
/// `(number, datatype)` pair so `NetEntry.Polygons` round-trips into
/// the sidecar without info loss.
type private PolyEntry = {
    StructureName: string
    Index        : int
    Layer        : int
    DataType     : int
    Points       : Point list
}

let private bbox (pts: Point list) : (int64 * int64 * int64 * int64) =
    let xs = pts |> List.map (fun p -> p.X)
    let ys = pts |> List.map (fun p -> p.Y)
    List.min xs, List.min ys, List.max xs, List.max ys

let private bboxOverlap a b =
    let (ax0, ay0, ax1, ay1) = a
    let (bx0, by0, bx1, by1) = b
    not (ax1 < bx0 || bx1 < ax0 || ay1 < by0 || by1 < ay0)

/// Two polygons on the SAME layer "touch" if their bboxes overlap and
/// at least one vertex of either lies inside (or on the edge of) the
/// other. Coarse but correct for the rectilinear shapes rekolektion
/// emits.
let private touch (a: Point list) (b: Point list) : bool =
    bboxOverlap (bbox a) (bbox b)
    && (
        a |> List.exists (fun p -> pointInPolygon p b)
        || b |> List.exists (fun p -> pointInPolygon p a)
    )

let private layerPair (layer: Layer) : int * int =
    Rekolektion.Viz.Core.Rkt.ToGds.layerToGds layer

/// Flatten every poly-bearing element across the document into a list
/// of `PolyEntry`. Only `PolyEl` and `RectEl` contribute; paths,
/// labels, refs, and ports do not (they aren't fill geometry).
let private flatten (doc: Document) : PolyEntry list =
    doc.Cells
    |> List.collect (fun c ->
        c.Elements
        |> List.indexed
        |> List.choose (fun (i, e) ->
            match e with
            | PolyEl p ->
                let n, d = layerPair p.Layer
                Some {
                    StructureName = c.Name
                    Index = i
                    Layer = n
                    DataType = d
                    Points = p.Points
                }
            | RectEl r ->
                let n, d = layerPair r.Layer
                let pts : Point list = [
                    { X = r.X1; Y = r.Y1 }
                    { X = r.X2; Y = r.Y1 }
                    { X = r.X2; Y = r.Y2 }
                    { X = r.X1; Y = r.Y2 }
                    { X = r.X1; Y = r.Y1 }
                ]
                Some {
                    StructureName = c.Name
                    Index = i
                    Layer = n
                    DataType = d
                    Points = pts
                }
            | _ -> None))

let private classOfName (n: string) : NetClass =
    let upper = n.ToUpperInvariant()
    if   upper = "VPWR" || upper = "VDD"      then Power
    elif upper = "VGND" || upper = "VSS"      then Ground
    elif upper.StartsWith "CLK"               then Clock
    else Signal

/// Tagged label: cell-of-origin, layer pair, text, and origin point
/// in the cell's local frame. Only `LabelEl` with non-empty text
/// participates.
type private LabelEntry = {
    StructureName: string
    Layer        : int
    DataType     : int
    Text         : string
    Origin       : Point
}

let private collectLabels (doc: Document) : LabelEntry list =
    doc.Cells
    |> List.collect (fun c ->
        c.Elements
        |> List.choose (function
            | LabelEl l when l.Text <> "" ->
                let n, d = layerPair l.Layer
                Some {
                    StructureName = c.Name
                    Layer = n
                    DataType = d
                    Text = l.Text
                    Origin = l.Origin
                }
            | _ -> None))

/// Build NetMap from labels in the document. For each `LabelEl`,
/// find the polygon on the same layer that contains the label point,
/// then flood-fill across same-layer touching polygons. Polygons not
/// reached by any label are not included in the result (they show as
/// net-unknown in the inspector).
let derive (doc: Document) : Map<string, NetEntry> =
    let polys = flatten doc
    let labels = collectLabels doc

    labels
    |> List.fold (fun (acc: Map<string, NetEntry>) lbl ->
        let seed =
            polys
            |> List.tryFind (fun p ->
                p.Layer = lbl.Layer && pointInPolygon lbl.Origin p.Points)
        match seed with
        | None -> acc
        | Some s0 ->
            // BFS over same-layer polygons that touch.
            let sameLayer =
                polys
                |> List.filter (fun p ->
                    p.Layer = s0.Layer && p.DataType = s0.DataType)
            let visited = System.Collections.Generic.HashSet<string * int>()
            let queue = System.Collections.Generic.Queue<PolyEntry>()
            queue.Enqueue s0 |> ignore
            visited.Add (s0.StructureName, s0.Index) |> ignore
            let collected = System.Collections.Generic.List<PolyEntry>()
            while queue.Count > 0 do
                let cur = queue.Dequeue()
                collected.Add cur
                for cand in sameLayer do
                    let key = (cand.StructureName, cand.Index)
                    if not (visited.Contains key) && touch cur.Points cand.Points then
                        visited.Add key |> ignore
                        queue.Enqueue cand |> ignore
            let polyRefs =
                collected
                |> Seq.map (fun p ->
                    { Structure = p.StructureName
                      Layer = p.Layer
                      DataType = p.DataType
                      Index = p.Index })
                |> Seq.toList
            let entry =
                match Map.tryFind lbl.Text acc with
                | Some existing ->
                    { existing with
                        Polygons = existing.Polygons @ polyRefs |> List.distinct }
                | None ->
                    { Name = lbl.Text
                      Class = classOfName lbl.Text
                      Polygons = polyRefs }
            Map.add lbl.Text entry acc) Map.empty
