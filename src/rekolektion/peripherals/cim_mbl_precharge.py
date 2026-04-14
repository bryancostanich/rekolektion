"""MBL precharge cell for CIM arrays.

Single PMOS switch that precharges MBL to an external reference voltage
(typically VDD/2) before CIM compute.

When MBL_PRE (active low) is asserted, PMOS conducts and pulls MBL
toward VREF. VREF is an external analog supply — no on-chip divider.

Ports:
    MBL_PRE — gate input (poly, active low)
    MBL     — drain, connects to MBL M4 stripe (via stack to M4)
    VREF    — source, external reference voltage (met1)
    VDD     — n-well bias (met1)
"""

from __future__ import annotations

from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


_PU_W = 0.84    # PMOS width for fast precharge
_L = 0.15
_DIFF_EXT = 0.33
_LICON_TO_GATE = 0.09
_LICON = RULES.LICON_SIZE
_LI_ENC = RULES.LI1_ENCLOSURE_OF_LICON
_NWELL_ENC = RULES.DIFF_MIN_ENCLOSURE_BY_NWELL
_POLY_EXT = RULES.POLY_MIN_EXTENSION_PAST_DIFF
_PSDM_ENC = RULES.PSDM_ENCLOSURE_OF_DIFF
_LI_PAD = _LICON + 2 * _LI_ENC

_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_PSDM = LAYERS.PSDM.as_tuple
_NWELL = LAYERS.NWELL.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MET1 = LAYERS.MET1.as_tuple


def _snap(v, g=0.005):
    return round(v / g) * g


def _rect(c, ly, x0, y0, x1, y1):
    c.add(gdstk.rectangle((_snap(x0), _snap(y0)), (_snap(x1), _snap(y1)),
                           layer=ly[0], datatype=ly[1]))


def _con(c, cx, cy, ly, sz):
    hs = sz / 2.0
    _rect(c, ly, cx - hs, cy - hs, cx + hs, cy + hs)


def _lipad(c, cx, cy, w=_LI_PAD, h=_LI_PAD):
    _rect(c, _LI1, cx - w/2, cy - h/2, cx + w/2, cy + h/2)


def generate_mbl_precharge() -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate one MBL precharge cell (single PMOS switch).

    Returns (cell, library).
    """
    cell = gdstk.Cell("cim_mbl_precharge")

    # Single PMOS transistor, LR-style: vertical diff, horizontal gate
    diff_h = _L + 2 * _DIFF_EXT  # 0.81
    margin = 0.30

    # X layout
    diff_x0 = _snap(margin)
    diff_x1 = _snap(diff_x0 + _PU_W)
    diff_cx = _snap((diff_x0 + diff_x1) / 2.0)
    cell_w = _snap(diff_x1 + margin)

    # Y layout
    diff_y0 = _snap(margin)
    diff_y1 = _snap(diff_y0 + diff_h)
    gate_cy = _snap(diff_y0 + _DIFF_EXT + _L / 2.0)
    cell_h = _snap(diff_y1 + margin)

    # S/D licon positions
    src_cy = _snap(gate_cy + _L / 2.0 + _LICON_TO_GATE + _LICON / 2.0)  # VREF (top)
    drn_cy = _snap(gate_cy - _L / 2.0 - _LICON_TO_GATE - _LICON / 2.0)  # MBL (bottom)

    # PMOS diff + implant
    _rect(cell, _DIFF, diff_x0, diff_y0, diff_x1, diff_y1)
    _rect(cell, _PSDM, diff_x0 - _PSDM_ENC, diff_y0 - _PSDM_ENC,
          diff_x1 + _PSDM_ENC, diff_y1 + _PSDM_ENC)

    # N-well
    _rect(cell, _NWELL,
          diff_x0 - _NWELL_ENC, diff_y0 - _NWELL_ENC,
          diff_x1 + _NWELL_ENC, diff_y1 + _NWELL_ENC)

    # Gate (horizontal poly)
    _rect(cell, _POLY,
          diff_x0 - _POLY_EXT, gate_cy - _L / 2.0,
          diff_x1 + _POLY_EXT, gate_cy + _L / 2.0)

    # S/D contacts
    _con(cell, diff_cx, src_cy, _LICON1, _LICON)
    _lipad(cell, diff_cx, src_cy)
    _con(cell, diff_cx, drn_cy, _LICON1, _LICON)
    _lipad(cell, diff_cx, drn_cy)

    # Labels
    cell.add(gdstk.Label("MBL_PRE", (_snap(diff_x0 - _POLY_EXT), _snap(gate_cy)),
                          layer=_POLY[0], texttype=_POLY[1]))
    cell.add(gdstk.Label("VREF", (_snap(diff_cx), _snap(src_cy)),
                          layer=_LI1[0], texttype=_LI1[1]))
    cell.add(gdstk.Label("MBL", (_snap(diff_cx), _snap(drn_cy)),
                          layer=_LI1[0], texttype=_LI1[1]))

    lib = gdstk.Library(name="cim_mbl_precharge_lib", unit=1e-6, precision=5e-9)
    lib.add(cell)
    return cell, lib
