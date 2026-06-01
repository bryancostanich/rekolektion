# Handoff — Interactive routing silicon-killer bug (2026-05-30)

This is a handoff to whoever picks up the interactive-routing work next. The current agent (me) has fully diagnosed the root cause via headless probes and live log forensics, but has been told to stop touching code and write down what's known before more whack-a-mole patches go in. Read this end-to-end **before** making any code change.

---

## The bug, in one sentence

When the user drags fast enough that the visibility-graph build hasn't completed by the time they click to commit, **`r.Auto` is empty, the commit polyline becomes `[anchor, cursor]`, and `lShape` emits a single straight rect that crosses any obstacle in its path** — including foreign-net silicon. The bug is silicon-killing on dense macros (d13_mux: 5622 obstacles, ~700ms cold build), and reproduces 100% with a fast drag-and-click.

The live preview shows nothing meaningful (no walkaround result ever produced), so the user has no warning. They click thinking they're committing the dodge path; they actually commit a straight line.

---

## Confirmed by log forensics + headless probe

**Live log evidence (2026-05-31T17:29:56 - 17:29:58):**

```
17:29:56.715  StartRoute
17:29:56.898  walkaround.schedule   cursor=(28895,4496)
17:29:56.900  walkaround.compute.start
17:29:56.920  walkaround.schedule   cursor=(29229,4496)  ← new schedule cancels previous via CTS
17:29:56.920  walkaround.compute.start
17:29:56.939  walkaround.schedule   cursor=(29364,4496)
[... 8 schedule/compute.start pairs in 1.3s, every one cancelled ...]
17:29:58.234  walkaround.cancelled  afterMs=1   ← bails at first ct check
17:29:58.267  RouteFinish           ← commit reads r.Auto = []
```

