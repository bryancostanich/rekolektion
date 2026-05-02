"""Bridged wlstrap wrapper for the post-Option-B production array.

Production bitcell pitch grew from 1.58 → 2.22 µm when the bridged-
bitcell wrapper landed (`sky130_sp_bridged.py`).  The foundry
`sky130_fd_bd_sram__sram_sp_wlstrap` is 1.58 µm tall and no longer
matches the bitcell row pitch — placing it in `_place_strap_columns`
at row pitch 2.22 leaves 0.64 µm gaps that overlap with the next
mirrored row's strap, breaking WL bridging and N-tap body bias.

This wrapper restores the alignment:
  - foundry strap shifted up by BRIDGE_H (0.30 µm) — matches the
    foundry-bitcell shift inside the bridged-bitcell wrapper.
  - NWELL filler polygons at wrapper y=[0, BRIDGE_H] and
    y=[1.88, WRAPPER_H] keep NWELL continuous through the wrapper
    so adjacent rows' NWELLs (with Y-mirror) merge.
  - No new transistors, no new contacts — this is structural
    routing only.
  - Same width as foundry strap (1.41 µm; LEF SIZE).

Net effect on macro size: zero.  The bitcell wrapper already added
the 0.64 µm row pitch growth; this strap wrapper just fills the
strap-column gap created by the foundry strap's narrower height.
"""
from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.bitcell.sky130_cim_drain_bridge import BRIDGE_H
from rekolektion.bitcell.sky130_sp_bridged import (
    BR_EXT_H, _LAYER_NWELL, _LAYER_AREAID_SRAM,
)


_FOUNDRY_STRAP_GDS = (
    Path(__file__).parent.parent
    / "array" / "cells" / "sky130_fd_bd_sram__sram_sp_wlstrap.gds"
)
_FOUNDRY_STRAP_NAME = "sky130_fd_bd_sram__sram_sp_wlstrap"
_FOUNDRY_STRAP_W = 1.410     # placement pitch (LEF SIZE)
_FOUNDRY_STRAP_H = 1.580
# Foundry strap NWELL X extent (from GDS inspection: NWELL polygon
# spans x=[0.000, 1.300]; the cell-pitch is 1.410 with 0.110 µm
# right-side margin where adjacent N+ DIFF tucks under SRAM-COREID
# diff/tap.9 relaxation).
_FOUNDRY_NWELL_X0 = 0.000
_FOUNDRY_NWELL_X1 = 1.300

WRAPPER_NAME = "sky130_fd_bd_sram__sram_sp_wlstrap_bridged"
WRAPPER_W: float = _FOUNDRY_STRAP_W                      # 1.41
WRAPPER_H: float = BRIDGE_H + _FOUNDRY_STRAP_H + BR_EXT_H  # 0.30 + 1.58 + 0.34 = 2.22


def _rect(cell: gdstk.Cell, layer: Tuple[int, int],
          x0: float, y0: float, x1: float, y1: float) -> None:
    cell.add(gdstk.rectangle(
        (x0, y0), (x1, y1), layer=layer[0], datatype=layer[1]
    ))


def create_sp_wlstrap_bridged_cell() -> Tuple[gdstk.Library, gdstk.Cell]:
    """Build the bridged wlstrap wrapper.

    Returns (library, wrapper_cell).  The library contains:
      - foundry sram_sp_wlstrap (instanced as sub-cell, unmodified)
      - the wrapper top cell with foundry strap shifted up by BRIDGE_H
        and NWELL filler polygons in the BL-bridge and BR-Phase2 zones.
    """
    lib = gdstk.Library(name=f"{WRAPPER_NAME}_lib", unit=1e-6, precision=5e-9)

    # Load foundry wlstrap GDS unmodified.
    src = gdstk.read_gds(str(_FOUNDRY_STRAP_GDS))
    foundry = next(c for c in src.cells if c.name == _FOUNDRY_STRAP_NAME)
    foundry_copy = foundry.copy(_FOUNDRY_STRAP_NAME)
    lib.add(foundry_copy)

    wrapper = gdstk.Cell(WRAPPER_NAME)
    lib.add(wrapper)

    # Foundry strap shifted up by BRIDGE_H so its M1 VPWR rails align
    # with the bridged-bitcell foundry M1 VPWR rails (both at wrapper
    # y=[0.30, 1.88]).
    wrapper.add(gdstk.Reference(foundry_copy, origin=(0.0, BRIDGE_H)))

    # NWELL filler at wrapper y=[0, BRIDGE_H] (BL-bridge area).
    # X range matches foundry strap NWELL so DRC relationship with
    # adjacent N+ DIFFs of bitcells is identical to foundry's design.
    _rect(wrapper, _LAYER_NWELL,
          _FOUNDRY_NWELL_X0, 0.0,
          _FOUNDRY_NWELL_X1, BRIDGE_H)
    # NWELL filler at wrapper y=[BRIDGE_H+_FOUNDRY_STRAP_H, WRAPPER_H]
    # (BR-Phase2 area).
    _rect(wrapper, _LAYER_NWELL,
          _FOUNDRY_NWELL_X0, BRIDGE_H + _FOUNDRY_STRAP_H,
          _FOUNDRY_NWELL_X1, WRAPPER_H)

    # SRAM areaid covering the entire wrapper (SRAM-COREID rule
    # relaxation applies to the foundry strap's NSDM/PSDM/contacts).
    _rect(wrapper, _LAYER_AREAID_SRAM, 0.0, 0.0, WRAPPER_W, WRAPPER_H)

    return lib, wrapper


def write_bridged_wlstrap_gds(out_path: Path) -> Path:
    """Generate and write the bridged wlstrap GDS to `out_path`."""
    lib, _ = create_sp_wlstrap_bridged_cell()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    lib.write_gds(str(out_path))
    return out_path
