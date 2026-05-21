module Rekolektion.Viz.Core.Routing.Draft

/// Which way the L-shape bends when the user is drawing diagonally.
/// `HorizontalFirst` runs along X then up/down to the target Y;
/// `VerticalFirst` runs along Y then across to the target X.
/// Flipped by the user mid-route (KiCad's `/` posture key).
type DraftPosture = HorizontalFirst | VerticalFirst

/// One axis-aligned rectangle that will be emitted into the cell
/// when the route finishes. `Width` is captured into the bbox at
/// segment build time; this record holds only the final bbox.
type DraftSegment = {
    Layer : int * int
    X1    : int64
    Y1    : int64
    X2    : int64
    Y2    : int64
}

/// In-flight route the user is drawing. `Points` holds the anchor
/// followed by each click-fixed corner; consecutive pairs become an
/// L-shape under the active `Posture`. `Cursor` (when `Some`) is the
/// live mouse position, generating a tentative L from the last fixed
/// point — rendered as preview, not yet committed.
type DraftRoute = {
    Layer   : int * int
    Width   : int64
    Posture : DraftPosture
    Points  : (int64 * int64) list
    Cursor  : (int64 * int64) option
}

/// Decompose p1 → p2 into 0, 1, or 2 axis-aligned rectangles. Zero
/// when degenerate, one when straight along an axis, two for the
/// L-shape under the active posture.
let private lShape
        (layer: int * int)
        (width: int64)
        (posture: DraftPosture)
        (p1: int64 * int64)
        (p2: int64 * int64)
        : DraftSegment list =
    let (x1, y1) = p1
    let (x2, y2) = p2
    let half = width / 2L
    let mkRect (ax: int64) (ay: int64) (bx: int64) (by: int64) : DraftSegment = {
        Layer = layer
        X1 = (min ax bx) - half
        Y1 = (min ay by) - half
        X2 = (max ax bx) + half
        Y2 = (max ay by) + half
    }
    if x1 = x2 && y1 = y2 then []
    elif x1 = x2 || y1 = y2 then [ mkRect x1 y1 x2 y2 ]
    else
        match posture with
        | HorizontalFirst ->
            [ mkRect x1 y1 x2 y1
              mkRect x2 y1 x2 y2 ]
        | VerticalFirst ->
            [ mkRect x1 y1 x1 y2
              mkRect x1 y2 x2 y2 ]

/// Begin a new route on `layer` with `width`, anchored at `anchor`.
let start
        (layer: int * int)
        (width: int64)
        (anchor: int64 * int64)
        : DraftRoute = {
    Layer = layer
    Width = width
    Posture = HorizontalFirst
    Points = [ anchor ]
    Cursor = None
}

/// Update the live cursor position. Tentative L re-derives off this.
let setCursor (cursor: int64 * int64) (r: DraftRoute) : DraftRoute =
    { r with Cursor = Some cursor }

/// Commit the tentative L by appending cursor to `Points`. No-op
/// when cursor is None (nothing to fix).
let fix (r: DraftRoute) : DraftRoute =
    match r.Cursor with
    | None -> r
    | Some c -> { r with Points = r.Points @ [c]; Cursor = None }

/// Remove the last fixed corner. The anchor is preserved — popping
/// past the first point is a no-op so the route stays alive.
let pop (r: DraftRoute) : DraftRoute =
    match List.rev r.Points with
    | []           -> r
    | [_]          -> r
    | _ :: rest    -> { r with Points = List.rev rest }

/// Flip L-shape orientation. Affects both tentative and future fixes;
/// already-fixed segments rebuild under the new posture too — the
/// `Points` list is the truth, posture is just how we draw between.
let flipPosture (r: DraftRoute) : DraftRoute =
    let next =
        match r.Posture with
        | HorizontalFirst -> VerticalFirst
        | VerticalFirst   -> HorizontalFirst
    { r with Posture = next }

