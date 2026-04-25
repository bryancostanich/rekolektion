module Rekolektion.Viz.Core.Layout.Picking

open Rekolektion.Viz.Core.Gds.Types

/// Crossing-number / even-odd rule. Boundary-inclusive: a point that
/// lands exactly on an edge is treated as "in" so the picker doesn't
/// have dead spots between adjacent rectangles.
let pointInPolygon (p: Point) (poly: Point list) : bool =
    // Strip closing point if present so we don't double-count edges.
    let pts =
        match poly with
        | [] -> []
        | _ ->
            let last = List.last poly
            if last = List.head poly then poly |> List.take (List.length poly - 1)
            else poly
    let n = List.length pts
    if n < 3 then false
    else
        let arr = List.toArray pts
        let mutable inside = false
        let mutable onEdge = false
        for i in 0 .. n - 1 do
            let a = arr.[i]
            let b = arr.[(i + 1) % n]
            // Edge inclusion: collinear and within bbox of segment.
            let cross =
                (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)
            let withinX = (min a.X b.X) <= p.X && p.X <= (max a.X b.X)
            let withinY = (min a.Y b.Y) <= p.Y && p.Y <= (max a.Y b.Y)
            if cross = 0L && withinX && withinY then
                onEdge <- true
            // Standard ray cast (point shoots ray to +X). Use inclusive at
            // bottom, exclusive at top to avoid double-counting on y-vertices.
            if (a.Y > p.Y) <> (b.Y > p.Y) then
                let xIntersect =
                    float (b.X - a.X) * float (p.Y - a.Y)
                        / float (b.Y - a.Y) + float a.X
                if float p.X < xIntersect then
                    inside <- not inside
        inside || onEdge

/// Pick the first matching boundary in a structure's element list.
/// Returns the element index alongside so the caller can relate it
/// to a Sidecar PolygonRef.
let pickBoundary (point: Point) (elements: Element list) : (int * Boundary) option =
    elements
    |> List.indexed
    |> List.tryPick (fun (i, e) ->
        match e with
        | Boundary b when pointInPolygon point b.Points -> Some (i, b)
        | _ -> None)
