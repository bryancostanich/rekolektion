module Rekolektion.Viz.Core.Tests.RulerTicksTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Layout

// ─────────────────────────────────────────────────────────────────
// Step-ladder selection — the 1-2-5 sequence's behaviour at the
// boundaries between adjacent rungs is the part most likely to
// regress. Every test pins both the chosen step AND its pitch in
// pixels so a future refactor that drops the 1-2-5 sequence (or
// flips to a /2 minor step) surfaces in a readable failure.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``major step at 1 px/µm with min-60 px → 100 µm`` () =
    // 1 px/µm is "zoomed way out" — fitting 60 px between labels
    // means the next viable step on the 1-2-5 ladder is 100 µm.
    // (50 µm only buys 50 px; 100 µm buys 100 px — first ≥ 60.)
    RulerTicks.pickMajorStepUm 1.0 60.0 |> should equal 100.0

[<Fact>]
let ``major step at 10 px/µm with min-60 px → 10 µm`` () =
    // Each candidate's pixel pitch = pxPerUm * stepUm.
    // 5 µm × 10 = 50 px (under threshold); 10 µm × 10 = 100 px.
    RulerTicks.pickMajorStepUm 10.0 60.0 |> should equal 10.0

[<Fact>]
let ``major step at 30 px/µm with min-60 px → 2 µm`` () =
    // 1 µm × 30 = 30 px (under); 2 µm × 30 = 60 px (exactly at).
    // The picker uses >= so the boundary lands on 2 µm, not 5 µm.
    RulerTicks.pickMajorStepUm 30.0 60.0 |> should equal 2.0

[<Fact>]
let ``major step at 100 px/µm with min-60 px → 1 µm`` () =
    // Common case: at 100 px/µm a 1 µm major gives 100 px pitch.
    RulerTicks.pickMajorStepUm 100.0 60.0 |> should equal 1.0

[<Fact>]
let ``major step at 1000 px/µm with min-60 px → 0.1 µm`` () =
    // Deep zoom-in. 0.05 µm × 1000 = 50 px (under); 0.1 × 1000 = 100.
    RulerTicks.pickMajorStepUm 1000.0 60.0 |> should equal 0.1

[<Fact>]
let ``major step at 10000 px/µm with min-60 px → 0.01 µm`` () =
    // Ultra-deep zoom (sub-PDK-grid). 0.005 × 10000 = 50; 0.01 × 10000 = 100.
    RulerTicks.pickMajorStepUm 10000.0 60.0 |> should equal 0.01

[<Fact>]
let ``major step degrades gracefully on non-positive px-per-µm`` () =
    System.Double.IsNaN (RulerTicks.pickMajorStepUm 0.0 60.0)
    |> should be True
    System.Double.IsNaN (RulerTicks.pickMajorStepUm -1.0 60.0)
    |> should be True

// ─────────────────────────────────────────────────────────────────
// Minor step — fixed division by 5 regardless of the major's
// leading digit. Documents the contract; if a future change goes
// to a /2 or /10 split, the test forces an explicit decision.
// ─────────────────────────────────────────────────────────────────

[<Theory>]
[<InlineData(1.0,   0.2)>]
[<InlineData(2.0,   0.4)>]
[<InlineData(5.0,   1.0)>]
[<InlineData(0.1,   0.02)>]
[<InlineData(100.0, 20.0)>]
let ``minor step is major / 5`` (major: float) (expected: float) =
    let got = RulerTicks.pickMinorStepUm major
    abs (got - expected) |> should be (lessThan (expected * 1e-9))

// ─────────────────────────────────────────────────────────────────
// Tick generation — exercises the gutter walking + major/minor
// de-dup logic. Each test pins both the count and a sampling of
// positions so the de-dup contract (no minor lands on a major)
// surfaces independently from the count.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``ticks across 0..10 µm at 100 px/µm produce majors every 1 µm`` () =
    let ts = RulerTicks.ticks 0.0 10.0 100.0 60.0
    let majors = ts |> List.filter (fun t -> t.IsMajor)
    // µm = 0..9 inclusive (endUm = 10 is exclusive in the loop).
    majors |> List.length |> should equal 10
    majors.[0].Um |> should equal 0.0
    majors.[9].Um |> should equal 9.0
    // Px offset = µm value × 100 at this zoom.
    majors.[5].PxOffset |> should equal 500.0

[<Fact>]
let ``minor ticks de-dup against majors at coincident positions`` () =
    let ts = RulerTicks.ticks 0.0 5.0 100.0 60.0
    // At pxPerUm=100, major step is 1 µm and minor step is 0.2 µm.
    // The minor-step walk would emit 0.0, 0.2, 0.4 ... 4.8 — which
    // overlaps majors at 0, 1, 2, 3, 4. After de-dup, those 5
    // coincident positions live ONLY in the major set.
    let majorsAt = ts |> List.filter (fun t -> t.IsMajor) |> List.map (fun t -> t.Um) |> Set.ofList
    let minorsAt = ts |> List.filter (fun t -> not t.IsMajor) |> List.map (fun t -> t.Um) |> Set.ofList
    Set.intersect majorsAt minorsAt |> Set.isEmpty |> should be True
    // 5 majors (0..4) + 20 minors (0.2,0.4,0.6,0.8 in each 1-µm cell × 5 cells).
    ts |> List.length |> should equal 25

