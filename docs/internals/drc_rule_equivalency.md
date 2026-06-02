# DRC rule equivalency table

*Initialized 2026-06-01 — silicon_correct Track 02 Phase 4.*

Per-rule status of the F# in-viz DRC checker against the two
external compat targets. The diagonal gates are what unlock
F#-primary promotion in Phase 5: once both columns of a row are
green, that rule can be served directly from F# without falling
back to the external binary.

## How this table works

The equivalency harness
(`rekolektion.verify.drc_equivalency.run_corpus`) walks every cell
in [`tests/drc_corpus/`](../../tests/drc_corpus/) and runs four
checks per cell:

| | external KLayout | external Magic |
|---|---|---|
| **F# Klayout** | diagonal GATE | informational |
| **F# Magic**   | informational | diagonal GATE |

A rule lands a green row in this table when its per-rule histogram
matches across the corresponding diagonal cell for every corpus
cell that fires it. Off-diagonal mismatches surface real engine
differences (Magic vs KLayout interpretation deltas) and feed the
informational column.

Run the harness yourself:

```bash
rekolektion verify-drc-equivalency tests/drc_corpus
```

Or via the API:

```python
from rekolektion.verify.drc_equivalency import run_corpus, render_report
print(render_report(run_corpus("tests/drc_corpus")))
```

## Status

**Coverage as of 2026-06-02: 31 rules promotable on the KLayout
diagonal. Every rule the corpus surfaces is now green, including
the foundry-primitive probe cell.**

Includes Width / Spacing / MinArea families on li1, met1, met2,
met3; mcon + via1 size rules; poly / nwell width+spacing; nsdm /
psdm width AND spacing; three polygon-style containment rules
(`ct.4`, `m1.4`, `m2.4_a`); edge-counted symmetric Enclosures
(`via.4a`, `m2.4`); asymmetric Enclosures (`via.5a`, `m2.5`,
`m1.5`); and the size-filtered edge-style `via.4a_a` via the
new `MustBeInsideEdgewise` rule kind.

