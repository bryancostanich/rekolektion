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
_NSDM_ENC = RULES.NSDM_ENCLOSURE_OF_DIFF
_DIFF_TAP_SPACE = RULES.DIFF_MIN_SPACING        # 0.27 (PSDM-to-NSDM diff space)
_LI_PAD = _LICON + 2 * _LI_ENC                  # 0.33
_MCON = RULES.MCON_SIZE                         # 0.17
_MCON_M1_ENC = RULES.MET1_ENCLOSURE_OF_MCON_OTHER  # 0.06
_M1_PAD = _MCON + 2 * _MCON_M1_ENC              # 0.29

_DIFF = LAYERS.DIFF.as_tuple
_TAP = LAYERS.TAP.as_tuple
_POLY = LAYERS.POLY.as_tuple
_PSDM = LAYERS.PSDM.as_tuple
_NSDM = LAYERS.NSDM.as_tuple
_NWELL = LAYERS.NWELL.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MCON_L = LAYERS.MCON.as_tuple
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
    """Generate one MBL precharge cell (single PMOS switch + n-well tap).

    Includes an N-tap inside the cell that ties the n-well to a VPWR
    rail.  Without this, Magic extracts the well as a separate auto-
    named net that becomes a per-instance floating node at the macro
    level — breaking LVS hierarchy because the macro sees the wells
    of all 64 precharge instances merged into one VPWR net while the
    reference SPICE has 64 separate well nets.
    """
    cell = gdstk.Cell("cim_mbl_precharge")

    # Single PMOS transistor, LR-style: vertical diff, horizontal gate
    diff_h = _L + 2 * _DIFF_EXT  # 0.81
    margin = 0.30

    # X layout: PMOS diff on the left, N-tap to its right (in n-well).
    diff_x0 = _snap(margin)
    diff_x1 = _snap(diff_x0 + _PU_W)
    diff_cx = _snap((diff_x0 + diff_x1) / 2.0)

    # N-tap region: must be in n-well, separated from PMOS diff by
    # DIFF_MIN_SPACING (0.27 µm).
    tap_x0 = _snap(diff_x1 + _DIFF_TAP_SPACE)
    tap_w = 0.30
    tap_x1 = _snap(tap_x0 + tap_w)
    tap_cx = _snap((tap_x0 + tap_x1) / 2.0)
    cell_w = _snap(tap_x1 + margin)

    # Y layout
    diff_y0 = _snap(margin)
    diff_y1 = _snap(diff_y0 + diff_h)
    gate_cy = _snap(diff_y0 + _DIFF_EXT + _L / 2.0)
    cell_h = _snap(diff_y1 + margin)

    # S/D licon positions
    src_cy = _snap(gate_cy + _L / 2.0 + _LICON_TO_GATE + _LICON / 2.0)  # VREF (top)
    drn_cy = _snap(gate_cy - _L / 2.0 - _LICON_TO_GATE - _LICON / 2.0)  # MBL (bottom)

    # PMOS diff + PSDM
    _rect(cell, _DIFF, diff_x0, diff_y0, diff_x1, diff_y1)
    _rect(cell, _PSDM, diff_x0 - _PSDM_ENC, diff_y0 - _PSDM_ENC,
          diff_x1 + _PSDM_ENC, diff_y1 + _PSDM_ENC)

    # N-tap (TAP layer + NSDM around it — N+ contact in p-side... no
    # wait, in n-well, N+ tap means the implant is NSDM, the diff is
    # tagged TAP).  TAP layer signals "well/substrate body contact".
    tap_h = 0.30
    tap_y0 = _snap(diff_y0 + diff_h / 2 - tap_h / 2)
    tap_y1 = _snap(tap_y0 + tap_h)
    tap_cy = _snap((tap_y0 + tap_y1) / 2.0)
    _rect(cell, _TAP, tap_x0, tap_y0, tap_x1, tap_y1)
    _rect(cell, _NSDM, tap_x0 - _NSDM_ENC, tap_y0 - _NSDM_ENC,
          tap_x1 + _NSDM_ENC, tap_y1 + _NSDM_ENC)

    # N-well (covers BOTH the PMOS diff and the N-tap)
    _rect(cell, _NWELL,
          diff_x0 - _NWELL_ENC, diff_y0 - _NWELL_ENC,
          tap_x1 + _NWELL_ENC, diff_y1 + _NWELL_ENC)

    # Gate (horizontal poly across PMOS diff)
    _rect(cell, _POLY,
          diff_x0 - _POLY_EXT, gate_cy - _L / 2.0,
          diff_x1 + _POLY_EXT, gate_cy + _L / 2.0)

    # PMOS S/D contacts (licon + li1 pad)
    _con(cell, diff_cx, src_cy, _LICON1, _LICON)
    _lipad(cell, diff_cx, src_cy)
    _con(cell, diff_cx, drn_cy, _LICON1, _LICON)
    _lipad(cell, diff_cx, drn_cy)

    # N-tap contact: licon to li1 pad to met1 (VPWR connection)
    _con(cell, tap_cx, tap_cy, _LICON1, _LICON)
    _lipad(cell, tap_cx, tap_cy)
    # mcon + met1 pad over the li1 pad
    _con(cell, tap_cx, tap_cy, _MCON_L, _MCON)
    _rect(cell, _MET1,
          tap_cx - _M1_PAD / 2, tap_cy - _M1_PAD / 2,
          tap_cx + _M1_PAD / 2, tap_cy + _M1_PAD / 2)

    # Labels + .pin shapes for Magic port detection.
    _PIN_HALF = 0.07
    _POLY_PIN = (_POLY[0], 16)
    _LI1_PIN = (_LI1[0], 16)
    _MET1_PIN = LAYERS.MET1_PIN.as_tuple
    for label, pos, drawing, pin_dt in (
        ("MBL_PRE", (_snap(diff_x0 - _POLY_EXT), _snap(gate_cy)), _POLY, _POLY_PIN),
        ("VREF",    (_snap(diff_cx), _snap(src_cy)),               _LI1, _LI1_PIN),
        ("MBL",     (_snap(diff_cx), _snap(drn_cy)),               _LI1, _LI1_PIN),
        ("VPWR",    (_snap(tap_cx),  _snap(tap_cy)),                _MET1, _MET1_PIN),
    ):
        cx, cy = pos
        cell.add(gdstk.rectangle(
            (cx - _PIN_HALF, cy - _PIN_HALF),
            (cx + _PIN_HALF, cy + _PIN_HALF),
            layer=pin_dt[0], datatype=pin_dt[1],
        ))
        cell.add(gdstk.Label(label, pos, layer=drawing[0], texttype=drawing[1]))

    lib = gdstk.Library(name="cim_mbl_precharge_lib", unit=1e-6, precision=5e-9)
    lib.add(cell)
    return cell, lib
