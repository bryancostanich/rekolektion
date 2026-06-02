# Autonomous run — 2026-06-01 — silicon_correct Track 02

## End-of-run status (2026-06-01, regenerated last)

**Status: TRACK CLOSED OUT.** All six phases of
`silicon_correct/tracks/02_drc_klayout_primary/plan.md` landed.

**Decisions made (4 forks logged below):**

1. Fork #1 — Thread `Compat` directly through every F# engine
   entry point (Option A). Removed the parallel-path `4136d29`
   wrapper.
2. Fork #2 — Derive `nonClusterableRules` from `view.Rules` by
   kind, skip in post-pass (Option B). Avoids per-violation field
   churn.
3. Fork #3 — Python ↔ F# bridge via CLI subprocess to
   `viz drc --compat` (Option A). Bit-identical to harness path;
   no new dependencies.
4. Fork #4 — Default `external` is compat-conditional
   (Option B). Klayout-target callers get F# primary (validated,
   31/31 corpus rules green). Magic-target callers stay on
   ext-Magic until F# Magic gets its own parity work.

**Blocked / deferred:** None at track scope. The Magic-side F#
parity work (spacing-tile delta on metal layers, nsdm/psdm
label-swap) is recorded in `docs/internals/drc_rule_equivalency.md`
as informational off-diagonal deltas; it's its own track, not a
Track 02 blocker.

**Followup landed 2026-06-02** (commit `01d2977`): the
"foundry waiver pipeline missing on F# primary" concern surfaced
in the deferred discussion turned out to be narrower — the actual
bug was that `LU.2` / `LU.3` (SKY130-Magic-only latch-up rules,
hard-coded in `checkWithToggles` outside the rule-list dispatch)
were firing unconditionally under both compats.  KLayout deck has
no latch-up family.  Both emits now gated on
`compat = Compat.Magic`; new corpus cell
`probe_foundry_waiver.rkt` (single foundry primitive SRef) drives
the regression.  Foundry-cell-instance waivers via
`Waiver.collectFoundryFootprints` were already in place — they
just weren't filtering the unconditional LU.* emits because LU.*
isn't in `foundryWaiverMarginNm`.

**Commits (post-autonomous-handoff, 7 new):**

| Commit | Resolves |
|---|---|
| `cdf436b` | Fork #1 — thread Compat through every engine entry point |
| `3f38157` | Fork #2 — nonClusterableRules in post-pass |
| `7eccbc6` | Edge-counting Enclosure under Compat.Klayout; promotes via.4a, m2.4 |
| `aa50df3` | AsymEnclosure nearby-outer guard; promotes via.5a, m2.5, m1.5 |
| `7683138` | MustBeInsideEdgewise rule kind; promotes via.4a_a — corpus close-out (31/31) |
| `79896e6` | Forks #3+#4 — F# primary path + compat-conditional external default |
| (this) | Phase 6 — workflow doc, memory entry, CLAUDE.md, plan + project README marked complete |

**Total Track 02 commits (pre + post handoff):** 21
(`43e31a0` → this).

**What to review first.** In suggested priority:

1. `docs/decisions/autonomous_2026-06-01.md` — this file. Read
   the forks first.
2. `docs/internals/drc_rule_equivalency.md` — per-rule status
   table (31 green, all rules the corpus exercises).
3. `tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs` — engine
   internals (~1500 lines touched; new MustBeInside / MustBeInside-
   Edgewise handlers; compat-aware Enclosure / AsymEnclosure;
   nonClusterableRules; threaded compat).
4. `tools/viz/src/Rekolektion.Viz.Core/Drc/Rules.fs` — see the
   `Klayout` submodule for the populated rules; also `MustBeInside`
   + `MustBeInsideEdgewise` type defs.
5. `src/rekolektion/verify/rkt_drc.py` + `src/rekolektion/verify/
   drc_klayout.py` — Python orchestration, `run_drc_fsharp`,
   compat-conditional default-flip.
6. `docs/workflows/rkt_primitive_workflow.md` — construction
   notice closed, Step 2 DRC section reflects new defaults.

**Test posture at close:**

