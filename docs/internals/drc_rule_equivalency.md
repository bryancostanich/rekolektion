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

**Coverage as of 2026-06-01: 3 rules promotable on the KLayout
diagonal (met1.1, met1.2, met1.6).** All three Magic-side and
KLayout-side rules now have matching F# implementations.

| Rule | F# Klayout ≡ ext-KLayout | F# Magic ≡ ext-Magic | Notes |
|---|:---:|:---:|---|
| `met1.1` | OK | OK | Width 0.14 µm. Proves green on `viol_met1.1_subwidth` (1 tile each side, all four paths). |
| `met1.2` | OK | FAIL | Spacing 0.14 µm. F# Klayout matches ext-KLayout (1 tile). **F# Magic vs ext-Magic delta:** Magic external reports 2 tiles on `viol_met1.2_subspacing` where F# Magic / ext-KLayout / F# Klayout all report 1. Pre-existing F#-vs-Magic edge-detection difference, not blocking KLayout-side promotion. |
| `met1.6` | OK | OK | Min area 0.083 µm². Proves green on `viol_met1.6_minarea`. |

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