/// Rectangles produced by the fixed `Points` list, in commit order.
/// Excludes the tentative segment.
let fixedSegments (r: DraftRoute) : DraftSegment list =
    r.Points
    |> List.pairwise
    |> List.collect (fun (a, b) -> lShape r.Layer r.Width r.Posture a b)

/// Rectangles for the live tentative L from the last fixed point to
/// the current cursor. Empty when cursor is None or the route has
/// only its anchor.
let tentativeSegments (r: DraftRoute) : DraftSegment list =
    match List.tryLast r.Points, r.Cursor with
    | Some last, Some cursor -> lShape r.Layer r.Width r.Posture last cursor
    | _ -> []

/// Fixed + tentative segments in render order. Used by the canvas
/// overlay so the user sees the route grow as they draw.
let allSegments (r: DraftRoute) : DraftSegment list =
    fixedSegments r @ tentativeSegments r

/// Segments to write into the cell on FinishRoute. Includes the
/// tentative L (so finishing on a target pad commits the in-flight
/// segment too); callers that want fixed-only should use
/// `fixedSegments` instead.
let finishSegments (r: DraftRoute) : DraftSegment list =
    let lastPoints =
        match r.Cursor with
        | Some c -> r.Points @ [c]
        | None   -> r.Points
    lastPoints
    |> List.pairwise
    |> List.collect (fun (a, b) -> lShape r.Layer r.Width r.Posture a b)

/// Square endpoint pads at the route's anchor and final point on
/// the route's layer. Used by `RouteFinish` so vias can land on
/// either end of the wire without breaking the layer's via-
/// enclosure rule. `padSide` comes from `Routing.Pads.endpointPadSide`
/// (DRC-driven) — DBU side length of the square.
///
/// The "final point" is the cursor when set, otherwise the last
/// fixed corner (mirrors `finishSegments` semantics — finishing on
/// a target pad commits the in-flight L too).
let endpointPads (padSide: int64) (r: DraftRoute) : DraftSegment list =
    if padSide <= 0L then [] else
    let half = padSide / 2L
    let mkPad ((cx, cy): int64 * int64) : DraftSegment = {
        Layer = r.Layer
        X1 = cx - half
        Y1 = cy - half
        X2 = cx + half
        Y2 = cy + half
    }
    let anchor =
        match r.Points with
        | first :: _ -> Some first
        | [] -> None
    let finalPoint =
        match r.Cursor, List.tryLast r.Points with
        | Some c, _ -> Some c
        | None, last -> last
    // Dedupe when anchor and final point coincide (single-click
    // route, no cursor yet) — one pad covers it.
    let points =
        match anchor, finalPoint with
        | Some a, Some f when a = f -> [a]
        | _ ->
            [ if anchor.IsSome then yield anchor.Value
              if finalPoint.IsSome then yield finalPoint.Value ]
    points |> List.map mkPad

/// Convert a list of DraftSegments into the FlatPolygon shape the
/// DRC engine consumes. Each segment becomes a closed rectangle
/// with a synthetic SourceStructure tag so live-DRC violations on
/// the draft can be told apart from cell violations downstream.
let toFlatPolygons
        (segments: DraftSegment list)
        : Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array =
    segments
    |> List.mapi (fun i seg ->
        let pts : Rekolektion.Viz.Core.Rkt.Types.Point array =
            [|
                { X = seg.X1; Y = seg.Y1 }
                { X = seg.X2; Y = seg.Y1 }
                { X = seg.X2; Y = seg.Y2 }
                { X = seg.X1; Y = seg.Y2 }
                { X = seg.X1; Y = seg.Y1 }
            |]
        let (num, dt) = seg.Layer
        ({
            Layer = num
            DataType = dt
            Points = pts
            SourceStructure = "<draft-route>"
            SourceIndex = i
            TopInstanceIndex = None
        } : Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon))
    |> List.toArray
