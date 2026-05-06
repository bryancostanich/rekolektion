"""Generic 2×2 abutment DRC validator for SRAM/CIM bitcells.

Tile a bitcell into a 2×2 with a configurable mirror pattern (the
standard array-tiling primitive) and run Magic DRC on the assembled
tile via ``verify.drc.run_drc``. Single-cell DRC and array DRC are
different problems: cells can be DRC-clean as standalone yet violate at
the boundaries when tiled (overlapping wells, shared-edge metal
spacing, mismatched substrate connections). This module catches it on
the smallest array-equivalent structure.

Mirror patterns
---------------
- ``xy``   X-mirror odd cols + Y-mirror odd rows. Standard for symmetric
           bitcells designed for shared BL contacts and shared power rails.
- ``y``    Y-mirror odd rows only. Tall-narrow or row-shared-power cells.
- ``x``    X-mirror odd cols only. Column-shared cells.
- ``none`` Identity placement, no mirroring. Useful as a control —
           comparing ``none`` vs ``xy`` makes the actual mirror geometry
           visible in the rendered tile, catching identity-transform-as-
           mirror bugs at build time.

Anti-pattern coverage
---------------------
From the ReRAM_IRL audit (cells that failed prior tape-outs):

- "Single-cell DRC ≠ array DRC" → the 2×2 tile is the gate, not the cell.
- "Identity-transform-as-mirror" → ``none`` vs mirrored diff exposes
  silent transform degeneration; the visual gate (open the tile in
  rekolektion-viz) confirms the placement is real.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path
from typing import Literal, Optional

import gdstk

from rekolektion.verify.drc import DRCResult, find_pdk_root, run_drc


MirrorPattern = Literal["xy", "y", "x", "none"]
"""Tile-time mirror pattern. See module docstring."""


@dataclass
class TileResult:
    """Outcome of :func:`validate_abutment_2x2`."""

    tile_gds: Path
    parent_cell: str
    cell_pitch: tuple[float, float]
    """Single-cell ``(cw, ch)`` in µm — bbox extent of the input cell."""
    tile_pitch: tuple[float, float]
    """Assembled 2×2 tile ``(2*cw, 2*ch)`` in µm."""
    drc: DRCResult


def _discover_top_cell(lib: gdstk.Library) -> gdstk.Cell:
    """Return the real top cell, filtering Magic's ``(UNNAMED)`` scratch buffer.

    Magic's ``gds read`` typically leaves an ``(UNNAMED)`` cell first in
    the top-level list; the actual GDS top is the survivor after that
    filter.
    """
    for cell in lib.top_level():
        if cell.name == "(UNNAMED)" or cell.name.startswith("$"):
            continue
        return cell
    raise ValueError("no real top cell found in GDS library")


def _placement(
    row: int,
    col: int,
    mirror: MirrorPattern,
    x0: float,
    y0: float,
    x1: float,
    y1: float,
    cw: float,
    ch: float,
) -> tuple[int, bool, tuple[float, float]]:
    """Return ``(rotation_deg, x_reflection, origin)`` for a 2×2 slot.

    gdstk applies ``x_reflection`` (Y-flip: ``y → -y``) before rotation,
    then translation. The four primitive transforms decompose as:

    =================  ========  ==============
    transform          rotation  x_reflection
    =================  ========  ==============
    identity           0         False
    Y-mirror (Y-flip)  0         True
    X-mirror (X-flip)  180       True
    XY-mirror          180       False
    =================  ========  ==============

    Origin is chosen so the transformed cell's bbox lands exactly in
    slot ``[col*cw, (col+1)*cw] × [row*ch, (row+1)*ch]`` world-coords —
    cells abut perfectly with no gap.
    """
    do_x_mirror = mirror in ("xy", "x") and (col % 2 == 1)
    do_y_mirror = mirror in ("xy", "y") and (row % 2 == 1)

    if do_x_mirror and do_y_mirror:
        rot, xrefl = 180, False
        ox = col * cw + x1
        oy = row * ch + y1
    elif do_x_mirror:
        rot, xrefl = 180, True
        ox = col * cw + x1
        oy = row * ch - y0
    elif do_y_mirror:
        rot, xrefl = 0, True
        ox = col * cw - x0
        oy = row * ch + y1
    else:
        rot, xrefl = 0, False
        ox = col * cw - x0
        oy = row * ch - y0
    return rot, xrefl, (ox, oy)


def build_2x2_tile(
    cell_gds: Path,
    *,
    top_cell: Optional[str] = None,
    mirror_pattern: MirrorPattern = "xy",
    out_gds: Path,
) -> tuple[Path, str, tuple[float, float]]:
    """Build a 2×2 abutment tile of the cell at ``cell_gds``.

    Returns ``(out_gds_path, parent_cell_name, (cw, ch))``.
    """
    src = gdstk.read_gds(str(cell_gds))
    if top_cell is not None:
        src_top = next((c for c in src.cells if c.name == top_cell), None)
        if src_top is None:
            raise ValueError(f"top cell {top_cell!r} not found in {cell_gds}")
    else:
        src_top = _discover_top_cell(src)

    bbox = src_top.bounding_box()
    if bbox is None:
        raise ValueError(f"source cell {src_top.name!r} has empty bounding box")
    (x0, y0), (x1, y1) = bbox
    cw, ch = x1 - x0, y1 - y0

    out_lib = gdstk.Library(name=f"{src_top.name}_2x2")
    for cell in src.cells:
        out_lib.add(cell)
    parent_name = f"{src_top.name}_2x2"
    parent = out_lib.new_cell(parent_name)
    for row in range(2):
        for col in range(2):
            rot, xrefl, origin = _placement(
                row, col, mirror_pattern, x0, y0, x1, y1, cw, ch
            )
            parent.add(
                gdstk.Reference(
                    src_top,
                    origin=origin,
                    rotation=rot * math.pi / 180.0,
                    x_reflection=xrefl,
                )
            )

    out_gds.parent.mkdir(parents=True, exist_ok=True)
    out_lib.write_gds(str(out_gds))
    return out_gds, parent_name, (cw, ch)


def validate_abutment_2x2(
    cell_gds: Path,
    *,
    top_cell: Optional[str] = None,
    mirror_pattern: MirrorPattern = "xy",
    out_dir: Optional[Path] = None,
    waiver_footprints: Optional[list[tuple[str, float, float, float, float]]] = None,
    pdk_root: Optional[Path] = None,
) -> TileResult:
    """Tile ``cell_gds`` into a 2×2 abutment under ``mirror_pattern``, run DRC.

    Args:
        cell_gds: Source GDS containing the bitcell.
        top_cell: Top-cell name. ``None`` auto-discovers (filters Magic's
            ``(UNNAMED)`` scratch buffer).
        mirror_pattern: One of ``"xy"`` / ``"y"`` / ``"x"`` / ``"none"``.
        out_dir: Where to land the tiled GDS and DRC artifacts.
            Defaults to ``<cell_gds_parent>/abutment_2x2``.
        waiver_footprints: Optional spatial waiver footprints passed
            through to :func:`verify.drc.run_drc`. See its docstring for
            semantics.
        pdk_root: Optional PDK_ROOT override.

    Returns:
        :class:`TileResult` with tile path, parent cell name, cell and
        tile pitches, and the underlying :class:`verify.drc.DRCResult`.

    Raises:
        FileNotFoundError: if ``cell_gds`` doesn't exist.
        ValueError: if the top cell isn't resolvable or has empty bbox.
    """
    cell_gds = Path(cell_gds)
    if not cell_gds.exists():
        raise FileNotFoundError(f"GDS not found: {cell_gds}")

    out_dir = Path(out_dir) if out_dir else cell_gds.parent / "abutment_2x2"
    out_dir.mkdir(parents=True, exist_ok=True)

    tile_gds = out_dir / "tile_2x2.gds"
    tile_path, parent_name, (cw, ch) = build_2x2_tile(
        cell_gds,
        top_cell=top_cell,
        mirror_pattern=mirror_pattern,
        out_gds=tile_gds,
    )

    drc_result = run_drc(
        tile_path,
        cell_name=parent_name,
        pdk_root=pdk_root or find_pdk_root(),
        output_dir=out_dir / "drc",
        waiver_footprints=waiver_footprints,
    )

    return TileResult(
        tile_gds=tile_path,
        parent_cell=parent_name,
        cell_pitch=(cw, ch),
        tile_pitch=(2 * cw, 2 * ch),
        drc=drc_result,
    )
