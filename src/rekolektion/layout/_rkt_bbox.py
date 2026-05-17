"""Minimal `.rkt` reader for primitive metadata.

Internal to the layout helpers. The full canonical reader lives in
F# (`tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs`); duplicating
it in Python would be expensive maintenance. Instead, we extract
the few pieces the placement / routing helpers actually need:

  - The primitive's overall bbox (union of every `(rect …)` and
    `(poly …)`).
  - The generator string from the `(meta …)` block.
  - Pin labels (D / G / S / B / numeric variants) parsed out of
    `(label …)` elements.

Primitive `.rkt` content is **content-addressed** (cell name encodes
generator + params + digest), so once a file's content is read it's
treated as immutable for the rest of the process. `read_primitive`
memoizes by absolute file path in a module-level dict. There's no
observable state in the cache that a caller would need to invalidate;
tests that need a fresh state call `_clear_primitive_cache()`.

This is fragile by design: it assumes the canonical writer's
formatting (one element per line, integer DBU coords). Round-trip
is exercised by the F# reader on every block load, so we have a
stronger validator elsewhere.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
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

# (label (layer <layer>) (text "<text>") (origin <x> <y>) <extras...>)
# The text may be quoted (string lit) or bare (symbol) per the .rkt
# canonical writer. We accept either form. Trailing sub-forms like
# `(kind device-terminal)` or `(internal #t)` may follow the origin;
# we stop at the origin's closing paren and ignore everything after
# (this reader only needs layer/text/origin from labels — kind is
# the F# reader's concern). The label's outer closing `)` is matched
# *if present immediately* (the legacy single-line form), otherwise
# we just take what we need and let the extras dangle in the
# remaining input; subsequent regex passes ignore them.
_LABEL_RE = re.compile(
    r"\(label\s+\(layer\s+([^)\s]+)\)\s+"
    r"\(text\s+(?:\"([^\"]+)\"|(\S+?))\)\s+"
    r"\(origin\s+(-?\d+)\s+(-?\d+)\)"
)


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
class PinLabel:
    """One device-terminal label parsed from a primitive `.rkt`.

    `terminal` is the label's text — typically `"D"`, `"G"`, `"S"`,
    `"B"` for single-finger FETs. Multi-finger or compound primitives
    can produce `"D1"`, `"S2"`, etc.; we keep the raw text so future
    generators are forward-compatible.

    `origin` is the label's coordinates in **primitive-local** DBU.
    Callers translate by the SRef origin to land in parent coords.

    `layer` is the `sky130:<name>` string from the `(layer …)` form
    — informative; routing helpers usually paint their patches on
    a fixed layer regardless.
    """

    terminal: str
    origin: tuple[int, int]
    layer: str


@dataclass(frozen=True)
class RktPrimitiveSummary:
    """Cached extract for one primitive `.rkt`. Treat as immutable —
    primitive `.rkt` files are content-addressed and don't change
    during a process lifetime."""

    name: str
    generator: str | None
    bbox: tuple[int, int, int, int]
    pins: tuple[PinLabel, ...] = field(default_factory=tuple)


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


def _extract_pins(text: str) -> tuple[PinLabel, ...]:
    """Pull every `(label …)` element. Returns in document order."""

    pins: list[PinLabel] = []
    for m in _LABEL_RE.finditer(text):
        layer = m.group(1)
        # text is either group(2) (quoted) or group(3) (bare).
        terminal = m.group(2) if m.group(2) is not None else m.group(3)
        x = int(m.group(4))
        y = int(m.group(5))
        pins.append(
            PinLabel(terminal=terminal, origin=(x, y), layer=layer)
        )
    return tuple(pins)


# Module-level cache, keyed by absolute file path. Primitive .rkts are
# content-addressed (cell name carries generator + params), and the
# workflow forbids hand-editing them, so once a path's content is
# parsed it's treated as immutable for the rest of the process. Tests
# that mint a stream of fresh primitives use per-test `primitives_dir`
# (distinct absolute paths), so collisions don't happen in practice.
_primitive_cache: dict[Path, RktPrimitiveSummary] = {}


def _clear_primitive_cache() -> None:
    """Drop everything. For tests that need explicit isolation. Not
    public — call paths from rekolektion itself should never need it."""

    _primitive_cache.clear()


_TOP_RE = re.compile(r"\(top\s+(\S+?)\b")
_SREF_RE = re.compile(
    r"\(sref\s+\(cell\s+(\S+?)\b[^)]*\)\s+\(origin\s+(-?\d+)\s+(-?\d+)\)"
)


def _resolve_child_cell(
    cell_name: str,
    parent_path: Path,
    search_dirs: list[Path],
) -> Path | None:
    """Find a child cell's `.rkt` file. Search order:

      1. Sibling `.rkt` in the same directory as the parent
      2. `<dir>/<cell_name>.rkt` in each path in `search_dirs`
      3. `<dir>/primitives/<cell_name>.rkt` for each search dir
         (covers blocks that SRef primitives from a sibling
         `primitives/` directory)

    Returns the first match or None.
    """

    candidates: list[Path] = [
        parent_path.parent / f"{cell_name}.rkt",
    ]
    for d in search_dirs:
        candidates.append(d / f"{cell_name}.rkt")
        candidates.append(d / "primitives" / f"{cell_name}.rkt")
    for c in candidates:
        if c.is_file():
            return c
    return None


def read_primitive(
    path: Path,
    *,
    search_dirs: list[Path] | None = None,
) -> RktPrimitiveSummary:
    """Parse a `.rkt` and return its bbox, generator, and pin labels.
    Memoized by absolute path.

    Handles three shapes:

      1. **Single-cell primitive.** Flat geometry, no SRefs.
         Generated by `mos_draw` / similar PDK procs.
      2. **Multi-cell primitive with origin-(0,0) child SRefs.**
         PDK fixed-geometry devices (substrate PNPs, VPP caps,
         varactors) where the top cell is a thin wrapper around a
         `getcell`-imported sub-cell.
      3. **Composite block.** A block-style `.rkt` that SRefs other
         blocks/primitives at non-zero origins (e.g. nand2_inv_lv
         SRef'd into srcmux at (3000, 0)). For these, the bbox is
         the union of: (a) the top cell's direct parent-paint
         geometry, and (b) each child's bbox translated by its
         SRef origin. Child cells are resolved by searching
         `parent_dir` and `search_dirs`.

    Raises:
        FileNotFoundError: path doesn't exist
        MultiCellPrimitiveError: top cell can't be identified
        MissingBboxError: file has no geometry AND no resolvable
            child SRefs to contribute a bbox
    """

    resolved = path.resolve()
    if resolved in _primitive_cache:
        return _primitive_cache[resolved]

    text = path.read_text(encoding="utf-8")
    cells = _CELL_DECL_RE.findall(text)
    if not cells:
        raise MissingBboxError(
            f"{path.name} has no (cell …) declaration."
        )

    if len(cells) > 1:
        top_match = _TOP_RE.search(text)
        if not top_match:
            raise MultiCellPrimitiveError(
                f"{path.name} declares {len(cells)} cells "
                f"({', '.join(cells[:3])}…) but no (top …) directive; "
                f"placement helpers can't pick which is the interface."
            )
        top_name = top_match.group(1)
        if top_name not in cells:
            raise MultiCellPrimitiveError(
                f"{path.name}: (top {top_name}) doesn't match any "
                f"(cell …) declaration ({', '.join(cells[:3])}…)."
            )
        name = top_name
    else:
        name = cells[0]

    # Start with the bbox of direct parent-paint geometry (rects + polys).
    direct_bbox = _extract_bbox(text)

    # Walk child SRefs and accumulate their translated bboxes (composite
    # blocks). Children at origin (0, 0) collapse into the direct bbox
    # naturally; non-zero-origin children need explicit translation.
    search = search_dirs or []
    xs: list[int] = []
    ys: list[int] = []
    if direct_bbox is not None:
        xs.extend([direct_bbox[0], direct_bbox[2]])
        ys.extend([direct_bbox[1], direct_bbox[3]])
    for sm in _SREF_RE.finditer(text):
        child_name = sm.group(1)
        ox, oy = int(sm.group(2)), int(sm.group(3))
        if (ox, oy) == (0, 0):
            # Origin-(0,0) child contributes via _extract_bbox already
            # only if the child cell body lives in this file. For
            # cross-file children we still need to read the child.
            child_path = _resolve_child_cell(child_name, path, search)
            if child_path is None:
                continue
            child = read_primitive(child_path, search_dirs=search)
            xs.extend([child.bbox[0], child.bbox[2]])
            ys.extend([child.bbox[1], child.bbox[3]])
        else:
            child_path = _resolve_child_cell(child_name, path, search)
            if child_path is None:
                # Can't find the child — fall back to the old
                # restrictive behavior so the user gets a meaningful
                # error instead of silently undersized bbox.
                raise MultiCellPrimitiveError(
                    f"{path.name}: child SRef of '{child_name}' at "
                    f"({ox}, {oy}) — can't resolve '{child_name}.rkt' "
                    f"in {path.parent} or {search}. Pass "
                    f"`search_dirs=[…]` to read_primitive."
                )
            child = read_primitive(child_path, search_dirs=search)
            xs.append(child.bbox[0] + ox)
            xs.append(child.bbox[2] + ox)
            ys.append(child.bbox[1] + oy)
            ys.append(child.bbox[3] + oy)

    if not xs:
        raise MissingBboxError(
            f"{path.name} has no rect/poly geometry and no resolvable "
            f"child SRefs."
        )
    bbox = (min(xs), min(ys), max(xs), max(ys))

    generator_match = _GENERATOR_RE.search(text)
    summary = RktPrimitiveSummary(
        name=name,
        generator=generator_match.group(1) if generator_match else None,
        bbox=bbox,
        pins=_extract_pins(text),
    )
    _primitive_cache[resolved] = summary
    return summary
