# Routing pipeline caches — invalidation contract

The walkaround / live-DRC / snap pipeline has five caches stacked on
top of one another. They all key on **reference identity** of two
upstream values:

- `FlatPolygons : FlatPolygon array` — the flattened geometry array.
  Reallocated when `Layout.Flatten.flatten doc` runs.
- `NetMap : Map<string, NetEntry>` — the per-net polygon claims.
  Reallocated when `Net.LabelFlood.derive doc` runs (or when a
  sidecar swaps it).

A new instance of either invalidates everything downstream. **Anyone
adding a new cache MUST key against one or both of these, by
`obj.ReferenceEquals` / `HashIdentity.Reference`** — not by
structural equality. Two structurally-equal Maps that happen to be
different instances must miss the cache, because their identity is
the signal that LabelFlood re-derived.

## The five caches, from bottom to top

| Cache | Key | Lives in | Invalidated by |
|---|---|---|---|
| `indexCache` (NetIndex) | `box nets` | `Obstacles.fs` | new `NetMap` instance (LabelFlood re-derived) |
| `obstacleSetCache` (ObstacleSet) | `Layer + StartNet + box flat + box idx` | `Obstacles.fs` | new `FlatPolygons`, new `NetIndex`, or different layer/startNet |
| `macroBoundsCache` | `box flat` | `WalkAround.fs` | new `FlatPolygons` |
| `cachedSnapTargets` | `cachedSnapTargetsFor === FlatPolygons` | `GdsCanvasControl.fs` | new `FlatPolygons` |
| `cachedDrcViolations` / `cachedRouteLiveViolations` | versioned `LiveDrc.State` | `GdsCanvasControl.fs` | every change via `bumpVersion`; stale results dropped at writeback |

`cachedCellCrossNet` in the canvas is keyed similarly to
`cachedDrcViolations` and follows the same versioned-state pattern.

## The one rule

> When you change `FlatPolygons` or `NetMap` upstream, **reallocate
> the array / Map**. Don't mutate in place. Every cache downstream
> uses the new instance's identity as its "invalidate me" signal.

Single-pipeline ownership:

- `Layout.Flatten.flatten` is the only thing that reallocates
  `FlatPolygons`.
- `Net.LabelFlood.derive` (and `Update.commitRouteWith` for the
  incremental commit-time claim) is the only thing that reallocates
  `NetMap`.
- The canvas and the dispatch layer (`Routing.LiveDrc`) consume
  these but never reallocate them themselves.

If you find yourself wanting to "force a cache miss without
changing the upstream value" (commit `b450595` did this with a
"force-different NetMap comparer"), stop. That's a bug-compat
patch for an invariant that was already broken upstream. Fix the
upstream issue.

## Why reference identity, not structural

Structural equality on a Map<string, NetEntry> with thousands of
entries is O(N · log N). The whole point of the caches is to make
the per-frame walkaround / DRC dispatch sub-millisecond. Reference
identity is O(1) and matches the actual mutation model — the data
flow already produces a fresh instance whenever the underlying
geometry or labeling changes, so reference equality is the correct
signal.
