"""Minimal `.rkt` reader for primitive metadata: bbox + generator name.

Internal to the layout helpers. The full canonical reader lives in
F# (`tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs`); duplicating
it in Python would be expensive maintenance. Instead, we extract the
two pieces the placement helpers actually need — the primitive's
overall bbox and the generator string from its `(meta …)` block —
via a small regex pass.

This is fragile by design: it assumes the canonical writer's
formatting (one element per line, integer DBU coords). If a future
.rkt schema change breaks the regexes, the bbox helper raises and
the caller surfaces a clear error rather than silently miscomputing
placements. Round-trip is exercised by the F# reader on every block
load, so we have a stronger validator elsewhere.

The bbox returned is the union of every `(rect …)` and `(poly …)`
in the file. For our primitives this is the top cell's geometry,
since each primitive is single-cell (verified at extraction time —
multi-cell primitives raise `MultiCellPrimitiveError`).
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

_RECT_RE = re.compile(
    r"\(rect\s+\(layer\s+\S+\)"
    r"\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\)"
)
# (poly (layer ...) ... (points (X Y) (X Y) ...) ...)
_POLY_BLOCK_RE = re.compile(
    r"\(poly\b[^()]*(?:\([^()]*\)[^()]*)*?\)",
    re.DOTALL,
)
_POINT_RE = re.compile(r"\(\s*(-?\d+)\s+(-?\d+)\s*\)")
_GENERATOR_RE = re.compile(r'\(generator\s+"([^"]+)"\)')
_CELL_DECL_RE = re.compile(r"\(cell\s+(\S+?)\b")


class MultiCellPrimitiveError(RuntimeError):
    """Raised when a primitive .rkt has more than one cell.

    The placement helpers assume a primitive is a single self-contained
    cell. Multi-cell primitives (a top cell SRef'ing helper sub-cells)
    need a different bbox strategy — flattening through hierarchy —
    that we haven't implemented. Surfacing this as an error keeps the
    helpers honest.
    """


class MissingBboxError(RuntimeError):
    """A primitive .rkt produced no rect/poly geometry. Almost
    certainly a bug in the generator that minted it."""


@dataclass(frozen=True)
class RktPrimitiveSummary:
    """Cached extract for one primitive `.rkt`."""

    name: str
    generator: str | None
    bbox: tuple[int, int, int, int]


def _extract_bbox(text: str) -> tuple[int, int, int, int] | None:
    """Union bbox of every rect + poly in the file. None if none."""

    xs: list[int] = []
    ys: list[int] = []
    for m in _RECT_RE.finditer(text):
        x1, y1, x2, y2 = (int(g) for g in m.groups())
        xs.extend((x1, x2))
        ys.extend((y1, y2))
    for m in _POLY_BLOCK_RE.finditer(text):
        for pm in _POINT_RE.finditer(m.group(0)):
            xs.append(int(pm.group(1)))
            ys.append(int(pm.group(2)))
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def read_primitive(path: Path) -> RktPrimitiveSummary:
    """Parse a primitive `.rkt` and return its bbox + generator name.

    Raises:
        FileNotFoundError: path doesn't exist
        MultiCellPrimitiveError: file declares > 1 cell
        MissingBboxError: file has no geometry
    """

    text = path.read_text(encoding="utf-8")
    cells = _CELL_DECL_RE.findall(text)
    if len(cells) > 1:
        raise MultiCellPrimitiveError(
            f"{path.name} declares {len(cells)} cells "
            f"({', '.join(cells[:3])}…); placement helpers only "
            f"support single-cell primitives today."
        )
    if not cells:
        raise MissingBboxError(
            f"{path.name} has no (cell …) declaration."
        )
    bbox = _extract_bbox(text)
    if bbox is None:
        raise MissingBboxError(
            f"{path.name} has no rect/poly geometry."
        )
    generator_match = _GENERATOR_RE.search(text)
    return RktPrimitiveSummary(
        name=cells[0],
        generator=generator_match.group(1) if generator_match else None,
        bbox=bbox,
    )
