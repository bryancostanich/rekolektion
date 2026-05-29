"""Auto-compute waiver footprints for verify_drc.

When DRC is run on a parent cell that SRefs primitives, the primitives'
internal geometry trips many "tight" rules (li.c2, licon.*, mcon.1,
poly.*, diff/tap.* etc.) that the foundry waives in silicon via
COREID-class waivers. The `run_drc` machinery already supports
classifying tiles as waivers when they fall inside a spatial footprint
(`waiver_footprints` parameter) AND the rule is in the global
`_KNOWN_WAIVER_RULES` set. What was missing was the bridge: an
automatic way to compute those footprints from the cell hierarchy.

This module does that bridge. For a `.rkt` block, it:

1. Walks the import graph and identifies which imported cells are
   `(meta (generator …))` primitives — i.e. PDK-minted FET / cap /
   resistor / BJT cells that pass DRC at their own boundary but
   carry intra-cell rule violations that don't matter in production.
2. Converts the block to GDS (the caller already does this once).
3. Walks the GDS hierarchy and accumulates a bbox per primitive SRef
   in top-cell coords. Nested primitives (e.g. primitives inside a
   user-authored stdcell that's SRef'd by the top block) are reached
   via recursion + per-SRef bbox transformation.

The returned list slots straight into `run_drc(waiver_footprints=…)`.

This is the "spatial classification" TODO the comment in
`drc.py:_KNOWN_WAIVER_RULES` flagged — tiles INSIDE a primitive
footprint that match a known-waiver rule are waivers, tiles outside
are real errors. Users see only their actual bugs.
"""

from __future__ import annotations

import math
import re
from pathlib import Path
from typing import Iterable


_META_GENERATOR_RE = re.compile(r"\(meta\b[^()]*\(generator\b")
_TOP_RE = re.compile(r"\(top\s+(\S+?)\b")
_CELL_DECL_RE = re.compile(r"\(cell\s+(\S+?)\b")
_IMPORT_RE = re.compile(r'\(import\s+"([^"]+)"')


