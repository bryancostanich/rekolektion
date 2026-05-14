# Canonical Layout Format — autonomous continuation

Written 2026-05-12. Picks up after the design doc was approved.

## tl;dr

Build the `.rkt` canonical layout format from scratch, end to end:
F# parser/writer in the viz tool, in-memory model migration,
GDS/.mag importers, Python writer for the generator, and viz Save
wired through the new path. Decisions for the four open questions
are already made in the design doc; everything else gets decided per
the design-decision protocol.

## Scope contract

**There is no deadline. There is no "done by morning" expectation.**

The unit of success is a **clean step**, not a count of steps. One
correctly-finished step is worth more than three rushed steps. If
you finish step 1 and step 2 cleanly, that's success. If you finish
all six cleanly, also success. If you get partway through step 1 and
hit a real decision that needs human input, **that's success too** —
you stopped before making a wrong call.

What "clean step" means, concretely:
- Tests written and passing for the new code.
- Design-decision protocol followed for every architectural choice
  (logged in the decisions file).
- Build is green at commit time.
- No TODO / FIXME / "fix later" comments in landed code unless they
  represent a genuinely deferred decision tracked in the log.
- No code paths that "work because we got lucky." Every assumption
  documented or replaced with a check.

The user explicitly does NOT want you to push past step N because
"step N+1 should fit in the remaining time." Time is not a factor.
Quality is the factor.

If you catch yourself thinking "I could rush this and finish step 4"
— stop, do step 3 properly, commit, and start step 4 fresh.

## Read these first (in order)

1. `docs/plans/canonical_layout_format.md` — the design doc.
   Approved 2026-05-12. The four open questions at the bottom are
   all resolved (struck-through, with the decision noted inline).
2. `/Users/bryancostanich/Git_Repos/bryan_costanich/khalkulo/workflow/design_decisions.md`
   — the **design-decision protocol**. Mandatory for every
   architectural choice. Not optional.
3. `CLAUDE.md` (this repo) — Magic-outside-Magic fragility traps,
   port-promotion bugs that motivate this format.
4. `~/.claude/CLAUDE.md` — global rules. Never `git push` without
   approval. No "Claude" in commits.

## What's already decided

From `canonical_layout_format.md`:

- **Format**: S-expressions, `.rkt` extension.
- **Layer references**: PDK-qualified names (`sky130:met1`); unknowns
  become `unknown:N/D`, kept visible.
- **Nets**: top-level `(nets ...)` block declares properties
  (domain, voltage, class); element-level `(net BL)` is
  membership-by-name. Undeclared nets default to `signal` domain.
- **Port flags**: enum source of truth
  (`signal | power | ground | clock | analog | scan` ×
  `input | output | inout | unspecified`). Magic flagstring preserved
  as `(props (magic_flags "..."))` for round-trip fidelity; enum
  wins on conflict, magic_flags passes through only when enum is
  `unspecified` AND the property is present.
- **Comments**: full preservation (CST-based parser). Trivia attached
  to adjacent nodes; writer walks the CST so untouched regions
  round-trip byte-exact. Rationale: comments are the provenance
  channel for intent, including AI-generated reasoning traces.
  Stripping them breaks the channel.
- **File composition**: multi-cell per file AND imports both
  supported. Editor preserves source structure on Save; flatten-to-
  inline is an explicit Save As / Export option, not the default.
- **Import path resolution**: relative to the importing file, full
  stop. No search paths, no env vars, no PDK-rooted lookups at v1.
- **Coordinates**: integer DBU only. No floats at the storage layer.
- **Versioning**: `(version N)` required from v1. Additive changes
  stay at v1; breaking changes require a major bump and migration
  code.

## Implementation order

Strictly sequential — each step depends on the previous. See the
"Implementation" section of `canonical_layout_format.md` for detail.

1. **F# parser/writer** in `tools/viz/src/Rekolektion.Viz.Core/Rkt/`
   (`Types.fs`, `Cst.fs`, `Reader.fs`, `Writer.fs`). CST carries
   trivia. AST derived by trivia-stripping walk. Import resolution
   with cycle detection.
2. **In-memory model migration** in the viz tool. `Library` type
   evolves to the new schema (preferred over a parallel type — make
   the case in your decision log if you go parallel).
3. **GDS import wired through Rkt.** Existing GDS loader produces an
   in-memory Rkt document; viz tool operates on that. The
   `(94, 20) → unknown:94/20` fallback is part of this step.
4. **.mag import wired through Rkt.** Magic layer names → PDK
   names; flagstring decoded into enum + preserved as property.
5. **Python writer** in `src/rekolektion/io/rkt.py`. Mirror the F#
   writer's structure. Tests round-trip generated `.rkt` through
   the F# reader.
