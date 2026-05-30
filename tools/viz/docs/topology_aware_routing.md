# Topology-aware routing — design proposal

**Status:** draft, pending user sign-off.

## Problem

Today's router is per-wire-id and stateless about other wires. The
duplicate-via-stack audit on `d11_ota_v4.rkt` (2026-05-30) surfaced
three patterns it cannot handle:

1. **Full-overlap dupes.** Two wires of the same net terminate at
   the same physical endpoint; each emits its own complete mcon +
   via1 + pad stack at that endpoint, byte-identical.
2. **Partial-overlap dupes.** Two wires of the same net traverse
   the same span on different layers (li1 trunk vs met1 trunk),
   with slightly mismatched pad sizes (290×290 vs 320×320). Same
   logical hop, two parallel physical realisations.
3. **Multiple-net-per-rect.** A single physical region claimed by
   multiple wire-ids; inspector shows multiple net entries for one
   geometry.

Pattern (1) is already covered by `Routing.Wire.dedupCoincidentRects`
(Option 1, shipped 701c470) — collapses byte-identical-bbox rects.
Patterns (2) and (3) need router-level awareness.

## Goals & non-goals

**Goals:**
- Router recognises shared topology elements (anchors, branches)
  across wires of the same net.
- New routes JOIN existing same-net geometry instead of paralleling
  it.
- No regressions to single-wire route commits.

**Non-goals (v1):**
- Cross-net merging (different nets stay distinct).
- Wholesale rewrite of `Routing.Pointer` / `Routing.Draft`.
- Backward-incompatible `.rkt` schema changes.

## Two-phase plan

### Phase 1 — pin-level sharing at commit

Smallest viable change. Each route commit looks up the start and
end anchors against the active net's existing polygons; if a rect
already exists at the anchor's (layer, xy), the new commit skips
emitting a via stack at that end.

**Data flow:**

```
commitRouteWith
  ↓ (existing: pads + segs + startVias + endVias, all stamped wireId)
  ↓
  rects' = drop any startVia rect whose (layer, bbox-center) is
           already covered by an existing net-N rect at that layer
           (within snap tolerance, e.g. 1 DBU).  Same for endVias.
  ↓
  docAppended = appendRectsToTop rects' mc.Document
  ↓
  doc' = Routing.Wire.dedupCoincidentRects docAppended  (existing — backstop)
  ↓
  ... unchanged ...
```

**Where to get "existing net-N rects":**
- `mc.Nets : Map<string, NetEntry>` keyed by net name.
- `NetEntry.Polygons : PolygonRef list` — each ref carries
  (Structure, Layer, DataType, Index, TopInstanceIndex).
- Resolve each ref to an actual Rectangle via the doc.

**Tolerance for "at the anchor":**
- The anchor's `(StartX, StartY)` is the snap-target centroid.
- An existing via cut rect at (layer = startVia.Layer, center within
  ε of (StartX, StartY)) counts as covering the anchor.
- ε = max(1 DBU, snap grid). Defensive default.

**What it solves:**
- Pattern (1) — robustly, even when the existing rect has a slightly
  different pad size than the new emit would produce.
- Pattern (3) — partially, when the "multiple net entries on one
  rect" is from the new wire claiming an existing rect's space.

**What it does NOT solve:**
- Pattern (2) — partial-overlap trunks on different layers. Phase 2.

**Tests:**
- Synthetic 2-wire-same-net same-endpoint case: commit wire 1, then
  commit wire 2 with shared endpoint; rect count = wire 1's rects +
  wire 2's segs (no extra via stack at shared end).
- Integration on a real cell where the bug manifests.

**Estimated effort:** small. ~50 lines in `commitRouteWith`, helper
in `Routing.Wire` or `Routing.ViaStack`, ~5 unit tests.

### Phase 2 — net-aware path planning

Make the router plan routes through a connectivity graph built from
the current net's geometry. A new route can JOIN existing edges
rather than parallel them.

**Data model:**
- `NetTopology` per net: nodes (anchors) + edges (wire segments) +
  layer info.
- Built once per route session from `NetEntry.Polygons`.
- Walkaround / VisibilityGraph consults it: traversing along an
  existing net edge is zero-cost (same metal, same net = electrically
  identical).

**Router behaviour change:**
- When planning A→B for net N, check if A is already connected to
  some part of net N via existing geometry (T-tap into existing
  trunk).
- If yes, plan A→nearest-net-N-point instead of A→B (free ride on
  existing trunk).
- If no, plan A→B as today.

**What it solves:**
- Pattern (2) — partial-overlap trunks merge instead of parallel.
- Foundation for later T-tap / branch / vertex-edit operations.

**What it requires:**
- New `Routing.NetTopology` module.
- `Routing.WalkAround` / `Routing.VisibilityGraph` extension to
  consume net topology as zero-cost edges.
- Probably a new `route_editing_plan.md v2`.

**Estimated effort:** large. Multi-week refactor of the route
planner. Needs its own design doc with reference paths through the
existing routing modules.

## Decision points for the user

1. **Phase 1 first, Phase 2 separately?** Recommended. Phase 1
   ships fast, demonstrably fixes 2 of 3 patterns, doesn't lock in
   any architectural commitment. Phase 2 gets a proper design pass
   with no rush.
2. **Tolerance ε for "anchor coincides"?** Default = max(1 DBU,
   snap grid). Comfortable with that?
3. **Migration of existing wire-id annotations?** Phase 1 doesn't
   touch them. Phase 2 may want to introduce a `(net "name")` prop
   alongside `(wire-id N)` to make net membership explicit at the
   rect level (today it's derived from label-flood). Discuss when
   Phase 2 starts.
4. **`(wire-ids n1 n2 …)` list prop for multi-id provenance?**
   Mentioned in the dedup commit as a future schema extension.
   Phase 1 leaves this open; if Phase 2 introduces a `(net …)`
   prop it might subsume the need.

## Reference paths (current code)

- `Model/Update.fs:154` — `commitRouteWith` (Phase 1 patch site).
- `Routing/Wire.fs` — wire-id + dedup primitives. Add Phase 1
  helper here (e.g. `dropRedundantViasAtAnchors`).
- `Routing/ViaStack.fs:emitAt` — current via-stack emit. Phase 1
  filter wraps its output.
- `Routing/Draft.fs` — DraftRoute construction (StartNet, StartXY
  etc. flow from here).
- `Net.Ratlines.compute` — net-graph builder for labels. Phase 2
  reference for the connectivity-graph builder.