def _read_text(path: Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None


def _cell_names(text: str) -> tuple[str, ...]:
    """Return all cell names declared in a `.rkt` text. The `(top …)`
    directive, if present, is the first element. Falls back to the
    order of `(cell …)` declarations otherwise."""

    cells = _CELL_DECL_RE.findall(text)
    if not cells:
        return ()
    top = _TOP_RE.search(text)
    if top and top.group(1) in cells:
        # Put top first, then the rest.
        rest = tuple(c for c in cells if c != top.group(1))
        return (top.group(1), *rest)
    return tuple(cells)


def _resolve_import(import_path: str, parent_rkt: Path) -> Path | None:
    """Resolve an `(import "...")` path string to an absolute `.rkt`
    file path, relative to the parent block's location."""

    candidate = (parent_rkt.parent / import_path).resolve()
    if candidate.is_file():
        return candidate
    return None


def collect_primitive_cell_names(top_rkt: Path) -> set[str]:
    """Walk the `.rkt` import graph starting at `top_rkt`. Return the
    set of cell names whose `.rkt` carries a `(meta (generator …))`
    block — those are the primitives whose internal violations should
    be footprint-waived.

    Visits each imported file at most once.
    """

    primitives: set[str] = set()
    seen: set[Path] = set()
    queue: list[Path] = [top_rkt.resolve()]

    while queue:
        path = queue.pop()
        if path in seen or not path.is_file():
            continue
        seen.add(path)

        text = _read_text(path)
        if text is None:
            continue

        if _META_GENERATOR_RE.search(text):
            # Every cell in this file is part of the primitive surface.
            # In practice primitives are single-cell or (PNP / VPP-cap
            # style) one wrapper + one child cell — covering both is
            # correct.
            for name in _cell_names(text):
                primitives.add(name)
            # Primitives are leaves; their `(import …)` directives, if
            # any, would point at sub-primitives but for our generators
            # they don't — skip the queue walk.
            continue

        for import_str in _IMPORT_RE.findall(text):
            child = _resolve_import(import_str, path)
            if child is not None:
                queue.append(child)

    return primitives


# Geometric transformation helpers. gdstk's `Reference` exposes
# `origin`, `rotation` (degrees), `x_reflection` (bool), `magnification`
# (float). For axis-aligned rotations (0 / 90 / 180 / 270) and the
# Y-axis reflection these compose cleanly without floating-point drift.

def _xform_point(
    x: float, y: float,
    *,
    tx: float, ty: float,
    rotation: float, x_reflection: bool, magnification: float,
) -> tuple[float, float]:
    """Apply one SRef transformation: reflect → rotate → magnify → translate."""

    if x_reflection:
        y = -y
    if magnification != 1.0:
        x *= magnification
        y *= magnification
    if rotation != 0.0:
        rad = math.radians(rotation)
        c, s = math.cos(rad), math.sin(rad)
        x, y = x * c - y * s, x * s + y * c
    return (x + tx, y + ty)


def _xform_bbox(
    bbox: tuple[tuple[float, float], tuple[float, float]],
    *,
    tx: float, ty: float,
    rotation: float, x_reflection: bool, magnification: float,
) -> tuple[float, float, float, float]:
    """Transform an axis-aligned bbox by an SRef. Returns (x1, y1, x2, y2)
    of the transformed bbox (still axis-aligned after rotation by
    multiples of 90°; for arbitrary rotations the returned bbox is the
    minimum axis-aligned enclosure)."""

    (lx1, ly1), (lx2, ly2) = bbox
    corners = [(lx1, ly1), (lx2, ly1), (lx1, ly2), (lx2, ly2)]
    txed = [
        _xform_point(
            x, y, tx=tx, ty=ty,
            rotation=rotation,
            x_reflection=x_reflection,
            magnification=magnification,
        )
        for (x, y) in corners
    ]
    xs = [p[0] for p in txed]
    ys = [p[1] for p in txed]
    return (min(xs), min(ys), max(xs), max(ys))


def _walk_refs(
    cell,
    primitive_names: set[str],
    out: list[tuple[str, float, float, float, float]],
) -> None:
    """Recursively visit `cell.references`. When a reference's target
    is a primitive, append its bbox (in `cell`'s coords) to `out`.
    Otherwise, recurse into the target and lift the resulting child
    footprints back into `cell`'s coords via the SRef transformation.
    """

    for ref in cell.references:
        target_name = ref.cell.name
        if target_name in primitive_names:
            local = ref.cell.bounding_box()
            if local is None:
                continue
            x1, y1, x2, y2 = _xform_bbox(
                local,
                tx=ref.origin[0], ty=ref.origin[1],
                rotation=ref.rotation,
                x_reflection=ref.x_reflection,
                magnification=ref.magnification,
            )
            out.append((target_name, x1, y1, x2, y2))
        else:
            # User cell — collect its sub-primitives in the cell's
            # local coords, then transform up.
            sub: list[tuple[str, float, float, float, float]] = []
            _walk_refs(ref.cell, primitive_names, sub)
            for (name, sx1, sy1, sx2, sy2) in sub:
                x1, y1, x2, y2 = _xform_bbox(
                    ((sx1, sy1), (sx2, sy2)),
                    tx=ref.origin[0], ty=ref.origin[1],
                    rotation=ref.rotation,
                    x_reflection=ref.x_reflection,
                    magnification=ref.magnification,
                )
                out.append((name, x1, y1, x2, y2))


def compute_primitive_footprints(
    rkt_path: Path,
    gds_path: Path,
    *,
    top_cell_name: str = "",
    margin_um: float = 0.5,
) -> list[tuple[str, float, float, float, float]]:
    """Compute waiver footprints (in micrometers) for every primitive
    SRef in `gds_path`'s top cell hierarchy. `rkt_path` is the source
    block — used to identify which imported cells are primitives via
    their `(meta (generator …))` markers.

    Returns a list of `(cell_name, x1, y1, x2, y2)` tuples ready to
    pass as `run_drc(waiver_footprints=…)`.

    Each primitive's bbox is expanded by `margin_um` on every side so
    that spacing-rule tiles at the primitive boundary (between two
    abutting primitives, e.g. diff/tap.22/.23 LV-vs-MV diffusion
    spacing in stdcells) land inside a footprint and get classified as
    waivers.

    The default 0.5 µm covers SKY130's widest single-rule cross-cell
    spacing (diff/tap.24 = 0.43 µm) with a small safety margin. This
    is a conservative middle ground:

    - Smaller margins under-cover boundary violations between abutted
      primitives → user sees noise.
    - Larger margins (e.g. 1.0 µm) capture more boundary violations
      but risk waiving REAL parent-paint vs primitive bugs in the
      µm-scale gap region near each primitive's edge. The 2-tile
      nwell.2a bug class that motivated this work fires exactly in
      that gap — primitives 425 nm apart with a missing parent-paint
      bridge. A 1 µm margin would hide it.

    Pass a larger `margin_um` only when you trust the design's parent
    paint to NOT touch the inter-primitive gap region (e.g. row-based
    stdcell composition where parent paint stays at rail level).

    If `top_cell_name` is empty, the GDS library's first cell is used
    (matches `run_drc`'s default).
    """

    try:
        import gdstk
    except ImportError:
        # gdstk is an optional dep for the verify path; if it isn't
        # available, skip the auto-footprint computation. Callers can
        # still pass `waiver_footprints` explicitly.
        return []

    primitives = collect_primitive_cell_names(rkt_path)
    if not primitives:
        return []

    lib = gdstk.read_gds(str(gds_path))
    if top_cell_name:
        top = next(
            (c for c in lib.cells if c.name == top_cell_name),
            None,
        )
    else:
        top = lib.cells[0] if lib.cells else None
    if top is None:
        return []

    footprints: list[tuple[str, float, float, float, float]] = []
    _walk_refs(top, primitives, footprints)
    if margin_um > 0.0:
        footprints = [
            (name, x1 - margin_um, y1 - margin_um,
                   x2 + margin_um, y2 + margin_um)
            for (name, x1, y1, x2, y2) in footprints
        ]
    return footprints
