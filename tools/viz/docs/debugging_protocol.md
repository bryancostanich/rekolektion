# viz debugging protocol

## Headless probes over viz restarts — NON-NEGOTIABLE

When investigating viz behavior — DRC violations, ratlines, label
flooding, layout transforms, MVP / camera math — write a **headless
test** that loads the real `.rkt` file in-process and calls the Core
function directly.

**NEVER ask the user to restart viz for A/B comparison.** Viz
restart costs tab state and breaks flow. The Core test infrastructure
already supports the same code path viz uses, in-process, in
sub-30-second turnaround.

### The pattern

`tests/Rekolektion.Viz.Core.Tests/` is the home for these probes.
Use a `[<Fact>]` that loads the cell, calls the function, and either
asserts a count or `Assert.Fail`s with the count / details in the
message — the failure message surfaces in `dotnet test` output
without any custom logger.

Minimal pattern (DRC violation count on a real cell):

```fsharp
module Rekolektion.Viz.Core.Tests.MyProbe

open Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout

[<Fact>]
let ``PROBE my_cell DRC count`` () =
    let path = "/absolute/path/to/some_cell.rkt"
    if not (System.IO.File.Exists path) then () else
    let lib, _ = LayoutLoader.loadAsLibrary path
    let doc = Rkt.OfGds.fromLibrary lib
    let flat = Flatten.flatten doc
    let units = Snap.unitsOfLibrary lib
    let view = Drc.Rules.defaultView
    let vios = Drc.Check.check view units flat
    let byRule =
        vios
        |> Array.groupBy (fun v -> v.Rule)
        |> Array.map (fun (r, vs) -> sprintf "  %s %d" r vs.Length)
        |> String.concat "\n"
    Assert.Fail(sprintf "total = %d\n%s" vios.Length byRule)
```

To dump bboxes / measurements for a specific rule, filter `vios` and
`sprintf` each violation's `BboxA` + `MeasuredDbu` + `LimitDbu` into
the failure message. Three rounds of probe / patch / rerun
identifies false-positive geometry patterns faster than any live-viz
investigation.

### Toggling production code paths

To A/B a code path, comment out the body with `(* ... *)` and rerun
the probe. F# block comments survive indentation rules cleanly when
wrapped around full statement blocks. Restore + rerun = full
comparison cycle.

### When viz IS the right tool

- Visual review of placement / routing geometry by a human
- 3D model inspection
- Interactive ratline / DRC overlay rendering
- Manual hit-test calibration

For everything else — counts, deltas, bbox lookups, rule-firing
diagnosis — use a headless probe.

### Why this rule exists

Walked 2026-05-29. The new same-component DRC step-detection pass
was over-firing on every cell. First instinct was to ask the user to
restart viz to A/B the violation count with the new code
disabled. User flagged hard — "you should be doing these tests
headless, not requiring my input." Wrote a headless probe in 4
minutes that loaded `d11_stage2.rkt` directly, ran `Drc.Check.check`
twice (enabled vs commented-out), confirmed 45 vs 0 violations, then
dumped the first 12 bboxes to identify the fat-pad-plus-bar pattern
as the false-positive source. End to end: ~10 minutes, zero viz
restarts.
