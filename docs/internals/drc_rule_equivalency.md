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

**Coverage as of 2026-06-01: 12 rules promotable on the KLayout
diagonal.** Width / Spacing / MinArea on li1, met1, met2, met3.

| Rule | F# Klayout ≡ ext-KLayout | F# Magic ≡ ext-Magic | Notes |
|---|:---:|:---:|---|
| `li.1` | OK | FAIL | Width 0.17 µm. ext-Magic fires `li.c1` (core variant) alongside `li.1` on non-COREID geometry → 2 tiles vs F# Magic's 1. Magic-only delta. |
| `li.3` | OK | FAIL | Spacing 0.17 µm. Same Magic core-vs-peri delta. |
| `li.6` | OK | FAIL | Min area 0.0561 µm². Same Magic core-vs-peri delta. |
| `met1.1` | OK | OK | Width 0.14 µm. |
| `met1.2` | OK | FAIL | Spacing 0.14 µm. Magic-side spacing-tile delta — see below. |
| `met1.6` | OK | OK | Min area 0.083 µm². |
| `met2.1` | OK | OK | Width 0.14 µm. |
| `met2.2` | OK | FAIL | Spacing 0.14 µm. Same. |
| `met2.6` | OK | OK | Min area 0.0676 µm². |
| `met3.1` | OK | OK | Width 0.30 µm. |
| `met3.2` | OK | FAIL | Spacing 0.30 µm. Same. |
| `met3.6` | OK | OK | Min area 0.240 µm². |

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
