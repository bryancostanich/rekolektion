# Preserve comments inside elements

Status: feature request, pre-decision. Open gap explicitly named in
[`docs/io/rkt.md`](../io/rkt.md) under "Open gaps (v1)":

> Comments inside an element (between sub-forms like `(layer ...)`
> and `(points ...)`) are dropped on parse. Comments before the
> outer form survive.

## Why this exists

`canonical_layout_format_decisions.md` D5 settled the
comment-preservation policy: comments are the **provenance channel
for intent** — DRC waivers, design rationale, AI reasoning traces.
Stripping comments on save erases that channel.

D5 implemented this at the **between-form** level: any `; …` run
preceding a top-level form (a `(cell …)`, an element inside a cell,
a header form) is attached to that form's AST node as a
`comments: string list` field, and re-emitted by the canonical
writer.

What D5 did **not** implement: comments **inside** an element,
between its sub-forms. Example:

```scheme
(poly
  ; bitline strap, drawn deliberately wide to match the bitcell pitch
  (layer sky130:met1)
  ; provenance: cell_designs/lr/bitline.rkt:42
  (points (0 0) (100 0) (100 50) (0 50))
  ; matched to BL_SHIELD per task #87
  (net BL))
```

The three `; …` lines are between the `(layer …)`, `(points …)`,
and `(net …)` sub-forms of the `(poly …)` element. Today the
reader drops all three on parse — they vanish from the AST,
disappear on the next write.

This is the place where comments matter most: a polygon's geometry
isn't self-documenting, and the reason a coordinate is what it is
often lives in a comment *right next to that coordinate*. Losing
those on every save is a regression against D5's stated goal.

## Goals

