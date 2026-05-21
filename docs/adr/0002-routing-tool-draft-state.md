# ADR-0002 — Routing tool uses draft state + per-route commit

**Status:** Accepted — 2026-05-20

## Context

When the user click-click-clicks to draw a route, the editor needs a clear model for in-progress segments. Two viable patterns:

- Each click immediately writes a wire to the cell and pushes an undo snapshot; `Esc` just exits the tool with all those wires retained.
- In-progress segments live in a separate "draft" buffer while drawing; finishing the route commits the whole batch as one undo step; `Esc` discards the draft entirely.

The user's spec explicitly references KiCad/EasyEDA/Altium UX. All three reference tools use the draft pattern: `Esc` aborts, `Enter`/pad-hit/double-click commits, `Backspace` unfixes the last in-flight segment.

## Decision

Introduce a `RouteInProgress` field on `Model` holding unfixed draft segments. Routing-tool message handling:

- `StartRoute(seed)` — initialise `RouteInProgress` from a pad / wire-end / cursor point on `ActiveLayer`
- `MouseMove(world)` — update the trailing tentative segment(s) under the cursor with the active bend mode
- `FixSegment` — append the current tentative segment(s) to the draft; cursor becomes the new origin
- `BackspaceSegment` — pop the last fixed draft segment
- `FinishRoute` — `EditSession.pushUndoSnapshot`, commit the draft into the cell as one operation, `EditSession.markDirty`, clear `RouteInProgress`
- `AbortRoute` (`Esc`) — discard `RouteInProgress`, no commit, no undo entry

The Canvas2D renderer composites cell + draft overlay; draft segments are coloured distinctly (matching KiCad's "unfixed" convention).

## Consequences

**Positive**
- UX matches every reference tool the user named
- One `Ctrl+Z` undoes a whole route, not 20 individual clicks
- Live DRC and net-flood queries that need "cell + draft" geometry compose cleanly
- The overlay-compositor pattern is reusable for drag-preview, paste-preview, and snap-ghost

**Negative**
- ~250–300 LOC: new field, six Msg cases, Update arms, renderer overlay
- `RouteInProgress` must be cleared on tab switch and macro close to avoid stale state
- Bug class: overlay/cell sync — a missed clear leaves draft segments rendered against the wrong cell

## Alternatives considered

- **Direct-to-cell with per-segment undo.** Each click is a real edit, no overlay rendering, no draft state. Simpler (~150 LOC). Rejected because it diverges from the UX the user spec'd and turns long routes into a Ctrl+Z marathon to abort.

## Related

- [ADR-0001](0001-active-edit-layer.md) — `ActiveLayer` is the target layer for draft segments
- [ADR-0003](0003-live-drc-scope.md) — live DRC queries against `cell + RouteInProgress`
- [ADR-0005](0005-test-surface.md) — Msg dispatch tests cover the routing state machine
