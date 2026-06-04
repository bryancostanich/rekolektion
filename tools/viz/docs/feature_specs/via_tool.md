# Via tool — snap targets and priority

## Overview

Pressing `V` enters via mode.  A left-click drops a single via
stack at the snapped cursor coordinate, running from the snap's
own routing layer up to the toolbar's active layer.  No drag —
press releases commit immediately.  Hover paints a preview circle
at the same snap result so the user sees where the via will land
before clicking.

The snap resolver is shared between the hover preview and the
commit so the preview matches the click result exactly.

> Implementation: `tools/viz/src/Rekolektion.Viz.Core/Routing/ViaTool.fs :: resolveSnap` (pure).  Canvas plumbing in `tools/viz/src/Rekolektion.Viz.App/Canvas2D/GdsCanvasControl.fs` (hover at the `ViaMode` branch of the pointer-moved handler; press at the `ViaMode && IsLeftButtonPressed` branch).  Commit in `tools/viz/src/Rekolektion.Viz.App/Model/Update.fs :: Msg.ViaToolCommit`.

## Snap targets

The resolver considers three kinds of targets and picks one:

- **Knuckle / wire** — a routing-layer rect whose bounding box
  contains the cursor.  No extra radius — the cursor has to be
  visibly on the rect.  See `findRoutingSnapAt`.
- **Pin label** — the centroid of a labeled pin on a routing
  layer below the active layer.  Pulled in within a screen-space
  radius.
- **Guide line** — a user-drawn editor guide.  A vertical guide
  constrains only X (Y stays at cursor); a horizontal guide
  constrains only Y.  Pulled in within a screen-space radius on
  the perpendicular axis.

## Behaviour rules

### 1. Radii

- 1.1 — Pin snap radius is **8 px** measured at the current zoom.
  At a typical sky130 zoom (one nanometre ≈ 0.02 px) that's
  roughly 0.4 µm — generous enough to click-near, tight enough
  that an unrelated label one micron away doesn't steal the snap.
- 1.2 — Guide snap radius is **8 px** as well, but it's measured
  on the perpendicular axis only.  A vertical guide pulls in when
  `|cursorX - guide.CoordDbu| ≤ 8 px`; the cursor's Y is free.
- 1.3 — Knuckle / wire snap has no radius — the cursor's world
  coord must lie inside the rect's bbox.
- 1.4 — All three radii live in canvas-space pixels, not file
  DBU, so they scale with zoom (closer zoom = tighter pull-in in
  DBU terms).  The conversion is `radiusDbu = 8.0 / pixelsPerDbu`
  computed every frame.

### 2. Priority

When more than one snap kind has a candidate under the cursor:

- 2.1 — **Knuckle / wire wins over both pin and guide.**  The
  user is visibly pointing at painted geometry — the snap should
  not chase a label or a guide line away from it.
- 2.2 — When no knuckle wins, compute the nearest pin (within
  pin radius) and the nearest guide (within guide radius).
  Whichever has the smaller cursor-to-target distance wins.
  This is rule 2 of the spec the user picked on 2026-06-04: no
  fixed priority between pin and guide — physically closer wins.
- 2.3 — When pin and guide distances tie exactly, pin wins
  (stable order — the resolver evaluates pin first and only
  switches on strictly-smaller guide distance).
- 2.4 — Cursor-to-pin distance is Euclidean (`sqrt(dx² + dy²)`).
  Cursor-to-guide distance is the perpendicular distance to the
  guide line, which is single-axis (`|cursorX - Gx|` for a
  vertical guide).  The comparison in rule 2.2 is between these
  two scalars.

### 3. Guide snap layer

A guide is just a coordinate constraint — it carries no implied
metal layer.  The via still needs a bottom and a top:

- 3.1 — When the toolbar's active layer is set to **met1 or
  higher**, a guide snap emits a single-step via with
  `bottomLayer = activeLayer - 1` and `topLayer = activeLayer`.
  Cleanest case — the user is drawing on met2, clicks near a
  guide → a met1↔met2 via lands on the guide.
- 3.2 — When the active layer is **li1** (the lowest routing
  layer): guide snap is **disabled**.  There's no routing layer
  below li1 to plumb to, so a single-step via has no meaningful
  shape.  The resolver behaves as if no guide were present and
  falls back to pin / no-snap.
- 3.3 — When **no active layer is set** (the toolbar has no
  layer highlighted): guide snap is **disabled** for the same
  reason — no anchor for the via stack's top.

### 4. Hover preview

- 4.1 — The hover preview circle uses the resolver result
  unchanged — same kind, same coords.  No separate snap path
  for preview vs commit.
- 4.2 — The hover preview circle's colour distinguishes between
  routing-rect (knuckle / wire), pin, and guide so the user can
  tell at a glance which kind of snap is about to fire.  (See
  the canvas's `hoveredSnapTarget` paint pass.)
- 4.3 — When the resolver returns `None`, no preview circle
  paints.  Press in that state logs `via.tool no-snap` and
  doesn't emit a stack.

### 5. Active-layer filter

- 5.1 — Knuckle / wire snap candidates are filtered to layers
  **strictly below** the active layer.  An active-met2 click
  on a met2 rect would yield a met2↔met2 via — no plumbing —
  which silently emits nothing.
- 5.2 — Pin snap candidates are filtered the same way.
- 5.3 — Guide snap is gated separately by rule 3 (only fires
  when active layer ≥ met1), so it doesn't need the layer-below
  filter.

## Open questions

- **OQ-1.** When the user holds a modifier (Alt? Shift?), should
  the resolver disable pin snap so they can drop a via at the
  exact cursor position?  Today there's no "raw cursor" escape
  hatch — you have to zoom in enough that no pin / guide is in
  range.

- **OQ-2.** Should guide snap consider only the guides currently
  visible in the viewport, or all guides in the document?  If
  the user has a guide off-screen at the same X as the cursor,
  do they expect it to grab?

- **OQ-3.** Two guides crossing — vertical at X=Gx and horizontal
  at Y=Gy — could combine into a 2D snap at (Gx, Gy).  Today
  rule 2 picks the single nearer guide, so one axis stays at
  cursor.  Is the combined intersection snap useful enough to
  add?

## Change log

| Date | Commit / PR | Change |
|---|---|---|
| 2026-06-04 | (this commit) | Initial spec — captures rules 1–5 + OQs 1–3.  Pin radius tightened from 20 px to 8 px; guide snap added; nearest-wins between pin and guide. |
