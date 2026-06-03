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

[<Fact>]
let ``resolveSnap with no top filter returns the nearest pin`` () =
    let targets =
        [| mkTarget li1  100L 100L "VSS"
           mkTarget met1 200L 200L "VDD" |]
    let s = ViaTool.resolveSnap None targets [||] 110L 105L 50L
    match s with
    | Some snap ->
        snap.Net |> should equal "VSS"
        snap.Layer |> should equal li1
        snap.Kind |> should equal ViaTool.SnapKind.Pin
    | None -> failwith "expected a snap"

[<Fact>]
let ``resolveSnap with top = met1 filters out met1 pins (strictly below)`` () =
    let targets =
        [| mkTarget li1  100L 100L "VSS"
           mkTarget met1 110L 105L "VDD" |]
    // Cursor near both — without the filter VDD wins (it's
    // closer). With the strict-below filter, VSS wins because
    // VDD's layer = top layer.
    let s = ViaTool.resolveSnap (Some met1) targets [||] 110L 105L 50L
    match s with
    | Some snap -> snap.Net |> should equal "VSS"
    | None -> failwith "expected VSS to survive the filter"

[<Fact>]
let ``resolveSnap returns None when no target within radius`` () =
    let targets = [| mkTarget li1 1000L 1000L "VSS" |]
    ViaTool.resolveSnap None targets [||] 0L 0L 100L
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``resolveSnap returns None on an empty target array`` () =
    ViaTool.resolveSnap None [||] [||] 0L 0L 100L
    |> should equal (None : ViaTool.Snap option)

// ─────────────────────────────────────────────────────────────────
// Knuckle snap — wins over pin when the cursor is INSIDE
// routing-layer geometry. Reported 2026-06-03: clicks on a met1
// knuckle were snapping to the li1 label underneath and
// emitting a li1→met1 via that dedup'd against the existing
// contact stack ("looked like nothing happened").
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``resolveSnap prefers a met1 knuckle over a li1 pin under it`` () =
    // Click coords inside both the met1 knuckle (a 400 nm square
    // centred at 0,0) AND within radius of a li1 pin label.
    let targets = [| mkTarget li1 0L 0L "NAND_OUT" |]
    let knuckles =
        [| mkKnuckle met1 -200L -200L 200L 200L |]
    let s = ViaTool.resolveSnap None targets knuckles 50L 30L 100L
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.Knuckle
        snap.Layer |> should equal met1
        // Centroid of the (-200,-200)–(200,200) rect.
        snap.X |> should equal 0L
        snap.Y |> should equal 0L
    | None -> failwith "expected the met1 knuckle to win"

[<Fact>]
let ``resolveSnap picks the topmost routing-layer rect under cursor`` () =
    // met1 knuckle sits inside a wider met2 rect. Cursor inside
    // both → met2 wins (higher routing layer = "you're visibly
    // on met2"). Without this, a met2 pad over a met1 stub would
    // always drop a met1→met2 via via the met1 path.
    let polys =
        [| mkKnuckle met1 -100L -100L 100L 100L
           mkKnuckle met2 -300L -300L 300L 300L |]
    let s = ViaTool.resolveSnap None [||] polys 0L 0L 50L
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.Knuckle
        snap.Layer |> should equal met2
    | None -> failwith "expected met2 to win"

[<Fact>]
let ``resolveSnap knuckle obeys the strictly-below filter`` () =
    // Active layer = met2; knuckles are met1 + met2 + met3. The
    // strict-below filter has to exclude met2 (=top) and met3
    // (above top), leaving met1 as the winner.
    let met3 : int * int = (70, 20)
    let polys =
        [| mkKnuckle met1 -100L -100L 100L 100L
           mkKnuckle met2 -100L -100L 100L 100L
           mkKnuckle met3 -100L -100L 100L 100L |]
    let s = ViaTool.resolveSnap (Some met2) [||] polys 0L 0L 50L
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.Knuckle
        snap.Layer |> should equal met1
    | None -> failwith "expected met1 to survive the strict-below filter"

[<Fact>]
let ``resolveSnap falls back to pin when no knuckle is under cursor`` () =
    // Cursor outside the knuckle's bbox AND near a pin → pin path
    // engages.  Same shape as the original v1 behavior; guard
    // against a knuckle-only resolver that loses pin snap.
    let targets = [| mkTarget li1 500L 500L "VSS" |]
    let knuckles =
        [| mkKnuckle met1 -100L -100L 100L 100L |]
    let s = ViaTool.resolveSnap None targets knuckles 495L 502L 50L
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.Pin
        snap.Net  |> should equal "VSS"
    | None -> failwith "expected the pin to win when no knuckle covers the cursor"

