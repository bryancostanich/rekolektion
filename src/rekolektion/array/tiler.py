"""Bitcell array tiler.

Takes a :class:`BitcellInfo` and array dimensions, then tiles the cell into
a rectangular array with proper mirroring:

* **X-mirror** for adjacent columns  (shared bit lines)
* **Y-mirror** for adjacent rows     (shared power rails)

Supports:
* Dummy cell border (one ring of dummy cells around the perimeter)
* WL strap columns inserted at regular intervals (alternating VDD/GND)
* Column end cells at top and bottom of each column
* Row end cells at left and right of each row
* Corner cells at the four array corners
* Word line, bit line, and power rail routing

Usage::

    from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
    from rekolektion.array.tiler import tile_array

    info = load_foundry_sp_bitcell()
    tile_array(info, num_rows=8, num_cols=32, output_path="array_8x32.gds")
"""

from __future__ import annotations

import math
from pathlib import Path
from typing import List, Tuple

import gdstk

from rekolektion.bitcell.base import BitcellInfo


def _add_cell_to_lib(
    lib: gdstk.Library,
    cell_map: dict[str, gdstk.Cell],
    gds_path: str | Path,
    cell_name: str,
) -> gdstk.Cell:
    """Load a cell from a GDS file into the library, avoiding duplicates.

    Returns the cell object in the library.
    """
    if cell_name in cell_map:
        return cell_map[cell_name]

    src_lib = gdstk.read_gds(str(gds_path))
    target_cell = None
    for c in src_lib.cells:
        if c.name == cell_name:
            target_cell = c
            break
    if target_cell is None:
        target_cell = src_lib.cells[0]

    # Add all cells from this GDS that aren't already in our library.
    for c in src_lib.cells:
        if c.name not in cell_map:
            new_cell = c.copy(c.name)
            cell_map[c.name] = new_cell
            lib.add(new_cell)

    return cell_map[target_cell.name]


def _place_cell(
    array_cell: gdstk.Cell,
    cell: gdstk.Cell,
    origin_x: float,
    origin_y: float,
    cell_width: float,
    cell_height: float,
    x_mirror: bool = False,
    y_mirror: bool = False,
) -> None:
    """Place a cell reference with mirroring.

    Mirroring convention (same as gdstk):
      Normal:            rotation=0,  x_reflection=False
      X-mirror (flip X): rotation=pi, x_reflection=True   (= Y-axis mirror)
      Y-mirror (flip Y): rotation=0,  x_reflection=True
      XY-mirror:         rotation=pi, x_reflection=False  (= 180 deg rotation)
    """
    if not x_mirror and not y_mirror:
        rot, x_ref = 0.0, False
        ox, oy = origin_x, origin_y
    elif x_mirror and not y_mirror:
        rot, x_ref = math.pi, True
        ox, oy = origin_x + cell_width, origin_y
    elif not x_mirror and y_mirror:
        rot, x_ref = 0.0, True
        ox, oy = origin_x, origin_y + cell_height
    else:
        rot, x_ref = math.pi, False
        ox, oy = origin_x + cell_width, origin_y + cell_height

    ref = gdstk.Reference(cell, origin=(ox, oy), rotation=rot, x_reflection=x_ref)
    array_cell.add(ref)


def _compute_column_layout(
    num_cols: int,
    strap_interval: int,
) -> List[str]:
    """Compute the column layout including WL strap insertions.

    Returns a list of column types: "bit" for bitcell columns,
    "strap" for WL strap columns.  Strap columns are inserted
    every `strap_interval` bitcell columns.
    """
    if strap_interval <= 0:
        return ["bit"] * num_cols

    layout: List[str] = []
    bit_count = 0
    for _ in range(num_cols):
        if bit_count > 0 and bit_count % strap_interval == 0:
            layout.append("strap")
        layout.append("bit")
        bit_count += 1

    return layout


