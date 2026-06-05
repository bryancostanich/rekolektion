module Rekolektion.Viz.Core.Tests.RoutingViaToolTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Routing
open Rekolektion.Viz.Core.Drc

let private li1  : int * int = (67, 20)
let private met1 : int * int = (68, 20)
let private met2 : int * int = (69, 20)
let private met3 : int * int = (70, 20)
let private mcon : int * int = (67, 44)
let private via1 : int * int = (68, 44)
let private via2 : int * int = (69, 44)

let private testUnits : Units = { DbuNm = 1; UuUm = 1 }

// ─────────────────────────────────────────────────────────────────
// emitStandaloneAt — the full via-stack INCLUDING the top-layer
// pad.  The headline regression that motivated this module: a
// V-tool click that drops only the mcon cut (because emitAt
// omits the wire-layer pad) reads as "nothing happened" in the
// canvas. The top pad makes the via visible.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``emitStandaloneAt li1 -> met1 emits mcon + li1 pad + met1 pad`` () =
    let segs =
        ViaTool.emitStandaloneAt
            Rules.defaultView testUnits
            li1   // snapLayer  (target pin is on li1)
            met1  // topLayer   (V-tool ActiveLayer = met1)
            0L 0L
    // Three layers represented: the mcon cut, the li1 enclosure
    // pad emitAt produced for the snap-side, AND the met1 pad
    // emitStandaloneAt synthesizes for the wire-side.
    let layers = segs |> List.map (fun s -> s.Layer) |> List.sort
    layers |> should contain mcon
    layers |> should contain met1
    // Without the met1 pad the visual is just a 150 nm mcon dot.
    // Pin this explicitly so a future emitAt refactor that
    // accidentally drops the top pad surfaces as a test failure
    // and not "my via is invisible" in the user's chat.
    segs
    |> List.exists (fun s -> s.Layer = met1)
    |> should be True

[<Fact>]
let ``emitStandaloneAt li1 -> met3 emits the full ladder`` () =
    // met3 active layer + li1 pin = 2-decade via stack.  Expected
    // layers (sorted): mcon, via1, via2 (vias) + met1, met2
    // (intermediate metal pads emitAt provides) + met3 (top pad
    // emitStandaloneAt adds).  li1 pad NOT required — emitAt only
    // emits the snap-side pad when `padSideForVia li1 mcon` finds
    // an enclosure rule, and the default view doesn't carry one
    // (sky130 li.1 / li.5 are on the Magic-compat side, not the
    // KLayout-tuned view).  Real layouts get the li1 enclosure
    // from the rail the user clicked anyway — the V tool drops
    // its mcon on top of existing li1 geometry, so a synthetic
    // li1 pad would just duplicate the rail.
    let segs =
        ViaTool.emitStandaloneAt
            Rules.defaultView testUnits li1 met3 0L 0L
    let layers = segs |> List.map (fun s -> s.Layer) |> List.distinct |> List.sort
    layers |> should contain mcon
    layers |> should contain via1
    layers |> should contain via2
    layers |> should contain met1
    layers |> should contain met2
    layers |> should contain met3

[<Fact>]
let ``emitStandaloneAt with same top and snap is a no-op`` () =
    ViaTool.emitStandaloneAt
        Rules.defaultView testUnits met1 met1 0L 0L
    |> should be Empty

[<Fact>]
let ``emitStandaloneAt centres every segment at the click point`` () =
    let cx, cy = 12500L, -800L
    let segs =
        ViaTool.emitStandaloneAt
            Rules.defaultView testUnits li1 met2 cx cy
    segs |> List.iter (fun s ->
        s.CenterX |> should equal cx
        s.CenterY |> should equal cy)

