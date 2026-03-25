"""Word line, bit line, and power rail routing for SRAM arrays.

Generates metal routing stripes that connect pins across the tiled array:
- Word lines (WL): horizontal met1 stripes connecting all cells in each row
- Bit line pairs (BL/BR): vertical met2 stripes connecting all cells in each column
- Power rails (VPWR/VGND): horizontal met2 stripes for each row's power pins

Uses pin positions from the BitcellInfo LEF data.

SKY130 layer numbers (GDS layer/datatype):
  met1 = 68/20
  met2 = 69/20
"""

from __future__ import annotations

from typing import List, Tuple

import gdstk

from rekolektion.bitcell.base import BitcellInfo

# ---------------------------------------------------------------------------
# SKY130 GDS layer map
# ---------------------------------------------------------------------------

# (layer_number, datatype)
LAYER_MET1 = (68, 20)
LAYER_MET2 = (69, 20)

# Routing widths in microns
WL_WIDTH = 0.14       # Word line on met1: minimum width
BL_WIDTH = 0.14       # Bit lines on met2: minimum width
POWER_WIDTH = 0.28    # Power rails on met2: 2x minimum for lower IR drop


# ---------------------------------------------------------------------------
# Routing helpers
# ---------------------------------------------------------------------------

def _hstripe(
    cell: gdstk.Cell,
    y_center: float,
    x_start: float,
    x_end: float,
    width: float,
    layer: Tuple[int, int],
) -> None:
    """Add a horizontal metal stripe to the cell."""
    y_lo = y_center - width / 2.0
    y_hi = y_center + width / 2.0
    cell.add(gdstk.rectangle(
        (x_start, y_lo), (x_end, y_hi),
        layer=layer[0], datatype=layer[1],
    ))


def _vstripe(
    cell: gdstk.Cell,
    x_center: float,
    y_start: float,
    y_end: float,
    width: float,
    layer: Tuple[int, int],
) -> None:
    """Add a vertical metal stripe to the cell."""
    x_lo = x_center - width / 2.0
    x_hi = x_center + width / 2.0
    cell.add(gdstk.rectangle(
        (x_lo, y_start), (x_hi, y_end),
        layer=layer[0], datatype=layer[1],
    ))


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def route_word_lines(
    cell: gdstk.Cell,
    bitcell: BitcellInfo,
    num_rows: int,
    num_cols: int,
    x_offset: float = 0.0,
    y_offset: float = 0.0,
    array_width: float | None = None,
) -> None:
    """Add horizontal met1 word-line stripes for each row.

    The WL pin in the bitcell LEF may not exist (the foundry opt1 cell
    doesn't have a WL pin in its LEF).  In that case we place the WL
    stripe at the met2 obstruction midpoint, which is where the WL
    crosses the cell on met1 (y ~ 0.38 from LEF OBS met2 RECT).

    Parameters
    ----------
    cell : gdstk.Cell
        Array cell to add routing to.
    bitcell : BitcellInfo
        Bitcell info with pin positions.
    num_rows, num_cols : int
        Array dimensions.
    x_offset, y_offset : float
        Offset of the bitcell array origin (e.g., after dummy border).
    array_width : float, optional
        Total width to extend WL stripes. Defaults to num_cols * cell_width.
    """
    cw = bitcell.cell_width
    ch = bitcell.cell_height
    total_w = array_width if array_width is not None else num_cols * cw

    # WL y-position within a cell.  The foundry cell opt1 doesn't have
    # an explicit WL pin in the LEF, but the met2 OBS at y=0.295..0.465
    # tells us the WL crosses at roughly y=0.38 on met1.
    if "WL" in bitcell.pins:
        wl_y_local = bitcell.pins["WL"].y
    else:
        # Fallback: WL runs at ~0.38um from cell bottom (from OBS analysis)
        wl_y_local = 0.38

    for row in range(num_rows):
        # In mirrored rows the WL y-position flips.
        if row % 2 == 0:
            wl_y = y_offset + row * ch + wl_y_local
        else:
            wl_y = y_offset + row * ch + (ch - wl_y_local)

        _hstripe(cell, wl_y, x_offset, x_offset + total_w, WL_WIDTH, LAYER_MET1)


