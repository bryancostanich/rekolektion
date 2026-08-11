module Rekolektion.Viz.Core.Tests.DrcImplantCloseTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Drc

// Track 02 follow-up #2 — the canvas's DRC overlay used to call
// `Check.checkWithToggles` directly without first running
// `Implant.applyImplantClose`.  Under Magic-compat that produced
// spurious `nsdm.1` / `psdm.1` spacing fires on abutting primitive
// rects whose ≤380 nm gaps the close would have bridged.
//
// Fix moved the implant-close pass inside `checkWithToggles` so
// every entry point (CLI, canvas, live DRC) gets it.  Klayout-
// compat short-circuits the close (the deck checks the literal
// gap), so the cost is zero under the default.

let private rect (x1, y1, x2, y2) (layer, dt) : FlatPolygon =
    { Layer = layer
      DataType = dt
      Points = [|
        { X = int64 x1; Y = int64 y1 }
        { X = int64 x2; Y = int64 y1 }
        { X = int64 x2; Y = int64 y2 }
        { X = int64 x1; Y = int64 y2 }
        { X = int64 x1; Y = int64 y1 }
      |]
      SourceStructure = "test"
      SourceIndex = 0
      TopInstanceIndex = None
      Net = None }

let private units1nm : Units = { DbuNm = 1; UuUm = 1 }

/// Two nsdm rects 300 nm apart on the X axis — well under the
/// 380 nm bridge threshold.  Under Magic-compat the implant-close
/// merges them; under Klayout-compat the rules fire on the literal
/// gap.  Sized at 500 nm square so the nsdm width rule (380 nm
/// min) is satisfied independently.
let private nsdmIslands () : FlatPolygon array = [|
    rect (0L,    0L,  500L,  500L) (93, 44)
    rect (800L,  0L, 1300L,  500L) (93, 44)   // 300 nm gap
|]

// ─────────────────────────────────────────────────────────────────
// checkWithToggles applies implant-close internally
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``Magic-compat: checkWithToggles bridges sub-380 nm nsdm gap (no spacing fire)`` () =
    let flat = nsdmIslands ()
    let violations =
        Check.checkWithToggles
            Compat.Magic true Rules.Magic.defaultView
            units1nm flat Set.empty
    // After close, the two islands merge into one region — no
    // spacing violation.  Pre-fix this fact failed because
    // checkWithToggles skipped the close and `nsdm.2` (the F#
    // Magic-side spacing rule on nsdm) fired on the 300 nm gap.
    violations
    |> Array.exists (fun v -> v.Rule = "nsdm.2")
    |> should equal false

[<Fact>]
let ``Klayout-compat: checkWithToggles does NOT bridge — gap fires nsdm.1`` () =
    // KLayout deck spec: `nsdm.1` spacing rule fires on the literal
    // gap, no close.  This test pins the asymmetry — the same flat
    // produces different output under the two compats, by design.
    let flat = nsdmIslands ()
    let violations =
        Check.checkWithToggles
            Compat.Klayout true Rules.Klayout.defaultView
            units1nm flat Set.empty
    violations
    |> Array.exists (fun v -> v.Rule = "nsdm.1")
    |> should equal true

// ─────────────────────────────────────────────────────────────────
// Equivalence: checkWithToggles ≡ Check.check on the same inputs
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``checkWithToggles matches Check.check under Magic (both apply close)`` () =
    // Pre-fix the two paths disagreed by exactly the number of
    // nsdm.* / psdm.* fires the close suppresses — `Check.check`
    // ran the close, `checkWithToggles` didn't.  Now they agree.
    let flat = nsdmIslands ()
    let checkPath =
        Check.check Compat.Magic true Rules.Magic.defaultView units1nm flat
    let togglesPath =
        Check.checkWithToggles
            Compat.Magic true Rules.Magic.defaultView
            units1nm flat Set.empty
    checkPath
    |> Array.map (fun v -> v.Rule)
    |> Array.sort
    |> should equal (togglesPath |> Array.map (fun v -> v.Rule) |> Array.sort)

[<Fact>]
let ``checkWithToggles matches Check.check under Klayout (both skip close)`` () =
    // Sanity check that the parity also holds in the no-op branch.
    let flat = nsdmIslands ()
    let checkPath =
        Check.check Compat.Klayout true Rules.Klayout.defaultView units1nm flat
    let togglesPath =
        Check.checkWithToggles
            Compat.Klayout true Rules.Klayout.defaultView
            units1nm flat Set.empty
    checkPath
    |> Array.map (fun v -> v.Rule)
    |> Array.sort
    |> should equal (togglesPath |> Array.map (fun v -> v.Rule) |> Array.sort)
