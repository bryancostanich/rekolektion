"""SKY130 CIM drain-bridge cell — external strap for issue #7.

Foundry sram_sp_cell_opt1 ships with 0 LICON1 / 0 MCON internally.  The
two access-tx drains (PG1 → BL, PG2 → BR) are electrically isolated
from the BL/BR met1 rails.  Foundry intent: external strap cells
(wlstrap, colend) provide the contacts.

This module supplies a strap cell for the BL drain (bottom access-tx →
BL met1 rail).  The cell is instanced inside each supercell at the
bottom (cell-local y=[0, BRIDGE_H]); the foundry cell sits on top
(cell-local y=[BRIDGE_H, BRIDGE_H + 1.58]).

Why external (not in qtap):
  Phase-2-in-qtap had the drain bridge polygons extending below the
  foundry cell (cell-local y < 0).  Under Y-mirror tiling, the
  mirrored bridge LICON1 ended up overlapping the next row's WL_BOT
  POLY strip by 0.04 µm — a 64-instances-per-row silicon short of
  BL drain to wl_0_<row>.  Externalising into a dedicated cell that
  sits *above* the supercell origin keeps every Phase 2 polygon
  inside its own row's pitch under both mirror orientations.

Layout (cell-local µm; cell at (0, 0) to (BRIDGE_W, BRIDGE_H)):
  - DIFF      x=[0.115, 0.405] y=[0.000, 0.300]
      Abuts foundry's bottom access-tx drain DIFF at y=BRIDGE_H
      via shared cell boundary (DIFF abutment merges nets).
  - NSDM      x=[0.065, 0.455] y=[0.000, 0.300]
      Encloses DIFF with sram-relaxed margin; merges with foundry NSDM.
  - LICON1    x=[0.175, 0.345] y=[0.115, 0.285]   (0.17 × 0.17, sram-relaxed)
      DIFF enclosure 0.04 each side (sram-relaxed licon.5a).
      Distance to nearest foundry POLY (WL_BOT at supercell-local
      y=0.42 onward) ≥ 0.135 µm — well clear of licon.5b 0.075 µm.
  - LI1       x=[0.095, 0.585] y=[0.000, 0.300]
      Covers LICON1 + MCON; full bridge height so adjacent bridge
      cells (foundry-foundry boundary in tiling) abut LI1 cleanly,
      avoiding li1.4 0.17 µm spacing violations.
  - MCON      x=[0.300, 0.470] y=[0.115, 0.285]   (0.17 × 0.17)
      Center at BL rail X (0.385 ≈ midpoint of foundry BL rail
      0.350-0.490).  Inside M1 wrapper with 0.04 enclosure.
  - M1 wrap   x=[0.220, 0.510] y=[0.000, 0.300]
      Width 0.29; spacing to foundry VGND M1 (x=[0,0.07]) = 0.15
      and to Q-related M1 (x=0.66) = 0.15 — both ≥ m1.2 0.14.
      Full bridge height so M1 wrappers abut foundry BL rail
      (which extends to cell boundary y=0) on both sides.
  - SRAM areaid  x=[0, BRIDGE_W] y=[0, BRIDGE_H]
      Whole bridge inside SRAM relaxed-rule region.
"""
from __future__ import annotations

import gdstk

# Bridge cell footprint.
BRIDGE_W: float = 1.310   # match foundry sram_sp_cell_opt1 cell width
BRIDGE_H: float = 0.300   # provides LICON1 enclosure + abutment headroom

# Layer mapping (must match sky130_cim_supercell.py).
_LAYER_DIFF        = (65, 20)
_LAYER_LICON1      = (66, 44)
_LAYER_LI1         = (67, 20)
_LAYER_MCON        = (67, 44)
_LAYER_MET1        = (68, 20)
_LAYER_NSDM        = (93, 44)
_LAYER_PSDM        = (94, 20)
_LAYER_NWELL       = (64, 20)
_LAYER_AREAID_SRAM = (81, 2)

_BRIDGE_CELL_NAME = "sky130_cim_drain_bridge_v1"

_LICON_HALF = 0.085   # 0.17 / 2
_DIFF_X0, _DIFF_X1 = 0.115, 0.405
_NSDM_ENC = 0.050
_LICON_CX, _LICON_CY = 0.260, 0.150
_MCON_CX,  _MCON_CY  = 0.385, 0.150
_LI1_X0, _LI1_X1 = 0.095, 0.585
_M1_X0,  _M1_X1  = 0.220, 0.510

