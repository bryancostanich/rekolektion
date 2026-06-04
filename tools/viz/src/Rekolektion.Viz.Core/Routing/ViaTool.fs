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

/// The picked snap target.  `Knuckle` wins over `Pin` when both
/// are within reach: clicking on visibly-painted met1+ geometry
/// should land the via on that layer rather than chasing a label
/// one layer down (the original v1 behavior produced a li1→met1
/// via on a met1 knuckle, then dedup'd it against the existing
/// contact and looked like nothing happened).
type SnapKind =
    | Pin
    | Knuckle
    | WireEnd
    | WireCenterline
    | Guide

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

/// Find the topmost routing-layer rect under the cursor and
/// classify it as a knuckle (square pad → centroid), a wire end
/// (long rect, cursor near a tip → snap to tip), or a wire
/// centerline (long rect, cursor along the body → project cursor
/// onto the midline).
///
/// "Topmost" = highest layer number (= higher metal in the
/// sky130 stack).  A met1 knuckle sitting on a li1 rail wins
/// over the rail because the user is visibly clicking on met1.
///
/// `topLayerOpt = Some L` filters to candidates strictly below L
/// (so an active met2 won't snap to a met2 rect — same-layer
/// via has no plumbing).  `None` lets every routing layer
/// through and the caller picks a top from snap.Layer + 1.
///
/// Net is left empty: a FlatPolygon doesn't carry one directly.
/// Downstream attribution can be added if needed.
let findRoutingSnapAt
        (topLayerOpt: (int * int) option)
        (flatPolys  : FlatPolygon array)
        (cursorX    : int64) (cursorY : int64) : Snap option =
    let candidates =
        flatPolys
        |> Array.filter (fun p ->
            if not (isRoutingLayerKey p.Layer p.DataType) then false
            else
                let xMin, yMin, xMax, yMax = polyBbox p
                cursorX >= xMin && cursorX <= xMax
                && cursorY >= yMin && cursorY <= yMax
                && (match topLayerOpt with
                    | Some (topN, _) -> p.Layer < topN
                    | None -> true))
    if Array.isEmpty candidates then None
    else
        let topmost = candidates |> Array.maxBy (fun p -> p.Layer)
        let bbox = polyBbox topmost
        let xMin, yMin, xMax, yMax = bbox
        let layer = (topmost.Layer, topmost.DataType)
        if not (isWireShape bbox) then
            // Knuckle / pad — snap to bbox centroid.
            Some {
                X     = (xMin + xMax) / 2L
                Y     = (yMin + yMax) / 2L
                Layer = layer
                Net   = ""
                Kind  = Knuckle
            }
        else
            // Wire — project cursor onto the centerline.  When
            // the projection lands within `2 × thin-axis` of an
            // end, snap to the end instead of mid-wire (matches
            // the user's "via at the corner where I stopped
            // routing" gesture).
            let w = xMax - xMin
            let h = yMax - yMin
            if w >= h then
                // Horizontal wire.
                let midY = (yMin + yMax) / 2L
                let endTol = max 1L (h * 2L)
                let snapX, kind =
                    if cursorX - xMin < endTol      then xMin, WireEnd
                    elif xMax - cursorX < endTol    then xMax, WireEnd
                    else cursorX, WireCenterline
                Some { X = snapX; Y = midY; Layer = layer; Net = ""; Kind = kind }
            else
                // Vertical wire.
                let midX = (xMin + xMax) / 2L
                let endTol = max 1L (w * 2L)
                let snapY, kind =
                    if cursorY - yMin < endTol      then yMin, WireEnd
                    elif yMax - cursorY < endTol    then yMax, WireEnd
                    else cursorY, WireCenterline
                Some { X = midX; Y = snapY; Layer = layer; Net = ""; Kind = kind }

