# Python `.rkt` reader (`rekolektion.io.rkt`)

Status: feature request, pre-decision. Open gap explicitly named in
[`docs/io/rkt.md`](../io/rkt.md) under "Open gaps (v1)":

> The Python writer has no reader yet. Python-side consumers that
> want to read `.rkt` go through the F# reader (or wait for the
> Python reader).

## Why this exists

`src/rekolektion/io/rkt.py` currently ships a **writer** only. Python
generators (`rekolektion.bitcell.*`, `rekolektion.peripherals.*`,
`rekolektion.macro.*`) mint `.rkt` from in-memory dataclasses and
write it to disk. Anyone who needs to **read** `.rkt` from Python —
to verify, to round-trip, to compose two existing files
programmatically — has to either:

1. Shell out to the F# `tools/viz/src/Rekolektion.Viz.Cli/` tool and
   parse its emitted JSON / re-serialized form (slow, brittle).
2. Re-implement parsing inline (already happening in at least one
   place per a quick grep — to be enumerated during P0).

Both are bad. Python is the primary generator language; round-trip
parity with F# is the format's stated goal ("The writer is canonical —
same input always produces the same output bytes. Round-trips through
the F# reader byte-for-byte." — `rkt.md → Python API`). A Python
reader closes the only direction that's currently one-way.

## Goals

- **Symmetric API.** `rkt.read(text) -> Document` mirrors
  `rkt.write(doc) -> str`. Round-trip:
  `rkt.read(rkt.write(doc)) == doc` (AST-equal) for every doc the
  writer produces.
- **F#-byte-for-byte parity on round-trip.** Read a `.rkt` written
  by either Python or F#, then write it back via the Python writer —
  output equals the canonical form the F# `Writer.write` produces on
  the same AST. (Comments preserved; whitespace normalized to
  canonical.)
- **All schema forms covered.** Every form in `rkt.md → Schema
  reference` parses: `(layout … (version …) (pdk …) (units …)
  (top …) (import …) (nets …) (cell …))` plus every element
  (`(poly …)`, `(path …)`, `(rect …)`, `(label …)`, `(port …)`,
  `(sref …)`, `(aref …)`, `(props …)`).
- **Import resolution as opt-in.** `rkt.read(text)` parses a single
  file. `rkt.load(path)` walks `(import …)` references the same way
  F#'s `Reader.loadSingle` does, returning a `Library` object with
  documents + a `cell_index` mapping cell-name → source path.
- **Helpful errors.** Parse failures report line + column. Schema
  failures (e.g., `(version 2)`) report which form failed and what
  was expected.
- **No new runtime deps.** Stdlib only. The S-expression grammar is
  small enough that a hand-written tokenizer + recursive-descent
  parser is the right tool. Mirrors the F# `Reader.fs` discipline.

## Non-goals

- **Editor-style CST round-trip.** F#'s `Reader.fs` exposes a CST
  for whitespace-exact round-tripping in the viz editor. Python
  consumers don't need it (per
  `canonical_layout_format_decisions.md` D5, CST was simplified to
  comment-preservation only). Python reader carries comments as
  fields on each AST node — same model the F# reader switched to
  after D5 — and produces canonical formatting on write.
- **Format extensions.** The Python reader implements the v1 schema
  in `rkt.md`. New forms land in the schema first, then both
  readers.
- **Replacing the F# reader for viz / DRC.** F# stays canonical for
  the viz tool's in-memory model. Python reader is for Python
  generators / scripts / tests.

## API

```python
from rekolektion.io import rkt

# Single-file parse (no imports walked).
doc: rkt.Document = rkt.read(text)
doc = rkt.read_file("bitcell.rkt")

# Multi-file load (imports walked, cycle-checked, cells indexed).
library: rkt.Library = rkt.load("macro.rkt")
assert "bitcell" in library.cell_index
bitcell_doc = library.documents[library.cell_index["bitcell"]]

# Errors are typed.
try:
    rkt.read(broken_text)
except rkt.ParseError as e:
    print(e.line, e.column, e.message)
except rkt.SchemaError as e:
    print(e.form_kind, e.expected, e.got)
```

### Types (additions to existing `rkt.py`)

```python
@dataclass
class Library:
    documents: dict[str, Document]      # path -> Document
    cell_index: dict[str, str]          # cell name -> defining path
    top_cell: str | None                # from root file's (top …)

class ParseError(Exception):
    line: int
    column: int
    message: str

class SchemaError(Exception):
    form_kind: str        # e.g., "port", "layout-header"
    expected: str
    got: str
    line: int | None
```

Symmetric to the F# `Reader.Library` and `Reader.parseFile` /
`Reader.loadSingle` shapes in
`tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs`.

## Acceptance criteria