6. **viz Save path** writes `.rkt` for `.rkt`-origin files. `.gds`
   and `.mag` origins keep their existing writers (no force-migrate
   on save).

**Tests are not optional.** Every step ships with:
- Unit tests on the new code.
- Round-trip property tests where applicable
  (`gds → rkt → gds`, `mag → rkt → mag`, `rkt → rkt`).
- For round-trip: byte-exact for the canonical-only case; semantic
  equivalence (modulo non-meaningful reordering) for cross-format.

## Decision protocol — verbatim from the user

Continue on autonomously. At every decision point, use the full
`@design_decision` matrix. Make the right decision based on
cleanliness and correctness. Rule out hacks. **DO NOT BIAS TOWARDS
SIMPLE OR CLEVER FOR THE SAKE OF GETTING IT DONE SOONER. THAT WILL
BITE US IN THE ASS LATER. TAKE PRIDE IN YOUR WORK.** Track all
design decisions and we'll review them when I'm back.

## Decision log

Append every architectural decision to
`docs/plans/canonical_layout_format_decisions.md` as you make it.
One entry per decision, with:

- Date + timestamp.
- The decision point (one sentence).
- Options enumerated (real, not strawmen).
- Cost/risk/reward/side-effects per option, symmetric.
- Hacks explicitly flagged.
- Counter-argument for each non-chosen option.
- The chosen option + why.
- File paths affected, if known at decision time.

If a decision conflicts with anything in `canonical_layout_format.md`,
**stop and flag it**, don't override the doc silently.

## Commit + push policy

- Commit locally after each working slice (one step from the
  implementation order = one commit, ideally).
- **Never push.** User will review and push when back.
- Commit messages: factual, scoped. No "Claude" anywhere in
  trailers or body. Standard project convention.
- Each commit should leave the build + tests green. If a step is
  too large to land atomically, split it; don't land broken code.

## Working state at hand-off

- Branch: `main`. Last commit: `68d34a4` (viz-v2 polygon
  multi-select + drag, recent files, GDS writer, ratlines, dotted
  bboxes). Working tree should be clean at thread start; verify
  with `git status` before any new work.
- Core tests: 81/81 passing.
- App tests: 21/21 passing.
- Render tests: 9/9 passing (last verified).
- Viz tool builds clean. Run `dotnet build tools/viz/src/Rekolektion.Viz.App/Rekolektion.Viz.App.fsproj`
  to confirm at thread start.

## Out of scope for the autonomous thread

- Directory layout / migration sequencing for existing
  `cell_designs/` files. Consumer-project decision, not part of the
  format spec.
- Verify-flow adapter rewrites (`src/rekolektion/verify/*.py`). One
  of the implementation order's later items mentions them; defer
  unless the F#/Python core is fully landed and there's spare time.
- Any change to non-`.rkt`-related parts of the viz tool. If you
  find a bug in something unrelated, write it up in the decision
  log and keep moving.

## When to stop

Stop conditions, in order of importance:

1. **You're about to cut a corner.** If the cleanest fix takes
   longer than the hacky fix, take longer. If "longer" means
   stopping mid-step and handing it back to the user, do that.
2. **A decision the protocol can't resolve.** Genuine ambiguity in
   the spec, a conflict with the existing codebase, evidence one
   of the four "already decided" answers is wrong. Stop and write
   it up. Don't unilaterally re-litigate format decisions
   overnight.
3. **Tests can't be made to pass cleanly.** Stop. Don't ship a
   fudge. Don't `[<Fact(Skip = "TODO")>]`. Don't comment out a
   test that's catching a real bug.
4. **You finish a step.** Commit, write up the decisions made
   during that step in the log, and start the next step fresh —
   don't carry rushed momentum forward.
5. **You finish every step in the implementation order.** Summary
   in the log, final commit, stop. User will review.

What is NOT a stop condition:
- Running out of "time" (there is no time budget).
- Sensing the user is waiting (they're asleep; they'll review when
  ready).
- Reaching some imagined "good enough" finish line short of
  step 6 (don't pre-stop; keep going as long as the work stays
  clean).

### Anti-pressure protocol

Periodically — at minimum at every step boundary, ideally every
significant decision — re-read the **Scope contract** above. The
purpose is to catch yourself if you've drifted into rushing. If
you read it and find you're already cutting corners or have
shipped something half-baked, **revert and redo it properly**.
That's not failure — that's the protocol working.

If you ever find yourself writing a comment like "TODO: properly
handle X" or "this is a temporary shortcut", **stop the commit**,
go back, and do it right. The continuation prompt's whole purpose
is to remove the pressure that produces those comments.
