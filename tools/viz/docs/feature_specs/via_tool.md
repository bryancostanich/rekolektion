# Via tool — snap targets and priority

## Overview

Pressing `V` enters via mode.  A left-click drops a single via
stack at the snapped cursor coordinate, running from the snap
source's routing layer up to the toolbar's active layer.

The snap model follows the convention Figma, Illustrator, and
AutoCAD all use: the cursor pulls to *specific candidate points*
(or alignment lines), and X and Y can come from *different
sources*.  Hold **Alt** during the click to suppress all snap and
drop the via at the raw cursor.

Hover paints a preview circle at the resolved snap location so
the user sees where the via will land before clicking; the
resolver is shared between hover and press so preview matches
commit.

> Implementation: `tools/viz/src/Rekolektion.Viz.Core/Routing/ViaTool.fs :: resolveSnap` (pure).  Canvas plumbing in `tools/viz/src/Rekolektion.Viz.App/Canvas2D/GdsCanvasControl.fs` (hover at the `ViaMode` branch of the pointer-moved handler; press at the `ViaMode && IsLeftButtonPressed` branch).  Commit in `tools/viz/src/Rekolektion.Viz.App/Model/Update.fs :: Msg.ViaToolCommit`.

## Snap sources

Three categories.  The first two are the bulk of the snap; the
third is the Alt-key escape hatch.

### 1. Point snaps — pull both X and Y to one (X, Y)

A point snap fires when the cursor is within an 8 px Euclidean
radius of the candidate point.  Point snaps win as a unit — both
axes come from the point.

Sources:

- **Labelled pin centroids.**  Top-cell pins on any routing
  layer strictly below the toolbar's active layer.
- **Routing-rect "knuckle" centers.**  A knuckle is a square-ish
  routing-layer rect (aspect ratio ≤ 1.5).  Single candidate per
  knuckle: its bbox centroid.
- **Routing-rect "wire" endpoints.**  A wire is a long thin
  routing-layer rect (aspect ratio > 1.5).  Two candidates per
  wire: the two tip-midpoints (e.g. `(xMin, midY)` and
  `(xMax, midY)` for a horizontal wire).

### 2. Line snaps — contribute one axis each; X and Y combine

A line snap fires when the cursor is within 8 px *perpendicular*
distance of the line.  Each line constrains one axis; the other
axis stays at the cursor unless a different line also fires on
that axis.

Sources:

- **Vertical guide lines** → X axis.
- **Horizontal guide lines** → Y axis.
- **Vertical wire centerlines** → X axis (the wire's mid-width
  X coordinate, for a vertical wire).  Only fires when the
  cursor is within the wire's Y range — the wire physically
  ends at yMin and yMax, and the centerline shouldn't pull
  from outside the wire's bbox.
- **Horizontal wire centerlines** → Y axis (mid-height Y, for
  a horizontal wire).  Only fires when the cursor is within
  the wire's X range — same finite-extent rule.

### 3. Raw cursor — Alt held

Holding Alt at press time suppresses every other snap and drops
the via at the literal cursor world coordinate.  Matches Figma /
Illustrator behaviour ("hold a key to bypass snap").

## What is NOT a snap source

Explicitly removed in this redesign:

- **Bounding-box containment.**  Old behaviour was "cursor inside
  any routing rect's bbox → yank to bbox centroid."  That's the
  "super coarse" feel the user reported — cursor could be 1 µm
  from the centroid and still get pulled.  No mature editor
  (Figma, Illustrator, AutoCAD, Inkscape) snaps on containment;
  they all snap to specific candidate points.

- **Rect edges and edge midpoints.**  Not in this version (see
  Open Questions).  Adding them would multiply candidate count
  per rect and add visual noise; current set covers the common
  cases (pin / pad / wire end / centerline / guide).

## Behaviour rules

### 1. Radii

- 1.1 — Point-snap radius is **8 px Euclidean**, measured at the
  current zoom.  Computed as `radiusDbu = 8.0 / pixelsPerDbu`
  every frame so the same physical pixel zone applies regardless
  of how zoomed in the user is.
- 1.2 — Line-snap radius is **8 px on the perpendicular axis
  only**.  A vertical line pulls when `|cursorX - lineX| ≤ 8 px`;
  the cursor's Y is unconstrained.
- 1.3 — Alt-held suppresses both; no radius applies.

### 2. Priority

The resolver picks at most one outcome per click.  Point and
axis snaps **compete on actual cursor-to-snap-point Euclidean
distance** — closer wins.  This is the Figma / Illustrator
behaviour: alignment lines win when they pull less than an
anchor point would.

- 2.1 — **Alt** wins over everything.  If held, skip resolve and
  return a raw-cursor snap (or `None` if active layer would make
  a via impossible — see rule 3).
- 2.2 — **Compute the best point snap.**  Cursor-to-point
  Euclidean distance, nearest pin / knuckle centre / wire
  endpoint within the 8 px radius.
- 2.3 — **Compute the best axis snap.**  X and Y solved
  independently:
  - X axis: nearest of (vertical guides, vertical wire
    centerlines) within perpendicular radius.  If none, X stays
    at cursor.
  - Y axis: nearest of (horizontal guides, horizontal wire
    centerlines) within perpendicular radius.  If none, Y stays
    at cursor.
  The resolved axis snap point is `(snapX, snapY)`; the
  axis-snap distance is the Euclidean cursor-to-snap-point
  distance from that point.
