"""Element-level `.rkt` comparator for primitive regeneration.

Built for track-06 step 5: when we regenerate primitives to pick up
the new `(kind device-terminal)` annotation on FET port labels, we
need to verify the regen **only** added tag annotations — no rect
moved, no poly resized, no SRef shifted, no labels gained or lost.

Usage:
    .venv/bin/python scripts/diff_primitives.py <old_dir> <new_dir>

Compares every `*.rkt` file in `<old_dir>` against the same-named
file in `<new_dir>`. For each pair, parses both, strips the new
`(kind …)` annotations on labels, and compares the resulting
element lists. Reports any element-level discrepancy.

Exit code:
    0 — all primitives match modulo `(kind …)` additions
    1 — at least one primitive has non-kind geometric drift

This is intentionally separate from the F# round-trip tests (which
exercise the schema fields in isolation). It compares actual file
content from before and after a regen pass, which is the regression
the spec is worried about.
"""

from __future__ import annotations

import argparse
import dataclasses
import re
import sys
from pathlib import Path

# We do a minimal "lex + walk" comparison rather than building a full
# tree. Each top-level element form (rect / poly / sref / aref / label
# / port / props / meta) becomes one normalized string after `(kind …)`
# sub-forms are stripped. The set of normalized strings before and
# after regen must match exactly.

# Captures `(kind <token-or-string>)` anywhere inside a label.
_KIND_RE = re.compile(r"\(kind\s+(?:\"[^\"]*\"|\S+?)\)")

# Captures the trailing whitespace-then-`(kind …)` pattern the writer
# produces, plus any leading whitespace before it, so we can normalize
# multi-line layout differences (label form split across lines because
# of the kind annotation vs. all on one line because no kind).
_LEADING_WS_KIND_RE = re.compile(r"\s+\(kind\s+(?:\"[^\"]*\"|\S+?)\)")


def _strip_kind_annotations(text: str) -> str:
    """Remove every `(kind …)` sub-form so the comparison ignores the
    intentional new additions. Preserves all other content."""

    out = _LEADING_WS_KIND_RE.sub("", text)
    # Belt-and-suspenders for any `(kind …)` we might have missed
    # (e.g. one not preceded by whitespace because hand-written).
    out = _KIND_RE.sub("", out)
    return out


def _strip_meta_block(text: str) -> str:
    """Remove the entire `(meta …)` block from each cell. The block is
    intentionally regen-volatile — digest changes whenever generator
    params change, generated-date changes on every run, and the
    params list itself may pick up new keys (topc/botc) as the
    generator evolves. None of that is geometric drift; this
    comparator's job is to verify the *geometry* didn't change.

    Implementation: scan for `(meta ` openers and skip a balanced
    parenthesized expression. Single-line and multi-line forms both
    work since paren-balance is independent of layout.
    """

    out: list[str] = []
    i = 0
    while i < len(text):
        if text.startswith("(meta", i) and (
            i + 5 >= len(text) or text[i + 5] in " \t\n("
        ):
            # Find the matching close paren.
            depth = 0
            j = i
            in_str = False
            escape = False
            while j < len(text):
                c = text[j]
                if in_str:
                    if escape:
                        escape = False
                    elif c == "\\":
                        escape = True
                    elif c == '"':
                        in_str = False
                elif c == '"':
                    in_str = True
                elif c == "(":
                    depth += 1
                elif c == ")":
                    depth -= 1
                    if depth == 0:
                        j += 1
                        break
                j += 1
            i = j
            continue
        out.append(text[i])
        i += 1
    return "".join(out)


def _normalize_whitespace(text: str) -> str:
    """Collapse runs of whitespace to single spaces and strip per-line
    edge whitespace. Two .rkt files that differ only in indentation or
    line breaks become identical after this pass.
    """

    return re.sub(r"\s+", " ", text).strip()


# Tokenize the .rkt text into top-level forms inside each cell so we
# can do per-form comparisons instead of one big string diff. A "form"
# is a balanced parenthesized expression at depth 2 (the cell's
# elements).

def _split_top_forms(text: str) -> list[str]:
    """Yield each balanced parenthesized form at the top level of the
    given text. Used both for the document body and recursively per
    cell."""

    out: list[str] = []
    depth = 0
    start = -1
    in_str = False
    escape = False
    for i, c in enumerate(text):
        if in_str:
            if escape:
                escape = False
            elif c == "\\":
                escape = True
            elif c == '"':
                in_str = False
            continue
        if c == '"':
            in_str = True
            continue
        if c == "(":
            if depth == 0:
                start = i
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0 and start >= 0:
                out.append(text[start:i + 1])
                start = -1
    return out


@dataclasses.dataclass
class DiffReport:
    """Per-cell comparison result."""

    file: str
    only_in_old: list[str] = dataclasses.field(default_factory=list)
    only_in_new: list[str] = dataclasses.field(default_factory=list)

    @property
    def clean(self) -> bool:
        return not self.only_in_old and not self.only_in_new


def compare_pair(old_path: Path, new_path: Path) -> DiffReport:
    """Compare two `.rkt` files, ignoring `(kind …)` annotations.
    Returns a report listing elements present in one but not the
    other after normalization."""

    old_raw = old_path.read_text(encoding="utf-8")
    new_raw = new_path.read_text(encoding="utf-8")
    old_text = _strip_kind_annotations(_strip_meta_block(old_raw))
    new_text = _strip_kind_annotations(_strip_meta_block(new_raw))

    # Normalize whitespace so multi-line forms compare correctly.
    old_forms = sorted(
        _normalize_whitespace(f)
        for f in _split_top_forms(old_text)
    )
    new_forms = sorted(
        _normalize_whitespace(f)
        for f in _split_top_forms(new_text)
    )

    only_old = [f for f in old_forms if f not in new_forms]
    only_new = [f for f in new_forms if f not in old_forms]
    return DiffReport(
        file=old_path.name,
        only_in_old=only_old,
        only_in_new=only_new,
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("old_dir", type=Path, help="primitives before regen")
    parser.add_argument("new_dir", type=Path, help="primitives after regen")
    parser.add_argument(
        "--verbose", "-v", action="store_true",
        help="print every diff hit, not just the headline",
    )
    args = parser.parse_args(argv)

    old_files = sorted(p for p in args.old_dir.glob("*.rkt"))
    if not old_files:
        print(f"no .rkt files in {args.old_dir}", file=sys.stderr)
        return 2

    fail = 0
    for old in old_files:
        new = args.new_dir / old.name
        if not new.is_file():
            print(f"MISSING in new_dir: {old.name}")
            fail += 1
            continue
        report = compare_pair(old, new)
        if report.clean:
            print(f"OK    {old.name}")
        else:
            fail += 1
            print(
                f"DIFF  {old.name}  "
                f"({len(report.only_in_old)} only-in-old, "
                f"{len(report.only_in_new)} only-in-new)"
            )
            if args.verbose:
                for f in report.only_in_old:
                    print(f"  <  {f}")
                for f in report.only_in_new:
                    print(f"  >  {f}")
    print()
    if fail == 0:
        print(f"all {len(old_files)} primitives match (kind-only diffs ignored)")
        return 0
    print(f"{fail} primitive(s) have non-kind drift")
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