- Python: 43/43 DRC tests pass (`test_drc_klayout.py`,
  `test_drc_equivalency.py`, `test_verify_drc_compat.py`,
  `test_drc_box_grow.py`).
- F# viz solution builds clean across Core / App / Cli / Mcp +
  all test projects.
- F# Drc tests: 90/91 pass. The 1 failure is
  `MagicVsVizDrcTests.b1_5_stage1` — pre-existing on `main`
  before the handoff, unchanged. Unrelated to Track 02.

**No git push.** Per the user's standing rule + autonomous-mode
"don't push" directive.

---

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

---

## Fork #3 — How does Phase 5's `verify_drc(external=False)` reach the F# checker?

**Decision point.** Phase 4 has Klayout side at 100% rule coverage
on the corpus diagonal. Phase 5 flips the Python `verify_drc`
default from "shell out to ext-KLayout / ext-Magic binary" to
"run our own F# checker". The wire between Python and F# is the
mechanism choice.

### Option A — Reuse the existing viz CLI `drc` verb

Python `verify_drc(external=False)` calls
`dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- drc
--compat <c> <rkt>`. The CLI already emits TSV (rule, layer, bbox,
...) on stdout plus a totals line on stderr. Parse those, build
the existing `DRCResult` shape.

The harness module `verify/drc_equivalency.py` already has
`_run_fsharp` doing exactly this — moving it to a reusable spot
+ wiring `verify_drc(external=False)` to call it = the whole
Phase 5.1 work.

- **Correctness.** F# checker runs against the same view +
  Compat that the user requested. Bit-identical to today's
  harness path.
- **Cleanliness.** Reuses the CLI surface that already exists +
  is tested by Phase 4 harness. One subprocess hop, one parse,
  done. No new dependency added.
- **Future cost.** Per-call dotnet startup is ~1 sec (mitigated
  by caching JIT in subsequent calls, but each `verify_drc`
  invocation pays it). Acceptable for the verify workflow, which
  runs per-cell at edit time, not in tight loops.

### Option B — pythonnet to load the F# assembly in-process

Add `pythonnet` as a Python dependency. Load
`Rekolektion.Viz.Core.dll` at import time. Call
`Rekolektion.Viz.Core.Drc.Check.check(...)` directly via .NET
interop.

- **Correctness.** Same checker, but type marshalling at the
  Python ↔ .NET boundary adds risk (FlatPolygon array, View
  record, etc.). Equivalence harness would need to cover the
  marshalled path too.
- **Cleanliness.** Removes the subprocess hop and stdout parsing.
  But adds a hefty dependency + Avalonia (UI library that
  Rekolektion.Viz.Core transitively pulls in via App) for the
  load.
- **Future cost.** Per-call cost is ~0 ms (in-process). Total
  worth if `verify_drc` is called hundreds of times per session.
  Today it's not.

### Option C — F# DLL with hand-written C ABI

Build a small Rust/F#-with-C-export shim. Python ctypes loads
the shim, marshals geometry as int64 arrays.

- **Correctness.** Same checker, hand-written ABI surface adds a
  failure mode at every boundary crossing.
- **Cleanliness.** Lowest dependency footprint (no dotnet, no
  pythonnet) BUT requires building + shipping a native shim per
  platform.
- **Future cost.** Most setup work; least per-call overhead.

### Symmetric quantification

| Axis | A: subprocess | B: pythonnet | C: native ABI |
|---|---|---|---|
| Correctness — bit-identical to harness | YES | mostly (boundary marshalling) | mostly (boundary marshalling) |
| Cleanliness — fewest moving parts | one subprocess hop | new dep + transitive UI lib | native build per platform |
| Future cost — per-call latency | ~1 sec (dotnet startup) | 0 ms | 0 ms |
| Future cost — total surface to maintain | the existing CLI | pythonnet bindings + bridge code | C ABI + shim + bridge |
| Hack tag | None | None — pythonnet is supported tooling | None |

### Counter-cases

- **B over A.** Per-call latency. Real concern if Phase 5+
  unlocks a workload that calls verify_drc(external=False) in a
  tight loop. Counter: today's use cases are per-cell edits
  (humans + small batches), where a 1-sec hop is invisible.
