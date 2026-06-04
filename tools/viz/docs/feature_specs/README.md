# Viz feature specs

Living specs for user-facing viz behaviour.  Each `.md` here is the
contract a feature obeys; code + tests reference it, and changes go
spec-first:

1. Read the spec for the feature you're about to change.
2. Edit the spec to describe the new behaviour.
3. Update code + tests to match.
4. Commit (or PR) all three changes in one revision.

The reason for this dance: viz features keep growing complex
enough that "what should X do?" is no longer obvious from the code
alone — selection rules, V-tool snap priority, guide drag
semantics, etc. each have a half-dozen edge cases the user noticed
once and want preserved.  The spec is the single source of truth
for those edge cases; the code is what implements them; the tests
are what protects them from drift.

## Naming + structure conventions

- One file per user-facing feature: `selection.md`, `via_tool.md`,
  `guides.md`, `rulers.md`, `norm_button.md`, etc.
- Each spec starts with a one-paragraph **Overview** of what the
  feature is and the user gestures that invoke it.
- A **Behaviour** section enumerates rules in numbered list form
  so future "rule 3.2" references are unambiguous.
- Each rule cites the code that implements it via
  `path/to/file.fs:functionName` so an audit can follow
  spec → code → tests.
- An **Open questions** section captures known ambiguities — the
  spec is allowed to admit "we haven't decided this case yet".
- A **Change log** at the bottom tracks every spec edit with a
  date, the commit / PR that bundles the change, and a one-line
  reason.

## Audit cadence

When a user-reported bug touches a spec'd feature, the response
should walk:

1. Quote the relevant rule from the spec.
2. Read the code that claims to implement it.
3. Identify the drift (code says X, spec says Y).
4. Decide: fix the code to match the spec, OR update the spec
   because the rule was wrong.
5. Land both in one commit so the spec never lies about the code.

The goal isn't perfect specs — it's *honest* ones.  A "TODO"
section that says "rule 4 doesn't currently match the code; user
wants the code's behaviour" is better than silently letting the
spec drift.

## Index

- [`selection.md`](selection.md) — wire and knuckle selection
  rules (click semantics, shift modifier, connected-component
  walk).
