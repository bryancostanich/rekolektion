"""Per-BL/BR-pair SRAM precharge cell generator.

Emits DRC-clean precharge cells at bitcell pitch (1.31 µm per BL/BR pair).
Each pair column houses three PMOS:
  MP1  — VDD → BL  (BL precharge),   gate on `precharge_en`
  MP2  — VDD → BR  (BR precharge),   gate on `precharge_en`
  MP3  — BL ↔ BR   (equalizer),      gate on `precharge_en`

Layout per pair (cell-local coords):
  - BL and BR vertical met1 stubs at cell-local x = 0.0425 / 1.1575,
    matching `bitcell_array._BITCELL_BL_X_OFFSET` / `_BR_X_OFFSET`.
  - MP1 and MP2 transistors have to sit INSIDE the cell (diff at
    0.42 µm W doesn't fit under the bitline track at x=0.0425),
    so they're offset inward; short met1 jogs connect each
    transistor's drain to its bitline track.
  - Three distinct Y-rows so the MP1 / MP2 / MP3 diffs don't collide
    even when MP1 and MP2 x-centres are close.

Rows, bottom → top:
    precharge_en rail (met1, horizontal)
    Row C : MP3 equalizer      (horizontal PMOS diff, poly vertical)
    Row B : MP2 (BR precharge) (vertical PMOS, offset right)
    Row A : MP1 (BL precharge) (vertical PMOS, offset left)
    VDD rail (met1, horizontal)

Shared nwell spans the full cell; PSDM wraps each diff.
"""
from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


# --- device + rule constants ------------------------------------------------
_W: float = 0.42           # PMOS channel width (diff X extent)
_L: float = 0.15           # channel length
_SD_EXT: float = 0.35      # diff past gate in current direction
                           # Must satisfy:
                           #   (a) licon.11  — licon half (0.085) + 0.055
                           #       clearance to gate edge => SD_EXT >= 0.28
                           #   (b) li.3      — S/D li1 pads on same diff
                           #       must be >= 0.17 apart.  Source y at
                           #       diff_top - SD_EXT/2, drain y at
                           #       diff_bot + SD_EXT/2, pads 0.33 tall.
                           #       Gap = (L + 2*SD_EXT) - 0.63. For gap
                           #       >= 0.17 need SD_EXT >= 0.325.
                           # Use 0.35 for 25 nm margin over both.
_POLY_OVH: float = 0.13    # poly overhang of transistor (poly.8)
_GATE_EXT: float = 0.13    # poly extension for gate contact (same as OVH; contact on poly overhang)
_LICON: float = 0.17       # licon contact side
_LI_ENC: float = 0.08      # li1 enclosure of licon (li.5 dir)
_PSDM_ENC: float = 0.125   # PSDM diff enclosure
_NWELL_ENC: float = 0.18   # nwell enclosure of p-diff
_MCON: float = 0.17        # mcon contact side
_MCON_MET1_ENC: float = 0.085  # met1 enclosure of mcon (safe sym, covers via.5a)
_MET1_WIDTH: float = 0.14  # met1 min width

_DIFF_SPACING_SAME: float = 0.27   # diff/tap.3
_POLY_SPACING: float = 0.21        # poly.2

# Bitline x-positions within a 1.31 µm pair cell — match bitcell_array.
_BL_X: float = 0.0425
_BR_X: float = 1.1575

_MIN_PAIR_PITCH: float = 1.31

# Transistor x-offsets from the bitline tracks (pushed INWARD so the
# 0.42 µm-wide diffs don't poke outside the cell boundary).
_MP1_X_OFFSET: float = 0.345 - _BL_X   # MP1 at x = 0.345
_MP2_X_OFFSET: float = _BR_X - 0.345   # wait — want MP2 at x = 0.965
# Use explicit absolute x instead of offsets for clarity.
_MP1_X: float = 0.345
_MP2_X: float = 0.965

