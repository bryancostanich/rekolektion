module Rekolektion.Viz.Core.Routing.Snap

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten

/// A single snap target: the geometric centroid of a labeled pin
/// polygon plus the label's text so the UI can say "snapped to
/// BL_3" or similar. Source key lets callers de-dupe overlapping
/// targets if needed.
type SnapTarget = {
    X    : int64
    Y    : int64
    Net  : string
    Layer: int
    DataType: int
    /// `(SourceStructure, SourceIndex)` of the polygon backing
    /// this target — handy for tie-breaking and for the UI.
    Source : string * int
}

let private polyBbox (p: FlatPolygon) : int64 * int64 * int64 * int64 =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    for pt in p.Points do
        if pt.X < xMin then xMin <- pt.X
        if pt.X > xMax then xMax <- pt.X
        if pt.Y < yMin then yMin <- pt.Y
        if pt.Y > yMax then yMax <- pt.Y
    xMin, yMin, xMax, yMax

let private contains
        (bbox: int64 * int64 * int64 * int64)
        (x: int64) (y: int64) : bool =
    let (xMin, yMin, xMax, yMax) = bbox
    x >= xMin && x <= xMax && y >= yMin && y <= yMax

/// Build the snap-target set from flat labels + flat polygons.
/// For every `NetName` label, find the same-layer polygon whose
/// bbox contains the label origin, and emit a target at that
/// polygon's bbox centroid. Polygons without a labeled pin are
/// NOT targets — routing wants to snap to declared connection
/// points, not random shapes.
///
/// Same polygon can carry multiple labels (e.g. an alias label
/// plus a port label); we emit one target per label so the UI
/// can attribute hits to whichever net name the user pointed at.
let buildTargets
        (labels: FlatLabel array)
        (polygons: FlatPolygon array)
        : SnapTarget array =
    let result = System.Collections.Generic.List<SnapTarget>()
    for lbl in labels do
        if lbl.Kind <> NetName || System.String.IsNullOrEmpty lbl.Text then ()
        else
            // Same-layer polygon containing the label origin → the
            // pin patch. Bbox center is the routing-target point.
            // Labels carry the same LAYER number as the polygon they
            // mark (drawing vs label datatypes differ — 68/20 drawing
            // vs 68/5 label — so we don't match on datatype). Same
            // rule LabelFlood uses for seed discovery.
            let mutable found = false
            let mutable i = 0
            while not found && i < polygons.Length do
                let p = polygons.[i]
                if p.Layer = lbl.Layer then
                    let bb = polyBbox p
                    if contains bb lbl.Origin.X lbl.Origin.Y then
                        let (xMin, yMin, xMax, yMax) = bb
                        result.Add {
                            X = (xMin + xMax) / 2L
                            Y = (yMin + yMax) / 2L
                            Net = lbl.Text
                            Layer = p.Layer
                            DataType = p.DataType
                            Source = p.SourceStructure, p.SourceIndex
                        }
                        found <- true
                i <- i + 1
    result.ToArray()

/// Find the snap target nearest to `cursor` within `radiusDbu`,
/// or None when nothing is in range. Distance is Euclidean in
/// world DBU; the caller computes the radius from screen pixels
/// at the current zoom.
let nearest
        (targets: SnapTarget array)
        ((cursorX, cursorY): int64 * int64)
        (radiusDbu: int64)
        : SnapTarget option =
    if targets.Length = 0 then None
    else
        let r2 = float radiusDbu * float radiusDbu
        let mutable bestDist = System.Double.MaxValue
        let mutable best : SnapTarget option = None
        for t in targets do
            let dx = float (t.X - cursorX)
            let dy = float (t.Y - cursorY)
            let d2 = dx * dx + dy * dy
            if d2 <= r2 && d2 < bestDist then
                bestDist <- d2
                best <- Some t
        best