| Rule | F# Klayout ≡ ext-KLayout | F# Magic ≡ ext-Magic | Notes |
|---|:---:|:---:|---|
| `li.1` | OK | FAIL | Width 0.17 µm. ext-Magic core/peri delta. |
| `li.3` | OK | FAIL | Spacing 0.17 µm. Same. |
| `li.6` | OK | FAIL | Min area 0.0561 µm². Same. |
| `mcon.1` | OK | FAIL | Min mcon width 0.17 µm (KLayout deck name `ct.1_a`). Per-cell gate is FAIL because ext-KLayout also fires `ct.4` (mcon must be covered by li) which F# Klayout hasn't implemented. Per-rule gate is OK — the size check itself matches. |
| `mcon.2` | OK | FAIL | Min mcon spacing 0.19 µm (KLayout `ct.2`). Same per-cell caveat as mcon.1. |
| `met1.1` | OK | OK | Width 0.14 µm. |
| `met1.2` | OK | FAIL | Spacing 0.14 µm. Magic spacing-tile delta. |
| `met1.6` | OK | OK | Min area 0.083 µm². |
| `met2.1` | OK | OK | Width 0.14 µm. |
| `met2.2` | OK | FAIL | Spacing 0.14 µm. Same. |
| `met2.6` | OK | OK | Min area 0.0676 µm². |
| `met3.1` | OK | OK | Width 0.30 µm. |
| `met3.2` | OK | FAIL | Spacing 0.30 µm. Same. |
| `met3.6` | OK | OK | Min area 0.240 µm². |
| `via.1` | OK | FAIL | Min via1 width 0.15 µm (KLayout deck name `via.1a_a`). |
| `via.2` | OK | FAIL | Min via1 spacing 0.17 µm. |
| `ct.4` | OK | FAIL | mcon must be covered by li1. New `MustBeInside` rule kind; polygon-style emit (1 violation per uncovered mcon). |
| `m1.4` | OK | FAIL | mcon must be enclosed by m1. Same kind. |
| `m2.4_a` | OK | FAIL | via1 must be enclosed by m2 (in periphery). Same kind. |
| `via.4a` | OK | FAIL | Symmetric m1 enclosure of via1 ≥ 0.055 µm. Edge-counted under Compat.Klayout — 4 violations per under-enclosed inner. Skipped when no m1 exists near the via (`via.4a_a` is the deck's rule for that case). |
| `m2.4` | OK | FAIL | Symmetric m2 enclosure of via1 ≥ 0.055 µm. Same edge-counting. |
| `via.5a` | OK | FAIL | Asymmetric m1 enclosure of via1 (0.085 / 0.055). Polygon-style under both compats — KLayout deck emits via the `via.interacting(...)` Region output, F# AsymEnclosure already emits one per failing inner. Nearby-outer guard added under Compat.Klayout to suppress the "no outer at all" case (caught by `via.4a_a` instead). |
| `m2.5` | OK | FAIL | Asymmetric m2 enclosure of via1 (0.085 / 0.055). Same. |
| `m1.5` | OK | FAIL | Asymmetric m1 enclosure of mcon (0.06 / 0.03). Same. |
| `via.4a_a` | OK | FAIL | 0.15 µm via1 squares must be enclosed by m1. New `MustBeInsideEdgewise (source, destination, sizeUm)` rule kind — only fires on sources whose bbox is a square of exact `sizeUm × sizeUm`; emits 4 edge violations per matching uncovered square. Matches KLayout deck's `squares.drc(width == 0.15).not(m1).output(...)` exactly. |
| `poly.1a` | OK | OK | Min poly width 0.15 µm. |
| `poly.2` | OK | OK | Min poly spacing 0.21 µm. Spacing delta seen on metal layers does NOT recur here. |
| `nwell.1` | OK | OK | Min nwell width 0.84 µm. |
| `nwell.2a` | OK | OK | Min nwell spacing 1.27 µm — the canonical abut-or-tub rule (workflow Hard Rule #7). |
| `nsdm.1` | OK | FAIL | KLayout deck = SPACING 0.38 µm. `applyImplantClose` is now compat-aware — bypassed under `Compat.Klayout` so F# Klayout matches the deck's literal-gap semantics. Magic-side still fails because F# Magic's labels are swapped vs the deck (pre-existing). |
| `nsdm.2` | OK | FAIL | KLayout deck = WIDTH 0.38 µm. Same label-swap. |
| `psdm.1` | OK | FAIL | Same as `nsdm.1`. |
| `psdm.2` | OK | FAIL | Same label-swap as `nsdm.2`. |

### Backlog: enclosure family

Surfaced by the mcon, via1, and via.4a_underenclosed corpus cells.
Splits into two structural problems that have to land before any
of these rules can flip green on the KLayout diagonal:

**Problem 1 — edge-pair counting vs polygon counting (landed
for symmetric Enclosure).** The `Enclosure` handler in
`Drc/Check.fs` now branches on compat:

- `Compat.Magic` — emit one Violation per region slab/interval
  (the existing Magic-fidelity morphology output).
- `Compat.Klayout` — for each inner polygon whose interior is
  not fully inside the outer-shrunk-by-N core AND whose bbox
  overlaps at least one outer (so the rule fires only when
  outer is present, matching KLayout's
  `outer.edges.enclosing(inner, N)` semantics): emit 4
  violations (one per inner bbox edge). The post-pass
  clustering skips these via `nonClusterableRules`.

Result: `via.4a` and `m2.4` (symmetric 0.055 µm enclosures)
green on the KLayout diagonal.

`AsymEnclosure` was simpler than expected — KLayout's deck for
these specific rules (`via.5a`, `m2.5`, `m1.5`) emits via
`via.interacting(error_corners...)` which is polygon-style (one
per failing inner). F# `AsymEnclosure` already emits polygon-
style, so no edge-count branch was needed. Only the
nearby-outer guard (suppress "no outer at all" → MustBeInside
handles that case) was added under `Compat.Klayout`. Three new
green rules: `via.5a`, `m2.5`, `m1.5`.

**Problem 2 — "must be inside" rule kind (landed).** New
`MustBeInside (name, source, destination)` rule kind in
`Drc/Rules.fs` + matching handler in `Drc/Check.fs`. Polygon-
style emission (1 violation per uncovered source) matches
KLayout deck rules whose output flows directly through
`.not().output()`:

- `ct.4` — mcon must be covered by li1 — OK
- `m1.4` — mcon must be enclosed by met1 — OK
- `m2.4_a` — via1 must be enclosed by met2 — OK

Still FAIL: `via.4a_a`. Its deck source is
`rectVIA.squares.drc(width == 0.15).not(m1).output(...)` — both
size-filtered AND edge-style emit. Needs either a new
`MustBeInsideEdgewise` rule kind OR a size filter on the existing
`MustBeInside`. Deferred to its own batch.

### Backlog: enclosure rules (queued for after the structural fixes)

Once Problem 1 + Problem 2 are resolved, these rules can land in
`Rules.Klayout.allRules` and flip their per-rule rows green:

- `via.4a` — met1 enclosure of via1, 0.055 µm (symmetric)
- `via.5a` — met1 enclosure of via1, 0.085 µm (asymmetric / 2 adj)
- `via.4a_a` — via1 must be enclosed by m1
- `m2.4` — met2 enclosure of via1, 0.055 µm (symmetric)
- `m2.5` — met2 enclosure of via1, 0.085 µm (asymmetric)
- `m2.4_a` — via1 must be enclosed by m2
- `ct.4` — mcon must be covered by li1
- `m1.4` — mcon must be enclosed by m1
- `791_m1.4` — met1 enclosure of mcon, 0.03 µm
- `m1.5` — met1 enclosure of mcon, 0.06 µm (asymmetric)

The corpus cell `viol_via.4a_underenclosed.rkt` is ready to drive
the per-rule promotion once the engine work lands.

### Backlog: difftap family

KLayout's diff/tap rules are area-gated by `areaid:ce` (sram core
marker). Periphery / core / across-core each get their own rule
name (`difftap.1`, `difftap.1_a`, `difftap.1_b`, `difftap.1_c`,
`difftap.2`). F# Magic collapses these into one rule per layer.
Promoting needs either (a) a corpus cell per area variant, or
(b) bucketing several KLayout deck names to one Magic name via
the normalizer. Lower priority than the enclosure family.

### Compat-aware latch-up (LU.2 / LU.3) — landed 2026-06-02

`Drc/Check.fs` hard-codes the `LU.2` (n-diff distance to p-tap)
and `LU.3` (p-diff per merged nwell needs an n-tap) Magic
deck rules outside the rule-list dispatch loop.  Under
Compat.Klayout, both emits are now gated: no LU.* violations
fire.  Matches the SKY130 KLayout deck (`sky130B_mr.drc`) which
has no latch-up family at all.

Was: F# Klayout over-fired LU.2 on any cell whose primitives
don't include their own p-tap (foundry pre-validates latch-up
at integration time, not the primitive level), creating spurious
"errors" the moment a real cell with foundry SRefs got
verify_drc'd.  Surfaced by `tests/drc_corpus/probe_foundry_waiver.rkt`
(single foundry-primitive SRef).