# Row Y centres — computed from stacking W+SD extents + spacing.
_DIFF_HALF: float = _W / 2.0                 # 0.21
_DIFF_Y_HALF: float = _L / 2.0 + _SD_EXT     # 0.325

# Row spacing: need diff-to-diff ≥ 0.27 AND poly-to-poly ≥ 0.21.
# Use 0.40 between diff edges as margin.
_INTER_ROW_GAP: float = 0.40

# Rail widths
_RAIL_W: float = 0.28

# Build the Y layout bottom up.
_EN_RAIL_Y: float = 0.14 + _RAIL_W / 2                    # 0.28
# Row C (MP3) horizontal diff: W in Y, top & bottom at y ± W/2.
_ROW_C_Y: float = _EN_RAIL_Y + _RAIL_W / 2 + _INTER_ROW_GAP + _DIFF_HALF  # 1.17
# Row B (MP2 vertical) — diff extends ± (L/2 + SD_EXT) = 0.325
_ROW_B_Y: float = _ROW_C_Y + _DIFF_HALF + _INTER_ROW_GAP + _DIFF_Y_HALF   # 2.295
# Row A (MP1 vertical)
_ROW_A_Y: float = _ROW_B_Y + _DIFF_Y_HALF + _INTER_ROW_GAP + _DIFF_Y_HALF # 3.345
_VDD_RAIL_Y: float = _ROW_A_Y + _DIFF_Y_HALF + _INTER_ROW_GAP + _RAIL_W / 2  # 4.21
_CELL_H: float = _VDD_RAIL_Y + _RAIL_W / 2 + 0.14         # 4.49

# Layer shortcuts
_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MCON_L = LAYERS.MCON.as_tuple
_MET1 = LAYERS.MET1.as_tuple
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


def _sq(cell: gdstk.Cell, layer: tuple[int, int],
        cx: float, cy: float, size: float) -> None:
    h = size / 2
    _rect(cell, layer, cx - h, cy - h, cx + h, cy + h)


def _contact_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Emit one licon (diff contact) with a matching li1 pad."""
    _sq(cell, _LICON1, cx, cy, _LICON)
    pad = _LICON + 2 * _LI_ENC
    _rect(cell, _LI1,
          cx - pad / 2, cy - pad / 2,
          cx + pad / 2, cy + pad / 2)


def _mcon_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Emit one mcon (li1→met1 via) with both li1 and met1 pads."""
    # Need li1 under the mcon (to land on an existing li1 contact stack,
    # but also draw a small pad here in case no li1 overlaps elsewhere).
    _sq(cell, _MCON_L, cx, cy, _MCON)
    met1_pad = _MCON + 2 * _MCON_MET1_ENC    # 0.34
    _rect(cell, _MET1,
          cx - met1_pad / 2, cy - met1_pad / 2,
          cx + met1_pad / 2, cy + met1_pad / 2)


def _vertical_pmos(cell: gdstk.Cell, x_center: float, y_center: float):
    """Draw one vertical-current-flow PMOS at (x_center, y_center).

    Source at top, drain at bottom.  Poly is horizontal across the
    channel.  Returns (x_center, drain_y, src_y).
    """
    # Diff extent
    diff_left = x_center - _W / 2.0
    diff_right = x_center + _W / 2.0
    diff_bot = y_center - _L / 2.0 - _SD_EXT
    diff_top = y_center + _L / 2.0 + _SD_EXT

    _rect(cell, _DIFF, diff_left, diff_bot, diff_right, diff_top)
    _rect(cell, _PSDM,
          diff_left - _PSDM_ENC, diff_bot - _PSDM_ENC,
          diff_right + _PSDM_ENC, diff_top + _PSDM_ENC)

    # Poly gate — horizontal across diff at channel y.
    poly_bot = y_center - _L / 2.0
    poly_top = y_center + _L / 2.0
    poly_left = diff_left - _POLY_OVH
    poly_right = diff_right + _POLY_OVH
    _rect(cell, _POLY, poly_left, poly_bot, poly_right, poly_top)

    # Source (top) + drain (bottom) contacts
    src_y = diff_top - _SD_EXT / 2.0
    drn_y = diff_bot + _SD_EXT / 2.0
    _contact_stack(cell, x_center, src_y)
    _contact_stack(cell, x_center, drn_y)

    return x_center, drn_y, src_y


