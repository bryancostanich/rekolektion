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
    /// True once `setCursor` has observed a dominant-axis cursor
    /// motion since the last `fix` / `start` — at that point the
    /// posture has been chosen to match the user's first
    /// decisive direction and is locked in for the rest of this
    /// segment. Manual `flipPosture` resets the lock so the next
    /// dominant motion can re-influence the posture. Lock is
    /// cleared by `fix` / `start` so each segment gets a fresh
    /// vote.
    PostureLocked : bool
    Points  : (int64 * int64) list
    Cursor  : (int64 * int64) option
    /// Walk-around corner nodes between the last fixed Point and
    /// the Cursor (ADR-0006). When non-empty, the tentative segment
    /// is decomposed as a polyline through these corners; on `fix`
    /// they all become Points. Empty = straight-L behaviour (the
    /// caller didn't compute an auto-jog path).
    Auto    : (int64 * int64) list
    /// Name of the net the route started on (ADR-0006). Captured
    /// from the snap target at `StartRoute` time and used by the
    /// walk-around dispatch to classify other licons / vias as
    /// "ours" vs "theirs". Empty when no net was attached.
    StartNet : string
    /// Layer of the snap target the route STARTED on. Often the
    /// same as `Layer` (when drawing met1 onto a met1 pin), but
    /// can differ for cross-layer connections (drawing a met2 wire
    /// onto a li1 drn_R pin). RouteFinish reads this to emit the
    /// start-side via stack between `StartLayer` and `Layer`.
    StartLayer : int * int
    /// Layer of the snap target under the cursor when the route
    /// ENDS. `None` when finishing in free air (no snap). Set by
    /// the canvas dispatch on cursor moves over snap targets so
    /// RouteFinish reads it at commit. Same role as `StartLayer`
    /// but for the cursor-side of the wire.
    EndLayer : (int * int) option
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
/// Initialises `StartLayer = layer` — same-layer start is the
/// common case. Cross-layer starts (e.g. a met2 wire onto a li1
/// pin) call `setStartLayer` after `start` with the snap target's
/// actual layer so RouteFinish emits the start-side via stack.
let start
        (layer: int * int)
        (width: int64)
        (anchor: int64 * int64)
        : DraftRoute = {
    Layer = layer
    Width = width
    Posture = HorizontalFirst
    PostureLocked = false
    Points = [ anchor ]
    Cursor = None
    Auto = []
    StartNet = ""
    StartLayer = layer
    EndLayer = None
}

/// Override the snap-target layer at the start side. Canvas calls
/// this after `start` when the snap target the user clicked is on
/// a different layer than the active routing layer.
let setStartLayer (startLayer: int * int) (r: DraftRoute) : DraftRoute =
    { r with StartLayer = startLayer }

/// Update the snap-target layer at the cursor side. Canvas calls
/// this when the cursor moves on/off a same-net snap target during
/// a draft, so RouteFinish knows the end-side layer for via-stack
/// emission.
let setEndLayer (endLayer: (int * int) option) (r: DraftRoute) : DraftRoute =
    { r with EndLayer = endLayer }

/// Stamp the route's StartNet (ADR-0006). The dispatch layer calls
/// this immediately after `start` with the net pulled from the snap
/// target the user clicked. Empty net leaves walk-around in its
/// "treat everything as foreign" default.
let setStartNet (net: string) (r: DraftRoute) : DraftRoute =
    { r with StartNet = net }

/// Threshold for posture auto-lock: the dominant axis of NET
/// motion from the last fixed point to the cursor must be at
/// least `postureFlipRatio`× the other axis before posture
/// locks in. Higher = needs more decisive motion before locking;
/// lower = locks sooner. 2.0 was picked by feel.
[<Literal>]
let private postureFlipRatio = 2L