def route_bit_lines(
    cell: gdstk.Cell,
    bitcell: BitcellInfo,
    num_rows: int,
    num_cols: int,
    x_offset: float = 0.0,
    y_offset: float = 0.0,
    array_height: float | None = None,
) -> None:
    """Add vertical met2 bit-line pair stripes for each column.

    Parameters
    ----------
    cell : gdstk.Cell
        Array cell to add routing to.
    bitcell : BitcellInfo
        Bitcell info with pin positions.
    num_rows, num_cols : int
        Array dimensions.
    x_offset, y_offset : float
        Offset of the bitcell array origin.
    array_height : float, optional
        Total height to extend BL stripes.
    """
    cw = bitcell.cell_width
    ch = bitcell.cell_height
    total_h = array_height if array_height is not None else num_rows * ch

    # BL and BR x-positions within a cell.
    # The foundry cell has BL on met1 centered around x=0.42, BR around x=0.78.
    bl_x_local = bitcell.pins["BL"].x if "BL" in bitcell.pins else 0.42
    br_x_local = bitcell.pins["BR"].x if "BR" in bitcell.pins else 0.78

    for col in range(num_cols):
        # In mirrored columns the BL/BR x-positions flip.
        if col % 2 == 0:
            bl_x = x_offset + col * cw + bl_x_local
            br_x = x_offset + col * cw + br_x_local
        else:
            bl_x = x_offset + col * cw + (cw - bl_x_local)
            br_x = x_offset + col * cw + (cw - br_x_local)

        _vstripe(cell, bl_x, y_offset, y_offset + total_h, BL_WIDTH, LAYER_MET2)
        _vstripe(cell, br_x, y_offset, y_offset + total_h, BL_WIDTH, LAYER_MET2)


def route_power_rails(
    cell: gdstk.Cell,
    bitcell: BitcellInfo,
    num_rows: int,
    num_cols: int,
    x_offset: float = 0.0,
    y_offset: float = 0.0,
    array_width: float | None = None,
) -> None:
    """Add horizontal met2 power rail stripes (VPWR and VGND).

    Power rails run at the top and bottom edges of each row, shared
    between adjacent rows (due to Y-mirroring).

    Parameters
    ----------
    cell : gdstk.Cell
        Array cell to add routing to.
    bitcell : BitcellInfo
        Bitcell info with pin positions.
    num_rows, num_cols : int
        Array dimensions.
    x_offset, y_offset : float
        Offset of the bitcell array origin.
    array_width : float, optional
        Total width to extend power stripes.
    """
    cw = bitcell.cell_width
    ch = bitcell.cell_height
    total_w = array_width if array_width is not None else num_cols * cw

    # VGND y-position within cell (from LEF: met2 RECT at y=0.635..0.895)
    # Center is ~0.765.
    # VPWR y-position within cell (from LEF: met2 RECT at y=1.025..1.285)
    # Center is ~1.155.
    vgnd_y_local = 0.765
    vpwr_y_local = 1.155

    if "VGND" in bitcell.pins:
        # Use the met2 port if available
        for x, y, layer in bitcell.pins["VGND"].ports:
            if layer == "met2":
                vgnd_y_local = y
                break

    if "VPWR" in bitcell.pins:
        for x, y, layer in bitcell.pins["VPWR"].ports:
            if layer == "met2":
                vpwr_y_local = y
                break

    for row in range(num_rows):
        if row % 2 == 0:
            gnd_y = y_offset + row * ch + vgnd_y_local
            pwr_y = y_offset + row * ch + vpwr_y_local
        else:
            gnd_y = y_offset + row * ch + (ch - vgnd_y_local)
            pwr_y = y_offset + row * ch + (ch - vpwr_y_local)

        _hstripe(cell, gnd_y, x_offset, x_offset + total_w, POWER_WIDTH, LAYER_MET2)
        _hstripe(cell, pwr_y, x_offset, x_offset + total_w, POWER_WIDTH, LAYER_MET2)


def route_array(
    cell: gdstk.Cell,
    bitcell: BitcellInfo,
    num_rows: int,
    num_cols: int,
    x_offset: float = 0.0,
    y_offset: float = 0.0,
    array_width: float | None = None,
    array_height: float | None = None,
) -> None:
    """Add all routing (WL, BL/BR, power) to the array cell.

    Convenience wrapper that calls all three routing functions.
    """
    route_word_lines(
        cell, bitcell, num_rows, num_cols,
        x_offset, y_offset, array_width,
    )
    route_bit_lines(
        cell, bitcell, num_rows, num_cols,
        x_offset, y_offset, array_height,
    )
    route_power_rails(
        cell, bitcell, num_rows, num_cols,
        x_offset, y_offset, array_width,
    )