- **A1.** Round-trip every existing `.rkt` file under
  `cell_designs/` and `tools/viz/testdata/` through
  `rkt.read(rkt.write(rkt.read(text))) == rkt.read(text)`. AST-equal.
- **A2.** Canonical-format match: for every test `.rkt`, writing via
  Python after parse produces byte-equal output to F#
  `Writer.write` on the same AST. Mechanism: a CI test that runs
  both writers on a shared fixture set and diffs the output.
- **A3.** Every schema form has at least one positive test
  (a fixture that exercises it).
- **A4.** Every schema-error path has at least one negative test
  (`(version 99)`, `(port (dir nonsense) …)`, malformed `(units …)`,
  etc.) and produces a typed `SchemaError` with sensible
  `form_kind` / `expected` / `got`.
- **A5.** Import resolution: `rkt.load("macro.rkt")` on a fixture
  with two levels of `(import …)` returns a `Library` whose
  `cell_index` covers every cell across all files, with cycle
  detection on a deliberate-cycle fixture (raises
  `rkt.ImportCycleError`).
- **A6.** Comments survive parse + write on a fixture with leading
  `;` block comments, mid-cell comments before elements, and
  trailing comments. Same comment-preservation contract the F#
  reader / writer satisfies after D5.
- **A7.** Performance: parse the largest checked-in `.rkt`
  (currently the v1 SRAM macro top, ~200 KB) in under 100 ms on the
  dev machine. Not a hard bar; just enforces "the parser doesn't
  pathologically backtrack."

## Implementation phases

### P0 — survey existing inline parsers

Grep for in-place `.rkt` parsing scattered through the Python
codebase. Catalogue and plan replacement during P3.

### P1 — tokenizer + parser → CST-light

- `src/rekolektion/io/rkt_reader.py` (new module, kept private; the
  public surface stays `from rekolektion.io import rkt`).
- Hand-written tokenizer: atoms, strings (double-quoted, escape-aware),
  comments (`; …\n`), parens, whitespace. Mirrors `Reader.fs`.
- Recursive-descent parser producing an internal sexpression tree
  with source positions on every node (lightweight CST for error
  messages, not user-visible).

### P2 — schema analyzer → AST

- Walk the internal tree, dispatch on head symbol, populate the
  existing `Document` / `Cell` / `Port` / `Poly` / `Path` / `Rect` /
  `Label` / `SRef` / `ARef` / `Props` / `Net` / `Import` /
  `LayerRef` dataclasses. Same AST shape the writer already uses.
- Comment attachment: every node carries `comments: list[str]`
  populated from `;`-runs immediately preceding the node.

### P3 — import resolution + `Library`

- `rkt.load(path)`: read root file, walk `(import …)` recursively
  with cycle detection.
- Path resolution relative to the importing file (matches
  `rkt.md → Schema reference → Imports`).
- Replace inline parsers found in P0.

### P4 — round-trip CI

- A1, A2 wired into pytest.
- A7 benchmark gated on the largest checked-in fixture.

## Risks

- **Whitespace divergence with F#.** "Same canonical form" is easy
  to claim and hard to verify. A2 protects against drift by running
  both writers on the same AST in CI; one fixture per element type
  + edge cases (empty cell, single-element cell, deeply nested
  sref). When either writer changes, the diff surfaces immediately.
- **Comment attachment ambiguity.** Trailing comments on a line
  vs leading comments for the next form is a parsing choice. F#'s
  policy (per D5): attach `;` lines from the preceding trivia run
  to the next AST node. Python mirrors exactly; A6 enforces.
- **Layer reference grammar.** `sky130:met1` vs `unknown:94/20` are
  two shapes; the parser must distinguish without ambiguity. Use a
  lookahead-free disambiguator: if the post-colon token contains a
  `/`, it's `unknown:n/d`; otherwise it's `pdk:name`.

## Open questions

- **Should `rkt.load` accept a `pathlib.Path` as well as `str`?**
  Yes by default; trivial. Confirm for stylistic consistency with
  the rest of `rekolektion.io`.
- **Where do the schema-error strings live?** Inline literals in
  `rkt_reader.py` are fine for v1. If they grow, move to a constants
  module. Not a v1 decision.

## Files affected

- `src/rekolektion/io/rkt.py` — extend to re-export `read`,
  `read_file`, `load`, `Library`, `ParseError`, `SchemaError`,
  `ImportCycleError`.
- `src/rekolektion/io/rkt_reader.py` (new) — tokenizer, parser,
  schema analyzer.
- `tests/io/test_rkt_reader.py` (new) — A1–A7.
- `docs/io/rkt.md` — strike the "Python writer has no reader yet"
  bullet from "Open gaps" once P3 lands.
- Anywhere P0 surfaces an inline parser: replace with
  `rkt.read` / `rkt.load`.