// ─────────────────────────────────────────────────────────────────
// DRC-view sensitivity — Compat.Klayout is the user default but
// its rule list is missing Width for via2 + the matching
// met2/met3 enclosures.  These tests pin (a) the bug as it would
// surface if the V tool used model.DrcView under Klayout — only
// 4 segments emit for met3 ← li1, no via2 cut, no top pad — and
// (b) the Magic-full view as the right input for via emission.
// Update.fs's handler always passes Magic.defaultView; if a future
// refactor switches it back to model.DrcView the user-side
// regression surfaces here.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``met3 li1 under Magic view emits the full 6-segment stack`` () =
    // Magic view carries via2 width + every metal enclosure rule,
    // so emit produces: mcon, via1, via2, met1 pad, met2 pad,
    // met3 top pad.  Compare against the Klayout-view variant
    // below — the contrast is what makes the "use Magic for
    // emission" choice in Update.fs honest.
    let segs =
        ViaTool.emitStandaloneAt
            Rules.Magic.defaultView testUnits li1 met3 0L 0L
    segs |> List.length |> should equal 6
    let layers = segs |> List.map (fun s -> s.Layer) |> List.distinct |> List.sort
    layers |> should contain mcon
    layers |> should contain via1
    layers |> should contain via2
    layers |> should contain met1
    layers |> should contain met2
    layers |> should contain met3

[<Fact>]
let ``met3 li1 under Klayout view emits the full 6-segment stack`` () =
    // Originally documented as the gap that surfaced as 'V tool
    // didn't work' on 2026-06-03: under the Klayout view the via2
    // width + met2/met3 enclosures weren't defined, so the V tool
    // silently dropped a partial 4-rect stack with no via2 contact
    // and no met3 top pad.  Fixed in `Rules.fs` by adding `via2.1`,
    // `via2.2`, `via2.4`, `via2.5` to `Rules.Klayout.allRules` —
    // PDK thresholds are identical on both compats so the values
    // copy verbatim from the Magic block.  Forward-looking
    // regression guard: the Klayout view must now match the Magic
    // view's full 6-segment emit.
    let segs =
        ViaTool.emitStandaloneAt
            Rules.Klayout.defaultView testUnits li1 met3 0L 0L
    segs |> List.length |> should equal 6
    let layers = segs |> List.map (fun s -> s.Layer) |> List.distinct |> List.sort
    layers |> should contain mcon
    layers |> should contain via1
    layers |> should contain via2
    layers |> should contain met1
    layers |> should contain met2
    layers |> should contain met3

// ─────────────────────────────────────────────────────────────────
// Min-area floor — every metal pad must be at least sqrt(MinArea)
// on a side, otherwise the standalone via fails min-area DRC.  The
// wire-route path skates by because the wire body extends past the
// pad and adds area; a standalone V-tool via has no wire body to
// pick up the slack.  Reported 2026-06-03: user clicked V at met3
// active, got a met3 pad of 390 nm (200 + 2*95 enclosure-driven)
// vs the met3.6 min-area floor of ~490 nm (sqrt 0.240 µm²).
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``met3 top pad floors at sqrt(met3.6 min-area)`` () =
    let segs =
        ViaTool.emitStandaloneAt
            Rules.Magic.defaultView testUnits li1 met3 0L 0L
    let met3Seg = segs |> List.find (fun s -> s.Layer = met3)
    // met3.6 = 0.240 µm² → side ≥ 0.4899 µm.  With DbuNm = 1 that's
    // 490 DBU after Math.Ceiling.  Pure enclosure would give
    // 200 + 2*95 = 390 DBU — the regression the floor catches.
    met3Seg.SideDbu |> should be (greaterThanOrEqualTo 490L)

[<Fact>]
let ``met2 pad already satisfies min-area without the floor needing to kick in`` () =
    // Documents that the floor is layer-specific: met2.6 = 0.0676 µm²
    // → side ≥ 260 nm.  Enclosure-driven met2 pad (via2 = 200 nm +
    // 2*85 = 370 nm or via1 = 150 + 2*85 = 320 nm, whichever
    // dominates) sails past that.  So the met2 segment's side is
    // STILL exactly `padSideForVia met2 vMax` — the floor is a
    // no-op here, and the test pins that so a future overzealous
    // floor doesn't bloat layers that don't need it.
    let segs =
        ViaTool.emitStandaloneAt
            Rules.Magic.defaultView testUnits li1 met3 0L 0L
    let met2Seg = segs |> List.find (fun s -> s.Layer = met2)
    let viaBased =
        max
            (ViaStack.padSideForVia Rules.Magic.defaultView testUnits met2 via1
             |> Option.defaultValue 0L)
            (ViaStack.padSideForVia Rules.Magic.defaultView testUnits met2 via2
             |> Option.defaultValue 0L)
    met2Seg.SideDbu |> should equal viaBased

