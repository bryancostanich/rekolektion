# Continuation Prompt — Track 06: Authoring Data Model

You are continuing work on **rekolektion track 06: Authoring Data
Model**. The work introduces a per-label `Kind` (NetName vs
DeviceTerminal) so the viz tool can distinguish signal nets from FET
device-terminal annotations without a name-based blacklist, and so
the `(nets …)` block auto-derives from labels instead of requiring
authors to declare it.

## Context

- **Project root:** `/Users/bryancostanich/git_repos/bryan_costanich/rekolektion`
- **Conductor:** `conductor/projects/production_features/tracks/06_authoring_data_model/`
- **Spec:** `conductor/projects/production_features/tracks/06_authoring_data_model/spec.md`
- **Plan:** `conductor/projects/production_features/tracks/06_authoring_data_model/plan.md`
- **Workflow doc** (read before touching primitives or labels):
  `docs/workflows/rkt_primitive_workflow.md`
- **Decision protocol** rules to follow (already in user memory; the
  short form): stop at every architectural decision point, present
  options as a comparison table, reject hacks, get user approval
  before coding the decision-dependent step.
- **RTL debugging protocol** lives at
  `docs/operational_protocols/RTL_debugging_protocol.md`. This track
  does not edit RTL, but the underlying habit applies: observe data
  first, reason to a conclusion, predict, probe, confirm, then change.

Read `spec.md` and `plan.md` fully before doing anything else. They
carry the design and the step-by-step checklist. This prompt is the
brief; those two files are the contract.

## What to do

Work through `plan.md` step by step. Steps 1, 2, 3, 4, 6, 7 are each
their own commit. Step 5 is the bulk regeneration commit. Step 8 is
verification.

### Decision points

Five design decisions in `spec.md` "Open design decisions" are
deliberately unresolved. Surface them to the user at the latest point
each is needed, **before** writing code that depends on it:

- Decisions 1 (F# representation) and 2 (sexpr syntax) gate step 2.
- Decision 3 (relationship to `IsInternal`) gates step 2.
- Decision 4 (in-memory `doc.Nets` post-refactor) gates step 6.
- Decision 5 (regen verification) gates step 5.

Present each decision as a single comparison table — options as
columns, dimensions as rows — in plain English. No prose-style option
blurbs above the table. One decision per turn; surface the next when
it becomes relevant. Don't bundle.

### Audit before edit

Step 1 is a non-coding audit pass. Complete it in full and report
findings before opening any code editor on this track. Specifically:
list every primitive generator file that emits FET port labels, every
in-process reader of `doc.Nets`, and every name-based filter on label
text. The user needs the surface area visible before approving
representation choices.

### Don't ship halfway

Each commit must leave the build green and tests passing. If a step
requires a coordinated change across F# and Python (label kind
serialization), make both changes in one commit so neither side
parses files the other side wrote. The Plan splits F# (step 2) and
Python (step 3) into separate commits because the file formats are
backward-compatible by spec — F# defaults missing kind to NetName,
Python should match. If implementation reveals incompatibility,
combine the commits rather than landing a broken intermediate state.

### Regeneration commit

When the user approves decision 5 and step 5 begins: run every
primitive generator, verify the diff is tag-only, commit as one
mechanical commit. Commit message should list which primitives were
regenerated. Don't mix this commit with any other change.

### After step 6

After ripping the declared-net gate, build the viz app and open
`cell_designs/khalkulo/cim_reram_drv_phaseA_srcmux.rkt` with ratlines
on. Confirm visually that ratlines appear between matching net labels
and do NOT appear between FET device terminals. Use the
`mcp__rekolektion-viz__rekolektion_viz_get_geometry` and
`mcp__rekolektion-viz__rekolektion_viz_tail_log` MCP tools to confirm
nets are populated and ratlines are emitted. Report what you see to
the user before declaring the step done.

## Constraints

- **No hacks.** No string blacklists for `D`/`G`/`S`/`B`. No
  scope-based filters that infer a label's role from its containing
  cell. The role lives on the label.
- **No backward-compatibility hedges.** Default missing `Kind` to
  `NetName` on read (so legacy files keep working without
  regeneration). Don't add feature flags or legacy modes anywhere.
- **No subagent edits.** Bulk edits across many files happen inline
  in this session.
- **No git push.** Commit locally; ask before push. Push approval is
  per-batch — even if the user previously approved a push, ask again
  for later commits in this run.
- **No "Claude" in commit messages.** Project convention strips the
  attribution.
- **Run targeted tests** for each step's verification rather than the
  full suite.

## Definition of done

- All eight plan steps checked off.
- `grep` confirms no name-based filter on `D`/`G`/`S`/`B` remains in
  `tools/viz/src` or `src/rekolektion`.
- Round-trip tests pass on both F# and Python sides.
- Viz ratline render works on a regenerated `.rkt` with no manual
  intervention.
- Workflow doc updated.
- Decisions log in `plan.md` captures the chosen option for each of
  the five decision points and why.
- User confirms ratlines render correctly on the test file before
  declaring done.