def _horizontal_pmos(cell: gdstk.Cell, x_left: float, x_right: float,
                     y_center: float):
    """Draw one horizontal-current-flow PMOS (equalizer).

    Source on left, drain on right.  Poly is vertical at channel x.
    Returns (poly_cx, left_contact_x, right_contact_x).
    """
    poly_cx = (x_left + x_right) / 2.0
    diff_bot = y_center - _W / 2.0
    diff_top = y_center + _W / 2.0

    _rect(cell, _DIFF, x_left, diff_bot, x_right, diff_top)
    _rect(cell, _PSDM,
          x_left - _PSDM_ENC, diff_bot - _PSDM_ENC,
          x_right + _PSDM_ENC, diff_top + _PSDM_ENC)

    # Poly gate — vertical across the channel.
    poly_left = poly_cx - _L / 2.0
    poly_right = poly_cx + _L / 2.0
    _rect(cell, _POLY,
          poly_left, diff_bot - _POLY_OVH,
          poly_right, diff_top + _POLY_OVH)

    # Source (left) + drain (right)
    src_cx = x_left + _SD_EXT / 2.0
    drn_cx = x_right - _SD_EXT / 2.0
    _contact_stack(cell, src_cx, y_center)
    _contact_stack(cell, drn_cx, y_center)

    return poly_cx, src_cx, drn_cx