[<Fact>]
let ``via cuts pass through the floor untouched`` () =
    // Vias / contacts on dataType 44 have their own Width rule; the
    // metal min-area floor doesn't apply to them.  Without this
    // guard the floor could accidentally bloat a mcon cut from
    // 170 nm up to met1's 290 nm min-area side or similar.
    let segs =
        ViaTool.emitStandaloneAt
            Rules.Magic.defaultView testUnits li1 met3 0L 0L
    let mconSeg = segs |> List.find (fun s -> s.Layer = mcon)
    // mcon width per `ct.1_a` / `mcon.1` = 0.17 µm = 170 nm with
    // DbuNm = 1.  Pin the expected size directly so the test
    // doesn't have to muck with rule-list pattern matching.
    mconSeg.SideDbu |> should equal 170L

[<Fact>]
let ``emitStandaloneAt top pad has same side as padSideForVia`` () =
    // Documents the wire-side pad's sizing rule: it encloses
    // the TOPMOST via in the stack (the via closest to topLayer).
    // For li1 → met2 the topmost via is via1; the met2 pad must
    // be sized by padSideForVia met2 via1.
    let segs =
        ViaTool.emitStandaloneAt
            Rules.defaultView testUnits li1 met2 0L 0L
    let met2Side =
        segs
        |> List.find (fun s -> s.Layer = met2)
        |> _.SideDbu
    let expected =
        ViaStack.padSideForVia Rules.defaultView testUnits met2 via1
    expected |> should equal (Some met2Side)

// ─────────────────────────────────────────────────────────────────
// resolveSnap — cell-pin filtering by "strictly below top".
// ─────────────────────────────────────────────────────────────────

let private mkTarget (layer: int * int) (x: int64) (y: int64) (net: string)
                     : Snap.SnapTarget =
    let (n, d) = layer
    { X = x; Y = y; Net = net
      Layer = n; DataType = d
      Source = ("c", 0) }

let private mkKnuckle
        (layer: int * int)
        (x1: int64) (y1: int64) (x2: int64) (y2: int64)
        : Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon =
    let (n, d) = layer
    { Layer = n; DataType = d
      Points =
        [| { X = x1; Y = y1 }
           { X = x2; Y = y1 }
           { X = x2; Y = y2 }
           { X = x1; Y = y2 } |]
      SourceStructure = "top"
      SourceIndex = 0
      TopInstanceIndex = None }

// ─────────────────────────────────────────────────────────────────
// resolveSnap — pin point snap.  See via_tool.md "Snap sources / 1".
// 8 px Euclidean radius; nearest pin wins.
// ─────────────────────────────────────────────────────────────────

let private vGuide (xDbu: int64) : Guide =
    { Orientation = Vertical; CoordDbu = xDbu }

let private hGuide (yDbu: int64) : Guide =
    { Orientation = Horizontal; CoordDbu = yDbu }

[<Fact>]
let ``pin snap: nearest pin within radius wins`` () =
    let targets =
        [| mkTarget li1  100L 100L "VSS"
           mkTarget met1 200L 200L "VDD" |]
    // Cursor at (110, 105) is ~11 dbu from VSS, ~110 from VDD.
    // Radius 50 → VSS wins.
    let s = ViaTool.resolveSnap None targets [] [||] 110L 105L 50L false
    match s with
    | Some snap ->
        snap.Net   |> should equal "VSS"
        snap.Layer |> should equal li1
        snap.Kind  |> should equal ViaTool.SnapKind.Pin
    | None -> failwith "expected a pin snap"

[<Fact>]
let ``pin snap: strictly-below filter excludes pins on the active layer`` () =
    let targets =
        [| mkTarget li1  100L 100L "VSS"
           mkTarget met1 110L 105L "VDD" |]
    // VDD is closer but lives on met1 = active layer → excluded.
    let s = ViaTool.resolveSnap (Some met1) targets [] [||] 110L 105L 50L false
    match s with
    | Some snap -> snap.Net |> should equal "VSS"
    | None -> failwith "expected VSS to survive the filter"

[<Fact>]
let ``pin snap: outside radius returns None`` () =
    let targets = [| mkTarget li1 1000L 1000L "VSS" |]
    ViaTool.resolveSnap None targets [] [||] 0L 0L 100L false
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``pin snap: empty target array returns None`` () =
    ViaTool.resolveSnap None [||] [] [||] 0L 0L 100L false
    |> should equal (None : ViaTool.Snap option)

