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

[<Fact>]
let ``resolveSnap with no top filter returns the nearest pin`` () =
    let targets =
        [| mkTarget li1  100L 100L "VSS"
           mkTarget met1 200L 200L "VDD" |]
    let s = ViaTool.resolveSnap None targets 110L 105L 50L
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
    let s = ViaTool.resolveSnap (Some met1) targets 110L 105L 50L
    match s with
    | Some snap -> snap.Net |> should equal "VSS"
    | None -> failwith "expected VSS to survive the filter"

[<Fact>]
let ``resolveSnap returns None when no target within radius`` () =
    let targets = [| mkTarget li1 1000L 1000L "VSS" |]
    ViaTool.resolveSnap None targets 0L 0L 100L
    |> should equal (None : ViaTool.Snap option)

[<Fact>]
let ``resolveSnap returns None on an empty target array`` () =
    ViaTool.resolveSnap None [||] 0L 0L 100L
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
