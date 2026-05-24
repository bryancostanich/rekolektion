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
    /// WireId of the rect under the cursor, when present. `None`
    /// means the rect was authored without a wire tag (pre-WireId
    /// or hand-drawn geometry) — projectGeometry treats this as
    /// a single-rect wire (no neighbour lookup, just the dragged
    /// segment + L-corner bridges on each anchored end), and the
    /// commit path stamps a fresh WireId on the new rects.
    WireId     : int option
    CellName   : string
    /// Index of the seed rect the user clicked on. The commit
    /// path uses this together with `GroupIndices` to know which
    /// rects to remove from the cell.
    SegmentIdx : int
    /// Indices of every rect in the seed's collinear-abutting
    /// group (always includes `SegmentIdx`). Three abutting rects
    /// that share a Y line are dragged as one virtual segment;
    /// commit replaces them with a single merged rect at the new
    /// position.
    GroupIndices : int list
    /// The virtual segment: bbox-union of every rect in the
    /// collinear group, projected as one Rectangle. Drag is
    /// computed against this synthesised rect, so the user's
    /// click on any group member moves the whole group together.
    /// When the group is just the seed (no abutting rects), this
    /// is the seed rect verbatim.
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
    /// Shift state at pickup. Captured at mouse-down so the
    /// modifier release mid-drag doesn't change the click's
    /// selection semantics. Only consumed by the click-without-
    /// drag path (delta = 0 commit → wire selection); ignored by
    /// the actual segment drag.
    ShiftAtPickup : bool
    /// Other wires that should move alongside the picked one,
    /// populated when the picked rect was part of the current
    /// selection. Each entry is a separate wire (one or more
    /// collinear-abutting rects) that gets translated by the
    /// same vector as the picked group.
    Extras : DragExtra list
}

/// One "extra" wire that follows the picked drag. Mirrors the
/// picked-wire fields but without pickup coords (extras share the
/// picked's Delta and don't track the cursor themselves).
and DragExtra = {
    CellName     : string
    GroupIndices : int list
    Original     : Rectangle
    Axis         : Wire.SegmentAxis
}

/// Begin a drag at the picked-up rect. Auto-groups collinear-
/// abutting rects in the same cell into a single virtual segment
/// so a wire stored as multiple legs along one Y line drags as
/// one piece (and merges to one rect on commit).
let start
        (wireId : int option)
        (cellName : string)
        (idx : int)
        (r : Rectangle)
        (pickupX : int64)
        (pickupY : int64)
        (shiftAtPickup : bool)
        (selection : Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>)
        (doc : Document) : DragState =
    let group = Wire.collinearGroupOf cellName idx doc
    let groupIndices, groupRects =
        if List.isEmpty group then [ idx ], [ r ]
        else group |> List.unzip
    let (uXLo, uYLo, uXHi, uYHi) = Wire.unionBbox groupRects
    let virtualRect : Rectangle =
        { r with X1 = uXLo; Y1 = uYLo; X2 = uXHi; Y2 = uYHi }
    // Build extras from selection. Rule: extras fire ONLY when
    // the picked rect is itself part of the current selection —
    // clicking a non-selected rect drags it alone, no surprise
    // multi-wire moves. The picked group's indices are excluded
    // so we don't list them twice.
    let pickedKey : Rekolektion.Viz.Core.Layout.Flatten.PolyKey =
        { Cell = cellName; Index = idx; TopInstance = None }
    let pickedSet = Set.ofList groupIndices
    let extras =
        if not (Set.contains pickedKey selection) then []
        else
            let mutable visited = pickedSet
            let acc = System.Collections.Generic.List<DragExtra>()
            for key in selection do
                if key.TopInstance = None
                   && key.Cell = cellName
                   && not (visited.Contains key.Index) then
                    let extraGroup =
                        Wire.collinearGroupOf cellName key.Index doc
                    if not (List.isEmpty extraGroup) then
                        let gIdxs, gRects = extraGroup |> List.unzip
                        // Mark every member of this group visited
                        // so a second Selection entry in the same
                        // group doesn't add a duplicate extra.
                        for gi in gIdxs do
                            visited <- visited.Add gi
                        let (xLo, yLo, xHi, yHi) = Wire.unionBbox gRects
                        let seedRect = gRects |> List.head
                        let exRect =
                            { seedRect with X1 = xLo; Y1 = yLo
                                            X2 = xHi; Y2 = yHi }
                        acc.Add
                            { CellName = cellName
                              GroupIndices = gIdxs
                              Original = exRect
                              Axis = Wire.segmentAxis exRect }
            acc |> List.ofSeq
    { WireId = wireId; CellName = cellName; SegmentIdx = idx
      GroupIndices = groupIndices
      Original = virtualRect; Axis = Wire.segmentAxis virtualRect
      PickupX = pickupX; PickupY = pickupY; Delta = 0L
      ShiftAtPickup = shiftAtPickup
      Extras = extras }

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