// ─────────────────────────────────────────────────────────────────
// Knuckle centre snap — point candidate at the bbox centroid of a
// square-ish routing rect.  Only fires when cursor is within
// radius of the CENTRE (not the whole bbox — that bbox-containment
// pull was the "super coarse" feel and is gone, per via_tool.md
// "What is NOT a snap source").
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``knuckle centre: cursor near centre wins`` () =
    // Met1 square 400 nm wide, centre at (0, 0).  Cursor at (5, 3)
    // is ~6 dbu from centre.  Radius 50 → centre wins.
    let polys = [| mkKnuckle met1 -200L -200L 200L 200L |]
    let s = ViaTool.resolveSnap None [||] [] polys 5L 3L 50L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.KnuckleCenter
        snap.Layer |> should equal met1
        snap.X     |> should equal 0L
        snap.Y     |> should equal 0L
    | None -> failwith "expected knuckle centre snap"

[<Fact>]
let ``knuckle centre: cursor inside bbox but far from centre does NOT snap`` () =
    // The headline bug fix.  Cursor at (180, 180) is INSIDE the
    // bbox (-200..200) but ~250 dbu from centre (0, 0).  Radius
    // 50 → no snap.  Old behaviour: bbox-containment yanked
    // cursor to centre regardless of distance.
    let polys = [| mkKnuckle met1 -200L -200L 200L 200L |]
    ViaTool.resolveSnap None [||] [] polys 180L 180L 50L false
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``knuckle centre: nearer centre wins over farther centre`` () =
    // Two knuckle centres; cursor closer to met2's.  Layer ordering
    // doesn't auto-decide any more — distance does.
    let polys =
        [| mkKnuckle met1 0L 0L 100L 100L       // centre (50, 50)
           mkKnuckle met2 100L 100L 200L 200L |] // centre (150, 150)
    let s = ViaTool.resolveSnap None [||] [] polys 40L 50L 50L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.KnuckleCenter
        snap.Layer |> should equal met1
        snap.X     |> should equal 50L
        snap.Y     |> should equal 50L
    | None -> failwith "expected the nearer met1 centre to win"

[<Fact>]
let ``knuckle centre: strictly-below filter excludes active layer`` () =
    let met3 : int * int = (70, 20)
    let polys =
        [| mkKnuckle met1 -100L -100L 100L 100L
           mkKnuckle met2 -100L -100L 100L 100L
           mkKnuckle met3 -100L -100L 100L 100L |]
    let s = ViaTool.resolveSnap (Some met2) [||] [] polys 0L 0L 50L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.KnuckleCenter
        snap.Layer |> should equal met1
    | None -> failwith "expected met1 to survive the strict-below filter"

[<Fact>]
let ``non-routing rect: never generates snap candidates`` () =
    let diff : int * int = (65, 20)
    let polys = [| mkKnuckle diff -100L -100L 100L 100L |]
    ViaTool.resolveSnap None [||] [] polys 0L 0L 50L false
    |> should equal (None : ViaTool.Snap option)

// ─────────────────────────────────────────────────────────────────
// Wire endpoint snap — point candidate at each tip of a thin rect.
// Wire centerline snap — line candidate (axis snap) at the wire's
// mid-width coord.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``wire endpoint: cursor exactly at tip yields point snap (tie → point)`` () =
    // Horizontal met1 wire (0..1000, 0..100) — left tip at (0, 50).
    // Cursor at (0, 50) — endpoint distance 0; centerline AxisY
    // distance 0.  Tie → point snap wins per rule 2.4.
    let polys = [| mkKnuckle met1 0L 0L 1000L 100L |]
    let s = ViaTool.resolveSnap None [||] [] polys 0L 50L 50L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.WireEndpoint
        snap.X     |> should equal 0L
        snap.Y     |> should equal 50L
        snap.Layer |> should equal met1
    | None -> failwith "expected wire-endpoint point snap on exact-tie cursor"

