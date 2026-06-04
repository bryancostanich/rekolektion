/// V-tool emission — pure function around `ViaStack.emitAt` that
/// also synthesizes the **wire-layer (top) pad**.
///
/// `emitAt` deliberately omits that pad because the route-finish
/// path emits endpoint pads through `Routing.Draft.endpointPads`.
/// A standalone via has no such pass — without the wire-layer pad
/// a met1 → li1 click drops just the mcon cut and looks invisible
/// (graphite-gray contact square buried under the existing met1).
/// This module fills that gap so the V tool's commit emits the
/// full stack: snap-layer pad, every intermediate metal + via, AND
/// the top-layer pad.
///
/// Stays in Core (not App) so unit tests can drive it without an
/// Avalonia / FuncUI harness.
module Rekolektion.Viz.Core.Routing.ViaTool

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Drc.Rules
open Rekolektion.Viz.Core.Layout.Flatten

/// Minimum square-side (DBU) that satisfies a metal layer's
/// `MinArea` rule.  `ceil(sqrt(minArea))` so a partially-rounded
/// pad never sneaks under the threshold.  Returns `None` when
/// no MinArea rule exists for the layer (rare for routing metals;
/// the V tool leaves the pad at its enclosure-driven size).
let private minAreaSideDbu
        (view  : RulesetView)
        (units : Units)
        (layer : int * int) : int64 option =
    let umPerDbu = float units.DbuNm * 1.0e-3
    let (n, dt) = layer
    view.Rules
    |> List.tryPick (fun r ->
        match r with
        | MinArea (_, l, areaUm2)
            when l.Number = n && l.DataType = dt ->
            let sideUm = sqrt areaUm2
            Some (int64 (System.Math.Ceiling (sideUm / umPerDbu)))
        | _ -> None)

/// Walk a list of segments and re-size every METAL pad so it
/// satisfies its layer's MinArea rule.  Via-cut segments
/// (those whose layer is a via / contact dataType 44) keep
/// their existing size — vias have their own width rule and
/// the metal pad enclosing each via is what carries the area.
///
/// Why this lives in V-tool space and not in `ViaStack.emitAt`:
/// the wire-route commit emits these same metal pads but the
/// wire body extends past the pad, so cumulative area meets
/// `min-area` even when an individual pad's enclosure-driven
/// side falls short.  A standalone via has no wire body, so the
/// pad has to carry the area on its own.  Reported 2026-06-03:
/// V-tool met3 pad came out 390 nm (via2.5 enclosure: 200 + 2×95)
/// vs met3.6 floor of ~490 nm (sqrt 0.240 µm²).
let private floorMetalPadsAtMinArea
        (view  : RulesetView)
        (units : Units)
        (segs  : ViaStack.ViaSegment list) : ViaStack.ViaSegment list =
    segs
    |> List.map (fun seg ->
        // Vias / contacts on dataType 44 keep their cut size —
        // metal floor doesn't apply.  Same `isViaOrContactLayer`
        // test the rest of the routing code uses to discriminate.
        if ViaStack.isViaOrContactLayer seg.Layer then seg
        else
            match minAreaSideDbu view units seg.Layer with
            | Some minSide when seg.SideDbu < minSide ->
                { seg with SideDbu = minSide }
            | _ -> seg)

/// Build the complete via-stack geometry for a standalone V-tool
/// click at `(cx, cy)`, going from `topLayer` (where the wire
/// would sit) down to `snapLayer` (the layer of the geometry
/// being plumbed to).
///
/// Returns the segments `ViaStack.emitAt` produces plus a wire-
/// layer pad at the click point.  Every metal pad is then floored
/// at its layer's `MinArea` to handle the standalone-via case
/// where there's no wire body to make up the missing area.
/// When `topLayer = snapLayer` (no plumbing needed) returns `[]` —
/// a click straight onto the target's own layer is a no-op for
/// the via tool.
let emitStandaloneAt
        (view : RulesetView)
        (units : Units)
        (snapLayer : int * int)
        (topLayer  : int * int)
        (cx : int64) (cy : int64) : ViaStack.ViaSegment list =
    if snapLayer = topLayer then []
    else
        let baseSegs = ViaStack.emitAt view units snapLayer topLayer cx cy
        if List.isEmpty baseSegs then []
        else
            // Top-layer pad sized to enclose the topmost via in the
            // stack.  `between` returns vias in snap→top order so
            // the topmost is the last entry.
            let vias = ViaStack.between snapLayer topLayer
            let topPad =
                match List.tryLast vias with
                | Some topmostVia ->
                    ViaStack.padSideForVia view units topLayer topmostVia
                    |> Option.map (fun side ->
                        ({ Layer   = topLayer
                           CenterX = cx
                           CenterY = cy
                           SideDbu = side } : ViaStack.ViaSegment))
                | None -> None
            let withTopPad =
                match topPad with
                | Some p -> baseSegs @ [ p ]
                | None   -> baseSegs
            floorMetalPadsAtMinArea view units withTopPad

