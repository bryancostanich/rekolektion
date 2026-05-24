/// In-flight perpendicular drag of an existing wire segment
/// (route_editing_plan.md v1.1, Respect mode).
///
/// The user clicks on a committed wire segment, drags the mouse,
/// and on mouse-up the segment slides perpendicular to its axis.
/// Endpoints anchored to pins/pads/vias stay put; the wire grows
/// L-shaped bridge segments to bridge from the fixed anchor to the
/// segment's new perpendicular position.
///
/// This module is the pure projection: given the drag state and
/// the document, produce the new list of wire rects. The canvas
/// owns the mouse-down / move / up dispatch and the document
/// mutation.
module Rekolektion.Viz.Core.Routing.SegmentDrag

open Rekolektion.Viz.Core.Rkt.Types

/// State of an in-flight drag. Created on mouse-down; updated on
/// each mouse-move (only `Delta` changes); consumed on mouse-up
/// to mutate the document.
type DragState = {
    WireId     : int
    CellName   : string
    /// Index of the dragged rect in the cell at pickup time.
    /// The commit path looks neighbours up by WireId, not by
    /// index, so it's safe even if other tools reorder elements
    /// mid-drag (none do today, but the invariant matters).
    SegmentIdx : int
    /// Original rect at pickup. Drag is computed against this
    /// snapshot — moving the mouse doesn't accumulate, it
    /// re-projects.
    Original   : Rectangle
    Axis       : Wire.SegmentAxis
    /// World-coord pickup point. Mouse-move computes
    /// `Delta = cursor - pickup` along the perpendicular axis.
    PickupX    : int64
    PickupY    : int64
    /// Perpendicular offset from pickup. For Horizontal segments
    /// this is the Y delta (segment slides up/down); for Vertical
    /// it's the X delta.
    Delta      : int64
}

/// Begin a drag at the picked-up segment.
let start
        (wireId : int)
        (cellName : string)
        (idx : int)
        (r : Rectangle)
        (pickupX : int64)
        (pickupY : int64) : DragState =
    { WireId = wireId; CellName = cellName; SegmentIdx = idx
      Original = r; Axis = Wire.segmentAxis r
      PickupX = pickupX; PickupY = pickupY; Delta = 0L }

/// Update the drag with the live cursor position. Off-axis cursor
/// motion is ignored — Manhattan-only is the explicit v1
/// constraint (route_editing_plan.md §"Guiding principles").
let setCursor (cursorX : int64) (cursorY : int64) (s : DragState) : DragState =
    let d =
        match s.Axis with
        | Wire.Horizontal -> cursorY - s.PickupY
        | Wire.Vertical   -> cursorX - s.PickupX
    { s with Delta = d }

/// The dragged segment at its current proposed position. Just the
/// original rect translated by `Delta` on the perpendicular axis.
let draggedSegment (s : DragState) : Rectangle =
    let r = s.Original
    match s.Axis with
    | Wire.Horizontal ->
        { r with Y1 = r.Y1 + s.Delta; Y2 = r.Y2 + s.Delta }
    | Wire.Vertical ->
        { r with X1 = r.X1 + s.Delta; X2 = r.X2 + s.Delta }

/// Bridge rect for an anchored endpoint of a horizontal segment.
/// Builds a vertical rect at `xEndpoint` spanning from the
/// original Y range to the dragged Y range — the L-corner that
/// keeps the pin connected when the segment slides.
///
/// The wire's width is preserved (it's already the original
/// rect's Y span). When `Delta = 0` the bridge collapses to the
/// width of the original — no visual change, harmless.
let private horizontalBridge
        (xEndpoint : int64) (original : Rectangle) (delta : int64) : Rectangle =
    let halfW = (original.Y2 - original.Y1) / 2L
    let yLoOrig = original.Y1
    let yHiOrig = original.Y2
    let yLoNew  = original.Y1 + delta
    let yHiNew  = original.Y2 + delta
    { original with
        X1 = xEndpoint - halfW
        X2 = xEndpoint + halfW
        Y1 = min yLoOrig yLoNew
        Y2 = max yHiOrig yHiNew }

/// Bridge rect for an anchored endpoint of a vertical segment.
let private verticalBridge
        (yEndpoint : int64) (original : Rectangle) (delta : int64) : Rectangle =
    let halfW = (original.X2 - original.X1) / 2L
    let xLoOrig = original.X1
    let xHiOrig = original.X2
    let xLoNew  = original.X1 + delta
    let xHiNew  = original.X2 + delta
    { original with
        X1 = min xLoOrig xLoNew
        X2 = max xHiOrig xHiNew
        Y1 = yEndpoint - halfW
        Y2 = yEndpoint + halfW }

/// Project the new full set of wire rects after the drag commits.
/// The caller replaces every rect carrying `s.WireId` in the
/// document with this list and pushes one undo snapshot.
///
/// MVP scope: handles the single-segment-wire case (the wire was
/// one rect; drag produces three — left bridge, dragged segment,
/// right bridge). Multi-segment wires fall back to "drag just
/// the picked segment, leave the rest untouched" — visually
/// disconnected at the drag's endpoints until the user fixes it
/// up. Stretch-of-flanking-segments is a follow-up.
///
/// Zero-delta drag returns the original rect unchanged so a
/// click without movement is a no-op commit.
let projectGeometry (s : DragState) (doc : Document) : Rectangle list =
    if s.Delta = 0L then [ s.Original ]
    else
        let allWireRects = Wire.segmentsOf s.WireId doc
        let singleSegment = List.length allWireRects = 1
        let dragged = draggedSegment s
        if not singleSegment then
            // Multi-segment: replace the dragged rect only.
            // Other rects in the wire stay as-is (caller handles
            // the per-rect substitution). See module-doc for
            // the stretch-flanking follow-up.
            allWireRects
            |> List.map (fun (_, idx, r) ->
                if idx = s.SegmentIdx then dragged else r)
        else
            let orig = s.Original
            match s.Axis with
            | Wire.Horizontal ->
                let halfW = (orig.Y2 - orig.Y1) / 2L
                let xLeft  = (min orig.X1 orig.X2) + halfW
                let xRight = (max orig.X1 orig.X2) - halfW
                [ horizontalBridge xLeft  orig s.Delta
                  dragged
                  horizontalBridge xRight orig s.Delta ]
            | Wire.Vertical ->
                let halfW = (orig.X2 - orig.X1) / 2L
                let yBot = (min orig.Y1 orig.Y2) + halfW
                let yTop = (max orig.Y1 orig.Y2) - halfW
                [ verticalBridge yBot orig s.Delta
                  dragged
                  verticalBridge yTop orig s.Delta ]