/// Nearest guide line that lies within `radiusDbu` of the cursor
/// on its perpendicular axis.  See `via_tool.md` rules 1.2 / 2.2.
///
/// A vertical guide constrains only X (Y stays at cursor).
/// A horizontal guide constrains only Y (X stays at cursor).
///
/// Returns the snap result alongside the perpendicular distance —
/// `resolveSnap` uses the distance to break ties against the
/// nearest pin (rule 2.2 — physically closer wins).
///
/// `bottomLayer` becomes the snap's `Layer`.  Guides carry no
/// implied metal, so the caller computes a sensible bottom from
/// the toolbar's active layer (see `resolveSnap` for the active-
/// layer ≥ met1 gating per rule 3).
let private findGuideSnap
        (guides     : Guide list)
        (bottomLayer: int * int)
        (cursorX    : int64) (cursorY : int64)
        (radiusDbu  : int64) : (Snap * int64) option =
    let mutable best : (Snap * int64) option = None
    for g in guides do
        let dist =
            match g.Orientation with
            | Vertical   -> abs (cursorX - g.CoordDbu)
            | Horizontal -> abs (cursorY - g.CoordDbu)
        if dist <= radiusDbu then
            let snap : Snap =
                match g.Orientation with
                | Vertical ->
                    { X = g.CoordDbu; Y = cursorY
                      Layer = bottomLayer; Net = ""; Kind = Guide }
                | Horizontal ->
                    { X = cursorX; Y = g.CoordDbu
                      Layer = bottomLayer; Net = ""; Kind = Guide }
            match best with
            | None -> best <- Some (snap, dist)
            | Some (_, d) when dist < d -> best <- Some (snap, dist)
            | _ -> ()
    best

/// Resolve the snap target under the cursor per `via_tool.md`:
///   1. Knuckle / wire — routing-layer rect whose bbox contains
///      the cursor.  Highest layer wins (see `findRoutingSnapAt`).
///   2. Pin and guide compete on physical distance — whichever is
///      closer wins.  Pin distance is Euclidean; guide distance
///      is single-axis (perpendicular to the guide line).  Ties
///      go to pin (stable order).
///
/// Knuckle wins over both pin and guide because the user is
/// visibly pointing at painted geometry — chasing a label or
/// guide line away from it is wrong.
///
/// `topLayerOpt = Some L` filters knuckle + pin candidates to
/// layers strictly below `L`.  Guide snap is gated separately:
/// guides need an implied bottom layer (`L - 1`) so they're only
/// considered when `topLayerOpt = Some L` with `L ≥ met1`.  When
/// `topLayerOpt = None` or `L = li1`, guide snap is disabled and
/// the resolver returns knuckle / pin / nothing.
let resolveSnap
        (topLayerOpt: (int * int) option)
        (targets    : Snap.SnapTarget array)
        (guides     : Guide list)
        (flatPolys  : FlatPolygon array)
        (cursorX    : int64) (cursorY : int64)
        (radiusDbu  : int64)
        : Snap option =
    match findRoutingSnapAt topLayerOpt flatPolys cursorX cursorY with
    | Some k -> Some k
    | None ->
        let pinCandidates =
            match topLayerOpt with
            | Some (topN, _) ->
                targets |> Array.filter (fun t -> t.Layer < topN)
            | None -> targets
        let pinHit =
            Snap.nearest pinCandidates (cursorX, cursorY) radiusDbu
            |> Option.map (fun t ->
                let dx = t.X - cursorX
                let dy = t.Y - cursorY
                // sqrt via float — cursors stay in int64 dbu range
                // (sky130 chip is ~10^7 dbu across) so the cast
                // round-trip is exact.
                let dist =
                    int64 (sqrt (float (dx * dx + dy * dy)))
                let snap : Snap =
                    { X     = t.X
                      Y     = t.Y
                      Layer = (t.Layer, t.DataType)
                      Net   = t.Net
                      Kind  = Pin }
                snap, dist)
        let guideHit =
            // Guide snap requires an active layer ≥ met1 (per
            // via_tool.md rule 3): we need an implied bottom layer
            // (`activeLayer - 1`) for the single-step via stack.
            match topLayerOpt with
            | Some (topN, topDt) when topN > 67 ->
                findGuideSnap guides (topN - 1, topDt)
                              cursorX cursorY radiusDbu
            | _ -> None
        match pinHit, guideHit with
        | Some (p, dp), Some (g, dg) ->
            // Rule 2.2 — nearest wins.  Rule 2.3 — ties go to pin
            // (strict `<` keeps pin on equality).
            if dg < dp then Some g else Some p
        | Some (p, _), None -> Some p
        | None, Some (g, _) -> Some g
        | None, None        -> None

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