**Zero `walkaround` events the entire route.** Every task bails in ~1ms at the first `ct.ThrowIfCancellationRequested()` (at `routeAdaptive`'s loop entry). Nothing completes. `r.Auto` never gets populated.

**Headless probe (`tests/Rekolektion.Viz.Core.Tests/D13MuxCollisionRepro.fs`):** loads d13_mux, runs `shortestPath` with the same anchor/cursor, returns a clean **9-node detour path** in ~17ms. The algorithm works. The bug is in the dispatch/cancellation layer, NOT in path-finding.

---

## Architectural history (read commits before touching dispatch)

| Gen | Commit | Pattern | Bug it fixed | Bug it introduced |
|---|---|---|---|---|
| 1 | `07be896` | `Task.Run` per move, drop stale at writeback | initial impl | 76 events queued → 44 tasks → 4.5s drain |
| 2 | `2c1417f` | Single-flight + coalesce, no cancellation | queue drain | stale computes ran to completion: 2.5s dead work per drag |
| 3 | `ad37b04` | Cooperative cancellation: cancel in-flight, run latest | dead work | **current**: cursor moves faster than first build → every task cancelled at startup → never produces a result |

Each generation fixed the prior's failure mode but introduced a new one. **The reason this keeps cycling: there is no written-down contract for what the dispatch layer must guarantee.** Each gen optimized for a different sub-property. Until the invariants are codified (see "Spec to write" below), the next agent will produce a gen-4 that introduces gen-5's bug.

---

## My recent changes (uncommitted on `main`)

Two of these are perf wins that should stay. One is the Lazy single-flight cache that is load-bearing for any future dispatch design (without it, "stale runs to completion" costs 800ms instead of 17ms — gen-2's main objection).

| File | Change | Status |
|---|---|---|
| `VisibilityGraph.fs` | Direct-edge short-circuit when both endpoints have a clear L. Verified safe by `RoutingZRouteProbe.fs` + 256 routing tests | **Keep** |
| `VisibilityGraph.fs` | Per-cell spatial filter on adjacency construction (R=8 cells). Build 2603→700ms on d13_mux. Edges drop from 20334→12998 (verified IDEAL test still passes) | **Keep** |
| `VisibilityGraph.fs` | NodeGrid added to `Prebuilt`; endpoint scan uses spatial filter instead of O(N) over all corners. shortestPath 217→17ms steady-state. | **Keep** |
| `VisibilityGraph.fs` | `lClearGrid` walks only L's row+column of cells, not 2D bbox rectangle | **Keep** |
| `WalkAround.fs` | Lazy single-flight in `graphCache`: concurrent callers for same key share one build instead of N racing builds. **Critical** for any new dispatch policy. | **Keep** |
| `GdsCanvasControl.fs` | Diagnostic log lines: `walkaround.schedule`, `walkaround.compute.start`, `walkaround.cancelled`, `walkaround.exception`. Pure observability. | **Keep** (helps next debugging session) |

**No fix for the silicon-killer has been written.** I stopped after the user (correctly) told me to audit before slapping a patch.

---

## Spec to write (the contract that's been missing)

The user agreed to a `docs/routing_spec.md` with this outline. Write this **first**, evaluate fixes against it, then implement. **The bug above happens because §2 has never been written down.**

1. **Scope** — cell-level interactive routing. Single-net wire from snap target to snap target. Out of scope: multi-net, ECO, batch.
2. **Silicon invariants** (the hard guarantees, never violated):
   - No commit may produce a wire that crosses foreign-net silicon on the route layer.
   - No commit may produce a wire that fails layer/spacing DRC against its known obstacle set, **except via explicit override (see §3)**.
   - If the system cannot prove a wire is silicon-safe, it MUST NOT commit silently.
3. **Override path** — the user noted "we had overrides for illegal routes" and the agent missed it. Find and document the existing override mechanism (modifier-key click? menu action? `feedback_endpoint_over_path.md` is referenced in `GdsCanvasControl.fs:2531`). The spec must state when override is allowed, what UI signals it, and what record exists post-commit that the wire was an explicit override (so it's not silently audited as "passed DRC").
4. **UX invariants**:
   - The committed wire equals the live-preview wire at click time (this is what gen-3's "removed click-time-sync" comment was protecting).
   - Live preview updates within stated latency budget (TBD: 50ms? 100ms?).
5. **Perf budgets**:
   - First build (cold) on dense macros: target ≤ 1s.
   - Per-cursor-frame search (warm): target ≤ 50ms.
   - Click → commit: target ≤ 200ms (excluding cold build).
6. **Component contracts** — each module states inputs / outputs / invariants:
   - `Routing.Obstacles` — ours-vs-foreign classification rules; when the obstacle set changes; cache invalidation triggers.
   - `VisibilityGraph.build` — pre/post; complexity; cache semantics; **Lazy single-flight** is now a contract, not an implementation detail.
   - `VisibilityGraph.shortestPath` — when `None` means "no path exists" vs "search bailed via cancellation". This distinction matters at commit time.
   - `WalkAround.routeAdaptive` — region expansion contract; what `noPath` after `maxExpansions` means.
   - `LiveDrc.schedule` — **dispatch policy** (the load-bearing piece — must satisfy §2 + §4 + §5).
   - `Routing.Draft` — what `r.Auto` contains, when it's stale, who clears it. **Must define a "stale" predicate**: r.Auto is stale iff it was computed for a different cursor than r.Cursor.
   - `commitRouteWith` — pre-commit validation gate. Currently NO validation runs here. The spec must say what gate(s) exist.
7. **Failure modes** — what happens when invariants can't be met:
   - Cold build + immediate click → behavior (current: silicon-killer; spec must define correct behavior).
   - Cursor moves faster than search → behavior.
   - Search returns `noPath` → behavior (current: silently commits straight line; this is the bug).
   - Walkaround throws → behavior (currently swallowed by `LiveDrc.schedule` catch-all `| _ -> None`).
   - **Stale `r.Auto`** at commit time → behavior.
8. **Probe suite** — headless tests that prove each invariant. Each invariant gets at least one test that fails before the fix, passes after.
9. **Open questions** — list honestly:
   - What's the override mechanism (find and document).
   - What's the right gen-4 dispatch policy? (Three candidates listed below.)
   - Should `commitRouteWith` re-validate against current obstacle set (defense-in-depth) or trust the draft layer?

---

## Three candidate gen-4 dispatch policies

To be evaluated against the spec, not picked yet:

| Policy | What it does | Tradeoffs |
|---|---|---|
| **A: Revert to single-flight + coalesce, no cancel** | Old in-flight runs to completion; new schedules overwrite a pending slot. With Lazy graph cache, "stale work" is now ~17ms not 800ms. | Click-time may still get no result during the cold ~700ms build. Requires commit-time gate. |
| **B: Keep cancellation, move check INSIDE A* loop only** | At least one full search per task completes. `routeAdaptive` doesn't bail at entry. | Cold build still uncancellable. Drag through cold build still loses results. |
| **C: Block RouteFinish until walkaround for current cursor returns** | Click handler waits up to N ms for fresh walkaround. Falls back to refuse-to-commit on `noPath`. | UI freezes for that N ms on cold build. Requires per-cursor-version tracking. |

**Likely correct answer: A + a commit-time gate (3 with C's fallback)**. Single-flight + coalesce makes drag responsive; commit-time gate enforces §2. But this is still a guess until the spec is written and probes are agreed.

---

## Tests / probes already written (run these before any change)

| File | What it proves |
|---|---|
| `tests/Rekolektion.Viz.Core.Tests/RoutingZRouteProbe.fs` | 4 probes for the bend-selection short-circuit (Z-route, only-V-clear, only-H-clear, neither-clear) |
| `tests/Rekolektion.Viz.Core.Tests/D13MuxPerfProbe.fs` | Per-layer timing on d13_mux; live-cadence 20-frame median; cold concurrent single-flight gate |
| `tests/Rekolektion.Viz.Core.Tests/D13MuxCollisionRepro.fs` | Headless repro of the live noPath scenario; confirms algorithm finds the detour when post-commit state is simulated correctly |

All 257 routing/walkaround/visibility/livedrc tests pass on the current uncommitted state. **The bug does not reproduce headlessly** — it's specifically in the dispatch+cancel+commit-timing path.

---

## Rules for whoever picks this up

These are from the user's explicit feedback during the session. Save to memory if you don't already have them.

1. **Probe before claiming a fix.** Write a headless probe that reproduces the EXACT bad observation first. Never claim "this will fix it" from REASON alone. The agent who handed this off broke this rule and gaslit the user. Don't.
2. **Believe the user's bug report.** If the user says "the wire went through obstacles" and the log says `silicon=0`, **the log is measuring the wrong thing** — find what's wrong with the measurement, don't dismiss the user's observation.
3. **Audit before patching.** When there's existing complexity (cancellation policy, cache layers, dispatch), READ the commit history and the existing comments before adding more. This codebase has gone through three generations of dispatch policy already.
4. **Preserve the override path.** There IS an existing mechanism for explicitly allowing illegal routes. The agent who handed this off failed to find it. Look for it (grep for modifier-key handling around `RouteFinish`, check `feedback_endpoint_over_path.md`).
5. **No schedules/timelines/cosmetic markdown** in the spec or status docs. The user is readiness-driven, not deadline-driven. Just state what's true.
6. **STOP and ASK** if you're guessing. The user prefers a clarifying question to a wrong patch.

---

## What to do, in order

1. Read this doc end-to-end.
2. Read `tools/viz/docs/route_editing_plan.md` and `tools/viz/docs/routing_caches.md` (already exist).
3. Find the override mechanism and document it (spec §3).
4. Write `tools/viz/docs/routing_spec.md` per §1-§9 above.
5. Translate every §2/§4 invariant into a headless probe in the test project. Each must fail against current `main`, define what "fail" means.
6. Once probes exist for all invariants, propose a gen-4 dispatch policy. Evaluate against the probes.
7. Implement the chosen policy. All probes must pass + existing 257 routing tests must pass.
8. Hand the user a live binary to test. Believe their report.

Do not skip step 5. The whole reason this bug exists is that step 5 never happened.