def tile_array(
    bitcell: BitcellInfo,
    num_rows: int,
    num_cols: int,
    output_path: str | Path | None = None,
    array_name: str | None = None,
    *,
    with_dummy: bool = False,
    strap_interval: int = 0,
    with_routing: bool = False,
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
    with_dummy : bool
        Add a ring of dummy cells around the array perimeter.
    strap_interval : int
        Insert WL strap columns every N bitcell columns (0 = no straps).
    with_routing : bool
        Add WL, BL/BR, and power rail routing.

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
    cell_map: dict[str, gdstk.Cell] = {}

    # Load the bitcell.
    bit_cell = _add_cell_to_lib(lib, cell_map, bitcell.gds_path, bitcell.cell_name)

    # --- load support cells if needed --------------------------------------
    dummy_cell = None
    dummy_w = dummy_h = 0.0
    colend_cell = None
    colend_w = colend_h = 0.0
    colend_cent_cell = None
    colend_cent_w = colend_cent_h = 0.0
    rowend_cell = None
    rowend_w = rowend_h = 0.0
    corner_cell = None
    corner_w = corner_h = 0.0
    wlstrap_cell = None
    wlstrap_w = wlstrap_h = 0.0
    wlstrap_p_cell = None

    need_support = with_dummy or strap_interval > 0

    if need_support:
        from rekolektion.array.support_cells import get_support_cell

        try:
            dummy_info = get_support_cell("dummy")
            dummy_cell = _add_cell_to_lib(
                lib, cell_map, dummy_info.gds_path, dummy_info.cell_name
            )
            dummy_w, dummy_h = dummy_info.width, dummy_info.height
        except (FileNotFoundError, KeyError):
            pass

        try:
            colend_info = get_support_cell("colend")
            colend_cell = _add_cell_to_lib(
                lib, cell_map, colend_info.gds_path, colend_info.cell_name
            )
            colend_w, colend_h = colend_info.width, colend_info.height
        except (FileNotFoundError, KeyError):
            pass

        try:
            colend_cent_info = get_support_cell("colend_cent")
            colend_cent_cell = _add_cell_to_lib(
                lib, cell_map, colend_cent_info.gds_path, colend_cent_info.cell_name
            )
            colend_cent_w, colend_cent_h = colend_cent_info.width, colend_cent_info.height
        except (FileNotFoundError, KeyError):
            pass

        try:
            rowend_info = get_support_cell("rowend")
            rowend_cell = _add_cell_to_lib(
                lib, cell_map, rowend_info.gds_path, rowend_info.cell_name
            )
            rowend_w, rowend_h = rowend_info.width, rowend_info.height
        except (FileNotFoundError, KeyError):
            pass

        try:
            corner_info = get_support_cell("corner")
            corner_cell = _add_cell_to_lib(
                lib, cell_map, corner_info.gds_path, corner_info.cell_name
            )
            corner_w, corner_h = corner_info.width, corner_info.height
        except (FileNotFoundError, KeyError):
            pass

        try:
            wlstrap_info = get_support_cell("wlstrap")
            wlstrap_cell = _add_cell_to_lib(
                lib, cell_map, wlstrap_info.gds_path, wlstrap_info.cell_name
            )
            wlstrap_w, wlstrap_h = wlstrap_info.width, wlstrap_info.height
        except (FileNotFoundError, KeyError):
            pass

        try:
            wlstrap_p_info = get_support_cell("wlstrap_p")
            wlstrap_p_cell = _add_cell_to_lib(
                lib, cell_map, wlstrap_p_info.gds_path, wlstrap_p_info.cell_name
            )
        except (FileNotFoundError, KeyError):
            pass

    # --- compute column layout ---------------------------------------------
    col_layout = _compute_column_layout(num_cols, strap_interval)

    # --- compute offsets ---------------------------------------------------
    # If we have dummy cells, the core array is offset by the dummy cell size.
    # Row ends go at left/right, column ends at top/bottom.
    left_margin = rowend_w if (with_dummy and rowend_cell) else 0.0
    bottom_margin = colend_h if (with_dummy and colend_cell) else 0.0

    # If we have dummy border, add one ring of dummy cells.
    # Dummy cells have the same size as the bitcell, so the border is
    # one bitcell wide/tall.
    if with_dummy and dummy_cell:
        left_margin = max(left_margin, dummy_w)
        bottom_margin = max(bottom_margin, dummy_h)

    # Use row end width as left margin (row ends are placed at x=0).
    if with_dummy and rowend_cell:
        left_margin = rowend_w

    # Use column end height as bottom margin (col ends placed at y=0).
    if with_dummy and colend_cell:
        bottom_margin = colend_h

    # --- create array cell -------------------------------------------------
    array_cell = gdstk.Cell(name)
    lib.add(array_cell)

    # Track x-positions for each logical column
    col_x_positions: List[float] = []
    current_x = left_margin
    strap_count = 0

    for col_type in col_layout:
        if col_type == "strap":
            col_x_positions.append(current_x)
            current_x += wlstrap_w
        else:
            col_x_positions.append(current_x)
            current_x += cw

    total_core_width = current_x - left_margin
    total_core_height = num_rows * ch
    right_edge = current_x

    # --- place core array --------------------------------------------------
    bit_col_index = 0
    for i, col_type in enumerate(col_layout):
        x_pos = col_x_positions[i]

        for row in range(num_rows):
            y_pos = bottom_margin + row * ch

            if col_type == "strap":
                # Alternate between VDD strap (wlstrap) and GND strap (wlstrap_p)
                if strap_count % 2 == 0:
                    strap_cell = wlstrap_cell
                else:
                    strap_cell = wlstrap_p_cell

                if strap_cell:
                    y_mirror = (row % 2) == 1
                    _place_cell(
                        array_cell, strap_cell,
                        x_pos, y_pos, wlstrap_w, wlstrap_h,
                        x_mirror=False, y_mirror=y_mirror,
                    )
            else:
                # Normal bitcell
                x_mirror = (bit_col_index % 2) == 1
                y_mirror = (row % 2) == 1
                _place_cell(
                    array_cell, bit_cell,
                    x_pos, y_pos, cw, ch,
                    x_mirror=x_mirror, y_mirror=y_mirror,
                )

        if col_type == "strap":
            strap_count += 1
        else:
            bit_col_index += 1

    # --- place dummy border ------------------------------------------------
    if with_dummy and dummy_cell:
        # Bottom row of dummies (below the core)
        for i, col_type in enumerate(col_layout):
            if col_type != "bit":
                continue
            x_pos = col_x_positions[i]
            _place_cell(
                array_cell, dummy_cell,
                x_pos, bottom_margin - dummy_h, dummy_w, dummy_h,
                x_mirror=False, y_mirror=True,
            )

        # Top row of dummies (above the core)
        for i, col_type in enumerate(col_layout):
            if col_type != "bit":
                continue
            x_pos = col_x_positions[i]
            top_y = bottom_margin + num_rows * ch
            _place_cell(
                array_cell, dummy_cell,
                x_pos, top_y, dummy_w, dummy_h,
                x_mirror=False, y_mirror=(num_rows % 2 == 1),
            )

        # Left column of dummies
        for row in range(num_rows):
            y_pos = bottom_margin + row * ch
            _place_cell(
                array_cell, dummy_cell,
                left_margin - dummy_w, y_pos, dummy_w, dummy_h,
                x_mirror=True, y_mirror=(row % 2 == 1),
            )

        # Right column of dummies
        for row in range(num_rows):
            y_pos = bottom_margin + row * ch
            _place_cell(
                array_cell, dummy_cell,
                right_edge, y_pos, dummy_w, dummy_h,
                x_mirror=(bit_col_index % 2 == 1),
                y_mirror=(row % 2 == 1),
            )

    # --- place column end cells --------------------------------------------
    if with_dummy and colend_cell:
        for i, col_type in enumerate(col_layout):
            if col_type != "bit":
                # Use center colend for strap columns if available
                if colend_cent_cell and col_type == "strap":
                    x_pos = col_x_positions[i]
                    # Bottom column end
                    _place_cell(
                        array_cell, colend_cent_cell,
                        x_pos, 0.0, colend_cent_w, colend_cent_h,
                        x_mirror=False, y_mirror=True,
                    )
                    # Top column end
                    top_y = bottom_margin + num_rows * ch
                    _place_cell(
                        array_cell, colend_cent_cell,
                        x_pos, top_y, colend_cent_w, colend_cent_h,
                        x_mirror=False, y_mirror=False,
                    )
                continue

            x_pos = col_x_positions[i]

            # Bottom column end (placed below dummy row or at bottom)
            _place_cell(
                array_cell, colend_cell,
                x_pos, 0.0, colend_w, colend_h,
                x_mirror=False, y_mirror=True,
            )

            # Top column end (placed above dummy row or at top)
            top_y = bottom_margin + num_rows * ch + (dummy_h if dummy_cell else 0.0)
            _place_cell(
                array_cell, colend_cell,
                x_pos, top_y, colend_w, colend_h,
                x_mirror=False, y_mirror=False,
            )

    # --- place row end cells -----------------------------------------------
    if with_dummy and rowend_cell:
        for row in range(num_rows):
            y_pos = bottom_margin + row * ch
            y_mirror = (row % 2) == 1

            # Left row end
            _place_cell(
                array_cell, rowend_cell,
                0.0, y_pos, rowend_w, rowend_h,
                x_mirror=True, y_mirror=y_mirror,
            )

            # Right row end
            right_x = right_edge + (dummy_w if dummy_cell else 0.0)
            _place_cell(
                array_cell, rowend_cell,
                right_x, y_pos, rowend_w, rowend_h,
                x_mirror=False, y_mirror=y_mirror,
            )

    # --- place corner cells ------------------------------------------------
    if with_dummy and corner_cell:
        # Bottom-left
        _place_cell(
            array_cell, corner_cell,
            0.0, 0.0, corner_w, corner_h,
            x_mirror=True, y_mirror=True,
        )
        # Bottom-right
        br_x = right_edge + (dummy_w if dummy_cell else 0.0)
        _place_cell(
            array_cell, corner_cell,
            br_x, 0.0, corner_w, corner_h,
            x_mirror=False, y_mirror=True,
        )
        # Top-left
        top_y = bottom_margin + num_rows * ch + (dummy_h if dummy_cell else 0.0)
        _place_cell(
            array_cell, corner_cell,
            0.0, top_y, corner_w, corner_h,
            x_mirror=True, y_mirror=False,
        )
        # Top-right
        _place_cell(
            array_cell, corner_cell,
            br_x, top_y, corner_w, corner_h,
            x_mirror=False, y_mirror=False,
        )

    # --- add routing -------------------------------------------------------
    if with_routing:
        from rekolektion.array.routing import route_array

        route_array(
            array_cell,
            bitcell,
            num_rows,
            num_cols,
            x_offset=left_margin,
            y_offset=bottom_margin,
            array_width=total_core_width,
            array_height=total_core_height,
        )

    # --- write output ------------------------------------------------------
    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return lib
