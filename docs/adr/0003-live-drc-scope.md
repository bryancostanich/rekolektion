# ADR-0003 — Live DRC: per-rule `liveEligible` flag + region-scoped spatial index

**Status:** Accepted — 2026-05-20

## Context

Live DRC fires on every mouse move while routing or dragging. The full DRC rule set (`tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs`) is too expensive at 60 fps on cells with thousands of polygons. Some kind of scoping is required.

Two real options:

- Tag rules as live-eligible (locally decidable) or commit-only (needs full topology). Live-eligible rules run on every mouse move, scoped to a region around the changed geometry using a spatial index. Commit-only rules wait for the route to finish.
- Hard-code a small subset of "collision" rules (clearance, width, layer-pair) that check live with brute-force bbox intersection; defer everything else (enclosure, via spacing, antenna, density) to commit.

The hardcoded-subset path defers rules a user can reasonably expect to fail live — enclosure violations on a freshly placed via, for example — and trips the trust-erosion failure mode that live DRC exists to prevent.

## Decision

Add a `liveEligible : bool` flag (plus optional `scopeRadius`) to every entry in `Drc/Rules.fs`. Build a spatial index (R-tree or uniform grid; choice deferred to implementation) over the cell's geometry once per route. On mouse move:

1. Compute the bounding region of the change (draft delta + per-rule influence radius).
2. Query the spatial index for geometry overlapping the region.
3. Run every `liveEligible` rule against that subset.
4. Render violations as an overlay on the canvas.

On `FinishRoute` commit (per [ADR-0002](0002-routing-tool-draft-state.md)), run the full rule set including commit-only rules and re-render.

Live-eligible: clearance, min-width, via enclosure, via spacing, layer-pair compatibility.
Commit-only: antenna, density, implant aggregates, well-tap distribution.

## Consequences

**Positive**
- What you see live agrees with what commit reports, for every live-eligible rule
- Deferred rules are explicit, not a hidden subset chosen by intuition
- Spatial index is reusable for hit-test, marquee, drag-preview, paste-preview
- Region scoping pressures DRC engine toward a first-class region-query API

**Negative**
- ~500 LOC: per-rule flag, scope radius, spatial index, region-query path on `Drc/Check.fs`
- Spatial-index correctness bugs cause silent missed violations — needs targeted tests
- Some rules have incremental semantics that look beyond a strict radius (e.g., notch rules); per-rule `scopeRadius` must capture that or the rule moves to commit-only

## Alternatives considered

- **Hardcoded collision subset (clearance + width + layer-pair) with brute-force bbox check.** ~150 LOC, no spatial index. Rejected because "live looked clean, commit failed" is precisely the failure mode live DRC exists to eliminate, and the curated whitelist is a maintenance trap as new rules get added.

## Related

- [ADR-0002](0002-routing-tool-draft-state.md) — `RouteInProgress` provides the draft geometry that live DRC queries against
- [ADR-0004](0004-drc-rules-yaml-layered.md) — `liveEligible` and `scopeRadius` are fields in the YAML rule schema