- 2.4 — **Pick whichever has the smaller distance.**  Tie goes
  to the point snap (stable order; matches "snap exactly to the
  labelled thing when both are equally close").
- 2.5 — At least one axis must snap (or a point must fire) to
  count as a snap.  If neither axis has a line source within
  radius and no point fires, the resolver returns `None` — no
  via is placed.  (Alt is the escape hatch for "I really want
  the cursor position".)

- 2.6 — **Layer tiebreak.**  When multiple snap candidates of
  the same kind sit at equal distance from the cursor (typical
  case: stacked wires at the same midY), the **higher routing
  layer wins**.  Mirrors the "what's visually on top of the
  stack" intuition and stops a li1 wire from stealing the snap
  from a met2 wire that's drawn on top of it at the same point.

> Worked example: cursor at (2510, 2400), radius 64.  A wire
> endpoint sits at (2530, 2405) — Euclidean distance ~21.  A
> vertical guide at X = 2495 — perpendicular distance 15, axis
> snap point (2495, 2400), distance 15.  15 < 21 → guide AxisX
> wins, even though both are within radius.  Pre-2.4 the wire
> endpoint would have stolen the snap because point snaps
> categorically beat axis snaps; that produced the user's "vias
> aren't snapping to guidelines" report.

### 3. Layer rules

The via stack bottoms out at the snap source's layer and tops at
the toolbar's active layer.

- 3.1 — **Pin / knuckle center / wire endpoint / wire centerline**
  snaps carry a real metal layer (the source rect's layer).
  bottomLayer = source.Layer.
- 3.2 — **Guide-only snap** (one axis from a guide, other from
  cursor) has no source metal.  bottomLayer = `activeLayer - 1`.
  Requires `activeLayer ≥ met1`; disabled otherwise.
- 3.3 — **Guide + wire centerline combination** (e.g. X from a
  guide, Y from a horizontal wire's centerline) inherits the
  wire's layer for bottomLayer.  The wire is real metal; the
  guide is just an X constraint.
- 3.4 — **Two-guide combination** (X from a vertical guide, Y
  from a horizontal guide, no wire involved): bottomLayer =
  `activeLayer - 1`, same rule as 3.2.
- 3.5 — **Raw cursor (Alt)**: bottomLayer = `activeLayer - 1`.
  Requires `activeLayer ≥ met1`; disabled otherwise.

### 4. Hover preview

- 4.1 — The hover preview uses the resolver result unchanged —
  same kind, same coords.  Preview matches commit exactly.
- 4.2 — A small circle paints at the snap point.  (Per-snap-type
  glyph and per-axis line indicators are future polish — see
  OQ-3.)
- 4.3 — `None` from the resolver: no preview circle.  Press in
  that state logs `via.tool no-snap` and doesn't emit.

### 5. Active-layer filter

- 5.1 — Point-snap candidates (pin, knuckle center, wire endpoint)
  are filtered to source layers **strictly below** the active
  layer.  An active-met2 click on a met2 candidate would yield
  a met2↔met2 via — no plumbing — which silently emits nothing.
- 5.2 — Line-snap sources are filtered the same way (the wire
  layer for centerlines, the guide's implied `activeLayer - 1`
  for guides) — see rule 3.

## Open questions

- **OQ-1.** Add **rect edges** (left / right / top / bottom edges
  as line sources, edge midpoints as point sources) so the user
  can align a via to the edge of a metal patch?  Adds candidate
  count and visual noise; defer until requested.

- **OQ-2.** Should the snap consider only **on-screen** geometry,
  or everything in the document?  Today: everything.  Fine for
  typical cell sizes; could matter on very large layouts.

- **OQ-3.** **Per-snap-type visual feedback** (Figma-style guide
  lines + intersection X marker, AutoCAD-style per-snap glyphs).
  Today: single circle at the resolved point.  Adequate but
  doesn't show which source contributed each axis.

- **OQ-4.** **Toggle keybind** (AutoCAD F3) to disable snap
  globally rather than holding Alt per-click?

## Change log

| Date | Commit / PR | Change |
|---|---|---|
| 2026-06-04 | `ddc4905` | Initial spec — pin radius 8 px, single-winner with knuckle / pin / guide priority. |
| 2026-06-04 | `b490165` | Full rewrite of the snap model to match Figma / Illustrator / AutoCAD convention.  Removed bbox-containment centroid pull (root cause of "super coarse" feel).  Added per-axis combination (X from one source, Y from another).  Added Alt-to-suppress.  Knuckle / wire snaps now require cursor to be near a specific candidate point (center, endpoint, centerline) within the 8 px radius — not just inside the rect's bbox. |
| 2026-06-04 | `a144b5e` | Priority rule 2 changed: point and axis snaps now compete on actual cursor-to-snap-point Euclidean distance (closer wins) rather than point-always-beats-axis.  Was: a wire endpoint near the cursor would always steal the snap from a closer guide line.  Now: the guide AxisX wins whenever its perpendicular distance is smaller than the endpoint's Euclidean distance.  Resolves the "vias aren't snapping to guidelines" report — the resolver was finding nearby wire endpoints and short-circuiting before evaluating the guide axis. |
| 2026-06-05 | (this commit) | Two fixes for "via plumbed two layers down and connected the wrong net": (a) wire centerlines now have a finite perpendicular extent — a horizontal wire's centerline at midY only fires when the cursor's X is within [xMin, xMax].  Pre-fix the centerline was treated as an infinite horizontal line, so a li1 wire ending at X=3420 still pulled the cursor's Y to its centerline when the cursor was at X=3450, even though the cursor was past the end of the wire.  (b) Layer tiebreak (rule 2.6) — when multiple candidates sit at equal distance (typical: stacked wires at the same midY), the higher routing layer wins so the snap lands on what's visually on top.  Together the fixes pick met2 (1-step via to met3) instead of li1 (2-step stack that shorted to a different net at the intermediate met2 landing). |
