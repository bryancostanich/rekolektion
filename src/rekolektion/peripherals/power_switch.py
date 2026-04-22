"""Power gating header switch generator (extraction-clean rewrite).

PMOS header switches between VDD_REAL (always-on supply) and VDD (the
virtual supply seen by the macro). SLEEP gates the switches; when
SLEEP is high the PMOS are OFF, isolating the macro.

Per-switch layout (cell-local, single PMOS at W=5 µm):
  - Vertical PMOS: diff vertical, poly gate horizontal across channel.
  - Source contact at the top of the diff → met1 stub → via1/via2 to
    the VDD_REAL met3 rail.
  - Drain contact at the bottom of the diff → met1 stub → via1/via2 to
    the VDD met3 rail.
  - Gate poly extends out to a poly head clear of the diff, where a
    licon + li1 + mcon + met1 + via1 + met2 + via2 lands on the SLEEP
    met3 rail.

Rails (canonical sky130 stack):
  VDD_REAL — horizontal met3 at the top
  SLEEP    — horizontal met3 in the middle
  VDD      — horizontal met3 at the bottom

Nwell taps tie the well to VDD_REAL (proper PMOS body bias).
"""
from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS


# --- device + rule constants ------------------------------------------------
_W_P: float = 5.0          # PMOS header switch width (low Rdson)
_L: float = 0.15
_SD_EXT: float = 0.50      # wider for high-current device
_POLY_OVH: float = 0.14
_LICON: float = 0.17
_LI_ENC: float = 0.08
_PSDM_ENC: float = 0.125
_NWELL_ENC: float = 0.18
_MCON: float = 0.17
_MET1_WIDTH: float = 0.14

_VIA1: float = 0.15
_VIA1_ENC: float = 0.055
_VIA2: float = 0.20
_VIA2_ENC_MET2_OTHER: float = 0.085
_VIA2_ENC_MET3: float = 0.065

_POLY_LICON_ENC: float = 0.08
_LI_PAD: float = _LICON + 2 * _LI_ENC            # 0.33

_SWITCH_PITCH: float = 8.0   # x pitch per switch (room for W=5 diff + taps)
_RAIL_W: float = 0.40        # met3 rail width

# N-well tap geometry.
_TAP_W: float = 0.26
_TAP_SIZE: float = 0.30      # mcon-like met1 pad over tap contact

# Layer shortcuts
_DIFF = LAYERS.DIFF.as_tuple
_TAP = LAYERS.TAP.as_tuple
_POLY = LAYERS.POLY.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MCON_L = LAYERS.MCON.as_tuple
_MET1 = LAYERS.MET1.as_tuple
_VIA_L = LAYERS.VIA.as_tuple
_MET2 = LAYERS.MET2.as_tuple
_VIA2_L = LAYERS.VIA2.as_tuple
_MET3 = LAYERS.MET3.as_tuple
_NWELL = LAYERS.NWELL.as_tuple
_PSDM = LAYERS.PSDM.as_tuple
_NSDM = LAYERS.NSDM.as_tuple
_BOUNDARY = LAYERS.BOUNDARY.as_tuple


def _snap(v: float, grid: float = 0.005) -> float:
    return round(v / grid) * grid


def _rect(cell: gdstk.Cell, layer: tuple[int, int],
          x0: float, y0: float, x1: float, y1: float) -> None:
    cell.add(gdstk.rectangle(
        (_snap(x0), _snap(y0)), (_snap(x1), _snap(y1)),
        layer=layer[0], datatype=layer[1],
    ))


def _sq(cell: gdstk.Cell, layer: tuple[int, int],
        cx: float, cy: float, size: float) -> None:
    h = size / 2
    _rect(cell, layer, cx - h, cy - h, cx + h, cy + h)


def _diff_contact(cell: gdstk.Cell, cx: float, cy: float) -> None:
    _sq(cell, _LICON1, cx, cy, _LICON)
    _sq(cell, _LI1, cx, cy, _LI_PAD)