/// Translation vector applied to each extra by the picked drag.
/// The picked wire moves perpendicular to its own axis by Delta;
/// extras translate by the same VECTOR — so a same-axis extra
/// moves correctly along its own perpendicular, and a cross-axis
/// extra slides along its own parallel (no per-extra stretching,
/// just a rigid translate). Good enough for v1; if cross-axis
/// stretching is needed later, store cursor x/y separately and
/// give each extra its own Delta on its own perpendicular.
let private dragVector (s : DragState) : int64 * int64 =
    match s.Axis with
    | Wire.Horizontal -> 0L, s.Delta
    | Wire.Vertical   -> s.Delta, 0L

/// Project an extra wire under the picked drag's translation
/// vector. Returns the rects to commit for this extra (one per
/// group member, all translated by `(dx, dy)`).
let private projectExtra (s : DragState) (ex : DragExtra) (doc : Document) : Rectangle list =
    let dx, dy = dragVector s
    if dx = 0L && dy = 0L then []
    else
        let cellOpt = doc.Cells |> List.tryFind (fun c -> c.Name = ex.CellName)
        match cellOpt with
        | None -> []
        | Some c ->
            let indexSet = Set.ofList ex.GroupIndices
            c.Elements
            |> List.indexed
            |> List.choose (fun (i, el) ->
                if Set.contains i indexSet then
                    match el with
                    | RectEl r ->
                        Some
                            { r with
                                X1 = r.X1 + dx; X2 = r.X2 + dx
                                Y1 = r.Y1 + dy; Y2 = r.Y2 + dy }
                    | _ -> None
                else None)

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

// Normalised bbox helpers — work in (xLo, yLo, xHi, yHi) so the
// stretch math doesn't have to track which of X1/X2 is the lower
// coordinate (RectEls aren't normalised on disk).
let private bounds (r : Rectangle) =
    min r.X1 r.X2, min r.Y1 r.Y2, max r.X1 r.X2, max r.Y1 r.Y2

let private fromBounds
        (template : Rectangle)
        (xLo : int64) (yLo : int64) (xHi : int64) (yHi : int64) : Rectangle =
    { template with X1 = xLo; Y1 = yLo; X2 = xHi; Y2 = yHi }

/// Which end of the dragged segment does the neighbour `n` touch?
/// Decided by the overlap region on the dragged's PARALLEL axis
/// (its long axis). LowEnd = the X1/Y1 side of dragged, HighEnd =
/// X2/Y2. Returns None when there's no clean touching, OR when n
/// overlaps both ends (rare; n is then longer than dragged on that
/// axis and the stretch is ambiguous).
type private TouchedEnd = LowEnd | HighEnd

let private touchedEnd
        (dragged : Rectangle)
        (axis : Wire.SegmentAxis)
        (n : Rectangle) : TouchedEnd option =
    let (dxLo, dyLo, dxHi, dyHi) = bounds dragged
    let (nxLo, nyLo, nxHi, nyHi) = bounds n
    match axis with
    | Wire.Horizontal ->
        // Dragged is horizontal; its parallel axis is X. n touches
        // dragged at either the left (X1) or right (X2) endpoint.
        let lowOverlap  = nxLo <= dxLo && nxHi >= dxLo
        let highOverlap = nxLo <= dxHi && nxHi >= dxHi
        match lowOverlap, highOverlap with
        | true, false -> Some LowEnd
        | false, true -> Some HighEnd
        | _ -> None
    | Wire.Vertical ->
        let lowOverlap  = nyLo <= dyLo && nyHi >= dyLo
        let highOverlap = nyLo <= dyHi && nyHi >= dyHi
        match lowOverlap, highOverlap with
        | true, false -> Some LowEnd
        | false, true -> Some HighEnd
        | _ -> None

/// Stretch a perpendicular neighbour `n` so its near face follows
/// the dragged segment by `delta`. The "near face" is the bbox
/// face of n that lay inside dragged's bounds on the perpendicular
/// axis. Far face stays put. Result preserves the wire's width on
/// `n`'s parallel axis — only the perpendicular extent changes.
let private stretchPerpendicular
        (dragged : Rectangle)
        (axis : Wire.SegmentAxis)
        (delta : int64)
        (n : Rectangle) : Rectangle =
    let (dxLo, dyLo, dxHi, dyHi) = bounds dragged
    let (nxLo, nyLo, nxHi, nyHi) = bounds n
    match axis with
    | Wire.Horizontal ->
        // dragged moves in Y by delta. n is vertical; its Y faces
        // (top = nyHi, bottom = nyLo) are the candidates for the
        // near face. Pick whichever lies INSIDE dragged's Y range.
        let topInside = nyHi >= dyLo && nyHi <= dyHi
        let botInside = nyLo >= dyLo && nyLo <= dyHi
        if topInside && not botInside then
            fromBounds n nxLo nyLo nxHi (nyHi + delta)
        elif botInside && not topInside then
            fromBounds n nxLo (nyLo + delta) nxHi nyHi
        else
            n
    | Wire.Vertical ->
        let rightInside = nxHi >= dxLo && nxHi <= dxHi
        let leftInside  = nxLo >= dxLo && nxLo <= dxHi
        if rightInside && not leftInside then
            fromBounds n nxLo nyLo (nxHi + delta) nyHi
        elif leftInside && not rightInside then
            fromBounds n (nxLo + delta) nyLo nxHi nyHi
        else
            n

