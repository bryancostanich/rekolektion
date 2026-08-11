# Plan — viz Selection & Handle-Drag Oracle Harness

Phased, ordered so each phase produces a checkable artifact and the harness
goes **red on today's code before any fix is written**. Do not start a fix
phase until the harness is red for the right reason (proves the bug), not for a
harness bug.

Legend: each phase lists its deliverable, the exit check that must pass, and
which decision (D1/D2/D3) it implements.

---

## Root-cause model — CORRECTED after reading the live code

The original plan rested on **Confirmed Finding #1**: "two independent hit-test
paths over two index spaces resolve the same cursor differently," blamed for
symptom (a). **Reading the code refuted this for the affected path.** Ground
truth as of this investigation:

- **Committed-segment drag is 2D-only and ALREADY single-resolver.**
  `GdsCanvasControl` pickup (GdsCanvasControl.fs:2704) calls
  `Routing.Wire.findSegmentAt` — the *same* Core rule as `WireSelectAt`
  selection (Update.fs:1547). Highlight and drag resolve identically. **There is
  no dual-resolver seam in the segment-drag path.**
- The 3D `StackCanvasControl.HitTestTrackHandleFor` (nearest-12px over
  `Detect.Route` segments) **does not dispatch `SegmentDragStart` at all** (grep:
  zero `SegmentDrag`/`findSegmentAt` in `StackCanvasControl.fs`). It drives a
  *separate* in-flight route-**track** editing gesture (`/route/simulate-drag`,
  `RouteTrackDrag`). Unrelated to symptoms (a)/(b)/(c).
- **Finding #2 (global-`docPos` vs per-cell `idx` in `findSegmentAt`) is real
  but benign.** The returned `(cellName, per-cell idx)` pair is self-consistent;
  only the same-layer tie-break uses a global monotonic counter, which is still
  a coherent "later in document wins" ordering. It does **not** return a
  mismatched index.

**What is still genuinely live:**

- **Finding #3 (STRONG suspect for (b)/(c)):** untagged rects (`WireId=None` —
  e.g. index-9 in `tap_mux_strip.rkt`) flow through `SegmentDrag` as single-rect
  wires, so an endpoint drag emits L-corner bridges instead of extending. This
  is the mechanism of symptoms (b) and (c).
- **Symptom (a) ("wrong geometry moves") is NOT yet root-caused.** It is *not*
  a dual-resolver bug. Next suspects, in order: the collinear-group merge in
  `SegmentDragCommit`, and the `projectGeometry` chain replacement. **The
  harness (Phases 1–5) is the instrument that will locate it** — we build it
  first rather than speculate further.

## Phase 0 — Expose the already-pure core to the test project  (D1: step zero, demoted)

