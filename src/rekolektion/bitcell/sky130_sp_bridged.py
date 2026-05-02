"""Production-grade SP bitcell wrapper with drain-to-rail bridges.

The foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` ships with 0 LICON1
and 0 MCON internally — the access-transistor drains have no electrical
path to the BL/BR met1 rails inside the cell.  Foundry intent: external
strap cells (`colend`, etc.) provide the contact stack; without them,
the drain DIFFs are floating and the bitcell is non-functional silicon.

This wrapper closes the gap.  Same architecture as CIM Option B
(audit/smoking_guns.md, issue #7):
  - Foundry cell shifted up by BRIDGE_H so a `sky130_cim_drain_bridge_v1`
    sub-cell (already validated for CIM) sits at the bottom and bridges
    the bottom-access-tx drain to the BL met1 rail via DIFF abutment +
    LICON1 + LI1 + MCON + M1 stack.
  - Phase 2 BR contact stack (top-access-tx drain → BR rail) added
    inline at the wrapper level above the foundry top edge — uses the
    same coordinates and layer geometry as CIM's qtap modification, but
    placed at the wrapper level instead of inside a modified foundry
    cell so the foundry GDS stays untouched.
  - Pitch grows from 1.58 µm to 2.21 µm (+39%) to accommodate the BL
    bridge below and the BR Phase 2 NSDM extension above.

LVS-side: the wrapper subckt explicitly binds the foundry's
auto-named `a_38_0#` and `a_38_292#` ports (access-tx drain stubs) to
BL and BR respectively, modelling the silicon connectivity.  The
foundry cell's own SPICE stays unmodified.
"""
from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.bitcell.sky130_cim_drain_bridge import (
    BRIDGE_H,
    create_drain_bridge_cell,
)


_FOUNDRY_GDS = (
    Path(__file__).parent / "cells"
    / "sky130_fd_bd_sram__sram_sp_cell_opt1.gds"
)
_FOUNDRY_NAME = "sky130_fd_bd_sram__sram_sp_cell_opt1"
_FOUNDRY_W = 1.310    # placement pitch (LEF SIZE)
_FOUNDRY_H = 1.580

# Phase 2 BR extension above foundry top — needs 0.330 µm above
# foundry top edge for NSDM (DIFF + 0.05 enclosure on Y).
BR_EXT_H: float = 0.34   # round up for safety margin

# Wrapper pitch.
WRAPPER_W: float = _FOUNDRY_W
WRAPPER_H: float = BRIDGE_H + _FOUNDRY_H + BR_EXT_H   # 0.30 + 1.58 + 0.34 = 2.22

# GDS layer constants (must match foundry mapping).
_LAYER_NWELL    = (64, 20)
_LAYER_DIFF     = (65, 20)
_LAYER_POLY     = (66, 20)
_LAYER_LICON1   = (66, 44)
_LAYER_LI1      = (67, 20)
_LAYER_MCON     = (67, 44)
_LAYER_MET1     = (68, 20)
_LAYER_NSDM     = (93, 44)
_LAYER_AREAID_SRAM = (81, 2)

WRAPPER_NAME = "sky130_fd_bd_sram__sram_sp_cell_bridged"


def _rect(cell: gdstk.Cell, layer: Tuple[int, int],
          x0: float, y0: float, x1: float, y1: float) -> None:
    cell.add(gdstk.rectangle(
        (x0, y0), (x1, y1), layer=layer[0], datatype=layer[1]
    ))


