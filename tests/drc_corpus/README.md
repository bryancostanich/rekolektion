# DRC equivalency corpus

One `.rkt` per micro-test, each violating exactly one named rule in
a known way. Used by the Phase 4 equivalency harness
(`tests/test_drc_corpus_harness.py`) to compute a 2×2 matrix per
cell:

|              | external KLayout | external Magic |
|--------------|------------------|----------------|
| **F# Klayout** | must match (gate) | informational |
| **F# Magic**   | informational     | must match (gate) |

Diagonal cells are the gates that promote a rule to F#-primary
in Phase 5. Off-diagonal cells are informational deltas — these
ARE the differences between Magic and KLayout interpretations
that motivate keeping both compat targets.

## Naming convention

`viol_<rule>_<variant>.rkt` — one rule per cell, named after the
Magic-equivalent rule ID for cross-engine consistency.

`legal_<topic>.rkt` — known-clean cells that BOTH engines must
pass. Catches false-positive bugs in either engine's F#
implementation.

## Authoring

Each cell is hand-authored with the minimum geometry needed to
trip exactly one rule. Avoid foundry primitives — they carry
waivers + COREID that confuse the equivalency story. Pure
parent-paint rectangles on the relevant layer.

| Rule kind | Layer | Pattern |
|-----------|-------|---------|
| Width | met1 (68/20) | one rect with `width < min` (e.g. 100 × 3000 nm vs 140 min) |
| Spacing | met1 (68/20) | two rects with `gap < min` (e.g. 100 nm gap vs 140 min) |
| MinArea | met1 (68/20) | one tiny square with `area < min` (e.g. 200 × 200 = 0.04 µm² vs 0.083 min) |
| Enclosure | inner+outer pair | outer rect that doesn't cover inner by the required margin |

See `viol_met1.1_subwidth.rkt` for the canonical Width pattern;
copy and adapt.

## Status

The per-rule equivalency table lives at
[`docs/internals/drc_rule_equivalency.md`](../../docs/internals/drc_rule_equivalency.md).

This corpus drives that table. Adding a new cell here = adding a
row there (and vice-versa).
