"""Precharge circuit generator with PMOS transistor geometry.

Generates a precharge cell with three PMOS transistors per bit-line pair:
  - MP1: connects BL to VDD  (precharge BL)
  - MP2: connects BR to VDD  (precharge BR)
  - MP3: connects BL to BR   (equalization)

All gates are driven by the active-low ``precharge_en`` signal.

Layout: three PMOS transistors stacked vertically per column, each with
gate contact on a horizontal poly extension to the LEFT of the diffusion.
Single full-width n-well covers all PMOS devices.
Minimum bl_pitch = 1.9 um.

Usage::

    from rekolektion.peripherals.precharge import generate_precharge
    cell, lib = generate_precharge(num_cols=64)
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


# ---------------------------------------------------------------------------
# Design-rule-derived constants
# ---------------------------------------------------------------------------

_W = 0.42       # transistor channel width
_L = 0.15       # gate length
_SD_EXT = 0.36  # source/drain diff past gate (need S/D li1 pads 0.17 apart)
_POLY_OVH = 0.14          # poly extension past diff (right side, poly.8 + margin)
_GATE_EXT = 0.52           # poly extension past diff (left side, for gate contact)
_LICON = 0.17
_LI_ENC = 0.08
_PSDM_ENC = 0.125
_NWELL_ENC = 0.18
_MCON = 0.17

_LI_PAD = _LICON + 2 * _LI_ENC
_POLY_PAD_W = _LICON + 2 * 0.06      # 0.29 (licon enc 0.05 + margin)
_POLY_PAD_H = _LICON + 2 * 0.09      # 0.35 (licon enc 0.08 + margin)
_DIFF_H = _L + 2 * _SD_EXT
_TRANS_PITCH = 1.60

_MIN_BL_PITCH = 1.9

# Layer shortcuts
_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MCON_L = LAYERS.MCON.as_tuple
_MET1 = LAYERS.MET1.as_tuple
_VIA = LAYERS.VIA.as_tuple
_MET2 = LAYERS.MET2.as_tuple
_NWELL = LAYERS.NWELL.as_tuple
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
    """Draw a square contact guaranteed to be exactly `size` after snapping."""
    x0 = _snap(cx - size / 2)
    y0 = _snap(cy - size / 2)
    cell.add(gdstk.rectangle(
        (x0, y0), (x0 + size, y0 + size),
        layer=layer[0], datatype=layer[1],
    ))


def _draw_pmos_transistor(
    cell: gdstk.Cell,
    x_center: float,
    y_center: float,
) -> tuple[float, float]:
    """Draw one PMOS transistor with gate contact on the left.

    Returns (gate_pad_cx, gate_pad_cy) for routing.
    """
    hw = _W / 2.0
    hl = _L / 2.0
    hs = _LICON / 2.0

    diff_left = x_center - hw
    diff_right = x_center + hw
    diff_bot = y_center - hl - _SD_EXT
    diff_top = y_center + hl + _SD_EXT

    # Diffusion
    _rect(cell, _DIFF, diff_left, diff_bot, diff_right, diff_top)

    # PSDM implant
    _rect(cell, _PSDM,
          diff_left - _PSDM_ENC, diff_bot - _PSDM_ENC,
          diff_right + _PSDM_ENC, diff_top + _PSDM_ENC)

    # Poly gate — extended left for gate contact
    poly_left = diff_left - _GATE_EXT
    poly_right = diff_right + _POLY_OVH
    _rect(cell, _POLY, poly_left, y_center - hl, poly_right, y_center + hl)

    # Poly contact pad at far left (widen Y for licon enclosure)
    pad_left = poly_left
    pad_right = poly_left + _POLY_PAD_W
    _rect(cell, _POLY,
          pad_left, y_center - _POLY_PAD_H / 2,
          pad_right, y_center + _POLY_PAD_H / 2)

    # Gate licon + li1
    gate_cx = pad_left + _POLY_PAD_W / 2
    gate_cy = y_center
    _sq_contact(cell, _LICON1, gate_cx, gate_cy, _LICON)
    _rect(cell, _LI1,
          gate_cx - _LI_PAD / 2, gate_cy - _LI_PAD / 2,
          gate_cx + _LI_PAD / 2, gate_cy + _LI_PAD / 2)

    # Source contact (top — connects to VDD)
    src_y = y_center + hl + _SD_EXT / 2.0
    _sq_contact(cell, _LICON1, x_center, src_y, _LICON)
    _rect(cell, _LI1,
          x_center - _LI_PAD / 2, src_y - _LI_PAD / 2,
          x_center + _LI_PAD / 2, src_y + _LI_PAD / 2)

    # Drain contact (bottom — connects to bit-line)
    drn_y = y_center - hl - _SD_EXT / 2.0
    _sq_contact(cell, _LICON1, x_center, drn_y, _LICON)
    _rect(cell, _LI1,
          x_center - _LI_PAD / 2, drn_y - _LI_PAD / 2,
          x_center + _LI_PAD / 2, drn_y + _LI_PAD / 2)

    return gate_cx, gate_cy


def generate_precharge(
    num_cols: int,
    bl_pitch: float = 1.925,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a precharge cell with PMOS transistor geometry.

    Three PMOS transistors per column stacked vertically.
    """
    eff_pitch = max(bl_pitch, _MIN_BL_PITCH)

    name = cell_name or f"precharge_{num_cols}"
    width = _snap(num_cols * eff_pitch)

    # Y layout: precharge_en bus -> MP3 -> MP1 -> MP2 -> VDD rail
    pch_en_y = 0.35
    mp3_y = 1.5      # equalization
    mp1_y = 3.0      # BL precharge
    mp2_y = 4.5      # BR precharge
    vdd_y = 5.6       # VDD rail
    height = _snap(6.0)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    _rect(cell, _BOUNDARY, 0, 0, width, height)

    # N-well: covers all PMOS devices (full width)
    nw_bot = mp3_y - _L / 2 - _SD_EXT - _NWELL_ENC
    nw_top = mp2_y + _L / 2 + _SD_EXT + _NWELL_ENC
    # Ensure minimum nwell width
    if nw_top - nw_bot < RULES.NWELL_MIN_WIDTH:
        extra = (RULES.NWELL_MIN_WIDTH - (nw_top - nw_bot)) / 2
        nw_bot -= extra
        nw_top += extra
    _rect(cell, _NWELL, 0, nw_bot, width, nw_top)

    # VDD rail (met1, horizontal)
    rail_h = 0.28
    _rect(cell, _MET1, 0, vdd_y - rail_h / 2, width, vdd_y + rail_h / 2)
    cell.add(gdstk.Label(
        "VDD", (_snap(width / 2), _snap(vdd_y)),
        layer=_MET1[0], texttype=_MET1[1],
    ))

    # precharge_en bus (met1, horizontal)
    _rect(cell, _MET1, 0, pch_en_y - 0.07, width, pch_en_y + 0.07)
    cell.add(gdstk.Label(
        "precharge_en", (0.5, _snap(pch_en_y)),
        layer=_MET1[0], texttype=_MET1[1],
    ))

    met1_w = 0.15

    for i in range(num_cols):
        x_center = i * eff_pitch + eff_pitch / 2

        # --- BL/BR met2 stubs (full height) ---------------------------------
        _rect(cell, _MET2, x_center - 0.07, 0, x_center + 0.07, height)
        cell.add(gdstk.Label(
            f"BL[{i}]", (_snap(x_center), 0.15),
            layer=_MET2[0], texttype=_MET2[1],
        ))

        # --- MP3: equalization (at x_center) --------------------------------
        gate_cx, gate_cy = _draw_pmos_transistor(cell, x_center, mp3_y)
        # Gate mcon + met1 to precharge_en bus
        hs = _MCON / 2.0
        _sq_contact(cell, _MCON_L, gate_cx, gate_cy, _MCON)
        met1_pad = _MCON + 2 * 0.06
        _rect(cell, _MET1,
              gate_cx - met1_pad / 2, gate_cy - met1_pad / 2,
              gate_cx + met1_pad / 2, gate_cy + met1_pad / 2)
        _rect(cell, _MET1,
              gate_cx - met1_w / 2, pch_en_y - 0.07,
              gate_cx + met1_w / 2, gate_cy + met1_pad / 2)

        # --- MP1: BL precharge (at x_center) --------------------------------
        gate_cx1, gate_cy1 = _draw_pmos_transistor(cell, x_center, mp1_y)
        _sq_contact(cell, _MCON_L, gate_cx1, gate_cy1, _MCON)
        _rect(cell, _MET1,
              gate_cx1 - met1_pad / 2, gate_cy1 - met1_pad / 2,
              gate_cx1 + met1_pad / 2, gate_cy1 + met1_pad / 2)
        _rect(cell, _MET1,
              gate_cx1 - met1_w / 2, pch_en_y - 0.07,
              gate_cx1 + met1_w / 2, gate_cy1 + met1_pad / 2)

        # MP1 source (top) to VDD via met1
        src1_y = mp1_y + _L / 2 + _SD_EXT / 2
        _sq_contact(cell, _MCON_L, x_center, src1_y, _MCON)
        _rect(cell, _MET1,
              x_center - met1_pad / 2, src1_y - met1_pad / 2,
              x_center + met1_pad / 2, vdd_y + rail_h / 2)

        # --- MP2: BR precharge (at x_center) --------------------------------
        gate_cx2, gate_cy2 = _draw_pmos_transistor(cell, x_center, mp2_y)
        _sq_contact(cell, _MCON_L, gate_cx2, gate_cy2, _MCON)
        _rect(cell, _MET1,
              gate_cx2 - met1_pad / 2, gate_cy2 - met1_pad / 2,
              gate_cx2 + met1_pad / 2, gate_cy2 + met1_pad / 2)
        _rect(cell, _MET1,
              gate_cx2 - met1_w / 2, pch_en_y - 0.07,
              gate_cx2 + met1_w / 2, gate_cy2 + met1_pad / 2)

        # MP2 source (top) to VDD via met1
        src2_y = mp2_y + _L / 2 + _SD_EXT / 2
        _sq_contact(cell, _MCON_L, x_center, src2_y, _MCON)
        _rect(cell, _MET1,
              x_center - met1_pad / 2, src2_y - met1_pad / 2,
              x_center + met1_pad / 2, vdd_y + rail_h / 2)

    if output_path:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
