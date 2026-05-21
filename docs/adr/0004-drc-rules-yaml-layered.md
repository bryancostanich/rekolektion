# ADR-0004 — DRC rules: base + override YAML layers, strict by-name merge

**Status:** Accepted — 2026-05-20

## Context

Today DRC rules are F# code in `tools/viz/src/Rekolektion.Viz.Core/Drc/Rules.fs`. The interactive-router spec calls for a default PDK ruleset that users can modify and import/export without rebuilding the editor.

Two viable file-based shapes:

- Single YAML file. Repo ships a default; users fork the whole file to modify it.
- Base + override YAML files. Repo ships read-only base; user overrides live in a separate file that the loader layers on top at startup.

The expected modification pattern is small, targeted overrides ("our tape-out tightens met2 spacing to 0.18 µm"), not wholesale ruleset replacement. Forking the entire PDK ruleset to change one number creates a continuous merge tax when sky130 updates.

## Decision

Ship the default ruleset as `drc/base/<pdk>.yaml` (e.g. `drc/base/sky130.yaml`) inside the repo, treated as read-only from the editor's perspective. User overrides live in a separate file at a user-configurable path (default `drc/overrides/<chip>.yaml`).

Merge semantics at load time:

- **Strict by-name replacement.** If a rule name appears in both files, the override fully replaces the base rule. No partial-field stitching.
- **Override-adds-new.** A rule name only present in the override is added as a new rule.
- **Override-removes** via an explicit `disabled: true` field on a rule (not by absence — absence means "use base").

Each rule's effective source (base or override file) is recorded as provenance. The Inspector panel surfaces this on violation display: `met2.spacing: 0.18 µm (from overrides/v1_tapeout.yaml)`.

Import/export operates on the override file only.

YAML schema includes per-rule `liveEligible` and optional `scopeRadius` from [ADR-0003](0003-live-drc-scope.md).

## Consequences

**Positive**
- PDK ruleset updates flow through automatically; user overrides remain small, intentional diffs
- Sharing a custom ruleset is just the override file; collaborator's own overrides preserved if on different rule names
- Provenance display makes "where did this rule come from" answerable from the UI

**Negative**
- ~550 LOC: YAML schema, F# loader, validator, override merger, provenance tracking, Inspector hook
- Merge bugs can silently change a rule the user thought was preserved — needs targeted unit tests on the merge logic
- Two files to consult when debugging a DRC result, mitigated by provenance display

## Alternatives considered

- **Single YAML file.** Simpler (~400 LOC), one source of truth in the user's hands. Rejected because forking the entire PDK ruleset for a single-rule tweak creates an ongoing merge burden when the PDK updates, and sharing a "rule set" loses the receiver's own customizations.
- **Keep F# code, no external file.** Rejected by the spec's explicit modify/import/export requirement.

## Related

- [ADR-0003](0003-live-drc-scope.md) — `liveEligible` and `scopeRadius` are part of the YAML rule schema