**Reordered: the harness now comes first** (it is the diagnostic that will
actually locate (a)). Phase 0 shrinks from "collapse a dual path" (that path
doesn't exist in the affected code) to a thin enablement step:

- Confirm `Routing.Wire.findSegmentAt` and `Routing.SegmentDrag.projectGeometry`
  are the shared decision + commit points and are referenced by both selection
  and drag (they are — recorded above). No behavioral change.
- Ensure the test project can reference these Core functions (project reference /
  visibility only). No Avalonia in the test path.

**Deferred decision (only if the harness proves it needed):** whether to make
the drag path *structurally* re-resolve via `findSegmentAt` (decision (i) from
the earlier checkpoint). Since the 2D path already calls `findSegmentAt`, this
is now a *hardening* nicety, not a bug fix — hold it until Phase 5 evidence
says whether (a) traces to resolution at all.

**Exit check:** test project compiles against Core `findSegmentAt` /
`projectGeometry`; app build unchanged; no behavioral edit to selection or drag
in this phase.

## Phase 1 — Author `semantics.md` prose twin  (D2)

Distill **only** the three initial invariants from `route_editing_plan.md` v1.1
and `selection.md` into crisp, numbered, testable rules. Co-locate with the
oracle. Keep it tiny — it must be diffable against the oracle in review.

- Rule set for **single-answer hit-test**: given overlapping rects at a point,
  exactly one target is returned, chosen by the documented priority
  (instance > topmost layer# > latest document order).
- Rule set for **respect**: a drag moves only the grabbed wire (same `WireId` /
  the resolved target), never an ungrabbed neighbour.
- Rule set for **extend-not-jog**: a wire-endpoint drag along the run axis
  lengthens the flanking segment; Anchored vs Mid-wire classification per spec.

**Exit check:** `semantics.md` reviewed; every rule cites its
`route_editing_plan.md` / `selection.md` source §.

## Phase 2 — Build the oracle model  (D2)

Independent F# implementation of the Phase-1 rules, mirroring `semantics.md`
rule-for-rule (rule number in a comment on each branch). Lives in the test
project, **not** shared with the Core impl — it must be an independent second
opinion.

**Exit check:** oracle compiles; a handful of hand-written table cases
(constructed from the corpus, including the known index-9 untagged rect in
`tap_mux_strip.rkt`) return the spec-correct answer.

## Phase 3 — Abstract-gesture DSL + FsCheck generators  (D3)

- Define the gesture DSL: `ClickAt`, `ShiftClickAt`, `DragHandle`, `Marquee`
  (world coords + modifiers).
- FsCheck `Arbitrary` that generates valid gesture sequences seeded from corpus
  geometry (points sampled on/near real rects so hits are meaningful).
- Property harness: for each generated gesture, run it through the **pure core**
  and through the **oracle**, assert they agree on the three invariants.

**Exit check:** generators produce valid gestures (spot-check sample); property
runner executes at fuzzing speed (thousands/sec) — confirms the core is truly
Avalonia-free.

## Phase 4 — Headless-integration lowering  (D1 + D3)

- Write the **trivial, total** gesture→`Msg` lowering (each gesture → fixed
  1–2 `Msg`s, no state branching — Constraint).
- Reuse `HeadlessRender.renderToPngWithSession` / the shared session to dispatch
  the lowered `Msg`s through the real `Update.fs`, then read back
  `model.OpenMacros[active].Document`.
- Invariant #1 test: for a sampled subset of gestures, the document the **app**
  produces equals the document the **core** produces — proves the canvas adopted
  the core and the lowering is faithful.

**Exit check:** headless suite runs green on gestures where core and app already
agree; the subset run completes in CI-reasonable time.

## Phase 5 — Run red, confirm it catches the real bugs

Point the whole harness at the corpus on **current** behavior (before fixes).

- Expect (b)/(c) to surface as extend-not-jog failures on untagged rects
  (Finding #3) — the strong, code-confirmed suspect.
- Symptom (a) is **open**: watch which property it trips (respect? single-answer
  hit-test? extend-not-jog on the collinear-merge path?). Whichever property
  goes red on `tap_mux_strip.rkt` for (a) *is* the root-cause locator — this is
  why the harness runs before any (a) fix.
- Verify each red is a *real* divergence, not a harness/oracle/lowering bug. If a
  red traces to the harness, fix the harness first — do not proceed to a fix on
  a false red.

**Exit check:** harness is red; (b)/(c) reds trace to Finding #3; and (a) is
pinned to a specific property + code path (recorded), replacing the discredited
dual-resolver hypothesis with an evidence-backed one.

## Phase 6 — Fix symptom (a): TBD, driven by Phase 5 evidence

The fix is **not pre-decided** — the dual-hit-test framing was refuted. Once
Phase 5 pins (a) to a property + code path (collinear-merge in
`SegmentDragCommit`, `projectGeometry` chain replacement, or — only if the
evidence shows it — resolution), design the smallest change against *that* and
raise a design-decision checkpoint if more than one fix is viable.

**Exit check:** the property (a) tripped in Phase 5 goes green (fuzz +
headless); manual smoke on `tap_mux_strip.rkt` confirms the highlighted rect is
the one that moves.

## Phase 7 — Fix symptoms (b)/(c): endpoint extend vs jog

Address untagged-rect endpoint behavior per the Phase-1 rules (the deferred
fork from the original audit: enable extend on untagged, or ensure paint emits
`WireId`, or refuse-with-explain). **Raise as a design-decision checkpoint**
before coding — the harness now tells us which divergence dominates.

**Exit check:** extend-not-jog property green (fuzz + headless); manual smoke
confirms north-drag on a wire end lengthens, not jogs.

## Phase 8 — Land

- Remove any temporary probes/diagnostics added along the way.
- Ensure the three invariant suites run in the project's test entry point.
- Commit in coherent units (Phase 0 extraction; harness Phases 1–4; each fix)
  with conventional-commit messages. Do not push unless asked.

**Exit check:** all three invariant suites green on the full corpus; build
clean; app smoke-tested live via `viz app` (not GDS/PNG).

---

## Sequencing / risk notes

- **Phase 0 is now low-risk** (test-project enablement only; no behavioral edit
  to live selection/drag). The former high-risk "collapse the dual path" work is
  deferred and may prove unnecessary — the 2D path already shares the resolver.
- Phases 5→6→7 must stay in order: never write a fix while the harness is green
  or red-for-the-wrong-reason. Symptom (a)'s fix is explicitly TBD until Phase 5
  evidence lands — do not resurrect the dual-resolver fix without new evidence.
- If Phase 4's lowering is tempted to branch on state, stop — that violates the
  D3 constraint and means the gesture DSL is wrong; revisit before continuing.
- Corpus files are large (`tap_mux_strip` = 10113 flatPolys); keep the headless
  subset small and the fuzz on the pure (fast) core.
