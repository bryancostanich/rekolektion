"""Ring oscillator test structure for CIM cell process monitoring.

11-stage inverter ring using the same transistor sizing as the CIM cell
(NMOS W=0.42, PMOS W=0.42, L=0.15). Provides post-silicon frequency
measurement to characterize actual transistor performance.

Layout: 13 horizontal gates on shared NMOS/PMOS diff strips (LR-style).
11 ring stages + 2 output buffer stages. Output tapped from buffer.

Ports:
    VDD     — power (met1)
    VSS     — power (met1)
    RO_EN   — enable (poly, connects to one ring node to start/stop)
    RO_OUT  — frequency output (met1, buffered)
"""

from __future__ import annotations

from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


_W = 0.42        # both NMOS and PMOS (matches CIM cell 6T transistors)
_L = 0.15
_DIFF_EXT = 0.33
_LICON_TO_GATE = 0.09
_LICON = RULES.LICON_SIZE
_LI_ENC = RULES.LI1_ENCLOSURE_OF_LICON
_NSDM_ENC = RULES.NSDM_ENCLOSURE_OF_DIFF
_PSDM_ENC = RULES.PSDM_ENCLOSURE_OF_DIFF
_NWELL_ENC = RULES.DIFF_MIN_ENCLOSURE_BY_NWELL
_POLY_EXT = RULES.POLY_MIN_EXTENSION_PAST_DIFF
_LI_PAD = _LICON + 2 * _LI_ENC

_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_NSDM = LAYERS.NSDM.as_tuple
_PSDM = LAYERS.PSDM.as_tuple
_NWELL = LAYERS.NWELL.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MET1 = LAYERS.MET1.as_tuple

_NUM_RING = 11   # ring stages (must be odd)
_NUM_BUF = 2     # output buffer stages
_NUM_GATES = _NUM_RING + _NUM_BUF


def _snap(v, g=0.005):
    return round(v / g) * g


def _rect(c, ly, x0, y0, x1, y1):
    c.add(gdstk.rectangle((_snap(x0), _snap(y0)), (_snap(x1), _snap(y1)),
                           layer=ly[0], datatype=ly[1]))


def _con(c, cx, cy, ly, sz):
    hs = sz / 2.0
    _rect(c, ly, cx - hs, cy - hs, cx + hs, cy + hs)


def _lipad(c, cx, cy):
    _rect(c, _LI1, cx - _LI_PAD/2, cy - _LI_PAD/2,
          cx + _LI_PAD/2, cy + _LI_PAD/2)


def generate_ring_osc() -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate an 11-stage ring oscillator with output buffer.

    Returns (cell, library).
    """
    cell = gdstk.Cell("cim_ring_osc")

    # Y layout: N gates stacked with S/D zones between them
    gate_to_sd = _snap(_LICON_TO_GATE + _LICON / 2.0)
    sd_zone = _snap(max(2 * gate_to_sd + RULES.LI1_MIN_SPACING,
                        _LI_PAD + RULES.LI1_MIN_SPACING))

    # Compute gate Y centers and S/D positions
    gate_cys = []
    sd_cys = []
    y = _DIFF_EXT  # first gate offset from diff bottom
    for i in range(_NUM_GATES):
        gate_cys.append(_snap(y + _L / 2.0))
        if i < _NUM_GATES - 1:
            sd_cy = _snap(y + _L / 2.0 + _L / 2.0 + sd_zone / 2.0)
            sd_cys.append(sd_cy)
            y = _snap(sd_cy + sd_zone / 2.0)
        else:
            y += _L
    y_diff_top = _snap(y + _DIFF_EXT)

    # Bottom and top S/D (power connections)
    sd_bot = _snap(gate_cys[0] - _L / 2.0 - _LICON_TO_GATE - _LICON / 2.0)
    sd_top = _snap(gate_cys[-1] + _L / 2.0 + _LICON_TO_GATE + _LICON / 2.0)

    # X layout: NMOS left, gap, PMOS right
    np_gap = _snap(max(0.34 + _NWELL_ENC, RULES.DIFF_MIN_SPACING + 0.10))
    margin = 0.30

    nmos_x0 = _snap(margin)
    nmos_x1 = _snap(nmos_x0 + _W)
    nmos_cx = _snap((nmos_x0 + nmos_x1) / 2.0)

    pmos_x0 = _snap(nmos_x1 + np_gap)
    pmos_x1 = _snap(pmos_x0 + _W)
    pmos_cx = _snap((pmos_x0 + pmos_x1) / 2.0)

    cell_w = _snap(pmos_x1 + margin)

    # NMOS diff + implant
    _rect(cell, _DIFF, nmos_x0, 0.0, nmos_x1, y_diff_top)
    _rect(cell, _NSDM, nmos_x0 - _NSDM_ENC, -_NSDM_ENC,
          nmos_x1 + _NSDM_ENC, y_diff_top + _NSDM_ENC)

    # PMOS diff + implant
    _rect(cell, _DIFF, pmos_x0, 0.0, pmos_x1, y_diff_top)
    _rect(cell, _PSDM, pmos_x0 - _PSDM_ENC, -_PSDM_ENC,
          pmos_x1 + _PSDM_ENC, y_diff_top + _PSDM_ENC)

    # N-well
    _rect(cell, _NWELL,
          pmos_x0 - _NWELL_ENC, -_NWELL_ENC - 0.10,
          pmos_x1 + _NWELL_ENC, y_diff_top + _NWELL_ENC + 0.10)

    # Poly gates (horizontal, crossing both diffs)
    poly_x0 = _snap(nmos_x0 - _POLY_EXT)
    poly_x1 = _snap(pmos_x1 + _POLY_EXT)
    for gate_cy in gate_cys:
        _rect(cell, _POLY, poly_x0, gate_cy - _L / 2.0,
              poly_x1, gate_cy + _L / 2.0)

    # S/D contacts on both diffs
    all_sd = [sd_bot] + sd_cys + [sd_top]
    for diff_cx in [nmos_cx, pmos_cx]:
        for sd_cy in all_sd:
            _con(cell, diff_cx, sd_cy, _LICON1, _LICON)
            _lipad(cell, diff_cx, sd_cy)

    # Labels
    cell.add(gdstk.Label("VSS", (_snap(nmos_cx), _snap(sd_bot)),
                          layer=_LI1[0], texttype=_LI1[1]))
    cell.add(gdstk.Label("VDD", (_snap(pmos_cx), _snap(sd_top)),
                          layer=_LI1[0], texttype=_LI1[1]))
    cell.add(gdstk.Label("RO_EN", (_snap(poly_x0), _snap(gate_cys[0])),
                          layer=_POLY[0], texttype=_POLY[1]))
    cell.add(gdstk.Label("RO_OUT", (_snap(pmos_cx), _snap(sd_cys[-1])),
                          layer=_LI1[0], texttype=_LI1[1]))

    lib = gdstk.Library(name="cim_ring_osc_lib", unit=1e-6, precision=5e-9)
    lib.add(cell)
    return cell, lib
