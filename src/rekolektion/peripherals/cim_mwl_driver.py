"""MWL driver for CIM arrays — wraps SKY130 foundry stdcell buf_2.

Rather than hand-rolling a custom analog buffer (and its DRC/LVS), we
use the SkyWater PDK's `sky130_fd_sc_hd__buf_2` standard cell directly.
It's a fully characterised 2-stage CMOS buffer (4 transistors), DRC-
clean, LVS-clean, and shipped with the PDK.

The foundry cell ports are: A (input), X (output), VPWR/VGND (rails),
VPB/VNB (n-well / p-substrate body bias).  At the macro level we tie
VPB↔VPWR and VNB↔VGND through the PDN.

Cell footprint: 1.84 × 2.72 µm (drawn).  The bbox is wider when N-well
overhang is included (~2.22 × 3.20 µm).  All four CIM bitcell variants
have row pitch ≥ 3.915 µm, leaving comfortable margin for vertical
tiling.
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk


_CELL_NAME = "sky130_fd_sc_hd__buf_2"
_GDS_PATH = Path(__file__).parent / "cells" / f"{_CELL_NAME}.gds"


def generate_mwl_driver() -> Tuple[gdstk.Cell, gdstk.Library]:
    """Load the foundry buf_2 cell and return (cell, library).

    Reads the cached GDS that was extracted from the foundry library.
    Returns a fresh library each call so callers can copy cells into
    their own libraries without aliasing.
    """
    if not _GDS_PATH.exists():
        raise FileNotFoundError(
            f"Foundry buf_2 GDS missing at {_GDS_PATH}.  Re-extract it from "
            f"the SkyWater PDK (libs.ref/sky130_fd_sc_hd/gds/sky130_fd_sc_hd.gds)."
        )
    src = gdstk.read_gds(str(_GDS_PATH))
    cell = next((c for c in src.cells if c.name == _CELL_NAME), None)
    if cell is None:
        raise RuntimeError(
            f"Cell {_CELL_NAME!r} not found in {_GDS_PATH}"
        )
    lib = gdstk.Library(name=f"{_CELL_NAME}_lib", unit=src.unit, precision=src.precision)
    lib.add(cell, *cell.dependencies(True))
    return cell, lib


def get_cell_dimensions() -> Tuple[float, float]:
    """Return (width, height) of the buf_2 cell in µm.

    Width: rail-to-rail X span (1.84 µm).
    Height: rail-to-rail Y span (2.72 µm).
    Note: the GDS bbox is larger due to N-well overhang (~2.22 × 3.20 µm),
    but for placement we use the rail span which is what abuts neighbours.
    """
    return 1.84, 2.72
