# Canonical Layout Format — decision log

Per `canonical_layout_format_continuation.md`, every architectural choice made
during autonomous implementation lands here. One entry per decision.

Conventions:
- Date is the day the decision was made.
- Options enumerated as real alternatives (no strawmen).
- Comparison rendered as a single table: options across columns, dimensions
  down rows. Symmetric: every dimension applies to every option.
- Hacks flagged explicitly. Counter-argument given for every non-chosen
  option.
- Files affected listed once the decision becomes code.

---

## D1 — CST/AST type relationship — 2026-05-13

> **Superseded by D5 (2026-05-13 same-day).** The split was undone once
> whitespace preservation came off the goal list. Reading from this
> entry forward is fine for context but the conclusion no longer
> holds — see D5.



**Decision point.** How does the in-memory representation split between a
trivia-preserving concrete tree (CST) and a clean semantic tree (AST)?
Affects every downstream consumer (Reader, Writer, viz tool migration,
Python writer's mirror).

**Options.**

| Dimension | A: generic Sexp + projections | B: single typed tree with trivia | **C (chosen): distinct typed Cst + Ast** |
|---|---|---|---|
| Shape | `Sexp = Atom of string \| List of Sexp list`. Trivia attached. Domain types are functions `Sexp -> Cell list`. | One typed tree per element (`Poly`, `Path`, `Port`, ...) with required `Trivia` fields. Consumers ignore trivia. | Two trees: `Cst` is typed S-expression nodes carrying trivia; `Ast` is domain-typed (Document, Cell, Element, Port, Net, Layer) with no trivia. |
| LoC | Smallest. Single tree definition + lookup helpers. | Medium. Each element type holds trivia fields. | Largest. Two type hierarchies. ~2× type definitions. |
| Consumer ergonomics | Stringly-typed: consumers pattern-match on head symbol. Misspellings (`(poyly ...)`) are runtime errors. | Good. Typed access, but every consumer threads trivia fields through pattern matches. | Best. Consumers operate on `Ast.Document`. The viz tool, sidecar loaders, DRC, ratlines — all see clean types. CST is only touched at the I/O boundary. |
| Round-trip fidelity | Native. The Sexp tree IS what was parsed. | Native. Trivia round-trips with each node. | Native. CST stores the original tokens; Writer renders CST byte-exact. Edit-and-save synthesizes new CST subtrees from AST. |
| Edit semantics | Edits walk the Sexp tree; helpers re-tree. Footgun: easy to drop a comment by replacing a List wholesale. | Edits replace nodes; trivia comes along. Risk: stale trivia ("// width 100" left next to a `width 200` node) silently misleads. | Edits go to AST; synthesizer turns them into canonical CST. Unedited subtrees keep their CST origin verbatim. Stale-comment risk is constrained to the synthesis boundary. |
| Type safety | Weakest. All structural invariants are runtime. | Strong. Type invariants enforced at parse. | Strongest. Same as B for parsed nodes, plus a clear separation between "what's on disk" (CST) and "what consumers manipulate" (AST). |
| Maintenance over time | Bad. Adding a new element type means hunting through every consumer's pattern match. | OK. New element type = one type definition + each consumer adds a case. | OK. Same as B at AST level. CST is generic enough to absorb additive grammar changes without recompilation pressure on consumers. |
| Hack? | No, but loses type-safety value F# provides. Picking it would be choosing convenience over the language's strengths — a hack-shaped tradeoff. | No. | No. |

**Counter-argument for A.** Sexp + projections is canonical Lisp practice and
works fine when the consumer count is small. Truthfully: this is a
viz-tool-plus-generator-plus-future-consumers project. The consumer count is
not small. The CST-only world saves ~150 LoC of type definitions; over the
project's life that's a rounding error against the bugs F#'s type system
will catch in Ast-shaped consumer code.

**Counter-argument for B.** Single typed tree is a real middle ground and
many compiler frontends do exactly this (Roslyn green/red trees are
typed-with-trivia). The case against: it forces every consumer to look at a
type that mixes concerns (`Port` is "what a port is" AND "where the
whitespace around it lived"). Separation lets consumers operate on the
domain meaning without touching layout artifacts.

**Chosen.** **C.** Distinct typed `Cst` (low-level, typed S-expressions with
trivia + source positions) and `Ast` (domain types). Reader emits both.
Writer renders CST byte-exact for round-trip; synthesizes CST from AST for
edits and from-scratch generation.

**Files affected (planned).**
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Types.fs` — AST.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Cst.fs` — CST.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs` — `parse : string -> Cst.Document`, `analyze : Cst.Document -> Ast.Document`.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Writer.fs` — `renderCst : Cst.Document -> string`, `synthesize : Ast.Document -> Cst.Document`.

---

## D2 — Trivia attachment model — 2026-05-13

> **Superseded by D5 (2026-05-13 same-day).** "Trivia" as a concept
> (whitespace + comments together, attached to CST nodes) is no longer
> the model. Comments live on AST nodes; whitespace is canonicalised by
> the writer. See D5.



**Decision point.** Where does whitespace and comment text attach in the
CST? Determines whether round-trip is byte-exact and how edits behave.

**Options.**

| Dimension | A (chosen): leading-only per node | B: leading + trailing per node | C: interleaved trivia siblings |
|---|---|---|---|
| Shape | Every CST node carries `Leading: string` (verbatim whitespace + comments before the node's first token). Trailing whitespace is implicitly the next node's leading. | Every node carries `Leading: string` and `Trailing: string` (newline + same-line trailing comment). | Trivia is a first-class sibling: `List of NodeOrTrivia[]`. |
| Byte-exact round-trip on untouched input | Yes. Reader captures everything between significant tokens as leading trivia of the next token; writer emits node-by-node and the original byte sequence is reproduced. | Yes. More state to keep consistent. | Yes. |
| Behaviour when a node is deleted | The comment "explaining" the node is deleted with it (intuitive: the comment belonged to that node). | Same as A for leading; trailing trivia of the previous sibling survives correctly. | The deleted node's adjacent trivia must be merged with neighbors — non-trivial bookkeeping. |
| Behaviour when a node is inserted | New node attaches at the chosen position; surrounding trivia is unchanged. Insertion point dictates whether the new node lands before or after a trailing comment. | Same as A. The "trailing" attachment makes "comment on same line" stay glued correctly. | Insertion requires explicit decision: which adjacent trivia keeps which side. |
| Comment-on-end-of-line semantics | `(port ... ) ; comment` ends up as leading trivia of *the next node* — surprising; the comment "looks like" it belongs to the port. | `; comment` correctly attaches as trailing trivia of the port. | Comment is a sibling between the port and the next node; consumers can ask "what trivia follows this node?". |
| LoC | Smallest (~80). | Medium (~120). | Largest (~180). |
| Risk of accidental comment loss | Low for deletes; medium for "move node" (we move leading trivia with it, possibly the wrong comment). | Lowest. Trailing comments stick to their node. | Lowest, but consumers have to learn the model. |
| Hack? | No. | No. | No. |

**Counter-argument for B.** End-of-line comments are common in S-expression
code (`(port ...) ; main BL`) and option A glues that comment to the
following sibling, which is semantically wrong. If anyone hand-writes
`.rkt` files (or AI does), this misattribution will be visible. Real cost.

**Counter-argument for C.** The interleaved model is what `gleam_syntax`,
`rust-analyzer`, and tree-sitter do. It's the right shape for editors that
have to keep cursor positions stable through trivia. We're not building an
editor with that level of trivia-aware cursor logic at v1; the complexity
buys little here.

**Chosen.** **A — leading-only.** The end-of-line attribution issue (B's
strongest argument) is mitigated by a rule the writer enforces: **a comment
attached to the leading trivia of node N that begins on the same physical
line as the closing token of node N−1 is emitted *before* the newline,
producing a trailing-comment appearance.** This handles the common case
without doubling the trivia surface area. If real-world authoring shows
this rule is wrong, we revisit (none of this is locked).

Files affected: `Rkt/Cst.fs`, `Rkt/Reader.fs` (lexer captures leading
trivia per token), `Rkt/Writer.fs` (line-continuation emit rule).

---

## D3 — Import resolution architecture — 2026-05-13

**Decision point.** When and how does `(import "path.rkt")` get resolved?
Affects error attribution, testability, and cycle handling.

**Options.**

| Dimension | A: parse-time inclusion | **B (chosen): two-pass (parse, then resolve)** | C: lazy resolution at consumer query |
|---|---|---|---|
| Shape | Reader sees `(import ...)`, reads the file, splices its cells into the parent CST. | Reader parses each file independently into `Document { Imports; Cells; ... }`. A separate `Library.load : path -> Result<Library, Error>` walks imports, parses each, assembles a `Library` containing all loaded documents and a flat cell lookup. | Documents store import references unresolved; consumers asking for a cell by name resolve on demand. |
| Error attribution | Worst. Parse error in imported file shows up "during parse" of the importing file. Confusing stack traces. | Best. Each file's parse errors carry that file's path. | OK; resolution errors surface lazily and can be confusing. |
| Cycle detection | Must happen during parsing; tangles with file I/O. | Clean visited-set walk over a tree of parsed Documents. | Lazy = cycle observable only at first traversal; risk of "looks fine until you click cell X". |
| Testability | Hard: every parser test that touches imports needs file I/O. | Clean. `parse : string -> Cst` is pure (no file I/O). Resolver tests use a tiny in-memory file-system stub. | Lazy = state matters; testing intermediate states is painful. |
| Reverse-mapping (cell X came from file Y) | Lost; imports are inlined. | Preserved on every cell. | Preserved. |
| Save behaviour | Save would write the inlined form, losing source structure — direct violation of the design doc's "preserve what you opened". | Save writes each Document to its origin path; no inlining. | Save needs to walk a lazily-resolved graph — fiddly. |
| LoC | Smallest. | Medium. | Largest. |
| Hack? | Conflicts with the design doc's editor behavior contract. Picking A would force a workaround for Save. Hack-adjacent. | No. | No. |

**Counter-argument for A.** Inline-on-parse is the simplest from a parser
standpoint; some Lisp implementations do it. But the design doc explicitly
requires the editor to "preserve the source structure on Save," and
inline-on-parse loses that structure outright. Picking A would force the
parser-resolver split to live on the *write* side instead, which is worse.

**Counter-argument for C.** Lazy resolution suits a project where most
documents have a small fraction of their cells touched, and import graphs
are deep. Our typical case is a viz session that walks the whole hierarchy
on open (to render). Eager resolution is the matching shape.

**Chosen.** **B — two-pass.** Pure parser; separate `Library.load` resolves
imports with cycle detection and produces a `Library = { Roots; Documents;
CellIndex }`. Each loaded Document keeps its source path. Editor Save
writes each Document back to its source path.

Files affected: `Rkt/Reader.fs` (parse one file only — no I/O recursion),
new `Rkt/Library.fs` or `Rkt/Imports.fs` (resolver + cycle detection).
Decision on a separate file vs folding into Reader is deferred to
implementation; if Reader stays small the resolver may live there.

**Implementation note (post hoc).** Resolver landed in `Rkt/Reader.fs`
alongside `parse` / `analyze`. A separate `Library.fs` was considered
but Reader is still small (~600 LoC) and the resolver belongs with the
file-load surface. Promote to its own module when other consumers want
the `Library` type without pulling in lexer internals.

---

## D4 — In-memory model migration: in-place vs adapter — 2026-05-13

**Decision point.** The viz tool currently uses `Gds.Types.Library`
(structures of `Boundary | Path | SRef | ARef | Text`, layer = int/int
pair) as the canonical in-memory model. Step 2 of the .rkt rollout
calls for the in-memory model to evolve to the new schema. Two paths.

**Surveyed scope.** Consumers of `Gds.Types.Library` in the current
tree (post-step-1 commit `e69a354`):

- `Layout/Layer.fs`, `Layout/Hierarchy.fs`, `Layout/Picking.fs`,
  `Layout/Marquee.fs`, `Layout/Flatten.fs`, `Layout/Snap.fs`,
  `Layout/Instances.fs`, `Layout/LayerAlias.fs`,
  `Layout/MagToLayout.fs`, `Layout/LayoutLoader.fs`
- `Drc/Rules.fs`, `Drc/Check.fs`
- `Net/LabelFlood.fs`, `Net/Ratlines.fs`
- `Gds/Reader.fs`, `Gds/Writer.fs` (encoder; legitimate consumer)
- `App/*`, `Cli/Program.fs`, `Mcp/*`, `Render/*` (downstream
  projects in `tools/viz/src/`)
- Tests for every Core consumer above

Rough size estimate: ~1500–3000 lines of consumer code touching the
type. Mechanical changes per consumer:
- `Structures` → `Cells`
- `Boundary { Layer; DataType; Points }` →
  `PolyEl { Layer = Named(pdk, name); Points; ... }`
- `SRef { StructureName; Origin; ... }` →
  `SRefEl { Cell; Origin; Rot; Mag; Reflect; ... }`
- Layer comparison `(Layer, DataType) = (n, d)` →
  `Layer = Named(pdk, name)` plus a layer-name table lookup.

**Options.**

| Dimension | A (chosen): in-place evolution, multi-commit | B: parallel type with adapters |
|---|---|---|
| End state | One canonical in-memory type (`Rkt.Types.Document`). `Gds.Types.Library` retired or reduced to encoder-internal use only. | Two parallel models maintained side-by-side: `Gds.Types.Library` for legacy consumers, `Rkt.Types.Document` for new code. Bidirectional adapters bridge them. |
| Atomicity | Cannot land in a single commit without breaking everything mid-refactor. Plan splits the work into checkpoints: (2a) adapter Gds→Rkt + tests, (2b) adapter Rkt→Gds + tests, (2c)…(2N) consumers migrate one or two at a time, each leaves build+tests green, last commit retires `Gds.Types.Library` (or contracts it to encoder scope). | Lands incrementally by design — each adapter or consumer is its own diff. |
| Verifiability per commit | Each checkpoint is independently testable. Migrating Hierarchy (85 LoC) is one commit with one test-pass run. | Same. |
| Risk of "works because we got lucky" | Low. Each consumer migration is small enough to reason about end-to-end before commit. Tests adapt alongside. | Medium. Two models drift over time. Bugs at the boundary (adapter info-loss) are easy to miss. |
| Maintenance over project life | Lowest. One model. New element shapes land in one place. | Highest. Every schema change touches both models + the adapter. Adapter loss-of-information accumulates (port flags, nets, named layers have no GDS equivalent; round-tripping silently strips them). |
| Information fidelity | Native. Rkt is a superset of GDS semantics. | Lossy at the adapter boundary unless we synthesize sidecar storage for the surplus (port flags, nets, etc.) — and that's a new mini-model to maintain. |
| Effort to reach end state | High up front (one big refactor distributed across N small commits). | Lower up front (just write the adapters); but the consumer migration still needs to happen eventually — option B defers it, doesn't avoid it. |
| Hack? | No. | No, but if Phase B (consumer migration) never lands the project ships with two models indefinitely. That outcome wasn't the design intent — picking B with the unspoken assumption that "we'll migrate later" is the soft version of a hack. |

**Counter-argument for B.** The adapter approach lowers the immediate
risk surface: most of the codebase keeps using a battle-tested type,
and the new code paths land behind a translation layer. For a project
with many concurrent contributors or a frozen API surface, that's the
right move. The case against, for *this* repo: solo development,
no external API depending on `Gds.Types.Library`, and the design doc's
own implementation note ("preferred") signals the author wants the
single-model end state. The "we'll migrate later" outcome is the
project's actual risk — easy to start the adapter, hard to retire the
legacy type.

**Chosen.** **A — in-place evolution, multi-commit.** Step 2 is the
*foundation* of the migration, not the entirety. The work plan:

1. **Stage 2a (this session, or next).** Add adapter
   `Rkt.OfGds.fromLibrary : Gds.Types.Library -> Rkt.Types.Document`
   plus tests. Lives at `Rkt/OfGds.fs` (peer of `Reader.fs`,
   `Writer.fs`). No consumer changes; library wires in step 3 when
   the GDS reader is taught to emit Rkt directly.
2. **Stage 2b.** Add adapter
   `Rkt.ToGds.toLibrary : Rkt.Types.Document -> Gds.Types.Library`
   plus tests. Information-loss documented (named layers degrade to
   `Unknown.Number/Datatype` on export when no PDK map entry exists;
   port flags ride along as `(port_flags …)` text labels for
   round-trip recovery on re-import).
3. **Stages 2c…2N.** Migrate consumers one or two per commit.
   Recommended order, smallest first: `Hierarchy` → `Marquee` →
   `Snap` → `LayerAlias` → `Instances` → `Picking` → `Flatten` →
   `Net/LabelFlood` → `Net/Ratlines` → `Drc/Rules` → `Drc/Check`.
   `Layout/MagToLayout` and `Layout/LayoutLoader` come last (they're
   the load-time boundary; touching them retires the adapter).
   Downstream projects (`Render`, `App`, `Cli`, `Mcp`) migrate when
   the Core layer is fully on Rkt.

**Stop discipline.** Each stage commits independently with green
build + tests. If any stage stalls (test won't pass cleanly, a
consumer's logic doesn't map onto Rkt without an unresolved design
question), stop per the continuation prompt's stop condition #3 and
log the blocker.

**Files affected (stage 2a planned).**
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/OfGds.fs` (new).
- `tools/viz/src/Rekolektion.Viz.Core/Rekolektion.Viz.Core.fsproj`
  (compile entry).
- `tools/viz/tests/Rekolektion.Viz.Core.Tests/RktOfGdsTests.fs` (new).
- Test project's `.fsproj` to register the new test file.

---

## D5 — Collapse Cst into Ast; comments first-class on AST — 2026-05-13

**Decision point.** D1 picked a distinct CST (with trivia, for byte-
exact round-trip) and AST (clean semantic types). D2 picked leading-
only trivia attachment on the CST. Both decisions assumed whitespace
preservation was a goal. User feedback on the same day: **whitespace
preservation is not a goal; comment preservation is.** That reframes
the split's value.

**Re-evaluation.**

| Dimension | A: keep split (D1 + D2 status quo) | **B (chosen): collapse — single typed AST with comments per node** |
|---|---|---|
| Whitespace preservation | Byte-exact for unedited files; canonicalised for edited subtrees. | Always canonicalised by the writer. Not a goal, so not a loss. |
| Comment preservation | Round-trip-only when unedited. **Edits to a node lose comments attached to that node.** Step 6 (viz Save path) would need a CST-origin-on-AST-node bookkeeping layer to fix. | Native. Comments live on the AST node. Mutating other fields keeps comments along for the ride. |
| Type count | Two type hierarchies (Cst + Ast). Reader produces both, writer consumes both. | One type hierarchy (Ast). Reader produces it directly. Writer renders it. |
| Reader complexity | Lex → CST → analyze → AST. CST is a separate generic S-expression tree, analyze pattern-matches on head symbols. | Lex → AST (parser is the analyzer, dispatching on head symbol while reading). The Sexp-shaped intermediate can stay as a private parser scaffold, but it does not surface in the public API. |
| Writer complexity | `renderCst` (for byte-exact unedited case) AND `synthesize` (for from-AST case). Two paths. | One path: synthesize-from-AST emits canonical whitespace + inline comments. |
| Identity for "is this the same comment after editing X?" | Implicit via CST node identity. Edit-time bookkeeping required. | Trivial — comment is a field on the AST node. Touching another field doesn't disturb it. |
| AI-authored layout traceability | Same as comment preservation: only when not edited. | Native: the AST a model generates carries its reasoning comments through every subsequent edit. |
| Footprint hit on existing work | Reader + Writer + tests rewrite. Done before any Core consumer migration has begun, so blast radius is contained to Rkt module + its tests. | Same scope, just touching different code paths. |
| Hack? | No, but the design choice no longer matches the stated goal. Keeping it would be inertia. | No. |

**Counter-argument for A.** Keeping the split preserves cleaner
testing of the parser independent of the analyzer (CST tests, AST
tests separately). Real but small benefit; the parser is small enough
that parser-level tests can target the AST output directly without
losing diagnostic value.

**Chosen.** **B.** Collapse the split. Comments move to first-class
AST fields. CST disappears as a public concept; its Sexp-shaped data
may remain as a parser-internal scaffold inside `Reader.fs`, but it
is no longer exposed in `Types.fs` or referenced by tests / writer.

**Where comments attach.** Each major form gets a `Comments: string
list` field. "Major form" means anything that can have a comment line
preceding it in source:

- `Document.HeaderComments` — comments before `(layout ...)`.
- `Cell.Comments`, `Net.Comments`, `Import.Comments` — comments
  preceding the form within `(layout ...)`.
- Each element record (`Poly`, `Path`, `Rect`, `Port`, `Label`,
  `SRef`, `ARef`, plus the `PropsEl` wrapper) — comments preceding
  the element within its parent cell.

Comments inside an element form (between sub-forms like `(layer ...)`
and `(points ...)`) are dropped at v1. They're rare; supporting them
would require a per-subfield comments field on every element record,
which is not worth the LoC for the case it covers. If real-world
authoring produces interior comments often, revisit.

**Comment storage format.** Each comment is stored without the
leading `;` and without the trailing newline. Multi-line comment
blocks become multiple entries in the list, one per `;` line. The
writer emits each entry as `; <text>` on its own line.

**Implications for previous decisions.**
- D1 (distinct types) is superseded — annotation added at its head.
- D2 (leading-only trivia) is superseded — annotation added at its head.
- D3 (two-pass import resolution) stands; the resolver doesn't care
  about the CST/AST split.
- D4 (in-place evolution staged) stands; the per-stage targets just
  use the simpler model.

**Files affected (this stage).**
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Types.fs` — add `Comments`
  to Document/Cell/Net/Import + every element record.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs` — drop the
  public CST surface; merge Cst's Sexp into Reader as a private
  scaffold; analyze populates Comments by extracting `;` lines from
  the preceding trivia run.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Writer.fs` — drop
  `renderCst`. `write` synthesises canonical formatting and emits
  comments inline.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Cst.fs` — removed from
  fsproj (file may stay on disk as a no-op until cleanup).
- `tools/viz/tests/Rekolektion.Viz.Core.Tests/RktTests.fs` — replace
  byte-exact whitespace tests with comment-preservation tests; keep
  AST and import tests.

---