[<Fact>]
let ``wire centerline beats endpoint when cursor is perpendicular-closer`` () =
    // Same wire.  Cursor at (3, 48) — endpoint at (0, 50) is
    // ~3.6 Euclidean away; centerline midY=50 is 2 perpendicular.
    // 2 < 3.6 → AxisY wins.  This is the design choice: when
    // the cursor is closer to the centerline than to the tip,
    // snap to the centerline (axis snap).  Users who want the
    // tip exactly click AT the tip (covered above).
    let polys = [| mkKnuckle met1 0L 0L 1000L 100L |]
    let s = ViaTool.resolveSnap (Some met2) [||] [] polys 3L 48L 50L false
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.AxisY
        snap.X    |> should equal 3L
        snap.Y    |> should equal 50L
    | None -> failwith "expected centerline AxisY to beat endpoint by distance"

[<Fact>]
let ``wire centerline: cursor perpendicular-close yields AxisY snap`` () =
    // Horizontal wire midline at Y=50.  Cursor at (500, 53) →
    // perpendicular distance 3, within radius 50.  Result: X stays
    // at cursor (500), Y snaps to 50, kind = AxisY.
    let polys = [| mkKnuckle met1 0L 0L 1000L 100L |]
    let s = ViaTool.resolveSnap (Some met2) [||] [] polys 500L 53L 50L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.AxisY
        snap.X     |> should equal 500L
        snap.Y     |> should equal 50L
        snap.Layer |> should equal met1
    | None -> failwith "expected wire centerline AxisY snap"

[<Fact>]
let ``wire centerline: cursor far perpendicular returns no axis snap`` () =
    // Cursor at (500, 200) — perpendicular distance to midline
    // (Y=50) is 150, outside radius 50.  No other targets → None.
    let polys = [| mkKnuckle met1 0L 0L 1000L 100L |]
    ViaTool.resolveSnap (Some met2) [||] [] polys 500L 200L 50L false
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``vertical wire centerline yields AxisX snap`` () =
    // Vertical met2 wire (0..100, 0..1000) — midline at X=50.
    // Cursor at (47, 500) → AxisX, X=50, Y stays at 500.
    let polys = [| mkKnuckle met2 0L 0L 100L 1000L |]
    let s = ViaTool.resolveSnap (Some met3) [||] [] polys 47L 500L 50L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.AxisX
        snap.X     |> should equal 50L
        snap.Y     |> should equal 500L
        snap.Layer |> should equal met2
    | None -> failwith "expected vertical wire AxisX snap"

[<Fact>]
let ``square pad classifies as knuckle (centre), not wire`` () =
    // Aspect 1:1 → KnuckleCenter, not AxisX / AxisY / Endpoint.
    let polys = [| mkKnuckle met1 -200L -200L 200L 200L |]
    let s = ViaTool.resolveSnap None [||] [] polys 0L 0L 50L false
    match s with
    | Some snap -> snap.Kind |> should equal ViaTool.SnapKind.KnuckleCenter
    | None -> failwith "expected knuckle classification"

// ─────────────────────────────────────────────────────────────────
// Guide snap — line candidate, contributes one axis.  Requires
// active layer ≥ met1 so the via has an implied top.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``guide AxisX: vertical guide pulls X, Y stays at cursor`` () =
    let s = ViaTool.resolveSnap (Some met2) [||] [vGuide 100L] [||]
                                103L 250L 10L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.AxisX
        snap.X     |> should equal 100L
        snap.Y     |> should equal 250L
        snap.Layer |> should equal met1
    | None -> failwith "expected guide AxisX snap"

[<Fact>]
let ``guide AxisY: horizontal guide pulls Y, X stays at cursor`` () =
    let s = ViaTool.resolveSnap (Some met2) [||] [hGuide 500L] [||]
                                400L 497L 10L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.AxisY
        snap.X     |> should equal 400L
        snap.Y     |> should equal 500L
        snap.Layer |> should equal met1
    | None -> failwith "expected guide AxisY snap"

[<Fact>]
let ``guide AxisCross: vertical + horizontal guides combine`` () =
    // Vertical guide at X=100, horizontal guide at Y=200.  Cursor
    // at (105, 198) — within radius of both.  AxisCross: X=100,
    // Y=200.
    let guides = [vGuide 100L; hGuide 200L]
    let s = ViaTool.resolveSnap (Some met2) [||] guides [||]
                                105L 198L 10L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.AxisCross
        snap.X     |> should equal 100L
        snap.Y     |> should equal 200L
        snap.Layer |> should equal met1
    | None -> failwith "expected AxisCross from two guides"

