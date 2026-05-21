# ADR-0005 — Test surface: Msg dispatch (bulk) + headless Canvas2D (event-wiring)

**Status:** Accepted — 2026-05-20

## Context

Interactive routing and editing features are mouse-driven, traditionally hard to test. Existing tests in `tools/viz/tests/Rekolektion.Viz.Core.Tests/` reference `Core` only; `Update.fs`, `Msg.fs`, and `Canvas2D/GdsCanvasControl.fs` live in the `App` project, which has no test project yet.

Three candidate surfaces:

- Pure `Msg` dispatch into `Update.update` — fast, deterministic, runs anywhere, but cannot see Canvas2D's event-to-Msg translation
- Headless `Canvas2D` via `Avalonia.Headless` — instantiate the control in-process, synthesize pointer events, assert on Model; covers full input→Msg→Update path but heavier per-test setup
- MCP-driven end-to-end — drive the running app over JSON-RPC; rejected because MCP exists for external AI/scripting control, not as an in-process test mechanism

## Decision

Create `tools/viz/tests/Rekolektion.Viz.App.Tests/` referencing the `App` project. Use a layered split:

- **Bulk: Msg dispatch.** Tests build a `Model`, send sequences of `Msg` values into `Update.update`, assert on `Model` and `EditSession` state. Every Msg arm, every state transition, every DRC integration scenario goes here. Cheap to write, dense coverage.
- **Targeted: headless Canvas2D.** A small set of tests (~5–10) using `Avalonia.Headless` instantiate `GdsCanvasControl`, attach a test dispatcher, synthesize pointer events, and assert that the correct Msg fires. Covers the event-wiring slice that Msg-dispatch tests cannot see.

The split rule for contributors: *event-wiring tests go in Canvas2D, everything else goes in Msg dispatch*.

MCP remains an external-control feature for AI exploration and scripting — not part of the test surface.

## Consequences

**Positive**
- ~95% of routing/editing logic covered by fast, deterministic Msg tests
- Canvas2D event-wiring gap closed by a small fixed cost of headless tests
- Pressure on `Update.fs` to hold the interesting logic — Canvas2D stays a thin event-to-Msg translator
- `Avalonia.Headless` dependency stays scoped to the App test project

**Negative**
- New test project to maintain (offset: same project covers both layers)
- Two test patterns to teach future contributors
- `Avalonia.Headless` ties the App test project to specific Avalonia version

## Alternatives considered

- **Msg dispatch only.** Cheaper, but leaves a real bug surface uncovered — a regression where "right-click maps to the wrong Msg" passes every test.
- **Headless Canvas2D only.** One surface covers everything, but per-test setup cost (instantiate control, layout pass, synthesize event) discourages writing the dense logic tests we want most.
- **MCP-driven end-to-end.** Rejected — MCP is for external scripting and AI control. Routing tests through stdio JSON-RPC adds latency, flakiness, and indirection without buying anything tests need.

## Related

- [ADR-0002](0002-routing-tool-draft-state.md) — routing state machine is the primary subject of Msg-dispatch tests