/// What the resolver pulled to.  Drives the hover-preview glyph
/// (today just a circle, future polish per via_tool.md OQ-3).
///
/// `Pin`, `KnuckleCenter`, `WireEndpoint` are point snaps — both
/// axes come from the same source.  `AxisX` / `AxisY` /
/// `AxisCross` are per-axis snaps composed from one or two line
/// sources (guides and / or wire centerlines).  `RawCursor` is
/// the Alt-held escape hatch.
type SnapKind =
    | Pin
    | KnuckleCenter
    | WireEndpoint
    | AxisX        // X from a vertical line source; Y stayed at cursor
    | AxisY        // Y from a horizontal line source; X stayed at cursor
    | AxisCross    // X and Y from two line sources
    | RawCursor    // Alt held — snap suppressed

type Snap = {
    X        : int64
    Y        : int64
    Layer    : int * int
    Net      : string
    Kind     : SnapKind
}

/// Sky130 routing layers: li1 + met1..met5, all on dataType 20.
/// Hard-coded against the gds-stream numbers because the V tool's
/// snap path runs hot enough that a per-call lookup into
/// `Layout.Layer` is wasted work, and the routing stack is
/// PDK-stable (no fab is renumbering li1 between releases).
let private isRoutingLayerKey (n: int) (dt: int) : bool =
    dt = 20 && n >= 67 && n <= 72

let private polyBbox (poly: FlatPolygon) : int64 * int64 * int64 * int64 =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    for pt in poly.Points do
        if pt.X < xMin then xMin <- pt.X
        if pt.X > xMax then xMax <- pt.X
        if pt.Y < yMin then yMin <- pt.Y
        if pt.Y > yMax then yMax <- pt.Y
    xMin, yMin, xMax, yMax

/// Aspect-ratio threshold separating "knuckle" (roughly square
/// pad) from "wire" (long thin rect).  1.5× chosen empirically:
/// sky130 routing pads are usually within ~1.3× (different
/// enclosure on each axis), real wires hit 5× or more.  A user
/// clicking on a 1.4× rect almost certainly painted a pad, not a
/// wire — snap to centroid.
[<Literal>]
let private wireAspectThreshold : float = 1.5

let private isWireShape (xMin, yMin, xMax, yMax) : bool =
    let w = xMax - xMin
    let h = yMax - yMin
    let lo = float (max 1L (min w h))
    let hi = float (max w h)
    hi / lo > wireAspectThreshold

// ─────────────────────────────────────────────────────────────────
// Candidate gathering — see via_tool.md "Snap sources".
//
// Two flavours:
//
//   * Point candidates — (x, y, layer, kind).  Cursor pulls if
//     within `radiusDbu` Euclidean.  Pin, knuckle centre, wire
//     endpoint.
//
//   * Line candidates — (axis, coord, layer, isWire).  Cursor
//     pulls if within `radiusDbu` on the perpendicular axis.
//     Vertical guides + vertical wire centerlines feed the X
//     axis; horizontal ones feed Y.  `isWire = true` for wire
//     centerlines (the line carries a real metal layer; layer
//     is the wire's); `false` for guides (layer is just the
//     caller-supplied default).
// ─────────────────────────────────────────────────────────────────

type private PointCandidate = {
    X     : int64
    Y     : int64
    Layer : int * int
    Net   : string
    Kind  : SnapKind
}

type private LineCandidate = {
    Axis    : GuideOrientation  // Vertical → contributes X; Horizontal → contributes Y
    Coord   : int64             // the constrained value on that axis
    Layer   : int * int
    IsWire  : bool              // true → from a wire centerline (real metal); false → guide
}

