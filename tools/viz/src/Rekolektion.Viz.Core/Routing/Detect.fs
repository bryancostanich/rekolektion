module Rekolektion.Viz.Core.Routing.Detect

open Rekolektion.Viz.Core.Rkt.Types

/// Spine direction. A wire-shaped Rect's spine runs along its long
/// axis; the perpendicular axis is the drag axis for "slide a track."
type Axis = X | Y

/// One axis-aligned routing rect, parsed into wire-shaped form.
/// `SourceIndex` keys back into the parent cell's `Elements` list so
/// edits commit to the right element.
type Segment = {
    /// Index in the parent cell's `Elements` list.
    SourceIndex : int
    /// Layer + datatype (canonical GDS pair).
    Layer       : int
    DataType    : int
    /// Full bbox in DBU.
    XMin        : int64
    YMin        : int64
    XMax        : int64
    YMax        : int64
    /// Spine direction — long-axis of the rect.
    Spine       : Axis
    /// Perpendicular-axis coordinate of the spine centerline (Y for an
    /// X-spine, X for a Y-spine). The "slide" coordinate.
    Center      : int64
    /// Spine endpoints, ordered along the spine ascending. Sit at the
    /// center of each short edge of the rect.
    Start       : Point
    End         : Point
    /// Width of the segment (perpendicular extent).
    Width       : int64
}

/// Where a segment ends on its spine — used to classify Posts.
type PostKind =
    /// One segment endpoint, no other segment meets here.
    | Terminus
    /// Two perpendicular segments meet (an L corner).
    | Corner
    /// Three or more segments meet (a T or X intersection).
    | Junction

/// A spine-meet point: where ≥1 segment endpoints / spine intersections
/// land in world coords. The drag-post target.
type Post = {
    /// Position in DBU.
    Position         : Point
    /// Indices into `Route.Segments` of every segment whose spine
    /// touches this post (endpoint OR midpoint cross).
    AttachedSegments : int list
    /// Classification by attached count + axis arrangement.
    Kind             : PostKind
}

/// One detected route — the connected component of same-layer wire
/// rects starting from a click hit.
type Route = {
    /// All segments in the connected component.
    Segments : Segment array
    /// Posts derived from spine intersections + terminuses.
    Posts    : Post array
    /// Cell name these segments live in (top cell for v1 — we don't
    /// recurse into SRefs).
    Cell     : string
}

/// Aspect-ratio threshold below which a Rect is treated as a PAD
/// (square-ish, can't be a draggable track) rather than a wire
/// segment. Picked to admit min-width × 2-min-width-long segments
/// while excluding via-enclosure pads (typically ~1:1).
let private wireAspectMin = 1.5

let private rectBboxFromRect (r: Rekolektion.Viz.Core.Rkt.Types.Rectangle) =
    let xMin = min r.X1 r.X2
    let xMax = max r.X1 r.X2
    let yMin = min r.Y1 r.Y2
    let yMax = max r.Y1 r.Y2
    xMin, yMin, xMax, yMax

/// Classify an axis-aligned bbox as a wire segment (returning its
/// spine description) or None when it's pad-shaped.
let private classifyAsWire (xMin, yMin, xMax, yMax) =
    let w = xMax - xMin
    let h = yMax - yMin
    if w <= 0L || h <= 0L then None
    else
        let longSide = max w h |> float
        let shortSide = max 1L (min w h) |> float
        if longSide / shortSide < wireAspectMin then None
        else
            let spine = if w >= h then Axis.X else Axis.Y
            let width = if spine = Axis.X then h else w
            let center =
                if spine = Axis.X then (yMin + yMax) / 2L
                else (xMin + xMax) / 2L
            let s, e =
                if spine = Axis.X then
                    { X = xMin; Y = center }, { X = xMax; Y = center }
                else
                    { X = center; Y = yMin }, { X = center; Y = yMax }
            Some (spine, center, width, s, e)

