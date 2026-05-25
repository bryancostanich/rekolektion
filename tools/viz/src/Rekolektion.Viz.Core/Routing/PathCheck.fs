/// Path-vs-obstacle collision check.
///
/// The walk-around search returns a `Pt list` and trusts that
/// every axis-aligned segment between consecutive points clears
/// every obstacle in the field. There is no second check — if the
/// search has a bug, the wire commits through a foreign polygon
/// and the user sees a short.
///
/// This module is the second check. Given the same `Bbox array`
/// the search used (already expanded by clearance — what
/// `VisibilityGraph.Prebuilt.Obstacles` holds), it iterates every
/// segment in the path and reports every obstacle whose
/// strict-interior the segment crosses.
///
/// `crossings path obstacles = []` is the necessary correctness
/// post-condition for any routing result. Use it in tests to lock
/// down a regression repro, and in the live canvas to flag any
/// path the search returns that wouldn't survive a real DRC pass.
module Rekolektion.Viz.Core.Routing.PathCheck

open Rekolektion.Viz.Core.Routing.VisibilityGraph

/// One segment of a path crosses one obstacle.
///
/// `Segment` is the (start, end) of the axis-aligned segment that
/// crossed; `ObstacleIndex` is the position of the offending
/// obstacle in the original `Bbox array` so the caller can map
/// back to a FlatPolygon (the array is index-aligned with the
/// `ObstacleSet`'s polygon list).
type Crossing = {
    Segment       : Pt * Pt
    Obstacle      : Bbox
    ObstacleIndex : int
}

/// Strict-interior crossing test for a horizontal segment at `y`
/// running from `x1` to `x2` against bbox `b`. Strict means: a
/// segment exactly on `b.YMin` or `b.YMax` (the clearance-expanded
/// edge) is NOT a crossing — that's the wire's outer edge touching
/// the clearance limit, which is the legal extreme. Same rule
/// `VisibilityGraph` uses for adjacency tests.
let private hSegCrossesBboxInterior
    (y : int64) (x1 : int64) (x2 : int64) (b : Bbox) : bool =
    let xa = min x1 x2
    let xb = max x1 x2
    y > b.YMin && y < b.YMax && xa < b.XMax && xb > b.XMin

let private vSegCrossesBboxInterior
    (x : int64) (y1 : int64) (y2 : int64) (b : Bbox) : bool =
    let ya = min y1 y2
    let yb = max y1 y2
    x > b.XMin && x < b.XMax && ya < b.YMax && yb > b.YMin

/// Whether a segment is axis-aligned. Diagonal segments aren't
/// supported (every router output is manhattan); we treat them as
/// "skip" rather than guess a posture.
let private isAxisAligned (a : Pt) (b : Pt) : bool =
    a.X = b.X || a.Y = b.Y

/// Every (segment, obstacle) crossing in `path`. Order: outer loop
/// over segments, inner over obstacles, so all crossings of the
/// first segment come first. Returns `[]` for an empty or
/// single-point path, or when the path is fully clear.
///
/// `obstacles` MUST be the expanded-bbox array — i.e., the same
/// one the search used. Passing the original (unexpanded) bboxes
/// would miss clearance violations.
let crossings (path : Pt list) (obstacles : Bbox array) : Crossing list =
    let acc = System.Collections.Generic.List<Crossing>()
    let rec loop (pts : Pt list) =
        match pts with
        | [] | [_] -> ()
        | a :: (b :: _ as rest) ->
            if isAxisAligned a b then
                let isHorizontal = a.Y = b.Y
                for i in 0 .. obstacles.Length - 1 do
                    let o = obstacles.[i]
                    let hit =
                        if isHorizontal then
                            hSegCrossesBboxInterior a.Y a.X b.X o
                        else
                            vSegCrossesBboxInterior a.X a.Y b.Y o
                    if hit then
                        acc.Add(
                            { Segment = (a, b)
                              Obstacle = o
                              ObstacleIndex = i })
            loop rest
    loop path
    List.ofSeq acc

/// Convenience: just the count. Zero ⇒ path is clean.
let crossingCount (path : Pt list) (obstacles : Bbox array) : int =
    crossings path obstacles |> List.length

/// Shrink each bbox by `clearance` on every side — converts the
/// expanded bboxes the search uses back into the original polygon
/// bboxes (the actual silicon). A degenerate result (XMin > XMax,
/// YMin > YMax) is benign: hit tests against it always return
/// false, which matches "this polygon was thinner than 2×clearance
/// so its margin entirely subsumes its silicon."
///
/// Use the shrunk bboxes with `crossings` to ask the stricter
/// question: does the path cross any obstacle's actual silicon?
/// Endpoint-in-margin cases (start or goal landed in some
/// obstacle's clearance zone) will produce expanded crossings
/// that are NOT silicon crossings — those are pre-existing
/// snap-target situations the wire can't avoid, not bugs.
let shrinkByClearance (clearance : int64) (obstacles : Bbox array) : Bbox array =
    obstacles
    |> Array.map (fun b ->
        { XMin = b.XMin + clearance; YMin = b.YMin + clearance
          XMax = b.XMax - clearance; YMax = b.YMax - clearance })