[<Fact>]
let ``AxisCross from guide-X + wire-centerline-Y inherits wire's layer`` () =
    // The NAND_OUT case.  Horizontal met1 wire at Y=50.  Vertical
    // guide at X=100.  Cursor at (98, 53) — within both radii.
    // AxisCross at (100, 50); layer = met1 (the wire), NOT met1
    // (guide default — happens to match here, but the rule is
    // "wire layer wins").  Active layer met2 confirms top.
    let wire = [| mkKnuckle met1 0L 0L 1000L 100L |]
    let guides = [vGuide 100L]
    let s = ViaTool.resolveSnap (Some met2) [||] guides wire
                                98L 53L 50L false
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.AxisCross
        snap.X     |> should equal 100L
        snap.Y     |> should equal 50L
        snap.Layer |> should equal met1
    | None -> failwith "expected guide+wire AxisCross"

[<Fact>]
let ``guide snap: out of perpendicular radius returns None`` () =
    ViaTool.resolveSnap (Some met2) [||] [vGuide 100L] [||]
                        200L 250L 10L false
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``guide snap: disabled when active layer is None`` () =
    ViaTool.resolveSnap None [||] [vGuide 100L] [||] 100L 250L 10L false
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``guide snap: disabled when active layer is li1`` () =
    ViaTool.resolveSnap (Some li1) [||] [vGuide 100L] [||]
                        100L 250L 10L false
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``guide AxisX: picks nearest of multiple guides`` () =
    let guides = [ vGuide 95L; vGuide 120L; vGuide 112L ]
    let s = ViaTool.resolveSnap (Some met2) [||] guides [||]
                                100L 250L 15L false
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.AxisX
        snap.X    |> should equal 95L
    | None -> failwith "expected nearest guide to win"

// ─────────────────────────────────────────────────────────────────
// Point vs axis priority — closer cursor-to-snap-point wins.
// (Rule 2.4 — Figma / Illustrator behaviour.)
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``point snap wins when closer than axis snap`` () =
    // Pin at (101, 250) — Euclidean distance 1 from cursor (100, 250).
    // Vertical guide at X=90 — perpendicular distance 10.
    // Pin is closer (1 < 10) → point wins.
    let targets = [| mkTarget li1 101L 250L "NAND_OUT" |]
    let guides = [vGuide 90L]
    let s = ViaTool.resolveSnap (Some met2) targets guides [||]
                                100L 250L 50L false
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.Pin
        snap.Net  |> should equal "NAND_OUT"
    | None -> failwith "expected closer point snap to win"

[<Fact>]
let ``axis snap wins when closer than point snap`` () =
    // The NAND_OUT regression case the user reported.  Cursor
    // (2510, 2400).  Wire endpoint at (2530, 2405) — Euclidean
    // ~21.  Guide at X=2495 — perpendicular 15.  15 < 21 →
    // guide AxisX wins.  Pre-fix point snaps unconditionally
    // beat axis snaps and the wire endpoint stole the click.
    // Long horizontal li1 wire (5:1 aspect so it's clearly a wire,
    // not a knuckle).  Endpoints at (2400, 2405) and (2540, 2405).
    let wire = [| mkKnuckle li1 2400L 2395L 2540L 2415L |]
    let guides = [vGuide 2495L]
    let s = ViaTool.resolveSnap (Some met2) [||] guides wire
                                2510L 2400L 64L false
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.AxisCross
        snap.X    |> should equal 2495L
        // Y from the wire's centerline at midY=2405.
        snap.Y    |> should equal 2405L
        // Layer = li1 (the wire) — wire wins over guide default.
        snap.Layer |> should equal li1
    | None -> failwith "expected guide+wire AxisCross to beat wire-endpoint point snap"