let private bboxesOverlap
        (a: int64 * int64 * int64 * int64)
        (b: int64 * int64 * int64 * int64) =
    let (ax1, ay1, ax2, ay2) = a
    let (bx1, by1, bx2, by2) = b
    not (ax2 < bx1 || bx2 < ax1 || ay2 < by1 || by2 < ay1)

let private pointInBbox (p: Point) (xMin, yMin, xMax, yMax) =
    p.X >= xMin && p.X <= xMax && p.Y >= yMin && p.Y <= yMax

/// Collect every wire-shaped Rect on the given (layer, datatype)
/// from a Cell's Elements list. Returns parsed Segment records
/// (with SourceIndex preserved for later edits).
let private collectWireSegments
        (cell: Cell) (layer: int) (datatype: int) : Segment array =
    cell.Elements
    |> List.indexed
    |> List.choose (fun (i, el) ->
        match el with
        | RectEl r ->
            let n, d = Rekolektion.Viz.Core.Rkt.ToGds.layerToGds r.Layer
            if n <> layer || d <> datatype then None
            else
                let bbox = rectBboxFromRect r
                match classifyAsWire bbox with
                | None -> None
                | Some (spine, center, width, s, e) ->
                    let (xMin, yMin, xMax, yMax) = bbox
                    Some {
                        SourceIndex = i
                        Layer = layer
                        DataType = datatype
                        XMin = xMin; YMin = yMin
                        XMax = xMax; YMax = yMax
                        Spine = spine
                        Center = center
                        Start = s
                        End = e
                        Width = width
                    }
        | _ -> None)
    |> List.toArray

/// Spine-meet point between two segments, when their centerlines
/// actually cross (perpendicular case) or end at the same coordinate
/// (collinear adjacency / overlap). Returns None when the segments
/// don't share a meet point on the spine.
///
/// The perpendicular case uses centerline coordinates rather than
/// bbox edges so the JOG-fix overlap (each rect extends a half-width
/// past the corner) doesn't shift the post position; corners always
/// sit at (vertical's X, horizontal's Y).
let private spineMeet (a: Segment) (b: Segment) : Point option =
    let aLo, aHi =
        match a.Spine with
        | X -> min a.Start.X a.End.X, max a.Start.X a.End.X
        | Y -> min a.Start.Y a.End.Y, max a.Start.Y a.End.Y
    let bLo, bHi =
        match b.Spine with
        | X -> min b.Start.X b.End.X, max b.Start.X b.End.X
        | Y -> min b.Start.Y b.End.Y, max b.Start.Y b.End.Y
    match a.Spine, b.Spine with
    | X, Y ->
        // a horizontal at y = a.Center; b vertical at x = b.Center.
        // They meet iff b.Center is in a's spine range AND a.Center
        // is in b's spine range — geometric crossing of centerlines.
        if b.Center >= aLo && b.Center <= aHi
           && a.Center >= bLo && a.Center <= bHi then
            Some { X = b.Center; Y = a.Center }
        else None
    | Y, X ->
        if a.Center >= bLo && a.Center <= bHi
           && b.Center >= aLo && b.Center <= aHi then
            Some { X = a.Center; Y = b.Center }
        else None
    | X, X | Y, Y ->
        // Same-axis: only counts as a "meet" when collinear (matching
        // perpendicular coord) AND the spine ranges touch at exactly
        // one shared coordinate (chained segments). Overlapping
        // collinear segments produce a degenerate post we skip — the
        // user sees them as one effective wire and the post structure
        // doesn't add value.
        if a.Center <> b.Center then None
        else
            // Same-axis touch points: aHi == bLo or bHi == aLo.
            let touchAt =
                if aHi = bLo then Some aHi
                elif bHi = aLo then Some bHi
                else None
            match touchAt with
            | None -> None
            | Some pos ->
                match a.Spine with
                | X -> Some { X = pos; Y = a.Center }
                | Y -> Some { X = a.Center; Y = pos }