/// Project the new full set of wire rects after the drag commits.
/// The caller replaces every rect carrying `s.WireId` in `s.CellName`
/// with this list and pushes one undo snapshot.
///
/// Behaviour per endpoint of the dragged segment:
///   - Perpendicular flanking neighbour present → STRETCH that
///     neighbour so its near face follows the new position.
///   - No such neighbour → INSERT an L-corner bridge segment to
///     bridge the original endpoint coord to the new position.
///
/// Parallel-axis neighbours (rare — implies a degenerate wire) are
/// left untouched; the user sees a visual gap and can re-route.
///
/// Zero-delta drag returns the original rect unchanged so a
/// click without movement is a no-op commit.
let projectGeometry (s : DragState) (doc : Document) : Rectangle list =
    if s.Delta = 0L then [ s.Original ]
    else
        let dragged = draggedSegment s
        let orig = s.Original
        let perpAxis =
            match s.Axis with
            | Wire.Horizontal -> Wire.Vertical
            | Wire.Vertical -> Wire.Horizontal
        // No WireId → no peer lookup possible; treat as a single-
        // rect wire with both ends anchored. Commit replaces the
        // one picked rect with [dragged + two bridges].
        let neighbours =
            match s.WireId with
            | Some id -> Wire.neighborsOf id s.CellName s.SegmentIdx orig doc
            | None -> []
        // Classify each neighbour: perpendicular (stretch
        // candidate) vs parallel (leave alone). For each
        // perpendicular neighbour, note which end of dragged it
        // touches so the bridge-emit step knows which ends are
        // already handled.
        let mutable lowHandled = false
        let mutable highHandled = false
        let updatedNeighbours =
            neighbours
            |> List.map (fun (idx, n) ->
                if Wire.segmentAxis n <> perpAxis then
                    // Parallel neighbour — out of MVP scope.
                    (idx, n)
                else
                    match touchedEnd orig s.Axis n with
                    | Some LowEnd ->
                        lowHandled <- true
                        idx, stretchPerpendicular orig s.Axis s.Delta n
                    | Some HighEnd ->
                        highHandled <- true
                        idx, stretchPerpendicular orig s.Axis s.Delta n
                    | None ->
                        // No clean touching — couldn't tell which
                        // end. Leave the neighbour as-is.
                        (idx, n))
        // Bridges for the dragged endpoints that didn't get a
        // perpendicular stretch (true terminus, anchored to a pin).
        let bridges =
            match s.Axis with
            | Wire.Horizontal ->
                let halfW = (orig.Y2 - orig.Y1) / 2L
                let xLowCenter  = (min orig.X1 orig.X2) + halfW
                let xHighCenter = (max orig.X1 orig.X2) - halfW
                [ if not lowHandled  then horizontalBridge xLowCenter  orig s.Delta
                  if not highHandled then horizontalBridge xHighCenter orig s.Delta ]
            | Wire.Vertical ->
                let halfW = (orig.X2 - orig.X1) / 2L
                let yLowCenter  = (min orig.Y1 orig.Y2) + halfW
                let yHighCenter = (max orig.Y1 orig.Y2) - halfW
                [ if not lowHandled  then verticalBridge yLowCenter  orig s.Delta
                  if not highHandled then verticalBridge yHighCenter orig s.Delta ]
        // Final composition: every wire rect, with the dragged one
        // moved and the perpendicular neighbours stretched, plus
        // any anchor bridges. No-WireId case has just the dragged
        // rect (no peers exist by definition).
        let allWireRects =
            match s.WireId with
            | Some id -> Wire.segmentsOf id doc
            | None -> []
        let updatedMap =
            updatedNeighbours
            |> List.map (fun (idx, r) -> idx, r)
            |> Map.ofList
        let body =
            match s.WireId with
            | Some _ ->
                allWireRects
                |> List.map (fun (_, idx, r) ->
                    if idx = s.SegmentIdx then dragged
                    else
                        match Map.tryFind idx updatedMap with
                        | Some r' -> r'
                        | None -> r)
            | None ->
                [ dragged ]
        let extraRects =
            s.Extras |> List.collect (fun ex -> projectExtra s ex doc)
        body @ bridges @ extraRects
