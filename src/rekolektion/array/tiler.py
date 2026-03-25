"""Bitcell array tiler.

Takes a :class:`BitcellInfo` and array dimensions, then tiles the cell into
a rectangular array with proper mirroring:

* **X-mirror** for adjacent columns  (shared bit lines)
* **Y-mirror** for adjacent rows     (shared power rails)

This is Phase 2A/2B — basic tiling only.  Edge cells, dummy cells, and
well-strap insertion come later.

Usage::

    from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
    from rekolektion.array.tiler import tile_array

    info = load_foundry_sp_bitcell()
    tile_array(info, num_rows=8, num_cols=32, output_path="array_8x32.gds")
"""

from __future__ import annotations

import math
from pathlib import Path

import gdstk

from rekolektion.bitcell.base import BitcellInfo


def tile_array(
    bitcell: BitcellInfo,
    num_rows: int,
    num_cols: int,
    output_path: str | Path | None = None,
    array_name: str | None = None,
) -> gdstk.Library:
    """Tile a bitcell into an array and optionally write GDS.

    Parameters
    ----------
    bitcell : BitcellInfo
        The bitcell to tile.
    num_rows, num_cols : int
        Array dimensions (rows = word lines, cols = bit-line pairs).
    output_path : path, optional
        If given, write the result to this GDS file.
    array_name : str, optional
        Name for the top-level array cell (default: auto-generated).

    Returns
    -------
    gdstk.Library
        The library containing the array cell and its bitcell dependency.
    """
    if num_rows < 1 or num_cols < 1:
        raise ValueError("num_rows and num_cols must be >= 1")

    cw = bitcell.cell_width
    ch = bitcell.cell_height
    name = array_name or f"sram_array_{num_rows}x{num_cols}"

    # --- build library -----------------------------------------------------
    lib = gdstk.Library(name=f"{name}_lib")

    # Load the bitcell from its GDS so we have all geometry.
    src_lib = gdstk.read_gds(str(bitcell.gds_path))
    src_cell: gdstk.Cell | None = None
    for c in src_lib.cells:
        if c.name == bitcell.cell_name:
            src_cell = c
            break
    if src_cell is None:
        src_cell = src_lib.cells[0]

    # Deep-copy the source cell (and any dependencies) into our library.
    # We need to copy all cells from the source library that the bitcell
    # depends on, then add them to our library.
    cell_map: dict[str, gdstk.Cell] = {}
    for c in src_lib.cells:
        new_cell = c.copy(c.name)
        cell_map[c.name] = new_cell
        lib.add(new_cell)

    bit_cell = cell_map[src_cell.name]

    # --- create array cell -------------------------------------------------
    array_cell = gdstk.Cell(name)
    lib.add(array_cell)

    for row in range(num_rows):
        for col in range(num_cols):
            # Mirroring pattern:
            #   even col -> normal X;  odd col -> X-mirrored (x_reflection)
            #   even row -> normal Y;  odd row -> Y-mirrored (rotation 180 + x_reflection = y reflection)
            x_mirror = (col % 2) == 1
            y_mirror = (row % 2) == 1

            # Compute origin for this instance.
            # When mirrored, the cell flips about its local origin, so we
            # need to offset to keep the tiled grid aligned.
            origin_x = col * cw
            origin_y = row * ch

            # gdstk Reference: rotation is in radians, magnification is float.
            # x_reflection mirrors about the X-axis (flips Y).
            # To mirror about Y-axis (flip X), we rotate 180 and x_reflect.
            #
            # Our convention:
            #   X-mirror (flip across column boundary): mirror the cell
            #     left-right.  gdstk doesn't have y_reflection, so we use
            #     rotation=0, x_reflection=False but negate x-scaling —
            #     actually, the cleanest way is to use x_reflection + rotation.
            #
            # Let's define transforms:
            #   Normal:            rotation=0,  x_reflection=False
            #   X-mirror (flip X): rotation=pi, x_reflection=True   (= Y-axis mirror)
            #   Y-mirror (flip Y): rotation=0,  x_reflection=True
            #   XY-mirror:         rotation=pi, x_reflection=False  (= 180 deg rotation)

            if not x_mirror and not y_mirror:
                rot = 0.0
                x_ref = False
                # Origin is bottom-left corner.
                ox = origin_x
                oy = origin_y
            elif x_mirror and not y_mirror:
                # Mirror about Y-axis (flip left-right).
                rot = math.pi
                x_ref = True
                # After rotation=pi + x_reflection, the cell's (0,0) maps to
                # the cell's (width, 0) in global coords.
                ox = origin_x + cw
                oy = origin_y
            elif not x_mirror and y_mirror:
                # Mirror about X-axis (flip top-bottom).
                rot = 0.0
                x_ref = True
                # x_reflection flips Y, so cell's (0,0) maps to (0, height).
                ox = origin_x
                oy = origin_y + ch
            else:  # x_mirror and y_mirror
                # 180-degree rotation.
                rot = math.pi
                x_ref = False
                # Cell's (0,0) maps to (width, height).
                ox = origin_x + cw
                oy = origin_y + ch

            ref = gdstk.Reference(
                bit_cell,
                origin=(ox, oy),
                rotation=rot,
                x_reflection=x_ref,
            )
            array_cell.add(ref)

    # --- write output ------------------------------------------------------
    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return lib