Now: F# Klayout reports clean on the probe cell, matching
ext-KLayout. F# Magic still fires LU.2 under `full=True`,
matching ext-Magic full. Under the default `full=False`, F#
Magic suppresses LU.2 (Track B.4), matching ext-Magic fast.

### F# Magic parity follow-up (Track B, landed 2026-06-02)

Three Track 02 follow-up items closed out the Magic-side
delta backlog that was deferred at original-track close:

- **B.2 — nsdm/psdm Width↔Spacing label swap** (commit
  `7a891b2`).  The SKY130 deck (both Magic .tech and KLayout
  .drc) names `nsdm.1` as SPACING and `nsdm.2` as WIDTH; F#
  Magic had these reversed.  Same for psdm.  Corrected to match
  the deck.

- **B.3 — `li.c1` added** (commit `7a891b2`).  Magic external
  fires both `li.1` (peri, 0.17 µm) AND `li.c1` (COREID-core
  variant, 0.14 µm relaxation) on geometry outside any COREID
  marker.  F# Magic was missing `li.c1` entirely.  Added
  `Width("li.c1", li1, 0.14)` to the Magic-side ruleset.

- **B.4 — `full` flag threaded through F# engine** (commit
  `0b3b941`).  `Drc.Check.{check, checkWithToggles, runLive,
  runLiveWithIndex, runLiveWithIndexTimed}` all take
  `full: bool` as a required parameter after `compat`.  The
  hard-coded LU.2 / LU.3 emits in `checkWithToggles` are now
  gated on `compat = Compat.Magic && full` — matching
  ext-Magic's `drc style drc(full)` vs default fast-style
  distinction.  New `--full` flag on the viz CLI's `drc` verb;
  `run_drc_fsharp` forwards `full` to the CLI.  Canvas passes
  `full=true` (preserves pre-Track-02 in-viz behavior of
  showing every Magic-side check).

