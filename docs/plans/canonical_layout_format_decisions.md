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

---