/// Per-routing-rect candidates: a wire contributes two endpoint
/// points + one centerline.  A knuckle (square-ish pad)
/// contributes only its bbox centre — bbox-containment pull is
/// NOT a snap source any more (via_tool.md "What is NOT a snap
/// source").
let private rectCandidates
        (poly : FlatPolygon)
        : PointCandidate list * LineCandidate list =
    let xMin, yMin, xMax, yMax = polyBbox poly
    let layer = (poly.Layer, poly.DataType)
    let midX = (xMin + xMax) / 2L
    let midY = (yMin + yMax) / 2L
    if not (isWireShape (xMin, yMin, xMax, yMax)) then
        // Knuckle / pad — single centre candidate.
        let pt : PointCandidate =
            { X = midX; Y = midY; Layer = layer; Net = ""; Kind = KnuckleCenter }
        [pt], []
    else
        let w = xMax - xMin
        let h = yMax - yMin
        if w >= h then
            // Horizontal wire — endpoints at (xMin, midY) and
            // (xMax, midY); centerline at Y = midY.
            let endpoints : PointCandidate list = [
                { X = xMin; Y = midY; Layer = layer; Net = ""; Kind = WireEndpoint }
                { X = xMax; Y = midY; Layer = layer; Net = ""; Kind = WireEndpoint }
            ]
            let line : LineCandidate =
                { Axis = Horizontal; Coord = midY; Layer = layer; IsWire = true }
            endpoints, [line]
        else
            // Vertical wire — endpoints at top/bottom mid-width.
            let endpoints : PointCandidate list = [
                { X = midX; Y = yMin; Layer = layer; Net = ""; Kind = WireEndpoint }
                { X = midX; Y = yMax; Layer = layer; Net = ""; Kind = WireEndpoint }
            ]
            let line : LineCandidate =
                { Axis = Vertical; Coord = midX; Layer = layer; IsWire = true }
            endpoints, [line]

/// Gather every snap candidate the resolver should consider.
/// `topLayerOpt = Some L` restricts routing rects to layers
/// strictly below L; guides and pins are filtered separately.
let private gatherCandidates
        (topLayerOpt: (int * int) option)
        (pinTargets : Snap.SnapTarget array)
        (guides     : Guide list)
        (flatPolys  : FlatPolygon array)
        : PointCandidate list * LineCandidate list =
    let topN =
        match topLayerOpt with
        | Some (n, _) -> Some n
        | None -> None
    // Pin candidates — labelled centroids on a routing layer
    // below the active layer.
    let pinPts =
        pinTargets
        |> Array.toList
        |> List.filter (fun t ->
            match topN with
            | Some n -> t.Layer < n
            | None -> true)
        |> List.map (fun t ->
            { X = t.X; Y = t.Y
              Layer = (t.Layer, t.DataType)
              Net = t.Net
              Kind = Pin })
    // Routing-rect candidates — knuckle centres + wire endpoints
    // (points) and wire centerlines (lines).
    let rectPts, rectLines =
        flatPolys
        |> Array.toList
        |> List.filter (fun p ->
            isRoutingLayerKey p.Layer p.DataType
            && (match topN with
                | Some n -> p.Layer < n
                | None -> true))
        |> List.map rectCandidates
        |> List.unzip
        |> fun (pts, lines) -> List.concat pts, List.concat lines
    // Guide candidates — line sources only.  Layer hint is the
    // caller-supplied default (activeLayer - 1, gated to met1+
    // upstream).  We collect guides regardless of the active-
    // layer check here and let the caller filter; this keeps the
    // gather pure and the layer-gating in resolveSnap.
    let guideLines =
        guides
        |> List.map (fun g ->
            { Axis = g.Orientation
              Coord = g.CoordDbu
              Layer = (0, 0)  // sentinel; replaced when guides are usable
              IsWire = false })
    pinPts @ rectPts, rectLines @ guideLines

// ─────────────────────────────────────────────────────────────────
// Resolver.  See via_tool.md "Behaviour rules / Priority".
// ─────────────────────────────────────────────────────────────────

/// Squared Euclidean distance, integer-safe.
let private distSq (dx: int64) (dy: int64) : int64 = dx * dx + dy * dy