# T5.2-A — Per-supercell N-tap (P+ DIFF in NWELL) for proper PMOS body
# bias.  NWELL inherits the foundry sram_sp_cell_opt1's NWELL X range
# [0.720, 1.200] and extends DOWN through the bridge cell so the
# foundry NWELL (above) and bridge NWELL form a continuous well.  The
# P+ DIFF is contacted via LICON1 → LI1 → MCON to the foundry VPWR M1
# rail (x=[1.130, 1.200]), so every bitcell instance has its own real
# metal path to VPWR for body bias.  This replaces the
# `re.sub("w_n?\\d+_n?\\d+#", "VPWR")` LVS-rewrite hack.
_NWELL_X0, _NWELL_X1 = 0.720, 1.200
_PTAP_DIFF_X0, _PTAP_DIFF_X1 = 0.825, 1.095   # 0.27 wide
_PTAP_DIFF_Y0, _PTAP_DIFF_Y1 = 0.060, 0.240   # 0.18 tall
_PSDM_ENC = 0.050
_PTAP_LICON_CX, _PTAP_LICON_CY = 0.960, 0.150  # centred on P+ DIFF
_PTAP_LI1_X0, _PTAP_LI1_X1 = 0.795, 1.235     # spans LICON to MCON
_PTAP_MCON_CX, _PTAP_MCON_CY = 1.165, 0.150   # aligned with foundry VPWR rail
_VPWR_M1_X0, _VPWR_M1_X1 = 1.130, 1.200       # matches foundry VPWR rail


def _rect(cell: gdstk.Cell, layer: tuple[int, int], x0: float, y0: float,
          x1: float, y1: float) -> None:
    cell.add(gdstk.rectangle(
        (x0, y0), (x1, y1), layer=layer[0], datatype=layer[1]
    ))


def create_drain_bridge_cell() -> gdstk.Cell:
    """Build the BL drain-bridge cell.

    Returns a fresh gdstk.Cell named `sky130_cim_drain_bridge_v1`.
    The cell is intended to be instanced inside each supercell at
    (0, 0); the foundry cell sits at (0, BRIDGE_H) above.
    """
    cell = gdstk.Cell(_BRIDGE_CELL_NAME)

    # SRAM areaid — enables relaxed DRC rules in this region.
    _rect(cell, _LAYER_AREAID_SRAM, 0.0, 0.0, BRIDGE_W, BRIDGE_H)

    # DIFF spanning full bridge height; abuts foundry bottom drain
    # DIFF at the cell's TOP edge (y=BRIDGE_H), and abuts the
    # bridge cell of the row below at the BOTTOM edge (y=0) when
    # foundry-foundry boundary tiling produces bridge-bridge
    # abutment.  Same X as foundry's drain DIFF.
    _rect(cell, _LAYER_DIFF, _DIFF_X0, 0.0, _DIFF_X1, BRIDGE_H)

    # NSDM enclosing DIFF with sram-relaxed enclosure (0.05 X each
    # side, kept inside cell on Y so adjacent foundry NSDM at the
    # boundary picks up via abutment merge).
    _rect(cell, _LAYER_NSDM,
          _DIFF_X0 - _NSDM_ENC, 0.0,
          _DIFF_X1 + _NSDM_ENC, BRIDGE_H)

    # LICON1 — DIFF-to-LI1 contact at the centre of bridge DIFF.
    _rect(cell, _LAYER_LICON1,
          _LICON_CX - _LICON_HALF, _LICON_CY - _LICON_HALF,
          _LICON_CX + _LICON_HALF, _LICON_CY + _LICON_HALF)

    # MCON — LI1-to-M1 contact aligned with foundry BL rail X.
    _rect(cell, _LAYER_MCON,
          _MCON_CX - _LICON_HALF, _MCON_CY - _LICON_HALF,
          _MCON_CX + _LICON_HALF, _MCON_CY + _LICON_HALF)

    # LI1 covering LICON1 + MCON; full bridge height for boundary
    # abutment with adjacent-row bridges.
    _rect(cell, _LAYER_LI1, _LI1_X0, 0.0, _LI1_X1, BRIDGE_H)

    # M1 wrapper at BL rail X position; full bridge height so it
    # abuts foundry BL M1 rail on both adjacent rows under the
    # supercell stacking.
    _rect(cell, _LAYER_MET1, _M1_X0, 0.0, _M1_X1, BRIDGE_H)

    return cell


def cell_name() -> str:
    return _BRIDGE_CELL_NAME