def _tap_to_met3(cell: gdstk.Cell, cx: float, cy: float, rail_y: float) -> None:
    """Full stack diff-contact → met3 rail at (cx, rail_y).

    diff_contact draws licon+li1 at (cx, cy). This helper adds the rest:
    mcon + met1 stub from cy to rail_y, via1 + via2 at rail_y.
    """
    _sq(cell, _MCON_L, cx, cy, _MCON)
    # Met1 stub 0.30 µm wide from cy to rail edge (past via landing).
    half = 0.15
    y_lo = min(cy, rail_y) - half
    y_hi = max(cy, rail_y) + half
    _rect(cell, _MET1, cx - half, y_lo, cx + half, y_hi)
    # via1 + via2 at rail_y
    _sq(cell, _VIA_L, cx, rail_y, _VIA1)
    _sq(cell, _MET2, cx, rail_y, 0.30)
    _sq(cell, _MET2, cx, rail_y, _VIA2 + 2 * _VIA2_ENC_MET2_OTHER)
    _sq(cell, _VIA2_L, cx, rail_y, _VIA2)
    _sq(cell, _MET3, cx, rail_y, _VIA2 + 2 * _VIA2_ENC_MET3)


def _gate_tap_to_met3(cell: gdstk.Cell, cx: float, cy: float, rail_y: float) -> None:
    """Full stack poly-contact → met3 rail at (cx, rail_y). Caller
    provides poly geometry at (cx, cy) wide enough for the licon."""
    _sq(cell, _LICON1, cx, cy, _LICON)
    _sq(cell, _LI1, cx, cy, _LI_PAD)
    _sq(cell, _MCON_L, cx, cy, _MCON)
    half = 0.15
    y_lo = min(cy, rail_y) - half
    y_hi = max(cy, rail_y) + half
    _rect(cell, _MET1, cx - half, y_lo, cx + half, y_hi)
    _sq(cell, _VIA_L, cx, rail_y, _VIA1)
    _sq(cell, _MET2, cx, rail_y, 0.30)
    _sq(cell, _MET2, cx, rail_y, _VIA2 + 2 * _VIA2_ENC_MET2_OTHER)
    _sq(cell, _VIA2_L, cx, rail_y, _VIA2)
    _sq(cell, _MET3, cx, rail_y, _VIA2 + 2 * _VIA2_ENC_MET3)