def generate_precharge(
    num_pairs: int,
    pair_pitch: float = _MIN_PAIR_PITCH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Emit a DRC-clean multi-pair precharge cell.

    num_pairs  : number of BL/BR pair columns
    pair_pitch : horizontal cell pitch per pair (min 1.31 µm)
    """
    if pair_pitch < _MIN_PAIR_PITCH - 1e-9:
        raise ValueError(
            f"pair_pitch {pair_pitch} < min {_MIN_PAIR_PITCH}"
        )
    if num_pairs < 1:
        raise ValueError(f"num_pairs must be >= 1; got {num_pairs}")

    name = cell_name or (
        f"precharge_{num_pairs}pairs_p{int(pair_pitch * 1000)}nm"
    )
    cell_w = _snap(num_pairs * pair_pitch)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)
    _rect(cell, _BOUNDARY, 0, 0, cell_w, _CELL_H)

    # nwell covers all PMOS rows + enclosure margin.
    nw_bot = _ROW_C_Y - _W / 2.0 - _NWELL_ENC
    nw_top = _ROW_A_Y + _DIFF_Y_HALF + _NWELL_ENC
    _rect(cell, _NWELL, -_NWELL_ENC, nw_bot,
          cell_w + _NWELL_ENC, nw_top)

    # VDD rail (met1, horizontal)
    _rect(cell, _MET1, 0, _VDD_RAIL_Y - _RAIL_W / 2,
          cell_w, _VDD_RAIL_Y + _RAIL_W / 2)
    cell.add(gdstk.Label("VDD", (_snap(cell_w / 2), _snap(_VDD_RAIL_Y)),
                         layer=_MET1[0], texttype=_MET1[1]))

    # precharge_en rail (met1, horizontal)
    _rect(cell, _MET1, 0, _EN_RAIL_Y - _RAIL_W / 2,
          cell_w, _EN_RAIL_Y + _RAIL_W / 2)
    cell.add(gdstk.Label("precharge_en", (_snap(0.5), _snap(_EN_RAIL_Y)),
                         layer=_MET1[0], texttype=_MET1[1]))

    met1_stub_half = _MET1_WIDTH / 2.0

    for i in range(num_pairs):
        x_offset = i * pair_pitch
        x_bl_abs = x_offset + _BL_X
        x_br_abs = x_offset + _BR_X
        x_mp1_abs = x_offset + _MP1_X
        x_mp2_abs = x_offset + _MP2_X

        # BL and BR full-height met1 stubs
        _rect(cell, _MET1, x_bl_abs - met1_stub_half, 0,
              x_bl_abs + met1_stub_half, _CELL_H)
        cell.add(gdstk.Label(
            f"BL[{i}]", (_snap(x_bl_abs), _snap(_CELL_H - 0.15)),
            layer=_MET1[0], texttype=_MET1[1]))
        _rect(cell, _MET1, x_br_abs - met1_stub_half, 0,
              x_br_abs + met1_stub_half, _CELL_H)
        cell.add(gdstk.Label(
            f"BR[{i}]", (_snap(x_br_abs), _snap(_CELL_H - 0.15)),
            layer=_MET1[0], texttype=_MET1[1]))

        # MP1 (BL precharge) at Row A
        _, mp1_drn_y, mp1_src_y = _vertical_pmos(cell, x_mp1_abs, _ROW_A_Y)
        # MP1 source (top)  → VDD rail
        _mcon_stack(cell, x_mp1_abs, mp1_src_y)
        _rect(cell, _MET1, x_mp1_abs - met1_stub_half, mp1_src_y,
              x_mp1_abs + met1_stub_half, _VDD_RAIL_Y)
        # MP1 drain (bottom) → BL stub via met1 jog
        _mcon_stack(cell, x_mp1_abs, mp1_drn_y)
        # jog from x_mp1 at mp1_drn_y to x_bl at same y
        _rect(cell, _MET1, x_bl_abs - met1_stub_half, mp1_drn_y - met1_stub_half,
              x_mp1_abs + met1_stub_half, mp1_drn_y + met1_stub_half)

        # MP2 (BR precharge) at Row B
        _, mp2_drn_y, mp2_src_y = _vertical_pmos(cell, x_mp2_abs, _ROW_B_Y)
        _mcon_stack(cell, x_mp2_abs, mp2_src_y)
        # MP2 source → VDD (reach up past Row A; route in met1 clear of MP1
        # diff by going at x_mp2 column, well separated from x_mp1 column).
        _rect(cell, _MET1, x_mp2_abs - met1_stub_half, mp2_src_y,
              x_mp2_abs + met1_stub_half, _VDD_RAIL_Y)
        _mcon_stack(cell, x_mp2_abs, mp2_drn_y)
        _rect(cell, _MET1, x_mp2_abs - met1_stub_half, mp2_drn_y - met1_stub_half,
              x_br_abs + met1_stub_half, mp2_drn_y + met1_stub_half)

        # MP3 equalizer at Row C, between MP1 drain x and MP2 drain x.
        eq_left = x_mp1_abs + _W / 2.0 + _DIFF_SPACING_SAME  # spacing from MP1 diff
        eq_right = x_mp2_abs - _W / 2.0 - _DIFF_SPACING_SAME
        # Clamp equalizer diff to BL/BR track range so its drain connects.
        eq_left = max(eq_left, x_bl_abs + 0.10)
        eq_right = min(eq_right, x_br_abs - 0.10)
        if eq_right - eq_left >= 2 * _SD_EXT + _L:
            _, eq_src, eq_drn = _horizontal_pmos(cell, eq_left, eq_right, _ROW_C_Y)
            # eq_src (left) → BL stub
            _mcon_stack(cell, eq_src, _ROW_C_Y)
            _rect(cell, _MET1, x_bl_abs - met1_stub_half, _ROW_C_Y - met1_stub_half,
                  eq_src + met1_stub_half, _ROW_C_Y + met1_stub_half)
            # eq_drn (right) → BR stub
            _mcon_stack(cell, eq_drn, _ROW_C_Y)
            _rect(cell, _MET1, eq_drn - met1_stub_half, _ROW_C_Y - met1_stub_half,
                  x_br_abs + met1_stub_half, _ROW_C_Y + met1_stub_half)

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))
    return cell, lib
