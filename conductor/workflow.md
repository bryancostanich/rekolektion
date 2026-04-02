# Development Workflow

## Design Flow
For each new feature or generator component:

1. **Spec** — define the circuit, interface, and expected behavior
2. **Implement generator** — Python code that produces GDS geometry + output artifacts
3. **DRC** — run Magic batch DRC on generated layout
4. **SPICE verify** — characterize across PVT corners where applicable
5. **Integration test** — generate a macro with the feature enabled, verify all outputs

## Task Lifecycle
Tasks in plan.md use checkbox status:
- `[ ]` — Pending
- `[~]` — In progress
- `[x]` — Complete

## Commit Strategy
- One logical change per commit
- Commit message format: `<type>: <description>`
- Types: `gen` (generator), `periph` (peripherals), `verify` (verification), `doc`, `infra`

## Verification Protocol
Before marking a feature complete:
1. DRC clean on generated layout (Magic)
2. SPICE characterization where applicable
3. Macro generation with feature enabled produces all 6 output files
4. OpenLane integration test (synthesis through routing)