def generate_power_switches(
    num_switches: int = 4,
    macro_width: float = 30.0,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Emit a power-gating header switch array with `num_switches`
    parallel PMOS.

    num_switches : number of parallel PMOS (more = lower Rdson)
    macro_width  : target cell width (switches are evenly distributed)
    """
    if num_switches < 1:
        raise ValueError("num_switches must be >= 1")

    name = cell_name or f"power_switch_{num_switches}x"
    width = _snap(max(macro_width, num_switches * _SWITCH_PITCH))

    # Y layout: switch transistor between VDD_REAL rail (top) and VDD
    # rail (bottom), with SLEEP rail in the middle outside the diff.
    # Place SLEEP rail above the drain-side tap region, below the gate
    # poly head — roughly 1 µm above cell bottom.
    pg_y = 3.00                                 # transistor center y
    diff_top = pg_y + _L / 2 + _SD_EXT          # 3.25
    diff_bot = pg_y - _L / 2 - _SD_EXT          # 2.75
    src_y = diff_top - _SD_EXT / 2              # 3.00 + 0.25 (in source region)
    drn_y = diff_bot + _SD_EXT / 2              # 2.75 + 0.25
    # Hmm: with hl=_L/2=0.075, src/drn_y computed differently — let me redo
    src_y = pg_y + _L / 2 + _SD_EXT / 2         # 3.325
    drn_y = pg_y - _L / 2 - _SD_EXT / 2         # 2.675
    # Gate poly head y: above source region (clear of diff).
    gate_head_y = diff_top + 0.20                # 3.45
    # Rails
    vdd_real_rail_y = src_y + 1.00               # 4.325
    vdd_rail_y = drn_y - 1.00                    # 1.675
    # SLEEP rail between gate head and drain rail
    sleep_rail_y = (gate_head_y + vdd_real_rail_y) / 2   # ~3.89
    # Actually SLEEP rail above the gate head, between gate head and VDD_REAL rail
    sleep_rail_y = vdd_real_rail_y + 0.70         # 5.025
    cell_h = _snap(sleep_rail_y + _RAIL_W / 2 + 0.14)    # ~5.23

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    _rect(cell, _BOUNDARY, 0, 0, width, cell_h)

    # Full-cell nwell (PMOS body)
    _rect(cell, _NWELL, 0, 0, width, cell_h)

    # Rails (met3)
    _rect(cell, _MET3, 0, vdd_real_rail_y - _RAIL_W / 2,
          width, vdd_real_rail_y + _RAIL_W / 2)
    cell.add(gdstk.Label("VDD_REAL",
                         (_snap(0.5), _snap(vdd_real_rail_y)),
                         layer=_MET3[0], texttype=_MET3[1]))
    _rect(cell, _MET3, 0, vdd_rail_y - _RAIL_W / 2,
          width, vdd_rail_y + _RAIL_W / 2)
    cell.add(gdstk.Label("VDD",
                         (_snap(0.5), _snap(vdd_rail_y)),
                         layer=_MET3[0], texttype=_MET3[1]))
    _rect(cell, _MET3, 0, sleep_rail_y - _RAIL_W / 2,
          width, sleep_rail_y + _RAIL_W / 2)
    cell.add(gdstk.Label("SLEEP",
                         (_snap(0.5), _snap(sleep_rail_y)),
                         layer=_MET3[0], texttype=_MET3[1]))

    # Place PMOS header switches evenly across width
    switch_spacing = width / (num_switches + 1)

    for i in range(num_switches):
        cx = switch_spacing * (i + 1)

        hw = _W_P / 2.0
        hl = _L / 2.0

        # Diff vertical, poly horizontal across channel
        _rect(cell, _DIFF,
              cx - hw, diff_bot, cx + hw, diff_top)
        _rect(cell, _PSDM,
              cx - hw - _PSDM_ENC, diff_bot - _PSDM_ENC,
              cx + hw + _PSDM_ENC, diff_top + _PSDM_ENC)
        # Poly gate spans diff + overhang, extending further right to a
        # head where the licon tap lands (clear of diff y range).
        poly_left = cx - hw - _POLY_OVH
        poly_right = cx + hw + _POLY_OVH + 0.60    # extension for head
        _rect(cell, _POLY, poly_left, pg_y - hl, poly_right, pg_y + hl)
        # Poly head at the right end — widened square so a licon fits
        # with full poly enclosure.
        head_cx = poly_right - _POLY_LICON_ENC - _LICON / 2
        head_size = _LICON + 2 * _POLY_LICON_ENC   # 0.33
        _sq(cell, _POLY, head_cx, pg_y, head_size)

        # Source (top) diff contact → VDD_REAL rail
        _diff_contact(cell, cx, src_y)
        _tap_to_met3(cell, cx, src_y, vdd_real_rail_y)

        # Drain (bottom) diff contact → VDD rail
        _diff_contact(cell, cx, drn_y)
        _tap_to_met3(cell, cx, drn_y, vdd_rail_y)

        # Gate poly head → SLEEP rail
        _gate_tap_to_met3(cell, head_cx, pg_y, sleep_rail_y)

    # N-well taps tie the body to VDD_REAL. Place one tap per switch,
    # at the switch's x but displaced vertically to a dedicated row
    # above the PMOS diff (between diff_top and VDD_REAL rail).
    tap_y = (diff_top + vdd_real_rail_y) / 2      # midway
    for i in range(num_switches):
        cx = switch_spacing * (i + 1)
        tap_x = cx - _W_P / 2 - 0.40              # LEFT of diff, in nwell
        # Tap diff (N+ in nwell = n-tap)
        _rect(cell, _TAP,
              tap_x - _TAP_W / 2, tap_y - _TAP_W / 2,
              tap_x + _TAP_W / 2, tap_y + _TAP_W / 2)
        _rect(cell, _NSDM,
              tap_x - _TAP_W / 2 - _PSDM_ENC, tap_y - _TAP_W / 2 - _PSDM_ENC,
              tap_x + _TAP_W / 2 + _PSDM_ENC, tap_y + _TAP_W / 2 + _PSDM_ENC)
        _sq(cell, _LICON1, tap_x, tap_y, _LICON)
        _sq(cell, _LI1, tap_x, tap_y, _LI_PAD)
        _sq(cell, _MCON_L, tap_x, tap_y, _MCON)
        half = 0.15
        y_lo = tap_y - half
        y_hi = vdd_real_rail_y + half
        _rect(cell, _MET1, tap_x - half, y_lo, tap_x + half, y_hi)
        _sq(cell, _VIA_L, tap_x, vdd_real_rail_y, _VIA1)
        _sq(cell, _MET2, tap_x, vdd_real_rail_y, 0.30)
        _sq(cell, _MET2, tap_x, vdd_real_rail_y,
            _VIA2 + 2 * _VIA2_ENC_MET2_OTHER)
        _sq(cell, _VIA2_L, tap_x, vdd_real_rail_y, _VIA2)
        _sq(cell, _MET3, tap_x, vdd_real_rail_y, _VIA2 + 2 * _VIA2_ENC_MET3)

    if output_path:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
