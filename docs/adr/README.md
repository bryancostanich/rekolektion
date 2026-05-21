# Architecture Decision Records

Each ADR captures a single structural decision: the question, the chosen answer, why, and what was rejected. ADRs are revisable — if new evidence makes an old decision wrong, write a follow-up ADR that supersedes it rather than rewriting history in place.

Format follows Michael Nygard's original ADR proposal: Context / Decision / Consequences / Alternatives.

## Interactive router (2026-05)

The first ADR block covers the design of the interactive 2D routing/editing tool in `tools/viz/` — turning the existing read-only viewer into a KiCad-class hand-routing editor.

- [ADR-0001 — Active edit layer lives in Visibility.ToggleState](0001-active-edit-layer.md)
- [ADR-0002 — Routing tool uses draft state + per-route commit](0002-routing-tool-draft-state.md)
- [ADR-0003 — Live DRC = per-rule liveEligible flag + region-scoped spatial index](0003-live-drc-scope.md)
- [ADR-0004 — DRC rules: base + override YAML layers with strict by-name merge](0004-drc-rules-yaml-layered.md)
- [ADR-0005 — Test surface: Msg dispatch (bulk) + headless Canvas2D (event-wiring)](0005-test-surface.md)
- [ADR-0006 — Walk-around router for interactive wire drawing](0006-walk-around-router.md)
