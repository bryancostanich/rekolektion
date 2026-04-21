"""Wordline gating cell generator.

Generates a 2-input AND gate that gates the decoded wordline with a
WL_EN (active-high enable) signal.  When WL_EN=0, the wordline is
forced low regardless of the decoder output.

    WL_gated = WL_decoded & WL_EN

One cell per row, placed adjacent to the row decoder NAND gates.
Cell height matches the NAND2 decoder cell (1.715 µm) for vertical tiling.

Usage::

    from rekolektion.peripherals.wl_gate import generate_wl_gate
    cell, lib = generate_wl_gate()
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS


# ---------------------------------------------------------------------------
# Constants — match decoder cell height (1.715 µm)
# ---------------------------------------------------------------------------

_W_N = 0.42
_W_P = 0.42
_L = 0.15
_SD_EXT = 0.30
_POLY_OVH = 0.14
_LICON = 0.17
_LI_ENC = 0.08
_NSDM_ENC = 0.125
_PSDM_ENC = 0.125

_LI_PAD = _LICON + 2 * _LI_ENC
_CELL_WIDTH = 3.0    # narrow — just one AND gate
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
_BOUNDARY = LAYERS.BOUNDARY.as_tuple  # (235, 4) — sky130 prBoundary


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


def generate_wl_gate(
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a wordline gating AND cell.

    Implements WL_gated = WL_decoded & WL_EN using two series NMOS
    (NAND topology) plus a PMOS pull-up inverter.

    Pins: WL_IN (decoded WL), WL_EN (enable), Z (gated output),
          VDD, GND.

    Cell height: 1.715 µm (matches NAND2 decoder for row tiling).
    """
    name = cell_name or "wl_gate_and2"
    width = _snap(_CELL_WIDTH)
    height = _snap(_CELL_HEIGHT)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    _rect(cell, _BOUNDARY, 0, 0, width, height)

    # N-well for PMOS (upper half)
    _rect(cell, _NWELL, 0, height * 0.45, width, height)

    # VDD rail (met1, top)
    _rect(cell, _MET1, 0, height - 0.14, width, height)
    # GND rail (met1, bottom)
    _rect(cell, _MET1, 0, 0, width, 0.14)

    # Two series NMOS (bottom half) — NAND pull-down
    nmos_y1 = 0.45
    nmos_y2 = 0.80
    nmos_x = 1.0

    for ny in [nmos_y1, nmos_y2]:
        hw = _W_N / 2
        hl = _L / 2
        _rect(cell, _DIFF, nmos_x - hw, ny - hl - _SD_EXT, nmos_x + hw, ny + hl + _SD_EXT)
        _rect(cell, _NSDM,
              nmos_x - hw - _NSDM_ENC, ny - hl - _SD_EXT - _NSDM_ENC,
              nmos_x + hw + _NSDM_ENC, ny + hl + _SD_EXT + _NSDM_ENC)
        _rect(cell, _POLY, nmos_x - hw - _POLY_OVH, ny - hl, nmos_x + hw + _POLY_OVH, ny + hl)

    # Two parallel PMOS (upper half) — NAND pull-up
    pmos_y = 1.30
    for px in [0.8, 1.8]:
        hw = _W_P / 2
        hl = _L / 2
        _rect(cell, _DIFF, px - hw, pmos_y - hl - _SD_EXT, px + hw, pmos_y + hl + _SD_EXT)
        _rect(cell, _PSDM,
              px - hw - _PSDM_ENC, pmos_y - hl - _SD_EXT - _PSDM_ENC,
              px + hw + _PSDM_ENC, pmos_y + hl + _SD_EXT + _PSDM_ENC)
        _rect(cell, _POLY, px - hw - _POLY_OVH, pmos_y - hl, px + hw + _POLY_OVH, pmos_y + hl)

    # Output inverter NMOS (right side)
    inv_x = 2.2
    inv_ny = 0.60
    hw = _W_N / 2
    hl = _L / 2
    _rect(cell, _DIFF, inv_x - hw, inv_ny - hl - _SD_EXT, inv_x + hw, inv_ny + hl + _SD_EXT)
    _rect(cell, _NSDM,
          inv_x - hw - _NSDM_ENC, inv_ny - hl - _SD_EXT - _NSDM_ENC,
          inv_x + hw + _NSDM_ENC, inv_ny + hl + _SD_EXT + _NSDM_ENC)
    _rect(cell, _POLY, inv_x - hw - _POLY_OVH, inv_ny - hl, inv_x + hw + _POLY_OVH, inv_ny + hl)

    # Output inverter PMOS
    inv_py = 1.30
    _rect(cell, _DIFF, inv_x - hw, inv_py - hl - _SD_EXT, inv_x + hw, inv_py + hl + _SD_EXT)
    _rect(cell, _PSDM,
          inv_x - hw - _PSDM_ENC, inv_py - hl - _SD_EXT - _PSDM_ENC,
          inv_x + hw + _PSDM_ENC, inv_py + hl + _SD_EXT + _PSDM_ENC)
    _rect(cell, _POLY, inv_x - hw - _POLY_OVH, inv_py - hl, inv_x + hw + _POLY_OVH, inv_py + hl)

    # Pin labels
    cell.add(gdstk.Label("WL_IN", (_snap(0.2), _snap(nmos_y1)),
                          layer=_MET1[0], texttype=_MET1[1]))
    cell.add(gdstk.Label("WL_EN", (_snap(0.2), _snap(nmos_y2)),
                          layer=_MET1[0], texttype=_MET1[1]))
    cell.add(gdstk.Label("Z", (_snap(width - 0.2), _snap(inv_ny)),
                          layer=_MET1[0], texttype=_MET1[1]))
    cell.add(gdstk.Label("VDD", (_snap(width / 2), _snap(height)),
                          layer=_MET1[0], texttype=_MET1[1]))
    cell.add(gdstk.Label("GND", (_snap(width / 2), _snap(0)),
                          layer=_MET1[0], texttype=_MET1[1]))

    if output_path:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