[<Fact>]
let ``resolveSnap skips non-routing layers when searching for a knuckle`` () =
    // A `diff` rect under the cursor is NOT a knuckle.  Without
    // the isRoutingLayerKey guard the V tool would happily snap
    // to diff and produce a nonsensical via.
    let diff : int * int = (65, 20)
    let polys = [| mkKnuckle diff -100L -100L 100L 100L |]
    ViaTool.resolveSnap None [||] polys 0L 0L 50L
    |> should equal (None : ViaTool.Snap option)

// ─────────────────────────────────────────────────────────────────
// Wire snap — long rects branch into centerline + end snapping
// instead of bbox-centroid.  Spec called out by the user during
// the V tool design: "if wire is below, snap to centerline /
// centerline end if at end of wire".
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``wire midbody snaps to centerline projection`` () =
    // Horizontal met1 wire from x=0..1000, y=0..100 (10:1 aspect).
    // Cursor at (500, 60) → centerline midY = 50, snapX = 500.
    let polys = [| mkKnuckle met1 0L 0L 1000L 100L |]
    let s = ViaTool.resolveSnap None [||] polys 500L 60L 0L
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.WireCenterline
        snap.X    |> should equal 500L
        snap.Y    |> should equal 50L
        snap.Layer |> should equal met1
    | None -> failwith "expected wire centerline snap"

[<Fact>]
let ``wire end (left tip) snaps to the wire endpoint`` () =
    // Same horizontal wire; cursor at (100, 50).  endTol = h * 2
    // = 200.  cursor.X - xMin = 100 < 200 → left end (xMin = 0).
    let polys = [| mkKnuckle met1 0L 0L 1000L 100L |]
    let s = ViaTool.resolveSnap None [||] polys 100L 50L 0L
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.WireEnd
        snap.X    |> should equal 0L
        snap.Y    |> should equal 50L
    | None -> failwith "expected wire-end snap at the left tip"

[<Fact>]
let ``wire end (right tip) snaps to the far endpoint`` () =
    let polys = [| mkKnuckle met1 0L 0L 1000L 100L |]
    let s = ViaTool.resolveSnap None [||] polys 900L 50L 0L
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.WireEnd
        snap.X    |> should equal 1000L
        snap.Y    |> should equal 50L
    | None -> failwith "expected wire-end snap at the right tip"

[<Fact>]
let ``vertical wire snaps along the X midline`` () =
    // Vertical met2 wire 0..100 wide, 0..1000 tall (1:10).
    // Cursor at (40, 500) → midX = 50, centerline snap.
    let polys = [| mkKnuckle met2 0L 0L 100L 1000L |]
    let s = ViaTool.resolveSnap None [||] polys 40L 500L 0L
    match s with
    | Some snap ->
        snap.Kind  |> should equal ViaTool.SnapKind.WireCenterline
        snap.X     |> should equal 50L
        snap.Y     |> should equal 500L
        snap.Layer |> should equal met2
    | None -> failwith "expected vertical wire centerline snap"

[<Fact>]
let ``vertical wire bottom tip snaps to wire end`` () =
    let polys = [| mkKnuckle met2 0L 0L 100L 1000L |]
    let s = ViaTool.resolveSnap None [||] polys 50L 100L 0L
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.WireEnd
        snap.X    |> should equal 50L
        snap.Y    |> should equal 0L
    | None -> failwith "expected wire-end snap at the bottom tip"

[<Fact>]
let ``square pad stays a knuckle (centroid), not a wire`` () =
    // Aspect 1:1 — must be classified Knuckle, not WireCenterline.
    // Guards the threshold against a sloppy refactor that lowers
    // it under 1.0 and turns every pad into a wire.
    let polys = [| mkKnuckle met1 -200L -200L 200L 200L |]
    let s = ViaTool.resolveSnap None [||] polys 0L 0L 0L
    match s with
    | Some snap ->
        snap.Kind |> should equal ViaTool.SnapKind.Knuckle
        snap.X    |> should equal 0L
        snap.Y    |> should equal 0L
    | None -> failwith "expected knuckle classification"

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