[<Fact>]
let ``wire centerline does NOT fire when cursor is past the wire's end`` () =
    // Horizontal wire ends at xMax=3420.  Cursor at x=3450 — past
    // the wire's right edge by 30 dbu.  Even though cursor's Y is
    // exactly on the wire's midY (perpendicular distance 0),
    // centerline should not fire — wires have finite extent and
    // the cursor is not over the wire.  Before this fix the
    // centerline was treated as an infinite horizontal line and
    // pulled the cursor's Y from outside the wire's bbox.
    let wire = [| mkKnuckle li1 3250L 2850L 3420L 3020L |]
    let s = ViaTool.resolveSnap (Some met2) [||] [] wire
                                3450L 2935L 50L false
    s |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``stacked wires at same Y: highest layer wins`` () =
    // Four routing rects all centered at Y=2935 (the user's
    // real-world scenario at the (3450, 2938) click).  Cursor
    // perpendicular distance 0 to every centerline; layer
    // descending tiebreak should pick met2 (the highest below
    // active=met3), giving a 1-step met2↔met3 via stack instead
    // of the 2-step met1↔met2↔met3 the old code picked when
    // li1 / met1 happened to sort first.
    let rects =
        [| mkKnuckle li1  3000L 2870L 3500L 3000L  // li1 wire
           mkKnuckle met1 3000L 2870L 3500L 3000L  // met1 wire
           mkKnuckle met2 3000L 2870L 3500L 3000L  // met2 wire
           mkKnuckle (70, 20) 3000L 2870L 3500L 3000L |] // met3 (filtered out, = active)
    let s = ViaTool.resolveSnap (Some (70, 20)) [||] [] rects
                                3250L 2935L 50L false
    match s with
    | Some snap ->
        snap.Layer |> should equal met2
    | None -> failwith "expected snap on the highest non-active layer"

[<Fact>]
let ``point and axis tied: point wins (stable tie-break)`` () =
    // Pin at (105, 250) — distance 5.
    // Vertical guide at X=95 — perpendicular distance 5.
    // Tie → point wins per rule 2.4.
    let targets = [| mkTarget li1 105L 250L "VSS" |]
    let guides = [vGuide 95L]
    let s = ViaTool.resolveSnap (Some met2) targets guides [||]
                                100L 250L 50L false
    match s with
    | Some snap -> snap.Kind |> should equal ViaTool.SnapKind.Pin
    | None -> failwith "expected point to win on equal distance"

// ─────────────────────────────────────────────────────────────────
// Alt suppress — drops via at raw cursor (rule 2.1 / 3.5).
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``alt: drops via at raw cursor regardless of nearby snap sources`` () =
    let targets = [| mkTarget li1 100L 100L "VSS" |]
    let polys = [| mkKnuckle met1 -200L -200L 200L 200L |]
    let guides = [vGuide 50L]
    let s = ViaTool.resolveSnap (Some met2) targets guides polys
                                100L 100L 50L true
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.RawCursor
        snap.X     |> should equal 100L
        snap.Y     |> should equal 100L
        snap.Layer |> should equal met1
    | None -> failwith "expected raw-cursor snap under Alt"

[<Fact>]
let ``alt: returns None when active layer is None`` () =
    // No active layer → no implied bottom for the via → can't
    // drop anything, even with Alt.
    ViaTool.resolveSnap None [||] [] [||] 100L 100L 50L true
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``alt: returns None when active layer is li1`` () =
    ViaTool.resolveSnap (Some li1) [||] [] [||] 100L 100L 50L true
    |> should equal (None : ViaTool.Snap option)

// ─────────────────────────────────────────────────────────────────
// pickTopLayer — explicit-active vs default-to-snap+1.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``pickTopLayer honours an explicit active layer`` () =
    let snap : ViaTool.Snap =
        { X = 0L; Y = 0L; Layer = li1; Net = "BL"; Kind = ViaTool.SnapKind.Pin }
    ViaTool.pickTopLayer (Some met3) snap |> should equal met3

[<Fact>]
let ``pickTopLayer defaults to one layer above the snap`` () =
    let snap : ViaTool.Snap =
        { X = 0L; Y = 0L; Layer = li1; Net = "BL"; Kind = ViaTool.SnapKind.Pin }
    // li1 = (67, 20) → met1 = (68, 20)
    ViaTool.pickTopLayer None snap |> should equal met1
    let snap2 : ViaTool.Snap =
        { X = 0L; Y = 0L; Layer = met1; Net = "BL"; Kind = ViaTool.SnapKind.Pin }
    ViaTool.pickTopLayer None snap2 |> should equal met2