/// Build the Post array for a list of segments. Walks every pair to
/// find spine meets, groups by exact coordinate, classifies each
/// cluster by attached count + axis arrangement.
let private buildPosts (segments: Segment array) : Post array =
    let endpointBuckets =
        System.Collections.Generic.Dictionary<int64 * int64, System.Collections.Generic.HashSet<int>>()
    let addAttachment (pt: Point) (segIdx: int) =
        let key = (pt.X, pt.Y)
        match endpointBuckets.TryGetValue key with
        | true, set -> set.Add segIdx |> ignore
        | _ ->
            let set = System.Collections.Generic.HashSet<int>()
            set.Add segIdx |> ignore
            endpointBuckets.[key] <- set
    // Each segment's two endpoints are candidate posts on their own
    // (terminus case when no other segment meets there).
    segments
    |> Array.iteri (fun i s ->
        addAttachment s.Start i
        addAttachment s.End   i)
    // Cross-segment meets: pairwise spineMeet, register the meet
    // point as attached to BOTH segments.
    for i in 0 .. segments.Length - 1 do
        for j in i + 1 .. segments.Length - 1 do
            match spineMeet segments.[i] segments.[j] with
            | None -> ()
            | Some pt ->
                addAttachment pt i
                addAttachment pt j
    endpointBuckets
    |> Seq.map (fun kv ->
        let (x, y) = kv.Key
        let attached = kv.Value |> Seq.toList |> List.sort
        let kind =
            match attached.Length with
            | 1 -> Terminus
            | 2 -> Corner
            | _ -> Junction
        { Position = { X = x; Y = y }
          AttachedSegments = attached
          Kind = kind })
    |> Seq.toArray

/// Given a click point in DBU, find the routing component the user
/// hit. Walks only the named cell's top-level Elements — we
/// deliberately don't recurse into SRefs (PDK primitives are
/// read-only for routing edits).
///
/// Adjacency uses simple bbox overlap, which is correct for axis-
/// aligned rectangles. The detection is layer-scoped: routes don't
/// cross layers (vias bridge them, but via geometry isn't itself a
/// segment).
let locateRoute
        (doc: Document)
        (cellName: string)
        (layer: int)
        (datatype: int)
        (hit: Point)
        : Route option =
    match doc.Cells |> List.tryFind (fun c -> c.Name = cellName) with
    | None -> None
    | Some cell ->
        let segments = collectWireSegments cell layer datatype
        if segments.Length = 0 then None
        else
            // Seed = the first wire segment whose bbox contains the
            // click. If no segment matches, the user clicked on
            // padding / a pad / nothing — return None.
            let seedIdx =
                segments
                |> Array.tryFindIndex (fun s ->
                    pointInBbox hit (s.XMin, s.YMin, s.XMax, s.YMax))
            match seedIdx with
            | None -> None
            | Some i0 ->
                let visited = System.Collections.Generic.HashSet<int>()
                let queue = System.Collections.Generic.Queue<int>()
                queue.Enqueue i0 |> ignore
                visited.Add i0 |> ignore
                let collected = System.Collections.Generic.List<Segment>()
                while queue.Count > 0 do
                    let cur = queue.Dequeue()
                    let curSeg = segments.[cur]
                    collected.Add curSeg
                    let curBb = (curSeg.XMin, curSeg.YMin, curSeg.XMax, curSeg.YMax)
                    for j in 0 .. segments.Length - 1 do
                        if not (visited.Contains j) then
                            let candSeg = segments.[j]
                            let candBb =
                                (candSeg.XMin, candSeg.YMin,
                                 candSeg.XMax, candSeg.YMax)
                            if bboxesOverlap curBb candBb then
                                visited.Add j |> ignore
                                queue.Enqueue j |> ignore
                let routeSegments = collected.ToArray()
                let posts = buildPosts routeSegments
                Some {
                    Segments = routeSegments
                    Posts = posts
                    Cell = cellName
                }
