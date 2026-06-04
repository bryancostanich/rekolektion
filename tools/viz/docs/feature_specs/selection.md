# Selection — viz 2D canvas

## Overview

Click on geometry to select it.  The selection drives the
inspector overlay, the dimension overlay, drag/move operations,
and `DeleteSelection` / `MoveSelectionDbu` follow-ups.

Two related but distinct mechanisms:

- **Instance selection** — clicking an SRef bounding box selects
  the cell-level instance.  Sketched here for context only;
  drives Cmd+D delete-instance, rotate, mirror, drag.
- **Polygon / wire selection** — clicking a rect / poly / wire
  builds a `Set<PolyKey>` in `Model.Selection`.  This spec
  focuses on the wire-and-knuckle case because that's where the
  rules are non-obvious.

The user gestures that produce a polygon / wire selection:

| Gesture | Result |
|---|---|
| Left-click on a wire body | Whole connected wire chain selected (knuckles excluded) — see rule 3 |
| Left-click on a knuckle (pad) | Just that one rect selected — see rule 2 |
| Left-click on empty space | Selection cleared |
| Shift+left-click on a wire body | Toggle the chain into / out of the existing selection |
| Shift+left-click on a knuckle | Toggle that one rect |
| Esc | Selection cleared |
| Marquee drag | Every rect / poly whose bbox lies inside (left→right) OR touches (right→left) the marquee |

## Vocabulary

The selection rules talk about two shapes that share the same
storage (rects on routing layers, tagged with an optional
`WireId`) but behave differently under the cursor:

- **Knuckle** — a roughly-square pad.  Classifier: `max-side /
  min-side < 1.5`.  Emitted by routing's endpoint-pad pass
  (`Routing.Pads.endpointPadSide`), by the V-tool standalone via
  (`Routing.ViaTool.emitStandaloneAt`), and by primitive
  generators' contact stacks.  Implementation:
  `tools/viz/src/Rekolektion.Viz.Core/Routing/Wire.fs :: isKnuckleShape`.
- **Wire body** — a long thin rect.  Classifier: anything that
  isn't a knuckle (same threshold, complement).
- **WireId** — an int tag on a `Rectangle`'s `Props` that groups
  rects originating from a single route commit.  Wire bodies +
  the knuckles at their endpoints share a WireId.  Implementation:
  `Routing.Wire.getWireId` / `setWireId`.

The same 1.5× threshold appears in `Routing.ViaTool.isWireShape`
for the V-tool's snap classification.  The two MUST stay in sync:
a knuckle the user can drop a via on is the same shape they can
single-click to select.

## Behaviour rules

### 1. Hit-test priority

When the cursor's world coordinate falls on multiple rects, the
canvas picks one based on visual stacking — what the user sees
on top of the click point should be what gets selected.

- 1.1 — Instance selection runs first.  If a click lands on a
  top-cell SRef's bounding box and no top-cell rect sits on top
  of it at the click point, instance selection wins.

- 1.2 — Among top-cell rects whose bounding boxes contain the
  cursor, the canvas picks the rect on the visually-topmost
  layer.  For sky130 this maps directly to the GDS layer
  number: met5 (72) wins over met4 (71), wins over met3 (70),
  ..., wins over li1 (67).  Non-routing layers (poly, diff,
  nwell) sit below the routing stack and are reached only when
  no routing rect contains the cursor.

- 1.3 — When two rects on the same layer both contain the
  cursor, the later-authored rect (later in the top cell's
  Elements list) wins.  Renderers paint later rects on top
  within a single layer, so this matches what the user sees.

> Implementation: `tools/viz/src/Rekolektion.Viz.Core/Routing/Wire.fs :: findSegmentAt`.

### 2. Knuckle click

When the picked rect is a knuckle (rule applies to its own
`isKnuckleShape` result):

- 2.1 — **Only that rect is selected.**  The connected-component
  walk is short-circuited.  Lets the user grab a single
  endpoint pad without picking up its attached wire.
- 2.2 — Shift-click toggles that one rect in / out of the
  existing selection (no chain involved).
- 2.3 — A click on a knuckle does NOT pick up the wire it caps
  even when the knuckle and the wire share a `WireId`.