- **C over A.** Lowest dependency footprint. Counter: cross-
  platform build complexity in trade for shaving 1 sec on a
  workflow that already runs in the multi-second range
  (verify_drc includes GDS conversion + grid check). Not worth.
- **A over B.** Subprocess overhead. Counter: dotnet startup is
  already paid by the viz App on every launch; the CLI version
  is the same JIT'd code path. The 1 sec is mostly Avalonia +
  trace init, much of which we don't need for headless checking
  — could trim later if it bites.

### Recommendation

**Option A (CLI subprocess).** Matches the plan's stated
preference, reuses existing tested code paths (harness
`_run_fsharp` is a working Phase 4 proof), and avoids new deps.
Bit-identical to the Phase 4 harness so the equivalency table
predicts production behavior. Take it.

---

## Fork #4 — Should `verify_drc` flip its default to `external=False`?

**Decision point.** Phase 5.4 of the plan says: "Once Phase 4
coverage is ≥80% Magic + 100% Klayout-sign-off-rules, change
external default from True → False."

KLayout side hits 100% on the corpus seed (31/31 rules). Magic
side coverage is partial — F# Magic still has the spacing-tile
delta on met-layers + label-swap on nsdm/psdm, surfaced by the
harness as the off-diagonal informational column. These are F#
Magic engine bugs, not Klayout-side blockers.

The flip is binary: defaults to F#, callers requesting external
still get external on `external=True`.

### Option A — Flip default everywhere

`verify_drc(external=False)` is the new default. Existing call
sites get F# semantics. Klayout-target callers (the new default
under `compat`) get full F# coverage. Magic-target callers get
F# Magic which has known deltas — they should be passing
`external=True` to get the existing behavior, but a few will
silently change semantics.

### Option B — Flip default only under `compat="klayout"`

`verify_drc()` defaults to `external=False` if `compat="klayout"`
(today's default), `external=True` if `compat="magic"`. Reflects
that we've proven equivalence on the Klayout side but not the
Magic side.

### Option C — Don't flip yet, only enable opt-in

Leave `external=True` as the default. Callers can pass
`external=False` to opt into the F# path. Phase 6 of the plan
becomes the actual flip — once F# Magic gets its parity work.

### Symmetric quantification

| Axis | A: flip everywhere | B: flip Klayout only | C: don't flip |
|---|---|---|---|
| Correctness — every caller gets the engine they actually want | NO — Magic-target callers silently switch | YES — only proven-equivalent path becomes default | YES |
| Cleanliness — one consistent default | YES | NO — compat-dependent default | YES |
| Future cost — when F# Magic lands parity, what changes? | nothing (already default) | flip Magic default too | flip everything |
| Hack tag | None | None — orthogonal defaults aren't a hack | None — staying conservative isn't a hack |

### Counter-cases

- **A over B.** Cleanliness — one default for everything reads
  better. Counter: silently shifting Magic-target callers to a
  known-buggy-vs-ext-Magic F# path is correctness regression.
  Magic-callers expect ext-Magic semantics; F# Magic doesn't
  match. Defaults must point at the validated path; the Klayout
  side IS validated, the Magic side isn't.
- **A over C.** Faster F# adoption. Counter: same correctness
  problem.
- **B over C.** Phase 5 of the plan says flip default once
  coverage is met. Klayout side meets it. Honoring the plan
  while protecting Magic-target callers from a silent regression
  is the right move.

### Recommendation

**Option B (flip default only under `compat="klayout"`).**

- Correctness wins: Klayout-target callers get the validated
  fast path; Magic-target callers keep the validated slow path
  until F# Magic gets its own parity work.
- Cleanliness penalty (compat-dependent default) is real but
  small — one extra branch in the Python orchestrator, one
  sentence in the docstring.
- Future cost: when F# Magic lands parity, flip Magic side's
  default too. Single edit, no surprises.

The plan implicitly assumed Magic side would land parity first;
Phase 4 actually finished Klayout side first. Adapting the
default-flip rule to "where we have parity" is the cleanest
read.

---

## End-of-run status

(Regenerated at run close — see below.)
