module Rekolektion.Viz.Core.Net.LabelFlood

open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.Core.Layout.Picking

/// Axis-aligned bbox of a polygon. Cheap reject before edge math.
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
/// other. This is a coarse approximation of polygon intersection that
/// is correct for the rectilinear shapes rekolektion emits.
let private touch (a: Point list) (b: Point list) : bool =
    bboxOverlap (bbox a) (bbox b)
    && (
        a |> List.exists (fun p -> pointInPolygon p b)
        || b |> List.exists (fun p -> pointInPolygon p a)
    )

type private PolyEntry = {
    StructureName: string
    Index        : int
    Layer        : int
    DataType     : int
    Points       : Point list
}

let private flatten (lib: Library) : PolyEntry list =
    lib.Structures
    |> List.collect (fun s ->
        s.Elements
        |> List.indexed
        |> List.choose (fun (i, e) ->
            match e with
            | Boundary b ->
                Some {
                    StructureName = s.Name
                    Index = i
                    Layer = b.Layer
                    DataType = b.DataType
                    Points = b.Points
                }
            | _ -> None))

let private classOfName (n: string) : NetClass =
    let upper = n.ToUpperInvariant()
    if   upper = "VPWR" || upper = "VDD"      then Power
    elif upper = "VGND" || upper = "VSS"      then Ground
    elif upper.StartsWith "CLK"               then Clock
    else Signal

/// Build NetMap from labels. For each Text element, find the polygon
/// on the same layer that contains the label point. Then flood-fill
/// across same-layer touching polygons. Polygons not reached by any
/// label are not included in the NetMap (they show as net-unknown
/// in the inspector).
let derive (lib: Library) : Map<string, NetEntry> =
    let polys = flatten lib

    let labels =
        lib.Structures
        |> List.collect (fun s ->
            s.Elements
            |> List.choose (function
                | Text t when t.Text <> "" -> Some (s.Name, t)
                | _ -> None))

    labels
    |> List.fold (fun (acc: Map<string, NetEntry>) (_structName, t) ->
        let seed =
            polys
            |> List.tryFind (fun p -> p.Layer = t.Layer && pointInPolygon t.Origin p.Points)
        match seed with
        | None -> acc
        | Some s0 ->
            // BFS over same-layer polygons that touch.
            let sameLayer = polys |> List.filter (fun p -> p.Layer = s0.Layer && p.DataType = s0.DataType)
            let visited = System.Collections.Generic.HashSet<int>()
            let queue = System.Collections.Generic.Queue<PolyEntry>()
            queue.Enqueue s0 |> ignore
            visited.Add (s0.Index + (s0.StructureName.GetHashCode())) |> ignore
            let collected = System.Collections.Generic.List<PolyEntry>()
            while queue.Count > 0 do
                let cur = queue.Dequeue()
                collected.Add cur
                for cand in sameLayer do
                    let key = cand.Index + (cand.StructureName.GetHashCode())
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
                match Map.tryFind t.Text acc with
                | Some existing ->
                    { existing with Polygons = existing.Polygons @ polyRefs |> List.distinct }
                | None ->
                    { Name = t.Text; Class = classOfName t.Text; Polygons = polyRefs }
            Map.add t.Text entry acc) Map.empty
