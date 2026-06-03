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

/// The picked snap target.  Mirrors the priority order of the
/// upcoming knuckle / wire-snap commits — for v1 only `Pin` is
/// returned, but downstream code keeps a single sum-type so
/// adding new kinds doesn't ripple through the call site.
type SnapKind =
    | Pin

type Snap = {
    X        : int64
    Y        : int64
    Layer    : int * int
    Net      : string
    Kind     : SnapKind
}

/// Resolve the snap target under the cursor.  v1: cell-pin
/// centroid only (uses the pre-built target array from
/// `Routing.Snap.buildTargets`).
///
/// When `topLayerOpt = Some L`, candidates are filtered to those
/// strictly below `L`'s layer number — this is what gives "active
/// = met3 → only show pin snaps on li1 / met1 / met2" semantics.
/// When `None`, every target is a candidate and the caller picks
/// a top from the snap's own layer.
let resolveSnap
        (topLayerOpt: (int * int) option)
        (targets    : Snap.SnapTarget array)
        (cursorX    : int64) (cursorY : int64)
        (radiusDbu  : int64)
        : Snap option =
    let candidates =
        match topLayerOpt with
        | Some (topN, _) ->
            targets |> Array.filter (fun t -> t.Layer < topN)
        | None -> targets
    Snap.nearest candidates (cursorX, cursorY) radiusDbu
    |> Option.map (fun t ->
        { X     = t.X
          Y     = t.Y
          Layer = (t.Layer, t.DataType)
          Net   = t.Net
          Kind  = Pin })

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
