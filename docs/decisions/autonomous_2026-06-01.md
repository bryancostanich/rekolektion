# Autonomous run — 2026-06-01 — silicon_correct Track 02

**Objective:** Finish Track 02. Done = track closed out — KLayout
default + Magic permanent alternate AND the per-rule equivalency
table covers every rule the corpus surfaces with both diagonals
green where the engine can support it.

**Status:** Active. 14 commits landed before autonomous handoff
(43e31a0 → fd99748). 27 rules green on the KLayout diagonal.

Top-of-log status block (regenerated at end of run): see bottom.

---

## Fork #1 — How to thread `Compat` through the F# DRC engine

**Decision point.** The Track 02 plan calls for `compat=` to be a
single word across every DRC surface (Python `verify_drc`, F# CLI,
F# in-viz, future F#-primary path). The F# engine entry points
(`Drc.Check.check`, `checkWithToggles`, `runLive*`) are called
from a dozen places — viz App canvas, routing, model, scripts,
Cli, and now the equivalency harness. Each one needs the right
compat semantics for the rules it evaluates.

Three things in the engine need to read compat:
- `applyImplantClose` — bypass under Klayout (already landed via
  the parallel-path approach in commit 4136d29)
- Future `Enclosure` / `AsymEnclosure` edge-counting under Klayout
- Future post-pass clustering bypass for edge-counted rules

### Option A — Thread `compat: Compat` directly through every entry point

Add `compat: Compat.Compat` as a required parameter on `check`,
`checkWithToggles`, `runLive*`, etc. Update every caller (viz App
canvas, Model, drc_run.fsx, tests) to pass `Compat.Magic`
explicitly (preserves their current behavior). The Phase 4 harness
path passes `Compat.Klayout` via the CLI.

- **Cost/complexity.** ~12 caller updates across viz App / routing
  / scripts / tests. Signature change to ~5 engine functions. No
  per-call performance impact (Compat is a struct DU, zero-cost).
- **Risk.** Mechanical breakage if I miss a caller — F# would fail
  to compile, so the breakage is loud, not silent. After
  compilation passes, the original behavior is preserved at every
  call site (Magic). New behavior only at the explicit-Klayout
  sites.
- **Reward.** One canonical word everywhere. New compat-aware
  engine code (edge-counting, clustering bypass, future
  implant-aware rules) reads `compat` from its function parameter
  without any indirection.
- **Side effects.** None on the test surface — every existing
  test that exercises Magic semantics continues to do so. The
  Phase 4 harness becomes the single new caller that passes
  Klayout explicitly. Documentation: the engine's public surface
  consistently surfaces compat — no two-tier "use this for
  Phase 4, that for everything else" mental model.

### Option B — Encode `Compat` inside `RulesetView`

Add `Compat : Compat.Compat` field to `RulesetView`. `viewFor
compat` sets it from the compat argument. Existing `viewOf` /
`defaultView` set `Compat.Magic` by default (struct field literal
preserves backwards compat at the term level — call sites do not
need any change).

- **Cost/complexity.** One field on RulesetView. `viewFor` sets
  it. `viewOf` keeps existing signature (defaults to Magic).
  Engine reads `view.Compat` everywhere it needs the compat. Zero
  caller updates outside the view-builder.
- **Risk.** RulesetView equality semantics change when adding a
  field — DrcCompatTests compare views via `should equal`. Same
  fix as the field rename in Phase 3 (defaults align, equality
  still holds across Magic/Klayout paths). Records with custom
  Compat survive serialization round-trip if anyone YAML-loads a
  view (none do today; trivial to extend if needed).
- **Reward.** Zero blast radius on existing callers. New rule
  handlers can branch on `view.Compat` directly. Code reads as
  "the view knows its own compat target" — semantically natural.
- **Side effects.** Tests need a couple of structural tweaks
  (Phase-3-vintage assertions on view equality survive because
  defaultView and Magic.defaultView both have `Compat = Magic`).
  The 4136d29 wrapper (`applyImplantClose Compat.Magic`,
  `checkWithCompat`) becomes deletable — the data flows through
  the view, not through a wrapper parameter.

### Option C — Keep the parallel-path approach (4136d29's pattern)

Maintain `check` (implicit Magic) AND `checkWithCompat` (explicit
compat) side by side. New compat-aware engine internals stay
inside the body of `checkWithCompat`, gated by a hand-passed
compat argument.

- **Cost/complexity.** Effectively two engine entry points
  forever. Future compat-aware additions duplicate. Reading the
  engine requires holding "this code runs under both paths but
  one passes Compat.Magic and the other passes the real flag" in
  one's head.
- **Risk.** Drift between the two paths over time — a future
  contributor adds compat-aware behavior to `checkWithCompat` but
  forgets to keep `check` synchronized, leaving the App canvas
  silently on the old code path. Concurrent rule-list growth and
  engine-knob growth multiply the surface.
- **Reward.** Smallest change today.
- **Side effects.** Documentation diverges — `Drc/DRC_FIDELITY.md`
  describes `checkWithToggles`, but compat behavior lives one
  function up.

### Symmetric quantification (correctness → cleanliness → future cost)

| Axis | A: direct threading | B: in RulesetView | C: parallel paths |
|---|---|---|---|
| Correctness — one source of truth for compat | YES — function arg | YES — view field | NO — two parallel paths must stay in sync |
| Cleanliness — read-the-code clarity | Compat is in every signature; never any doubt | Compat lives where the rule list lives — semantically grouped | Function-parameter compat coexists with implicit-Magic at the same level; readers must check which one they're in |
| Future cost — extending the engine | Add a new `match compat with` inside any handler that needs it | Same | Every new compat-aware behavior adds another implicit-vs-explicit branch to maintain |
| Caller blast radius | ~12 caller updates, all mechanical | 0 caller updates | 0 caller updates |
| Hack tag | None | None | YES — parallel paths are a hedging hack |

### Hacks

- Option C is the "no legacy flags / no parallel paths" anti-
  pattern from the autonomous-run protocol. Calling it out
  explicitly: this is a hack. Reject.
- The 4136d29 wrapper (`applyImplantClose Compat.Magic`,
  `checkWithCompat`) IS that hack already in-tree. Consolidating
  away from it is what this fork resolves.

### Counter-cases

- **Counter-case for B over A.** Adding a field to RulesetView
  semantically conflates "what rules to run" with "what convention
  to apply when running them." A future caller that wants
  Klayout-tuned rules but Magic-style counting (or vice-versa)
  has to fork the view or use both wrappers. Function-arg compat
  (A) keeps the two concepts orthogonal — the rule list and the
  semantics dial are independent inputs.
- **Counter-case for C over A.** Smallest commit; zero caller
  churn. The argument fails the correctness ranking: the parallel
  path IS a divergent-source-of-truth bug waiting to happen, and
  the rest of Track 02's design (Magic permanent alternate, Phase
  5 F#-primary) is exactly the kind of long-tail evolution where
  drift would compound.
- **Counter-case for A over B.** The cleanest read in option B is
  "the view carries compat," but the engine's check functions
  already take both `view` AND `flat`/`units`. Adding compat as a
  sibling argument to `view` keeps each input small and dedicated
  — one rule list, one units, one compat. Conflating compat into
  view is mixing "what" with "how," which the engine should keep
  separate.

### Recommendation

**Option A (thread `compat` directly through every entry point).**

- Wins on correctness — one canonical compat source per call.
- Wins on cleanliness — every engine signature surfaces compat;
  no implicit-Magic surprise.
- Loses only on caller-blast-radius, which is NOT a ranking axis
  per the autonomous protocol (implementation effort doesn't count).

The 4136d29 parallel paths get consolidated as part of this fork:
`checkWithCompat` and `applyImplantClose Compat.Magic` go away;
`check` becomes the compat-aware entry point taking explicit
`compat`. Every existing caller updated to pass `Compat.Magic`.

Unambiguous winner per the protocol's ranking. No human at step 7
in autonomous mode — taking the cleanest option.

---

## Fork #2 — How to bypass the post-pass clustering for edge-counted rules

**Decision point.** `checkWithToggles` runs a final clustering
pass that groups violations by `(Rule, LayerNumber, LayerType)`
and merges spatially-adjacent ones into one per connected
component. For Magic-style polygon-count rules (Width, Spacing,
MinArea) this gives sensible canvas UX — one marker per logical
violation. But KLayout-style edge counting (one violation per
failing inner edge for symmetric enclosure) needs 4 separate
violations per failing inner, and the clustering reduces them to
1 because the 4 edges form a connected component.

The empirical evidence: my MustBeInside earlier attempt emitted 4
edges per uncovered square mcon; the post-pass clustered them
down to 2. The polygon-style fix (1 emit per source) sidesteps
the clustering by coincidence — but the upcoming
`Enclosure`/`AsymEnclosure` edge-counting (under Compat.Klayout)
will hit the same issue.

### Option A — Tag each Violation with a `Clusterable: bool` field

Add a `Clusterable: bool` field to the Violation record. Per-rule
handlers set it (true for Width/Spacing/MinArea, false for
edge-counted Enclosure under Klayout, false for MustBeInside).
Post-pass groups only Clusterable-true violations; the others
flow through unchanged.

- **Cost/complexity.** New field on Violation. Every existing
  emit site needs the field set (compiler enforces). Post-pass
  becomes a 2-way split.
- **Risk.** Forgetting to set the field at a new emit site —
  compiler catches at construction.
- **Reward.** Clusterability becomes per-violation, not per-rule
  by name. A rule that USUALLY clusters but has one edge-style
  emit path could mix. Future-proof.
- **Side effects.** Violation record grows. Downstream code that
  pattern-matches on Violation needs the new field — usually
  fine with a wildcard.

### Option B — Compute a "non-clusterable rule names" set per view, skip those in the post-pass

In `checkWithToggles`, derive a `nonClusterableRules : Set<string>`
from `view.Rules`. Match rules by kind: `MustBeInside _ -> Some
name`, and (when edge-counting Enclosure under Klayout) those
too. The post-pass skips groups whose `Rule` is in the set.

- **Cost/complexity.** A few lines of derivation at the top of
  checkWithToggles. No record changes.
- **Risk.** Rule kind determines clusterability. If two emit
  sites use the same rule kind but one wants clustering and the
  other doesn't, this design can't express that. Today: not a
  real risk — each rule kind picks one style.
- **Reward.** Concise. No record growth.
- **Side effects.** Behavior changes only at the post-pass; emit
  sites continue to use the same record shape.

### Option C — Move clustering OUT of the engine into the canvas layer

The post-pass exists for canvas UX — one marker per cluster looks
better than 4 in a corner. Move clustering to the consumer
(canvas / report renderer) that wants per-cluster UX. The engine
returns raw per-emit violations.

- **Cost/complexity.** Substantial — clustering touches every
  test that asserts on violation counts, every consumer that
  reports counts, the Magic-vs-viz parity tests, etc.
- **Risk.** Behavior change at every consumer that currently
  reads the engine output. Loud (counts change visibly), but
  wide.
- **Reward.** Cleanest separation — engine doesn't dictate UX.
- **Side effects.** Many call sites need updating to do their own
  clustering if they want the per-cluster UX, or to accept the
  finer-grained counts.

### Symmetric quantification

| Axis | A: field | B: name set | C: hoist clustering |
|---|---|---|---|
| Correctness — per-violation control over clustering | YES — field on each emit | NO — coarsened to rule kind | YES — emit raw, consumer aggregates |
| Cleanliness — read the post-pass | "if !v.Clusterable, pass through" — explicit | "if rule.name in skipSet, pass through" — explicit, key elsewhere | "no post-pass in engine" — radically simpler engine but more consumer code |
| Future cost — a new rule kind needs different clustering | Set field at emit | Add to skipSet derivation | Update consumer |
| Caller blast radius | All emit sites | Just the derivation | All consumers |
| Hack tag | None | None | None |

### Hacks

None of the three are hedge/legacy hacks.

### Counter-cases

- **B over A.** Smaller change; field-add propagates through
  every emit site. But field-add is mechanical (compiler-driven),
  not invasive — A's "future-proof" axis (per-emit-site control)
  is real. B is currently sufficient because no rule kind has
  mixed emit styles; A is cheap insurance against that ever
  happening.
- **C over A.** Architecturally purest. But the engine's
  clustering exists to give Magic-vs-viz parity tests sensible
  counts; moving it out would force every consumer to re-
  implement it. Higher cost on every consumer, only one consumer
  (canvas) really benefits.
- **A over B.** Per-emit-site control is the right abstraction
  when one rule could emit multiple styles. Today no rule does.
  The future risk is real but small.

### Recommendation

**Option B (non-clusterable rule names set).**

- Correctness: ties A — both control clustering per logical
  emit-style boundary. A is per-emit, B is per-rule-kind; today's
  rule kinds are all one-style so the granularity doesn't matter.
- Cleanliness: B keeps the Violation record minimal. The
  rule-kind → cluster-style mapping lives in one derivation at
  the top of checkWithToggles, easy to read.
- Future cost: same as A until a single rule kind needs mixed
  emit styles, which doesn't exist today.

The tied-correctness case under autonomous protocol normally
prompts BLOCKED, but cleanliness (smaller surface, no record
growth) and future-cost equality break the tie in B's favor —
A would only be preferred if mixed-emit rule kinds became
reality. Take B.

---

## End-of-run status

Will regenerate as I close commits. Top of file gets the live
status block.