[<Fact>]
let ``ticks across negative range emit majors with correct sign`` () =
    // Real macros sit in negative-quadrant space too — a SRef at
    // (-100, -50) should produce labels like "-100" on the ruler.
    let ts = RulerTicks.ticks -5.0 0.5 100.0 60.0
    let majors = ts |> List.filter (fun t -> t.IsMajor) |> List.map (fun t -> t.Um)
    majors |> should contain -5.0
    majors |> should contain -1.0
    majors |> should contain 0.0
    // 0.5 is exclusive, so no positive majors expected.
    majors |> List.filter (fun um -> um > 0.0) |> List.isEmpty |> should be True

[<Fact>]
let ``ticks ordered ascending`` () =
    let ts = RulerTicks.ticks -3.0 3.0 100.0 60.0
    let positions = ts |> List.map (fun t -> t.Um)
    positions |> should equal (List.sort positions)

[<Fact>]
let ``degenerate ranges produce empty tick lists`` () =
    RulerTicks.ticks 5.0 5.0 100.0 60.0 |> should be Empty
    RulerTicks.ticks 5.0 1.0 100.0 60.0 |> should be Empty
    RulerTicks.ticks 0.0 10.0 0.0 60.0  |> should be Empty
    RulerTicks.ticks nan 10.0 100.0 60.0 |> should be Empty
    RulerTicks.ticks 0.0 nan 100.0 60.0 |> should be Empty

// ─────────────────────────────────────────────────────────────────
// Camera → gutter range — the canvas's centerX is in DBU, but
// the rulers think in µm. Tests pin the unit conversion against
// the two PDK regimes: rkt-native 5 nm/DBU vs gds-native 1 nm/DBU.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``gutter range covers half-span each way from center`` () =
    // 1 nm/DBU (gds-style), pxPerDbu = 1, span = 200 px.
    // Half-span DBU = 100; world µm at center = 0;
    // → range = (-0.1 µm, +0.1 µm).
    let s, e = RulerTicks.gutterRangeUm 0.0 1.0 200.0 1
    abs (s - -0.1) |> should be (lessThan 1e-9)
    abs (e -  0.1) |> should be (lessThan 1e-9)

[<Fact>]
let ``gutter range honours rkt 5 nm DBU when reading center DBU`` () =
    // 5 nm/DBU, centerDbu = 1000 → 5 µm.
    // pxPerDbu = 20 (so each px covers 0.05 DBU = 0.25 nm),
    // span = 400 px → half-span DBU = 10 → 50 nm = 0.05 µm.
    // Range = 5 µm ± 0.05 µm.
    let s, e = RulerTicks.gutterRangeUm 1000.0 20.0 400.0 5
    abs (s - 4.95) |> should be (lessThan 1e-9)
    abs (e - 5.05) |> should be (lessThan 1e-9)

[<Fact>]
let ``gutter range degrades gracefully on invalid inputs`` () =
    RulerTicks.gutterRangeUm 0.0 0.0 200.0 1
    |> should equal (0.0, 0.0)
    RulerTicks.gutterRangeUm 0.0 1.0 200.0 0
    |> should equal (0.0, 0.0)

// ─────────────────────────────────────────────────────────────────
// px-per-µm helper + label formatting — small but consequential
// for legibility at the boundary between µm-scale and nm-scale
// labels.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``pxPerUm converts via the dbuNm bridge`` () =
    // 5 nm/DBU, 1 px/DBU → 0.2 px/µm   (1 µm = 1000 nm = 200 DBU → 200 px? wait)
    // Re-derive: µm = nm * 1e-3; DBU = nm / dbuNm. So 1 µm = 1000 nm = 200 DBU.
    // At 1 px/DBU that's 200 px per µm — NOT 0.2.
    RulerTicks.pxPerUm 1.0 5 |> should equal 200.0
    // 1 nm/DBU, 1 px/DBU → 1000 px/µm (1 µm = 1000 DBU = 1000 px).
    RulerTicks.pxPerUm 1.0 1 |> should equal 1000.0
    // Degenerate units returns 0 (caller short-circuits).
    RulerTicks.pxPerUm 1.0 0 |> should equal 0.0

[<Fact>]
let ``formatLabel renders at the right precision for each step size`` () =
    // step ≥ 1 µm → integer
    RulerTicks.formatLabel 12.0 1.0 |> should equal "12"
    RulerTicks.formatLabel -7.0 5.0 |> should equal "-7"
    // step in [0.1, 1.0) → one decimal
    RulerTicks.formatLabel 0.5 0.2 |> should equal "0.5"
    RulerTicks.formatLabel -1.2 0.1 |> should equal "-1.2"
    // step in [0.01, 0.1) → two decimals
    RulerTicks.formatLabel 0.05 0.02 |> should equal "0.05"
    // step < 0.01 → three decimals
    RulerTicks.formatLabel 0.002 0.001 |> should equal "0.002"
    // zero collapses regardless of step
    RulerTicks.formatLabel 0.0 0.1 |> should equal "0"
    RulerTicks.formatLabel 0.0 1.0 |> should equal "0"

[<Fact>]
let ``formatLabel uses invariant culture (period decimal)`` () =
    // CI machines or users on a German locale shouldn't see "0,5"
    // on the ruler — invariant culture pins the decimal separator.
    let s = RulerTicks.formatLabel 0.5 0.2
    s |> should equal "0.5"
    s.Contains(',') |> should be False