/// Update the live cursor position. Tentative L re-derives off this.
///
/// Auto is PRESERVED across cursor moves so the wire keeps rendering
/// the latest walk-around corners while the BG search recomputes for
/// the new position. Auto is fully reset on `fix` / `start` /
/// commit.
///
/// **Posture auto-lock** (the "softly driven by mouse direction"
/// behaviour the user asked for): when the cursor moves far enough
/// in one axis from the last fixed point that the axis dominates
/// the other by `postureFlipRatio`×, posture locks in to match —
/// HorizontalFirst when X dominates (long leg runs along the
/// direction the user moved first), VerticalFirst when Y dominates.
/// Once locked, subsequent cursor moves do NOT flip posture: the
/// classic "drew right then down" case keeps the corner where the
/// user expects it instead of snapping each frame as dx/dy ratio
/// crosses. Manual `flipPosture` clears the lock so the user can
/// hand off control or re-influence with a fresh dominant motion.
let setCursor (cursor: int64 * int64) (r: DraftRoute) : DraftRoute =
    let lastFixed = r.Points |> List.tryLast
    let posture', locked' =
        if r.PostureLocked then r.Posture, true
        else
            match lastFixed with
            | None -> r.Posture, false
            | Some (lx, ly) ->
                let dx = abs (fst cursor - lx)
                let dy = abs (snd cursor - ly)
                if dx > dy * postureFlipRatio then HorizontalFirst, true
                elif dy > dx * postureFlipRatio then VerticalFirst, true
                else r.Posture, false
    { r with
        Cursor = Some cursor
        Posture = posture'
        PostureLocked = locked' }

/// Set the walk-around corner sequence between the last fixed Point
/// and the Cursor (ADR-0006). Empty list resets to straight-L.
let setAuto (auto: (int64 * int64) list) (r: DraftRoute) : DraftRoute =
    { r with Auto = auto }

/// Drop intermediate points that are collinear with their neighbours
/// on the same axis. The walk-around's Steiner-point construction
/// emits one corner per obstacle boundary, so multiple consecutive
/// jogs along the same X (or Y) column show up as separate path
/// nodes even when they should be one straight segment. Without
/// this, `finishSegments` turns each into its own rect: a route
/// that should commit as 7 rects shows up as 14 because of stacked
/// V-segments along the start column.
///
/// A point `b` is removed when (`a`, `b`, `c`) all share the same X
/// or all share the same Y. The endpoints (`Points.Head`, cursor)
/// are always preserved.
let simplifyCollinear (pts: (int64 * int64) list) : (int64 * int64) list =
    let rec loop acc remaining =
        match remaining with
        | a :: b :: c :: rest ->
            let sameX = fst a = fst b && fst b = fst c
            let sameY = snd a = snd b && snd b = snd c
            if sameX || sameY then
                loop acc (a :: c :: rest)
            else
                loop (a :: acc) (b :: c :: rest)
        | xs -> (List.rev acc) @ xs
    loop [] pts

/// Commit the tentative segment by appending the auto-router's
/// corner list (`r.Auto`) plus the cursor onto `Points`. The user
/// expects what they SEE in the live preview to be what they
/// commit — if the auto-router has worked out a detour around a
/// foreign feature, the click locks that detour in.
///
/// Materialise the implicit L-corner so committed segments are
/// posture-independent thereafter.  If the last point on the
/// path-so-far is neither X-aligned nor Y-aligned with the cursor,
/// the tentative preview was rendering an L under the CURRENT
/// `r.Posture` — we insert an explicit corner at that L's bend
/// before appending the cursor.  Without this, `fixedSegments`
/// would re-render the committed pair using whatever `r.Posture`
/// happens to be at render time, and a subsequent dominant-axis
/// motion that flips Posture would visually swing the committed
/// corner to the wrong end.  Reported 2026-06-10: "ran a wire up,
/// committed a corner, went right; viz changed the committed
/// route to straight up."  The change is a no-op for axis-aligned
/// last→cursor pairs (no L to materialise) and for the case where
/// `r.Auto` already has the corner.
///
/// Visibility-graph correctness (no jogs through obstacles) is
/// required for this to be a good user experience. Pre-fix, the
/// search emitted Steiner corners on the wrong side of obstacles
/// (the endpoint-exemption removed the obstacle wholesale from
/// the start-edge test); that was the "snake-shaped wire" the
/// user reported. The fix in `VisibilityGraph.shortestPath`
/// (`shrinkForMargin`) keeps endpoint-augment edges off the
/// obstacle's silicon while letting the wire escape the clearance
/// margin, so auto-routed corners now reflect real legal jogs.
let fix (r: DraftRoute) : DraftRoute =
    match r.Cursor with
    | None -> r
    | Some c ->
        let pathSoFar = r.Points @ r.Auto
        let appended =
            match List.tryLast pathSoFar with
            | None -> [c]
            | Some (lx, ly) ->
                let (cx, cy) = c
                if lx = cx || ly = cy then
                    [c]
                else
                    let cornerPt =
                        match r.Posture with
                        | HorizontalFirst -> (cx, ly)
                        | VerticalFirst   -> (lx, cy)
                    [cornerPt; c]
        { r with
            Points = simplifyCollinear (pathSoFar @ appended)
            Cursor = None
            Auto   = []
            // New segment starts: posture lock releases so the
            // user's next dominant motion can re-pick.
            PostureLocked = false }

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
    // Manual flip clears the auto-lock — the user explicitly
    // took control. Subsequent dominant motion may re-lock the
    // opposite direction if they keep dragging that way.
    { r with Posture = next; PostureLocked = false }

/// Rectangles produced by the fixed `Points` list, in commit order.
/// Excludes the tentative segment.
let fixedSegments (r: DraftRoute) : DraftSegment list =
    r.Points
    |> List.pairwise
    |> List.collect (fun (a, b) -> lShape r.Layer r.Width r.Posture a b)

/// Rectangles for the live tentative segment(s) from the last fixed
/// point through any walk-around corners to the current cursor.
/// Empty when cursor is None or the route has only its anchor.
/// When `Auto` is non-empty, the path is the polyline
/// last → Auto[0] → ... → Auto[N-1] → cursor; each consecutive pair
/// is decomposed under the active posture.
let tentativeSegments (r: DraftRoute) : DraftSegment list =
    match List.tryLast r.Points, r.Cursor with
    | Some last, Some cursor ->
        let polyline = last :: r.Auto @ [ cursor ]
        polyline
        |> List.pairwise
        |> List.collect (fun (a, b) -> lShape r.Layer r.Width r.Posture a b)
    | _ -> []

/// Fixed + tentative segments in render order. Used by the canvas
/// overlay so the user sees the route grow as they draw.
let allSegments (r: DraftRoute) : DraftSegment list =
    fixedSegments r @ tentativeSegments r

/// Segments to write into the cell on FinishRoute. Includes the
/// tentative path: last-fixed-point → r.Auto corners → cursor.
/// The user expects to commit what they see in the live preview —
/// the auto-router's detour around foreign features included.
/// See the `fix` doc-comment for the rationale.
let finishSegments (r: DraftRoute) : DraftSegment list =
    let lastPoints =
        match r.Cursor with
        | Some c -> r.Points @ r.Auto @ [c]
        | None   -> r.Points
    lastPoints
    |> simplifyCollinear
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
            Net = None
        } : Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon))
    |> List.toArray
