"""Wordline driver mux for burn-in test mode.

Generates a 2:1 mux at each wordline driver output that selects between
normal decoded operation and all-wordlines-active stress mode:

    WL_out = TM ? VDD : WL_decoded

When TM (test mode) is asserted, every wordline is driven high
simultaneously, stressing all bitcells in parallel at elevated voltage
for infant mortality screening.

One cell per row, placed adjacent to the row decoder (after WL gate
if present).  Cell height matches the NAND2 decoder (1.715 µm).

Implementation: transmission-gate mux with two NMOS pass transistors.
  - MN1: passes WL_decoded when TM=0 (gate=TM_B)
  - MN2: passes VDD when TM=1 (gate=TM)
  - Inverter generates TM_B from TM

Usage::

    from rekolektion.peripherals.wl_mux import generate_wl_mux
    cell, lib = generate_wl_mux()
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS


# ---------------------------------------------------------------------------
# Constants — match decoder cell height
# ---------------------------------------------------------------------------

_W_N = 0.42
_L = 0.15
_SD_EXT = 0.30
_POLY_OVH = 0.14
_LICON = 0.17
_LI_ENC = 0.08
_NSDM_ENC = 0.125
_PSDM_ENC = 0.125

_LI_PAD = _LICON + 2 * _LI_ENC
_CELL_WIDTH = 3.5
_CELL_HEIGHT = 1.715  # match decoder NAND2 row height

# Layer shortcuts
_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MCON = LAYERS.MCON.as_tuple
_MET1 = LAYERS.MET1.as_tuple
_VIA = LAYERS.VIA.as_tuple
_MET2 = LAYERS.MET2.as_tuple
_NWELL = LAYERS.NWELL.as_tuple
_NSDM = LAYERS.NSDM.as_tuple
_PSDM = LAYERS.PSDM.as_tuple
_BOUNDARY = (235, 0)


def _snap(v: float, grid: float = 0.005) -> float:
    return round(v / grid) * grid


def _rect(cell: gdstk.Cell, layer: tuple[int, int],
          x0: float, y0: float, x1: float, y1: float) -> None:
    cell.add(gdstk.rectangle(
        (_snap(x0), _snap(y0)), (_snap(x1), _snap(y1)),
        layer=layer[0], datatype=layer[1],
    ))


def _sq_contact(cell: gdstk.Cell, layer: tuple[int, int],
                cx: float, cy: float, size: float) -> None:
    x0 = _snap(cx - size / 2)
    y0 = _snap(cy - size / 2)
    cell.add(gdstk.rectangle(
        (x0, y0), (x0 + size, y0 + size),
        layer=layer[0], datatype=layer[1],
    ))


def generate_wl_mux(
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a wordline driver mux for burn-in test mode.

    2:1 mux: WL_out = TM ? VDD : WL_in

    Uses two NMOS pass transistors + TM inverter.

    Pins: WL_IN (decoded WL), TM (test mode), WL_OUT (to bitcell array),
          VDD, GND.

    Cell height: 1.715 µm (matches NAND2 decoder for row tiling).
    """
    name = cell_name or "wl_mux_burnin"
    width = _snap(_CELL_WIDTH)
    height = _snap(_CELL_HEIGHT)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    _rect(cell, _BOUNDARY, 0, 0, width, height)

    # N-well for inverter PMOS (upper portion)
    _rect(cell, _NWELL, 0, height * 0.55, width, height)

    # VDD rail (met1, top)
    _rect(cell, _MET1, 0, height - 0.14, width, height)
    # GND rail (met1, bottom)
    _rect(cell, _MET1, 0, 0, width, 0.14)

    hw = _W_N / 2
    hl = _L / 2

    # --- Pass transistor 1: WL_in to WL_out (gate = TM_B, passes when TM=0) ---
    mn1_x = 0.8
    mn1_y = 0.55
    _rect(cell, _DIFF, mn1_x - hw, mn1_y - hl - _SD_EXT, mn1_x + hw, mn1_y + hl + _SD_EXT)
    _rect(cell, _NSDM,
          mn1_x - hw - _NSDM_ENC, mn1_y - hl - _SD_EXT - _NSDM_ENC,
          mn1_x + hw + _NSDM_ENC, mn1_y + hl + _SD_EXT + _NSDM_ENC)
    _rect(cell, _POLY, mn1_x - hw - _POLY_OVH, mn1_y - hl, mn1_x + hw + _POLY_OVH, mn1_y + hl)

    # --- Pass transistor 2: VDD to WL_out (gate = TM, passes when TM=1) ---
    mn2_x = 1.8
    mn2_y = 0.55
    _rect(cell, _DIFF, mn2_x - hw, mn2_y - hl - _SD_EXT, mn2_x + hw, mn2_y + hl + _SD_EXT)
    _rect(cell, _NSDM,
          mn2_x - hw - _NSDM_ENC, mn2_y - hl - _SD_EXT - _NSDM_ENC,
          mn2_x + hw + _NSDM_ENC, mn2_y + hl + _SD_EXT + _NSDM_ENC)
    _rect(cell, _POLY, mn2_x - hw - _POLY_OVH, mn2_y - hl, mn2_x + hw + _POLY_OVH, mn2_y + hl)

    # --- TM inverter (generates TM_B for pass transistor 1) ---
    # NMOS
    inv_x = 2.8
    inv_ny = 0.55
    _rect(cell, _DIFF, inv_x - hw, inv_ny - hl - _SD_EXT, inv_x + hw, inv_ny + hl + _SD_EXT)
    _rect(cell, _NSDM,
          inv_x - hw - _NSDM_ENC, inv_ny - hl - _SD_EXT - _NSDM_ENC,
          inv_x + hw + _NSDM_ENC, inv_ny + hl + _SD_EXT + _NSDM_ENC)
    _rect(cell, _POLY, inv_x - hw - _POLY_OVH, inv_ny - hl, inv_x + hw + _POLY_OVH, inv_ny + hl)

    # PMOS
    inv_py = 1.25
    _rect(cell, _DIFF, inv_x - hw, inv_py - hl - _SD_EXT, inv_x + hw, inv_py + hl + _SD_EXT)
    _rect(cell, _PSDM,
          inv_x - hw - _PSDM_ENC, inv_py - hl - _SD_EXT - _PSDM_ENC,
          inv_x + hw + _PSDM_ENC, inv_py + hl + _SD_EXT + _PSDM_ENC)
    _rect(cell, _POLY, inv_x - hw - _POLY_OVH, inv_py - hl, inv_x + hw + _POLY_OVH, inv_py + hl)

    # --- Met2 TM bus (horizontal, shared across all rows) ---
    tm_y = 0.90
    _rect(cell, _MET2, 0, tm_y - 0.07, width, tm_y + 0.07)

    # --- Pin labels ---
    cell.add(gdstk.Label("WL_IN", (_snap(0.15), _snap(mn1_y)),
                          layer=_MET1[0], texttype=_MET1[1]))
    cell.add(gdstk.Label("WL_OUT", (_snap(width - 0.2), _snap(mn1_y)),
                          layer=_MET1[0], texttype=_MET1[1]))
    cell.add(gdstk.Label("TM", (_snap(0.15), _snap(tm_y)),
                          layer=_MET2[0], texttype=_MET2[1]))
    cell.add(gdstk.Label("VDD", (_snap(width / 2), _snap(height)),
                          layer=_MET1[0], texttype=_MET1[1]))
    cell.add(gdstk.Label("GND", (_snap(width / 2), _snap(0)),
                          layer=_MET1[0], texttype=_MET1[1]))

    if output_path:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
