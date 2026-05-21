# ADR-0001 — Active edit layer lives in `Visibility.ToggleState`

**Status:** Accepted — 2026-05-20

## Context

The interactive router and every editing feature downstream of it (drag, click-to-select-net-on-layer, layer-aware DRC) need an "active edit layer" — the layer that pressing `1`/`2`/`3`/`4` brings into focus, that newly drawn routes target, and that drag operations modify.

Today `tools/viz/src/Rekolektion.Viz.Core/Visibility.fs` tracks per-layer show/hide via `Visibility.ToggleState`, but has no active-layer concept. Reference tools (KiCad, EasyEDA, Altium) all couple focus → visible: focusing a layer always makes it visible.

The choice is where in the Elmish model active-layer state lives.

## Decision

Add `ActiveLayer : LayerKey option` to `Visibility.ToggleState`.

Focusing a layer implies showing it — match the reference-tool convention. Co-locating focus with visibility in one type lets us enforce that coupling at the type boundary (or via a smart constructor) rather than relying on `Update.fs` discipline at every call site.

## Consequences

**Positive**
- One place to read "current layer story" for renderer, picking, and routing
- Focus-implies-visible enforceable at the boundary
- Per-tab `ToggleState` snapshots in `Model` already exist; persistence is free

**Negative**
- `Visibility.ToggleState` accretes future per-layer state (alpha, DRC overrides) over time, widening its scope
- Visibility tests must cover an invariant: toggling layer X off must not clear focus on layer Y

## Alternatives considered

- **Separate `Focus : LayerKey option` field on `Model`.** Clean separation between "what's shown" and "what edits target." Rejected because the two fields drift in practice — focusing a hidden layer leaves clicks doing nothing until `Update.fs` remembers to auto-show, and that discipline is exactly what the type system can enforce in the chosen option.

## Related

- [ADR-0002](0002-routing-tool-draft-state.md) consumes `ActiveLayer` to decide which layer drawn segments target
