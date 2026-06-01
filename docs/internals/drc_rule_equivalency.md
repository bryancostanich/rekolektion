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

**Coverage as of 2026-06-01: 24 rules promotable on the KLayout
diagonal.** Width / Spacing / MinArea families on li1, met1, met2,
met3 plus mcon + via1 size rules plus poly / nwell width+spacing
plus nsdm / psdm width AND spacing (compat-aware implant-close
lets the KLayout side bypass the F# Magic grow-shrink).

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
| `via.1` | OK | FAIL | Min via1 width 0.15 µm (KLayout deck name `via.1a_a`). Per-cell gate is FAIL because ext-KLayout also fires the via1 enclosure family (`via.4a`/`via.5a`) which F# Klayout hasn't implemented yet. |
| `via.2` | OK | FAIL | Min via1 spacing 0.17 µm. Same per-cell caveat. |
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

**Problem 1 — edge-pair counting vs polygon counting.** KLayout's
deck output counts edge-pairs: a symmetric under-enclosure on a
square inner counts 4× (one per failing edge). F#'s `Enclosure`
check emits one violation per failing inner polygon. Probed on
`viol_via.4a_underenclosed`: F# reports `via.4a=1` per via,
ext-KLayout reports 4. Same logical violation, different counts.
The harness's exact-match gate would always read FAIL on
symmetric enclosure rules even though F#'s check is functionally
correct.

Two ways out:

- (a) Tighten F# Enclosure to emit one violation per failing
  edge, matching KLayout's edge-pair convention.  Engine
  internals change inside `Drc/Check.fs`.
- (b) Relax the harness to accept "both > 0" on edge-vs-polygon
  counting rules.  Soft signal, easier landing.

(a) is the right answer long-term — Phase 5 F#-primary needs
KLayout-equivalent counts, not just KLayout-equivalent rule
firing.  Pre-Phase-5 batch.

**Problem 2 — "must be inside" rule kind.** KLayout's `via.not(m1)`
fires on any via that isn't covered by m1, even when m1 doesn't
exist anywhere near.  F#'s `BoundaryCrossing` (the closest
existing kind) only fires when the destination layer is present
and the source partially crosses or is too near.  It doesn't fire
when the destination is absent entirely.

KLayout deck rules in this family:

- `ct.4` — mcon must be covered by li1
- `via.4a_a` — via1 must be enclosed by met1
- `m1.4` — mcon must be enclosed by met1
- `m2.4_a` — via1 must be enclosed by met2

These need a new `MustBeInside (name, source, destination)` rule
kind on the F# side — fires once per source polygon that isn't
fully contained inside any destination polygon.  Both in
`Drc/Rules.fs` (the type + nameOf entry) and `Drc/Check.fs` (the
matching pattern in `checkWithToggles`).  Pre-Phase-5 batch.

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