> Implementation: `tools/viz/src/Rekolektion.Viz.App/Model/Update.fs :: Msg.WireSelectAt` — branches on `Routing.Wire.isKnuckleShape seed`.

### 3. Wire-body click

When the picked rect is a wire body:

- 3.1 — Walk the connected component of same-layer + same-WireId
  rects that bbox-touch.  Knuckles ACT AS BRIDGES — the walk
  passes through them.
- 3.2 — Knuckles are filtered OUT of the final selection.  The
  user gets every wire body in the chain, NONE of the pads.
- 3.3 — Untagged rects (no `WireId`) fall back to a same-layer
  bbox-walk — same as pre-WireId era.
- 3.4 — The walk does NOT cross layers via the WireId
  (via-stack siblings on other layers share the WireId but
  live on different rects; rule 3.1's same-layer guard keeps
  them out).
- 3.5 — Shift-click toggles the whole chain in / out of the
  existing selection.

> Implementation: `tools/viz/src/Rekolektion.Viz.Core/Routing/Wire.fs :: connectedComponentWireBodiesOnly` + `Update.fs Msg.WireSelectAt` else branch.

### 4. Empty-space click

- 4.1 — Plain click on empty space clears `Model.Selection`.
- 4.2 — Shift-click on empty space leaves the selection
  unchanged (don't punish a misclick).

> Implementation: `Update.fs Msg.WireSelectAt` `None` branch.

### 5. Modifier keys

| Modifier | Effect on click |
|---|---|
| (none) | Replace selection with the rule-2 / rule-3 result |
| Shift | Toggle: if the new set is already a subset of `Model.Selection`, subtract it; otherwise add it |
| Esc (separate gesture) | Clear `Model.Selection`, `InstanceSelection`, `SelectedRatlines` |

> Implementation: shift-modifier in `Update.fs Msg.WireSelectAt`; Esc in `App.fs KeyMap` / canvas `OnKeyDown`.

### 6. Diagnostic logging

Every `Msg.WireSelectAt` dispatch emits a `wire.select` log
entry with:

- `seedIdx`, `seedX1..Y2` — the rect document index + bbox
- `seedIsKnuckle` — classifier result on the seed
- `chainCount` — size of the connected component before
  knuckle filter
- `selectionCount` — size of the final selection

Used to diagnose "click should have selected X but didn't" /
"click selected too much" reports without re-instrumenting.

## Open questions

These are deliberately unresolved in the spec — pick one and
amend the rule when the user has a clear answer.

- **OQ-1.** A knuckle click currently picks just the knuckle
  (rule 2.1).  Should there be a modifier (Alt? Cmd+Click?) that
  ALSO picks up the wire the knuckle caps?  Useful when the user
  wants to grab a wire by clicking its more-visible endpoint pad.

- **OQ-2.** Rule 3.4 keeps the wire-body walk on a single
  layer.  Should a wire-body click optionally chain through a
  via stack to wires of the same `WireId` on adjacent metals?
  Mirror question: should knuckle-click pick up the whole via
  stack of same-WireId knuckles across layers?

- **OQ-3.** When a knuckle's `WireId` doesn't match any wire's
  `WireId` (a stray pad emitted by the V tool and not yet
  routed away from), should clicking it offer "select the
  whole pad stack here" (mcon + met1 pad + ...) as a unit?
  Today rule 2.1 gives just the picked rect.

- **OQ-4.** Marquee selection currently doesn't apply the
  knuckle / wire-body distinction — it selects every poly /
  rect whose bbox lies inside (or touches, for right→left).
  Should marquee respect the same "knuckles are separable"
  rule, or is "marquee = grab everything in the box" a
  separate gesture?

## Change log

| Date | Commit / PR | Change |
|---|---|---|
| 2026-06-03 | (initial spec) | Captures rules 1–6 + OQs 1–4. |
| 2026-06-04 | (this commit) | Rule 1.2/1.3 clarified: hit-test picks visually-topmost layer, not last-in-doc.  Code aligned (findSegmentAt now consults `Rkt.ToGds.layerToGds` to pick by GDS layer number); WireSelectAt refactored to call `Routing.Wire.connectedComponentWireBodiesOnly`. |