/// Resolve the snap target under the cursor per via_tool.md.
///
/// Priority (rule 2):
///   1. Alt held → raw-cursor snap (subject to active-layer rule).
///   2. Nearest point candidate within radius (Euclidean).
///   3. Per-axis line candidates — X and Y solved independently
///      (perpendicular distance).
///
/// `topLayerOpt = Some L` is the toolbar's active layer; required
/// for any guide-derived or raw-cursor snap (the via needs an
/// implied top).  When `None`, guide / raw snaps are disabled;
/// point and wire snaps still work and the caller derives top
/// from `snap.Layer + 1`.
let resolveSnap
        (topLayerOpt: (int * int) option)
        (targets    : Snap.SnapTarget array)
        (guides     : Guide list)
        (flatPolys  : FlatPolygon array)
        (cursorX    : int64) (cursorY : int64)
        (radiusDbu  : int64)
        (altHeld    : bool)
        : Snap option =
    // Alt-suppress (rule 2.1) — drop a via at the raw cursor.
    // Requires active layer ≥ met1 so we have an implied
    // bottomLayer (= activeLayer - 1).
    if altHeld then
        match topLayerOpt with
        | Some (topN, topDt) when topN > 67 ->
            Some { X = cursorX; Y = cursorY
                   Layer = (topN - 1, topDt)
                   Net = ""
                   Kind = RawCursor }
        | _ -> None
    else
    let pointCands, lineCands =
        gatherCandidates topLayerOpt targets guides flatPolys
    let rSq = radiusDbu * radiusDbu
    // ── Best POINT snap (Euclidean to candidate) ──────────────
    let pointResult : (Snap * int64) option =
        pointCands
        |> List.choose (fun pc ->
            let d = distSq (pc.X - cursorX) (pc.Y - cursorY)
            if d <= rSq then Some (pc, d) else None)
        |> List.sortBy (fun (_, d) -> d)
        |> List.tryHead
        |> Option.map (fun (pc, d) ->
            { X = pc.X; Y = pc.Y
              Layer = pc.Layer
              Net = pc.Net
              Kind = pc.Kind }, d)
    // ── Best AXIS snap (Euclidean to resolved point) ─────────
    // Guides need an implied bottom layer (activeLayer - 1) so
    // they're only usable when active layer ≥ met1.  Wire
    // centerlines carry their own layer and are always usable.
    let guideBottom : (int * int) option =
        match topLayerOpt with
        | Some (n, dt) when n > 67 -> Some (n - 1, dt)
        | _ -> None
    let nearestLine (axis : GuideOrientation) =
        lineCands
        |> List.choose (fun lc ->
            if lc.Axis <> axis then None
            elif not lc.IsWire && guideBottom.IsNone then None
            else
                let cursorOnAxis =
                    match axis with
                    | Vertical   -> cursorX
                    | Horizontal -> cursorY
                let dist = abs (cursorOnAxis - lc.Coord)
                if dist <= radiusDbu then Some (lc, dist) else None)
        |> List.sortBy (fun (_, d) -> d)
        |> List.tryHead
    let xHit = nearestLine Vertical    // contributes X
    let yHit = nearestLine Horizontal  // contributes Y
    let pickLayer (hits : LineCandidate list) : (int * int) option =
        let wireLayers =
            hits |> List.filter (fun lc -> lc.IsWire) |> List.map (fun lc -> lc.Layer)
        match wireLayers with
        | [] -> guideBottom
        | xs -> Some (xs |> List.minBy fst)
    let xLine = xHit |> Option.map fst
    let yLine = yHit |> Option.map fst
    let activeHits = [ xLine; yLine ] |> List.choose id
    let layerOpt = pickLayer activeHits
    let axisResult : (Snap * int64) option =
        match xLine, yLine, layerOpt with
        | Some x, Some y, Some layer ->
            let dx = x.Coord - cursorX
            let dy = y.Coord - cursorY
            let d = distSq dx dy
            Some ({ X = x.Coord; Y = y.Coord
                    Layer = layer; Net = ""; Kind = AxisCross }, d)
        | Some x, None, Some layer ->
            let dx = x.Coord - cursorX
            let d = distSq dx 0L
            Some ({ X = x.Coord; Y = cursorY
                    Layer = layer; Net = ""; Kind = AxisX }, d)
        | None, Some y, Some layer ->
            let dy = y.Coord - cursorY
            let d = distSq 0L dy
            Some ({ X = cursorX; Y = y.Coord
                    Layer = layer; Net = ""; Kind = AxisY }, d)
        | _ -> None
    // ── Pick nearer of point vs axis ─────────────────────────
    // Squared Euclidean distances compare directly (monotone).
    // Ties go to point snap (stable; matches the user's "snap
    // to the labelled thing exactly when both are equally close"
    // intuition).
    match pointResult, axisResult with
    | Some (p, dp), Some (_, da) when dp <= da -> Some p
    | Some (p, _),  None                       -> Some p
    | _, Some (a, _)                           -> Some a
    | None, None                               -> None

/// Pick the via-stack top layer per the V-tool rule: prefer the
/// caller's `topLayerOpt` (toolbar's ActiveLayer) when present,
/// else one routing layer above the snap's own layer.
///
/// Sky130 routing layers occupy consecutive integers 67..72
/// (li1, met1, met2, ..., met5), all with dataType 20.  The
/// "+1" arithmetic relies on that registry stability; if a new
/// PDK ships with a non-contiguous routing stack, swap to a
/// proper index-lookup via `ViaStack.routingLayers`.
let pickTopLayer
        (topLayerOpt: (int * int) option)
        (snap: Snap) : int * int =
    match topLayerOpt with
    | Some l -> l
    | None ->
        let (n, _) = snap.Layer
        (n + 1, 20)
