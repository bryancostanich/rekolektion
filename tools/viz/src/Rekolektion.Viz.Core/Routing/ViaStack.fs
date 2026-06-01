/// Via-stack geometry for connecting a wire on routing layer A
/// to a snap-target pin on routing layer B (e.g., a met2 wire
/// landing on a li1 drn_R pin needs mcon + via1 plus pads on li1
/// and met1).
///
/// Sky130 routing stack, bottom to top:
///   li1   (67/20)
///   met1  (68/20)
///   met2  (69/20)
///   met3  (70/20)
///   met4  (71/20)
///   met5  (72/20)
///
/// Bridging contact / via layers between adjacent metals:
///   li1   ↔ met1 :  mcon  (67/44)
///   met1  ↔ met2 :  via   (68/44)
///   met2  ↔ met3 :  via2  (69/44)
///   met3  ↔ met4 :  via3  (70/44)
///   met4  ↔ met5 :  via4  (71/44)
///
/// `between` returns the layered sequence of (metal, via) pairs
/// needed to connect two routing layers. The caller emits one
/// via-cut shape per via layer and one pad shape per intermediate
/// metal layer, all centred at the endpoint.
module Rekolektion.Viz.Core.Routing.ViaStack

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Drc.Rules

/// Sky130 routing layers in stack order. Index 0 is bottom (li1).
let private routingLayers : (int * int) array = [|
    (67, 20)   // li1
    (68, 20)   // met1
    (69, 20)   // met2
    (70, 20)   // met3
    (71, 20)   // met4
    (72, 20)   // met5
|]

/// Sky130 via layer between two adjacent routing layers, in the
/// order routingLayers presents them. `viaBetween.[i]` connects
/// `routingLayers.[i]` to `routingLayers.[i+1]`.
let private viaBetween : (int * int) array = [|
    (67, 44)   // mcon  (li1  ↔ met1)
    (68, 44)   // via   (met1 ↔ met2)
    (69, 44)   // via2  (met2 ↔ met3)
    (70, 44)   // via3  (met3 ↔ met4)
    (71, 44)   // via4  (met4 ↔ met5)
|]

let private indexOf (layer : int * int) : int option =
    let mutable i = 0
    let mutable found = -1
    while found < 0 && i < routingLayers.Length do
        if routingLayers.[i] = layer then found <- i
        i <- i + 1
    if found < 0 then None else Some found

/// True if `layer` is one of the sky130 routing layers ViaStack
/// understands. Caller can short-circuit when neither endpoint is
/// on a routing layer (no via stack to build).
let isRoutingLayer (layer : int * int) : bool =
    (indexOf layer).IsSome

/// True if `layer` is a contact / via cut layer that ANCHORS a wire
/// to something below or above it: licon1 (66, 44), mcon (67, 44),
/// or any of via..via4. Used by SegmentDrag to decide whether an
/// endpoint truly anchors to a pin / via stack (so a bridge keeps
/// the connection intact across the drag) or is a free terminus
/// (no bridge needed — would just paint a stub into empty space).
let isViaOrContactLayer (layer : int * int) : bool =
    Array.contains layer viaBetween
    || layer = (66, 44)   // licon1 — diff/poly ↔ li1

/// Single step in a via stack: a metal layer plus the via that
/// connects it UPWARD to the next metal. The last step's `Via`
/// connects to the top metal, which is included as the next step's
/// `Metal`. Callers iterate pairwise.
type StackStep = {
    Metal : int * int
    Via   : int * int
}

/// Via layers between `a` and `b`, ordered from `a` toward `b`.
/// Empty when the layers are the same OR when at least one isn't
/// a known routing layer.
///
///   between li1 met1  = [ (67, 44) ]                   // mcon
///   between li1 met2  = [ (67, 44); (68, 44) ]         // mcon, via
///   between met1 li1  = [ (67, 44) ]                   // same set (order-insensitive)
let between (a : int * int) (b : int * int) : (int * int) list =
    match indexOf a, indexOf b with
    | Some ia, Some ib when ia = ib -> []
    | Some ia, Some ib ->
        let lo = min ia ib
        let hi = max ia ib
        [ for i in lo .. hi - 1 -> viaBetween.[i] ]
    | _ -> []

/// Intermediate metal layers BETWEEN `a` and `b` exclusive of both
/// endpoints. The wire and the snap target already have their own
/// pads on `a` and `b` respectively; this is the set of layers
/// that need a pad added at the via stack location.
///
///   intermediateMetals li1  met1 = []
///   intermediateMetals li1  met2 = [ (68, 20) ]         // met1
///   intermediateMetals li1  met3 = [ (68, 20); (69, 20) ]
let intermediateMetals (a : int * int) (b : int * int) : (int * int) list =
    match indexOf a, indexOf b with
    | Some ia, Some ib when ia <> ib ->
        let lo = min ia ib
        let hi = max ia ib
        [ for i in lo + 1 .. hi - 1 -> routingLayers.[i] ]
    | _ -> []

/// Square via-cut geometry: a single rectangle. Side length comes
/// from the via layer's `Width` rule in the active DRC view.
/// Returns `None` when no width rule names this via (no PDK data).
type ViaCut = {
    Layer    : int * int
    SideDbu  : int64
}

let viaCut
        (view : RulesetView)
        (units : Units)
        (viaLayer : int * int) : ViaCut option =
    let umPerDbu = float units.DbuNm * 1.0e-3
    let (n, dt) = viaLayer
    view.Rules
    |> List.tryPick (fun r ->
        match r with
        | Width (_, l, m) when l.Number = n && l.DataType = dt ->
            Some { Layer = viaLayer
                   SideDbu = int64 (m / umPerDbu) }
        | _ -> None)