Harness movement after Track B: Magic gate climbs from 13/27
to 15/27 cells.  KLayout gate steady at 27/27.

### Track B.1 — F# emits-per-polygon vs Magic emits-per-tile (informational, deferred indefinitely)

The remaining 12/27 Magic-side cell-gate FAILs all trace to a
single fundamental semantic difference between F#'s polygon-
oriented DRC engine and Magic's tile-oriented DRC engine.  Not a
missing rule; not an incorrect rule.  A count convention.

#### What it looks like

| Cell | F# Magic | ext-Magic | Delta |
|---|---:|---:|---|
| `viol_met1.2_subspacing` | `met1.2: 1` | `met1.2: 2` | 2× per gap region |
| `viol_met2.2_subspacing` | `met2.2: 1` | `met2.2: 2` | same |
| `viol_met3.2_subspacing` | `met3.2: 1` | `met3.2: 2` | same |
| `viol_li.3_subspacing` | `li.3: 1` | `li.3: 2` | same |
| `viol_li.6_minarea` | `li.6: 1` | `li.6: 2` | 2× per single rect |

ext-KLayout reports `1` everywhere in this table — KLayout's
deck output matches F#'s polygon-count convention, not Magic's
tile-count convention.  So this delta is **strictly an F# Magic
vs ext-Magic** issue; KLayout-side diagonals are unaffected.

#### Root cause

Magic decomposes every polygon into a tile list (trapezoid tile
representation) for the DRC tile counter.  Each Magic-internal
tile that contributes to a violation region is reported as one
"tile" in `drc count` output.  For a parallel-rect spacing gap,
the gap region is bordered on two sides by tiles from the two
rects — each side contributes one tile, hence the 2× count on
symmetric square geometry.  For a single rectangle violating
min-area, the rectangle decomposes into a single tile but Magic
still emits two reports under some `drc(full)` modes (the
second appears to be a per-edge report at the perimeter).

F#'s `Spacing` handler emits one Violation per gap region
between two polygons (or one per facing-edge in the
edge-counting Klayout branch).  The `MinArea` handler emits one
per failing polygon.  Both honor the rule semantically but the
count convention is different.