def _add_phase2_br(cell: gdstk.Cell, foundry_origin_y: float) -> None:
    """Add the top-access-tx-drain → BR contact stack.

    Coordinates are foundry-local; the wrapper instance places the
    foundry cell at (0, foundry_origin_y), so we add foundry_origin_y
    to every Y in the polygon.

    Geometry mirrors `sky130_cim_supercell._load_foundry_cell_with_q_tap`
    Phase 2 BR section.  See that function for the constraint analysis.
    """
    LICON_HALF = 0.085   # LICON1 0.17×0.17

    # ----- TOP access tx → BR -----
    # DIFF extension above foundry POLY (foundry POLY at y=[1.310, 1.460])
    # plus 0.075 poly.4 spacing → DIFF y_min ≥ 1.535.  Extends UP into
    # wrapper annex, abuts foundry drain DIFF (0.190, 1.460)-(0.330, 1.580)
    # at y=1.535-1.580 in X overlap.
    _DRN_X0, _DRN_X1 = 0.115, 0.405
    _DRN_Y0, _DRN_Y1 = 1.535, 1.860
    _rect(cell, _LAYER_DIFF, _DRN_X0, foundry_origin_y + _DRN_Y0,
          _DRN_X1, foundry_origin_y + _DRN_Y1)
    # NSDM with 0.05 enclosure (sram-relaxed).
    _rect(cell, _LAYER_NSDM, _DRN_X0 - 0.05, foundry_origin_y + _DRN_Y0 - 0.05,
          _DRN_X1 + 0.05, foundry_origin_y + _DRN_Y1 + 0.05)
    # LICON1 centred at (0.260, 1.680) — DIFF enclosure 0.06 each side.
    _LIC_CX, _LIC_CY = 0.260, 1.680
    _rect(cell, _LAYER_LICON1,
          _LIC_CX - LICON_HALF, foundry_origin_y + _LIC_CY - LICON_HALF,
          _LIC_CX + LICON_HALF, foundry_origin_y + _LIC_CY + LICON_HALF)
    # LI1 wrapper around LICON1 (li.5 enclosure ≥ 0.08).
    _rect(cell, _LAYER_LI1,
          0.095, foundry_origin_y + 1.495,
          0.550, foundry_origin_y + 1.825)
    # LI1 east extension toward BR MCON area.
    _rect(cell, _LAYER_LI1,
          0.660, foundry_origin_y + 1.495,
          0.990, foundry_origin_y + 1.705)
    # MCON over BR rail at x=0.825 — 0.14 gap from foundry M1 at x=0.540
    # (BR rail is x=[0.71, 0.85], foundry M1 stub at x=[0.30, 0.54]).
    _MCON_CX, _MCON_CY = 0.825, 1.540
    _rect(cell, _LAYER_MCON,
          _MCON_CX - LICON_HALF, foundry_origin_y + _MCON_CY - LICON_HALF,
          _MCON_CX + LICON_HALF, foundry_origin_y + _MCON_CY + LICON_HALF)
    # M1 wrapper extending BR rail east, 0.14 gap from foundry M1 west
    # edge at x=0.540 and from VPWR strap at x=1.130.
    _rect(cell, _LAYER_MET1,
          0.680, foundry_origin_y + 1.395,
          0.970, foundry_origin_y + 1.685)


def create_sp_bridged_cell() -> Tuple[gdstk.Library, gdstk.Cell]:
    """Build the bridged SP bitcell wrapper.

    Returns (library, wrapper_cell).  The library contains:
      - foundry sram_sp_cell_opt1 (instanced as sub-cell, unmodified GDS)
      - sky130_cim_drain_bridge_v1 (instanced as sub-cell)
      - the wrapper top cell with foundry shifted up, bridge cell at
        bottom, and Phase 2 BR polygons in the annex above foundry.
    """
    lib = gdstk.Library(name=f"{WRAPPER_NAME}_lib", unit=1e-6, precision=5e-9)

    # Load foundry cell GDS unmodified.
    foundry_src = gdstk.read_gds(str(_FOUNDRY_GDS))
    foundry = next(c for c in foundry_src.cells if c.name == _FOUNDRY_NAME)
    foundry_copy = foundry.copy(_FOUNDRY_NAME)
    lib.add(foundry_copy)

    # Bridge cell (BL drain → BL rail strap).
    bridge = create_drain_bridge_cell()
    lib.add(bridge)

    # Wrapper top cell.
    wrapper = gdstk.Cell(WRAPPER_NAME)
    lib.add(wrapper)

    # Bridge cell at the bottom (cell-local y=[0, BRIDGE_H]).
    wrapper.add(gdstk.Reference(bridge, origin=(0.0, 0.0)))
    # Foundry cell shifted up by BRIDGE_H.
    wrapper.add(gdstk.Reference(foundry_copy, origin=(0.0, BRIDGE_H)))

    # Phase 2 BR polygons in the annex above foundry top (cell-local
    # y > BRIDGE_H + _FOUNDRY_H = 1.88), reaching to y ~2.21.  Emitted
    # at wrapper level so foundry GDS stays untouched.
    _add_phase2_br(wrapper, foundry_origin_y=BRIDGE_H)

    # SRAM areaid covering the entire wrapper (foundry-COREID rule
    # relaxation applies to all the contact-stack polygons).
    _rect(wrapper, _LAYER_AREAID_SRAM, 0.0, 0.0, WRAPPER_W, WRAPPER_H)

    return lib, wrapper


def write_bridged_cell_gds(out_path: Path) -> Path:
    """Generate and write the bridged-cell GDS to `out_path`."""
    lib, _ = create_sp_bridged_cell()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    lib.write_gds(str(out_path))
    return out_path
