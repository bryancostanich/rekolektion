"""MBL sense output buffer for CIM arrays.

NMOS source follower with current bias. Buffers the analog MBL voltage
to an output pin without digitizing (ADC is external).

M1 (driver): gate=MBL, drain=VDD, source=MBL_OUT
M2 (bias):   gate=VBIAS, drain=MBL_OUT, source=VSS

VBIAS is an external bias voltage that sets the quiescent current.

Ports:
    MBL     — gate input (connects to MBL M4 via routing)
    MBL_OUT — analog output (met1)
    VBIAS   — bias voltage input (met1)
    VDD     — power (met1)
    VSS     — power (met1)
"""

from __future__ import annotations

from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


_DRV_W = 1.00   # Driver NMOS width (>10MHz BW at ~1pF)
_BIAS_W = 0.50   # Bias NMOS width
_L = 0.15
_DIFF_EXT = 0.33
_LICON_TO_GATE = 0.09
_LICON = RULES.LICON_SIZE
_LI_ENC = RULES.LI1_ENCLOSURE_OF_LICON
_NSDM_ENC = RULES.NSDM_ENCLOSURE_OF_DIFF
_POLY_EXT = RULES.POLY_MIN_EXTENSION_PAST_DIFF
_LI_PAD = _LICON + 2 * _LI_ENC

_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_NSDM = LAYERS.NSDM.as_tuple
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


def generate_mbl_sense() -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate one MBL sense buffer (NMOS source follower + bias).

    Two NMOS transistors stacked on a shared diff strip:
    - gate1 (bottom) = VBIAS (current source)
    - gate2 (top) = MBL (source follower driver)

    S/D nodes: VSS (bottom) | VBIAS gate | MBL_OUT (shared) | MBL gate | VDD (top)

    Returns (cell, library).
    """
    cell = gdstk.Cell("cim_mbl_sense")

    # Use wider of the two widths for the shared diff
    diff_w = max(_DRV_W, _BIAS_W)
    margin = 0.30

    # Y layout: two gates stacked
    gate_to_sd = _snap(_LICON_TO_GATE + _LICON / 2.0)
    sd_zone = _snap(max(2 * gate_to_sd + RULES.LI1_MIN_SPACING,
                        _LI_PAD + RULES.LI1_MIN_SPACING))

    y_diff_bot = 0.0
    gate1_cy = _snap(y_diff_bot + _DIFF_EXT + _L / 2.0)  # VBIAS
    gate2_cy = _snap(gate1_cy + _L / 2.0 + sd_zone + _L / 2.0)  # MBL
    y_diff_top = _snap(gate2_cy + _L / 2.0 + _DIFF_EXT)

    sd_bot_cy = _snap(gate1_cy - _L / 2.0 - _LICON_TO_GATE - _LICON / 2.0)  # VSS
    sd_mid_cy = _snap((gate1_cy + gate2_cy) / 2.0)  # MBL_OUT
    sd_top_cy = _snap(gate2_cy + _L / 2.0 + _LICON_TO_GATE + _LICON / 2.0)  # VDD

    # X layout
    diff_x0 = _snap(margin)
    diff_x1 = _snap(diff_x0 + diff_w)
    diff_cx = _snap((diff_x0 + diff_x1) / 2.0)
    cell_w = _snap(diff_x1 + margin)

    # NMOS diff + implant
    _rect(cell, _DIFF, diff_x0, y_diff_bot, diff_x1, y_diff_top)
    _rect(cell, _NSDM, diff_x0 - _NSDM_ENC, y_diff_bot - _NSDM_ENC,
          diff_x1 + _NSDM_ENC, y_diff_top + _NSDM_ENC)

    # Poly gates (horizontal)
    poly_x0 = _snap(diff_x0 - _POLY_EXT)
    poly_x1 = _snap(diff_x1 + _POLY_EXT)
    for gate_cy in [gate1_cy, gate2_cy]:
        _rect(cell, _POLY, poly_x0, gate_cy - _L / 2.0,
              poly_x1, gate_cy + _L / 2.0)

    # S/D contacts
    for sd_cy in [sd_bot_cy, sd_mid_cy, sd_top_cy]:
        _con(cell, diff_cx, sd_cy, _LICON1, _LICON)
        _lipad(cell, diff_cx, sd_cy)

    # Labels
    cell.add(gdstk.Label("VBIAS", (_snap(poly_x0), _snap(gate1_cy)),
                          layer=_POLY[0], texttype=_POLY[1]))
    cell.add(gdstk.Label("MBL", (_snap(poly_x0), _snap(gate2_cy)),
                          layer=_POLY[0], texttype=_POLY[1]))
    cell.add(gdstk.Label("VSS", (_snap(diff_cx), _snap(sd_bot_cy)),
                          layer=_LI1[0], texttype=_LI1[1]))
    cell.add(gdstk.Label("MBL_OUT", (_snap(diff_cx), _snap(sd_mid_cy)),
                          layer=_LI1[0], texttype=_LI1[1]))
    cell.add(gdstk.Label("VDD", (_snap(diff_cx), _snap(sd_top_cy)),
                          layer=_LI1[0], texttype=_LI1[1]))

    lib = gdstk.Library(name="cim_mbl_sense_lib", unit=1e-6, precision=5e-9)
    lib.add(cell)
    return cell, lib