- **Sub-form comments survive parse and re-emit.** Comments between
  sub-forms of an element attach to the *following* sub-form
  (matching D5's convention for top-level comments) and re-emit
  inline in canonical formatting.
- **No new schema.** This is a Reader / Writer policy change, not a
  format extension. The `.rkt` grammar already permits comments
  anywhere — the parser already tokenizes them — they're just
  dropped at the schema-analyzer step.
- **Round-trip byte-equality on canonical inputs.** A `.rkt` file
  whose formatting already matches the canonical writer's output,
  with inside-element comments, round-trips byte-for-byte.
- **Symmetric in F# and Python.** Both readers carry the same
  attachment model; both writers re-emit in the same place.

## Non-goals

- **End-of-line trailing comments.** D5 deliberately excluded
  trailing `(foo bar) ; trailing` from the model. This plan keeps
  that exclusion; only leading `;` runs preceding a sub-form
  attach. Out-of-line is the convention; converting trailing
  comments to leading on write is acceptable (and the canonical
  formatter does this for top-level comments already).
- **Comments inside `(points …)` between point pairs.** A polygon
  point list is its own micro-grammar; adding comments mid-list is
  syntactically possible but practically rare. Punt to v2 if a
  consumer ever asks for it.
- **Comments inside string literals.** Strings don't get parsed
  for `;` (already the case). Not changing.

## Schema reference (informative)

Where comments can attach after this change, illustrated by example:

```scheme
; before the outer form  ─── already preserved (D5)
(poly
  ; before (layer …)     ─── NEW: preserved by this plan
  (layer sky130:met1)
  ; before (points …)    ─── NEW: preserved by this plan
  (points (0 0) (100 0) (100 50) (0 50))
  ; before (net …)       ─── NEW: preserved by this plan
  (net BL))
; trailing  ─── attaches to NEXT form (D5)
```

Attachment model: comments attach to the *immediately following*
sub-form. If the comment run is at the *end* of the element (after
the last sub-form, before the closing paren), it attaches to a
synthetic "tail" slot on the element so it can be re-emitted in
place rather than collapsing onto the next outer form.

## AST representation

Each element record gains a `SubFormComments: Map<SubFormKey, string list>`
field. `SubFormKey` is the symbol name of the sub-form (e.g.,
`"layer"`, `"points"`, `"net"`, plus a sentinel `"<tail>"`).

For F#:

```fsharp
// before
type Poly = {
    Layer: LayerRef
    Points: (int * int) list
    Net: string option
    Comments: string list      // D5 — leading comments on (poly …)
    // …
}

// after
type Poly = {
    Layer: LayerRef
    Points: (int * int) list
    Net: string option
    Comments: string list                          // D5
    SubFormComments: Map<string, string list>      // new
    // …
}
```

For Python: same shape, `subform_comments: dict[str, list[str]]`.

Rationale for the keyed-by-symbol shape: addressable by sub-form
name (so writer code doesn't have to walk in lockstep with reader
code) and additive (new sub-forms added to an element type get
their own slot for free). The sentinel `<tail>` handles
between-last-form-and-close.

## Acceptance criteria

- **A1.** A fixture `inside_comments.rkt` with leading,
  between-sub-form, and tail comments inside a `(poly …)`, a
  `(port …)`, an `(sref …)`, and a `(cell …)` parses + writes
  byte-equal to its source (assuming the source is already in
  canonical form).
- **A2.** Edit a sub-form of an element via the AST API; re-emit;
  the unchanged sibling sub-forms' comments still appear in the
  output.
- **A3.** Python and F# readers + writers agree byte-for-byte on
  the round-trip of A1's fixture.
- **A4.** Migration: every existing `.rkt` in the repo that
  currently has zero inside-element comments parses and re-emits
  unchanged. (D5's existing round-trip suite, extended with the
  new element shape.)
- **A5.** Adding a new sub-form to an existing element type
  (hypothetical schema extension) requires zero changes to the
  comment-attachment code — the `Map<string, string list>` slot
  auto-absorbs it. Verified by a "synthetic future sub-form" test.

## Implementation phases

### P0 — F# Reader

- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs`: extend the
  per-element analyzer to collect `;` runs between sub-forms and
  populate `SubFormComments`.
- Tests for `inside_comments.rkt` parse + AST shape.

### P1 — F# Writer

- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Writer.fs`: emit
  `SubFormComments[key]` before sub-form `key`; emit the `<tail>`
  slot before the element's closing paren.
- Tests for byte-equal round-trip (A1, A2).

### P2 — Python parity

- `src/rekolektion/io/rkt.py` writer side.
- Once the Python reader (`docs/plans/rkt_python_reader.md`) lands,
  add the same support there.

### P3 — Cross-language CI

- Same fixture, both writers, diff = 0 (A3).

## Risks

- **Comment attachment ambiguity in edge cases.** A blank line
  between two sub-forms (`(layer …)\n\n; comment\n(points …)`)
  arguably "belongs" with either sub-form. Convention: attach to
  the next sub-form regardless of blank lines. Document and test.
- **Writer over-eagerly canonicalizing whitespace.** If the
  canonical formatter normalizes "no blank line between sub-forms,"
  the round-trip on existing slightly-non-canonical files won't be
  byte-equal — but D5 already accepts that ("canonical formatting
  on save" is the contract). A4 protects against unrelated
  drift.
- **Element types with positional sub-forms.** Most elements have
  named sub-forms (`(layer …)`, `(points …)`); a few have
  positional ones (numbers in `(origin x y)`). For positional
  sub-forms, attachment falls back to a counter (`"<pos:0>"`,
  `"<pos:1>"`). Or: leave positional sub-forms uncomment-able in
  v1 (likely fine — nobody writes `(origin ; comment\n 0 ; comment\n
  0)` in practice).

## Open questions

- **`<tail>` slot or absorb into a synthetic `"<close>"` key?**
  Either works; `<tail>` reads cleaner. No semantic difference.
- **Backwards compat for consumers reading the AST.** Adding the
  `SubFormComments` field is additive. F# record-update syntax is
  field-name-based, so existing code that constructs `Poly` via
  partial-update or full-construct keeps working as long as the
  field has a default (empty map). Python dataclass `field(default_factory=dict)`
  ditto. Verify in P0.

## Implementation status (2026-05-27)

- **Type scaffold**: complete. `SubFormComments: Map<string, string list>`
  field added to every element record (`Poly`, `Path`, `Rectangle`,
  `Port`, `Label`, `SRef`, `ARef`, `Props`) plus `Cell`, `Meta`,
  `Import`, and `Document`. Every existing record-literal construction
  site (~200 across src + tests) updated to default the new field to
  `Map.empty`. Full F# solution + Python writer still build clean and
  the existing 540-test suite stays green.
- **F# Reader/Writer wiring**: complete for `Poly`. `Reader.subFormCommentsOf`
  walks the CST children of an element, extracts `;`-runs preceding
  each sub-form, and indexes them by the sub-form's head symbol.
  `Writer.subFormLead` consumes the map and forces affected sub-forms
  onto their own line with prefixed `;` comments. Two tests pass:
  parse attaches `SubFormComments[layer]` / `SubFormComments[points]`
  on the canonical `(poly …)` shape, and a round-trip through
  `Writer.write` preserves both interior comments.
- **Other F# element types**: **deferred**. `Path`, `Rectangle`,
  `Port`, `Label`, `SRef`, `ARef`, `Props` each need (a) the
  `SubFormComments = subFormCommentsOf children` wiring in their
  analyzer, and (b) `subFormLead` threading through their synthesizer.
  Pattern is identical to Poly; mechanical extension. Until done, the
  `SubFormComments = Map.empty` default means non-Poly elements drop
  interior comments on parse (status quo, no regression).
- **Python parity (P2)**: **deferred**. The Python reader's
  `_analyze_*` functions and the writer's `_emit_*` functions would
  follow the same pattern — collect comments preceding each sub-form
  on parse, emit before each sub-form on write. Python's `Property`
  dataclass already carries `comments`; adding a `sub_form_comments`
  field per element dataclass + populating in the reader is the work.
  Default-empty initialization avoids breaking existing call sites
  (Python's dataclass `field(default_factory=dict)` makes this
  trivial — no equivalent of F#'s "every field required" issue).
- **Cross-language CI (P3)**: **deferred**, blocked on the per-
  element extensions above.

The "narrow but representative" outcome shipped here: type-level
scaffold for the feature + a fully-working end-to-end implementation
for one element type, so the next agent extending the feature has a
template to follow.

## Files affected

- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Types.fs` — add
  `SubFormComments` to every element record.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs` — populate it.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/Writer.fs` — emit it.
- `tools/viz/tests/Rekolektion.Viz.Core.Tests/RktTests.fs` (or the
  comment-preservation file added by D5) — add A1–A5 cases.
- `src/rekolektion/io/rkt.py` — Python writer side; Python reader
  once that lands.
- `tools/viz/testdata/inside_comments.rkt` (new) — canonical
  fixture covering every element type.
- `docs/io/rkt.md` — strike the in-element comments bullet from
  "Open gaps" once P1 lands.
