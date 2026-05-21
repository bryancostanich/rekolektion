# ADR-0006 — Walk-around router for interactive wire drawing

**Status:** Accepted — 2026-05-21

## Context

The current routing tool (ADR-0002) draws a fixed L-shape from the start point to the cursor under the active posture (HorizontalFirst / VerticalFirst). It does not understand obstacles. The user identified this concretely on li1: when a wire starts on a FET source/drain licon and heads perpendicular to the FET wall, the straight L-shape passes directly over neighbouring licons of other nets — those licons touch li1 and would short the start net to the foreign nets.

The user wants the router to do the offset automatically rather than just show a red DRC overlay and let the user adjust by hand. Modern PCB EDA tools (KiCad PNS, Altium ActiveRoute, EasyEDA) implement this as a "walk-around" router: a visibility-graph shortest path around obstacles, run continuously as the cursor moves.

## Decision

Implement an interactive walk-around router that operates on every routing layer (li1, met1, met2, met3, met4) under the continuous-trigger model. The router does **not** introduce vias or shove existing wires — both are explicit non-goals for v1.

### Obstacle model

For each layer L, the obstacle set is computed from two sources:

1. **Same-layer foreign-net polygons** — any polygon on L whose net (looked up via `Macro.Nets`) is not the start net of the current draft route. The start net comes from the snap target the wire was anchored to.
2. **Cross-layer features that bridge to a foreign net through L** — for li1 specifically, any licon belonging to a foreign net. Generally: any contact / via that, if overlapped by a wire on L, would electrically connect L to a foreign net.

DRC-rule violations (spacing, enclosure, min-area) are **not** part of the obstacle set. Those continue to fire through the existing live-DRC overlay (ADR-0003); the walk-around handles electrical shorts, the DRC handles geometric rules. The two pipelines are independent and run concurrently.

### Net info source

Reuse `Macro.Nets : Map<string, NetEntry>` — already populated either from the sidecar JSON or from `Net.LabelFlood.derive`. Build a `PolygonRef → net name` reverse index once per `Macro.Nets` change (the same lifetime as the existing flat polygon array) and query it during obstacle classification. No new net inference.

### Search representation

Visibility graph + Dijkstra shortest path. Nodes are:

- The start point (snap target centroid)
- The cursor point (continuously updated)
- Obstacle corners, expanded outward by the wire's half-width-plus-spacing for layer L (from the existing per-layer DRC view)

Edges are manhattan-visibility: two nodes are connected if a horizontal-then-vertical (or vertical-then-horizontal) path between them does not intersect any obstacle, accounting for wire half-width.

Build cost: O(O · log O) per layer per geometry change, cached and reused per mouse move.
Per-query cost: O((O + 2)²) Dijkstra over the cached graph. For typical macros (tens of obstacle corners near the start) this is sub-millisecond. Larger cells stay under the UI-thread frame budget.

### Trigger and dispatch

Continuous — run on every `RouteMouseMove`. Reuse the `Routing.LiveDrc` scheduling pattern (ADR not yet filed; see `Routing.LiveDrc.fs`): version-counter on a `Routing.WalkAround.State`, compute on a `Task.Run`, write back via `Dispatcher.UIThread.Post` guarded by `tryAccept`. The per-move UI cost is the snapshot copy + the task queue; the search runs off the UI thread.

The visibility-graph build happens on the UI thread when geometry or active layer changes (bounded, cached). The search query runs on the thread pool.

### Posture interaction

The existing posture flag (HorizontalFirst / VerticalFirst, toggled with `/`) becomes a bias for the search: equal-cost paths break the tie in favour of the current posture. When the walk-around finds a strictly shorter detour, posture is ignored — the user wanted the offset.

### Failure mode

If no path exists from the start net through the obstacle set to the cursor (e.g., the cursor is inside a foreign-net polygon, or fully enclosed by them), the wire renders to the last reachable point on the path; live-DRC paints red on whatever gap remains. The wire does not jump or freeze the cursor — only the rendered geometry stops at the last clear node.

### What v1 deliberately omits

- **Auto-via / layer changes.** Single-layer per route. The user changes layers with the existing hotkeys (`` ` `` / `1` / `2` / `3` / `4`).
- **Shove.** No existing wires are pushed aside. If a foreign wire blocks the only path, the route fails per the failure-mode behaviour above. Shove is a separate ADR when interactive editing of already-committed routes lands.
- **Rubber-band geometry.** The wire is a discrete sequence of fixed manhattan segments, not a relaxed taut string. Walk-around gives the topology; the segments are emitted as straight legs between graph nodes.
- **Diagonal routing.** Manhattan only.

## Consequences

**Positive**
- The user's stated FET-wall escape problem is solved automatically on li1; every other routing layer gets the same treatment with no per-layer code path.
- The obstacle map is mechanically derived from data the cell already carries — no annotation burden on primitives.
- Walk-around is the foundation every modern PCB router builds on; later additions (shove, auto-via, rubber-band) extend this pipeline rather than replace it.
- The visibility graph is cached per geometry change, not per move — per-frame cost stays small even on big cells.

**Negative**
- ~600–800 LOC across `Routing/Obstacles.fs`, `Routing/VisibilityGraph.fs`, `Routing/WalkAround.fs`, the per-move dispatch glue, and tests.
- Two pipelines (walk-around, live-DRC) running on every move. Both are backgrounded, but they share `FlatPolygons` snapshots and we need to make sure neither stalls the other under sustained motion.
- The visibility graph cache must invalidate correctly on `FlatPolygons` change, `Macro.Nets` change, `ActiveLayer` change, and start-net change. Bug class: stale graph after a net rename or geometry edit.

## Alternatives considered

- **Grid + A\*.** Rasterize the cell at routing pitch; A* over clear cells. Rejected: per-query cost scales with raster area, not obstacle count; for continuous mode this would push us back into the same UI-thread cost regime that DRC just escaped. Visibility graph queries are O(obstacles²), independent of cell size.

- **Rubber-band / shove from day one.** The canonical KiCad PNS model. Rejected for v1 because we have no existing committed wires to shove — the first feature is single-wire drawing from a fresh primitive pin. Walk-around is what shove falls back to in clear regions anyway; ship walk-around first, layer shove on top later.

- **Local-only auto-jog.** Only run path search when the cursor is in a dense-obstacle region; elsewhere keep the L-shape. Rejected: visibility-graph queries in clear regions are trivially cheap (start and cursor have direct manhattan visibility), so the L-shape falls out naturally. No reason to special-case it.

- **Per-primitive escape lanes.** Primitives declare their own li1 escape corridors in `.rkt`. Rejected: pushes routing knowledge into the primitive library; doesn't generalise to met1+; couples the .rkt schema to the router's representation. The data-driven obstacle model gets the same outcome without the coupling.

## Related

- [ADR-0001](0001-active-edit-layer.md) — `ActiveLayer` selects the layer the walk-around runs on
- [ADR-0002](0002-routing-tool-draft-state.md) — the walk-around output replaces the straight L-shape inside `RouteMouseMove`
- [ADR-0003](0003-live-drc-scope.md) — live-DRC and walk-around run side by side; DRC catches geometric rule violations, walk-around handles electrical shorts
- [ADR-0004](0004-drc-rules-yaml-layered.md) — per-layer wire width / spacing from the DRC view drives obstacle-corner expansion
- [ADR-0005](0005-test-surface.md) — `Routing.WalkAround` is a pure module testable without an Avalonia host; the dispatch glue gets a `LiveDrc`-style version-counter test
- `Routing.LiveDrc` (Core) — the version-counter / stale-drop dispatch pattern the walk-around reuses
