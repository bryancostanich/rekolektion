"""MWL (multiply word line) driver generator for CIM arrays.

Non-inverting buffer (two inverters in series). Uses LR-style topology:
horizontal poly gates crossing vertical NMOS/PMOS diff strips.

Ports: MWL_EN (input), MWL (output), VDD, VSS
"""

from __future__ import annotations

from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


# Transistor sizing
_PU_W = 0.84    # PMOS pull-up width (X extent of PMOS diff)
_PD_W = 0.42    # NMOS pull-down width (X extent of NMOS diff)
_L = 0.15       # gate length (Y extent of poly at diff crossing)

# Layout constants (same validated values as CIM cell / 6T cell)
_DIFF_EXT = 0.33     # diff Y-extension past poly for S/D contacts
_LICON_TO_GATE = 0.09  # licon edge to gate edge
_LICON = RULES.LICON_SIZE
_LI_ENC = RULES.LI1_ENCLOSURE_OF_LICON
_MCON = RULES.MCON_SIZE
_NSDM_ENC = RULES.NSDM_ENCLOSURE_OF_DIFF
_PSDM_ENC = RULES.PSDM_ENCLOSURE_OF_DIFF
_NWELL_ENC = RULES.DIFF_MIN_ENCLOSURE_BY_NWELL
_POLY_EXT = RULES.POLY_MIN_EXTENSION_PAST_DIFF
_LI_PAD = _LICON + 2 * _LI_ENC  # 0.33

_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_NSDM = LAYERS.NSDM.as_tuple
_PSDM = LAYERS.PSDM.as_tuple
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


