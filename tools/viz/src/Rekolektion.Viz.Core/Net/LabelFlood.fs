module Rekolektion.Viz.Core.Net.LabelFlood

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.Core.Layout.Picking

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

let private classOfName (n: string) : NetClass =
    let upper = n.ToUpperInvariant()
    if   upper = "VPWR" || upper = "VDD"      then Power
    elif upper = "VGND" || upper = "VSS"      then Ground
    elif upper.StartsWith "CLK"               then Clock
    else Signal

/// Build NetMap from labels in the document. Operates on
/// `Layout.Flatten`'s world-coord polys + labels so a label authored
/// at the TOP cell can anchor to a polygon living inside an SRef'd
/// child cell — that case (e.g. `drn_L` placed at top against a FET
/// drain pin) was silently dropping out of the Nets panel under the
/// previous local-frame implementation.
///
/// For each label: find the world-coord polygon on the same layer
/// that contains the label point, then flood across same-layer
/// touching polygons. Output `PolyRef`s deduplicate by (cell, index)
/// so multiple instances of the same source polygon collapse to one
/// entry — the Nets panel only needs to know the net exists.
let derive (doc: Document) : Map<string, NetEntry> =
    let polys = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
    let labels = Rekolektion.Viz.Core.Layout.Flatten.flattenLabels doc
    let pointsList (p: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon) =
        Array.toList p.Points

    labels
    |> Array.fold (fun (acc: Map<string, NetEntry>) (lbl: Rekolektion.Viz.Core.Layout.Flatten.FlatLabel) ->
        if lbl.Text = "" then acc else
        let seedIdx =
            polys
            |> Array.tryFindIndex (fun p ->
                p.Layer = lbl.Layer
                && pointInPolygon lbl.Origin (pointsList p))
        match seedIdx with
        | None -> acc
        | Some i0 ->
            let s0 = polys.[i0]
            // BFS over same-(layer, datatype) world polygons that
            // touch. Index keyed by flat-array position so the two
            // instances of the same source poly count as separate
            // candidates (their world geometry sits in different
            // places, so flood reaches one only via a real touch).
            let sameLayer =
                polys
                |> Array.mapi (fun i p -> i, p)
                |> Array.filter (fun (_, p) ->
                    p.Layer = s0.Layer && p.DataType = s0.DataType)
            let visited = System.Collections.Generic.HashSet<int>()
            let queue = System.Collections.Generic.Queue<int>()
            queue.Enqueue i0 |> ignore
            visited.Add i0 |> ignore
            let collected = System.Collections.Generic.List<Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon>()
            while queue.Count > 0 do
                let curIdx = queue.Dequeue()
                let cur = polys.[curIdx]
                collected.Add cur
                let curPts = pointsList cur
                for (cIdx, cand) in sameLayer do
                    if not (visited.Contains cIdx)
                       && touch curPts (pointsList cand) then
                        visited.Add cIdx |> ignore
                        queue.Enqueue cIdx |> ignore
            let polyRefs =
                collected
                |> Seq.map (fun p ->
                    { Structure = p.SourceStructure
                      Layer = p.Layer
                      DataType = p.DataType
                      Index = p.SourceIndex })
                |> Seq.distinct
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