/// Side length (DBU) of a pad on `metalLayer` that encloses the
/// via on `viaLayer` — `viaSide + 2 × enclosure`. The enclosure
/// threshold comes from the `Enclosure` / `AsymEnclosure` rule
/// with `outer = metalLayer` and `inner = viaLayer`. Returns
/// `None` when no rule covers the pair.
let padSideForVia
        (view : RulesetView)
        (units : Units)
        (metalLayer : int * int)
        (viaLayer   : int * int) : int64 option =
    let umPerDbu = float units.DbuNm * 1.0e-3
    let isMetal (l : LayerKey) =
        l.Number = fst metalLayer && l.DataType = snd metalLayer
    let isVia (l : LayerKey) =
        l.Number = fst viaLayer && l.DataType = snd viaLayer
    // The via's own Width gives the inner side length.
    let viaWidthUm =
        view.Rules
        |> List.tryPick (fun r ->
            match r with
            | Width (_, l, m) when isVia l -> Some m
            | _ -> None)
    let enclosureUm =
        view.Rules
        |> List.choose (fun r ->
            match r with
            | Enclosure (_, outer, inner, m, _)
                when isMetal outer && isVia inner -> Some m
            | AsymEnclosure (_, outer, inner, a, b, _)
                when isMetal outer && isVia inner -> Some (max a b)
            | _ -> None)
        |> function
           | [] -> None
           | xs -> Some (List.max xs)
    match viaWidthUm, enclosureUm with
    | Some w, Some e -> Some (int64 ((w + 2.0 * e) / umPerDbu))
    | _ -> None

/// One concrete rectangle in a via stack: a square shape on
/// `Layer` centered at `(CenterX, CenterY)` with side `SideDbu`.
/// Callers turn these into DraftSegments / RectEls.
type ViaSegment = {
    Layer   : int * int
    CenterX : int64
    CenterY : int64
    SideDbu : int64
}

/// Build the via-stack geometry between a wire on `wireLayer`
/// and a snap target on `snapLayer` at the world-coord point
/// `(cx, cy)`. Empty when `snapLayer = wireLayer`, when either
/// isn't a known routing layer, or when no PDK rules are
/// available to size the rectangles.
///
/// **Invariant — full ladder.** The auto-stitcher MUST emit a
/// metal enclosure pad at EVERY intermediate metal layer in the
/// stack (not just at the wire's own endpoints). A met3 wire
/// descending to an li1 pin gets met1, met2 enclosure pads as
/// well as the via cuts. Downstream consumers (interactive
/// editor commit, ratlines DRC, magic GDS export) all assume
/// the ladder is complete; emitting a partial stack silently
/// fails via.4b / via.5b in magic.
///
/// The list includes:
///   - One via-cut shape per intermediate via layer (sized from
///     the via's `Width` rule).
///   - One pad shape per intermediate metal layer (sized to
///     enclose both adjacent vias per the metal's Enclosure rule).
///   - One pad shape on `snapLayer` itself, sized to enclose the
///     via directly above it. The cell's existing pin polygon
///     usually already covers this, but emitting it explicitly
///     keeps the wire's commit self-sufficient and lets the DRC
///     merge with the pin.
///
/// The wire layer's own pad is NOT emitted here — `Routing.Draft.endpointPads`
/// already does that at both endpoints.
///
/// Filters that run AFTER `emitAt` (e.g. `Pads.dropPadsContainedByForeignPolys`,
/// `viaCovered` in `Model.Update`) MAY drop individual segments
/// only when the foreign poly on the corresponding layer already
/// provides the enclosure-rule-required margins around the via
/// cut. A filter that drops a pad without an enclosure-rule
/// check is a bug — it breaks the full-ladder invariant.
let emitAt
        (view : RulesetView)
        (units : Units)
        (snapLayer : int * int)
        (wireLayer : int * int)
        (cx : int64)
        (cy : int64) : ViaSegment list =
    let vias = between snapLayer wireLayer
    if List.isEmpty vias then [] else
    let metals = intermediateMetals snapLayer wireLayer
    let half (side : int64) = side / 2L
    let mk (layer : int * int) (side : int64) : ViaSegment =
        { Layer = layer; CenterX = cx; CenterY = cy; SideDbu = side }
    // Via cuts.
    let viaSegs =
        vias
        |> List.choose (fun v ->
            viaCut view units v |> Option.map (fun vc -> mk v vc.SideDbu))
    // Snap-layer pad: encloses the bottommost via (first in `vias`
    // when iterating from snapLayer toward wireLayer; `between`
    // already returns them in that order).
    let snapPadSide =
        match vias with
        | first :: _ -> padSideForVia view units snapLayer first
        | [] -> None
    let snapPadSeg =
        snapPadSide
        |> Option.map (fun s -> mk snapLayer s)
        |> Option.toList
    // Intermediate metal pads: each must enclose the via BELOW
    // and the via ABOVE. `metals` and `vias` line up such that
    // `metals.[i]` is sandwiched between `vias.[i]` (below) and
    // `vias.[i+1]` (above).
    let metalSegs =
        metals
        |> List.mapi (fun i m ->
            let below = vias.[i]
            let above = vias.[i + 1]
            let sBelow = padSideForVia view units m below
            let sAbove = padSideForVia view units m above
            match sBelow, sAbove with
            | Some a, Some b -> Some (mk m (max a b))
            | Some a, None
            | None,   Some a -> Some (mk m a)
            | None,   None -> None)
        |> List.choose id
    snapPadSeg @ viaSegs @ metalSegs