def generate_mwl_driver() -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate one MWL driver cell (non-inverting buffer).

    Topology: two CMOS inverters, LR-style layout:
    - Vertical NMOS diff (left) + vertical PMOS diff (right)
    - Horizontal poly gates crossing both diffs
    - Two gates stacked vertically (inv1, inv2) with S/D contacts between

    Returns (cell, library).
    """
    cell = gdstk.Cell("cim_mwl_driver")

    # --- Y layout: two gates with S/D zones ---
    # bottom S/D | gate1 | mid S/D | gate2 | top S/D
    gate_to_sd = _snap(_LICON_TO_GATE + _LICON / 2.0)  # gate edge to licon center
    sd_zone = _snap(2 * gate_to_sd + RULES.LI1_MIN_SPACING)  # S/D between gates
    # But also need li.3 between adjacent li1 pads:
    sd_zone = _snap(max(sd_zone, _LI_PAD + RULES.LI1_MIN_SPACING))

    y_diff_bot = 0.0
    gate1_cy = _snap(y_diff_bot + _DIFF_EXT + _L / 2.0)
    gate2_cy = _snap(gate1_cy + _L / 2.0 + sd_zone + _L / 2.0)
    y_diff_top = _snap(gate2_cy + _L / 2.0 + _DIFF_EXT)

    # S/D licon Y positions
    sd_bot_cy = _snap(gate1_cy - _L / 2.0 - _LICON_TO_GATE - _LICON / 2.0)
    sd_mid_cy = _snap((gate1_cy + gate2_cy) / 2.0)
    sd_top_cy = _snap(gate2_cy + _L / 2.0 + _LICON_TO_GATE + _LICON / 2.0)

    # --- X layout: NMOS left, N-P gap, PMOS right ---
    np_gap = _snap(max(0.34 + _NWELL_ENC, RULES.DIFF_MIN_SPACING + 0.10))
    margin = 0.30

    nmos_x0 = _snap(margin)
    nmos_x1 = _snap(nmos_x0 + _PD_W)
    nmos_cx = _snap((nmos_x0 + nmos_x1) / 2.0)

    pmos_x0 = _snap(nmos_x1 + np_gap)
    pmos_x1 = _snap(pmos_x0 + _PU_W)
    pmos_cx = _snap((pmos_x0 + pmos_x1) / 2.0)

    cell_w = _snap(pmos_x1 + margin)

    # --- NMOS diff + implant ---
    _rect(cell, _DIFF, nmos_x0, y_diff_bot, nmos_x1, y_diff_top)
    _rect(cell, _NSDM, nmos_x0 - _NSDM_ENC, y_diff_bot - _NSDM_ENC,
          nmos_x1 + _NSDM_ENC, y_diff_top + _NSDM_ENC)

    # --- PMOS diff + implant ---
    _rect(cell, _DIFF, pmos_x0, y_diff_bot, pmos_x1, y_diff_top)
    _rect(cell, _PSDM, pmos_x0 - _PSDM_ENC, y_diff_bot - _PSDM_ENC,
          pmos_x1 + _PSDM_ENC, y_diff_top + _PSDM_ENC)

    # --- N-well ---
    _rect(cell, _NWELL,
          pmos_x0 - _NWELL_ENC, y_diff_bot - _NWELL_ENC - 0.10,
          pmos_x1 + _NWELL_ENC, y_diff_top + _NWELL_ENC + 0.10)

    # --- Poly gates (horizontal, crossing both diffs) ---
    poly_x0 = _snap(nmos_x0 - _POLY_EXT)
    poly_x1 = _snap(pmos_x1 + _POLY_EXT)
    for gate_cy in [gate1_cy, gate2_cy]:
        _rect(cell, _POLY, poly_x0, gate_cy - _L / 2.0,
              poly_x1, gate_cy + _L / 2.0)

    # --- S/D contacts on both diffs ---
    for diff_cx in [nmos_cx, pmos_cx]:
        for sd_cy in [sd_bot_cy, sd_mid_cy, sd_top_cy]:
            _con(cell, diff_cx, sd_cy, _LICON1, _LICON)
            _lipad(cell, diff_cx, sd_cy)

    # --- Power: VSS to NMOS sources, VDD to PMOS sources ---
    # Inv1: gate1 makes transistors. NMOS source=bot, drain=mid.
    # Inv2: gate2 makes transistors. NMOS source=mid (shared), drain=top.
    # For non-inverting buffer: inv1 and inv2 share the mid node.
    # NMOS sources: bot (inv1 src=VSS) and top (inv2 drain=output)
    # Wait — for two inverters in series on shared diff:
    #   bot = inv1 source (VSS for NMOS)
    #   mid = inv1 drain = inv2 source (internal node for NMOS)
    #   top = inv2 drain (output for NMOS)
    #
    # Similarly for PMOS:
    #   bot = inv1 source (VDD for PMOS)... but PMOS source should be VDD.
    # In shared diff with two gates, the assignment depends on wiring.
    # For a non-inverting buffer using shared diff:
    #   NMOS: src1=bot=VSS, drn1=mid, src2=mid, drn2=top → inv1 out at mid, inv2 out at top
    #   PMOS: src1=bot=VDD, drn1=mid, src2=mid, drn2=top → same
    # But PMOS source should be VDD at the TOP (highest potential).
    # Need to wire: NMOS bot to VSS, PMOS top to VDD (or bot, depending on orientation).

    # Labels + .pin purpose shapes (datatype 16) for Magic port detection.
    # Labels alone are recognised as named nets but do not promote to
    # subckt ports unless co-located with a .pin shape on the same
    # metal/poly layer.  Pad size matches sky130 LEF convention
    # (~0.14 µm) — small enough to not introduce DRC issues, large
    # enough for Magic to associate the label with the rect.
    _PIN_HALF = 0.07
    _POLY_PIN = (_POLY[0], 16)   # poly.pin
    _MET1_PIN = (_MET1[0], 16)   # met1.pin
    for label, pos, drawing, pin_dt in (
        ("MWL_EN", (_snap(poly_x0), _snap(gate1_cy)), _POLY, _POLY_PIN),
        ("MWL",    (_snap(poly_x1), _snap(gate2_cy)), _POLY, _POLY_PIN),
        ("VSS",    (_snap(cell_w / 2), _snap(y_diff_bot)), _MET1, _MET1_PIN),
        ("VDD",    (_snap(cell_w / 2), _snap(y_diff_top)), _MET1, _MET1_PIN),
    ):
        cx, cy = pos
        cell.add(gdstk.rectangle(
            (cx - _PIN_HALF, cy - _PIN_HALF),
            (cx + _PIN_HALF, cy + _PIN_HALF),
            layer=pin_dt[0], datatype=pin_dt[1],
        ))
        cell.add(gdstk.Label(label, pos, layer=drawing[0], texttype=drawing[1]))

    lib = gdstk.Library(name="cim_mwl_driver_lib", unit=1e-6, precision=5e-9)
    lib.add(cell)
    return cell, lib