ext-KLayout's deck uses `space.output(...)` and `with_area
.output(...)` patterns that ALSO emit one record per logical
violation — same convention as F#.

So:

- F# polygon convention ≡ ext-KLayout deck convention
- Magic external tile convention is unique to Magic

#### Why we're leaving it where it is

Fixing it cleanly would mean **emulating Magic's tile
decomposition** in F#'s `Spacing` and `MinArea` handlers under
`Compat.Magic` only.  This is ~150–250 lines of carefully-tuned
Region geometry work (the trapezoid tile decomposition isn't
trivially expressed in our existing Region API; would likely
need a new module under `Drc/Geometry/Tiles.fs`).  And the
return is narrow:

- KLayout-side users see no benefit (they're already
  bit-identical to ext-KLayout on the same rule).
- Magic-side users see PASS/FAIL outcomes unchanged — only the
  count goes from 1 to 2.  Layouts that were Magic-clean stay
  clean; layouts that fired N rules still fire N rules.  Counts
  appear in error messages and the canvas marker layer; both
  scale 1:N with the underlying issue regardless of convention.
- The corpus harness's `magic_gate` (which requires exact-count
  match) reads FAIL on these cells.  But that's a strict
  equivalency test; the cells in question fire the right rules,
  just with the F# count rather than the Magic count.

#### What this means for the default-flip

Phase 5 Fork #4 chose `verify_drc(compat="magic")` to default to
`external=True` until F# Magic gets parity.  Track B closed
B.2/B.3/B.4 but B.1 is the remaining gap.  Recommend keeping
the compat-conditional default as it is: ext-Magic is the
default Magic path, F# Magic is opt-in via `external=False`
(with the documented understanding that counts may differ by
1:N tiles per logical violation).

If a future caller specifically needs bit-identical-to-Magic
counts (e.g. a regression suite comparing against pre-Track-02
F# Magic output), they should pass `external=True` to get
ext-Magic semantics directly.

#### Reopening criteria

Land B.1 if and when:

- A specific workflow needs F# Magic bit-identical to ext-Magic
  on per-rule tile counts.  None today.
- Or someone independently wants to replace the polygon-based
  Region engine with a tile-based one for performance reasons.
  Unlikely; the polygon engine is fine for in-viz interactive
  use.

Until either condition, Track B.1 stays deferred.  No further
action needed.

### Compat-aware implant-close (landed)

`Drc/Check.fs` ships two engine entry points now:

- `check view units flat` — implicitly Magic-compat. Calls
  `applyImplantClose Compat.Magic flat`, preserving the
  pre-Track-02 behavior so the App canvas / routing / model /
  scripts callers continue working unchanged.
- `checkWithCompat compat view units flat` — Phase 4 / Phase 5
  entry point. Threads compat through `applyImplantClose`, which
  bypasses the grow-shrink under `Compat.Klayout` so F# Klayout
  matches the deck's literal-gap semantics for nsdm/psdm spacing.

The viz CLI's `drc` verb uses `checkWithCompat` so the
equivalency harness gets the right behavior under either flag.

**F# Magic spacing delta (informational).** On every Width / Spacing
metal-layer corpus cell that tests two parallel rects with a
sub-min gap, ext-Magic reports 2 tiles while F# Magic / ext-KLayout
/ F# Klayout all report 1. Magic's tiler may be reporting each side
of the violating gap as a separate tile (one per parallel edge),
where the other three engines collapse the violation to one
edge-pair. Pre-existing F# Magic implementation behavior; doesn't
block KLayout-side promotion since the KLayout diagonal is the
load-bearing one for Phase 5.

(More rules land as the corpus grows.)

## What "OK" means

A diagonal cell is OK when, for every corpus cell, the F# checker
and the external tool agree on **both** the per-rule tile total
AND the total violation count. Per-rule comparison uses the
normalized rule name (KLayout `m1.1` → Magic `met1.1`) so the two
engines' naming conventions don't trip the comparison.

A diagonal cell is FAIL when the F# side fires a different count
than the external side on at least one corpus cell that exercises
the rule. The harness output (`render_report`) prints the per-cell
deltas so root-causing is targeted.

## What "informational" means

Off-diagonal cells (F# Klayout vs ext Magic, F# Magic vs ext
Klayout) are NOT gates. They surface deltas between the two
compat targets — exactly the reason both Magic and KLayout exist
as supported compat targets in the first place. Logged in the
per-cell matrix for traceability; not promoted to per-rule
status.

## Promoting a rule to F#-primary (Phase 5)

A rule graduates to F#-primary when **both** of its diagonal
gates are OK in this table AND a corpus cell exists that triggers
it (otherwise it's untested). Phase 5's `external=False` default
will route those rules through the F# checker; rules still showing
FAIL fall back to the external binary on a per-rule basis until
they land here green.

## Adding a rule

1. Author a `viol_<rule>_<variant>.rkt` (and optionally a
   `legal_<topic>.rkt`) under
   [`tests/drc_corpus/`](../../tests/drc_corpus/). See the
   [corpus README](../../tests/drc_corpus/README.md) for the
   conventions.
2. Implement the rule on whichever side(s) are RED — usually
   `Rules.Klayout.allRules` in
   [`Drc/Rules.fs`](../../tools/viz/src/Rekolektion.Viz.Core/Drc/Rules.fs).
   Magic side is typically already populated; only re-touch it if
   the diagonal is RED for a Magic-only reason.
3. Re-run the harness and confirm the row flips.  Update this
   table.
4. Commit corpus cell + rule impl + table update together so a
   later reader can trace the green checkmark back to the proof.

## Related

- Track plan:
  [`silicon_correct/tracks/02_drc_klayout_primary/plan.md`](../../../khalkulo/conductor/projects/silicon_correct/tracks/02_drc_klayout_primary/plan.md)
- F# rule list:
  [`Drc/Rules.fs`](../../tools/viz/src/Rekolektion.Viz.Core/Drc/Rules.fs)
  (look for `module Magic` and `module Klayout`)
- Harness module:
  [`verify/drc_equivalency.py`](../../src/rekolektion/verify/drc_equivalency.py)
- Corpus:
  [`tests/drc_corpus/`](../../tests/drc_corpus/)
