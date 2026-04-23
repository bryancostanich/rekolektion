"""M:1 SRAM column mux cell generator (architecturally correct).

For a mux_ratio of M:
  - Every bit owns M BL/BR pairs, 1 BL pass-gate per pair, 1 BR
    pass-gate per pair — M NMOS per bit per side = 2M pass-gates per
    mux group.
  - Pair j within a mux group has its BL and BR pass-gates both driven
    by sel[j]. One-hot select (exactly one sel[k] asserted) connects
    exactly one pair's BL and BR to the bit's muxed bus.
  - Shared outputs per bit: muxed_BL[bit] and muxed_BR[bit] (one each
    per mux group). These exit the cell at the bottom edge for the
    sense amp / write driver below.

Replaces the previous per-pair stack (which had mux_ratio levels per
pair and per-pair muxed outputs — not actually a mux, logged as the D2
C7 architectural fix).

Layout summary (bottom → top):
    muxed_BL / muxed_BR exit stubs (met1 vertical, one pair x_mp1/x_mp2
      of the *first pair in each mux group*)
    muxed_BL / muxed_BR horizontal bus (met2, spans the mux group)
    pass-gate row (vertical NMOS per pair, 2 pg per pair)
    drain → BL/BR met1 jogs
    sel[0..M-1] horizontal rails (met3)
    BL/BR met1 stubs (full cell height, per pair)

Pins:
    BL[i] / BR[i]              — per pair (cols = num_pairs)
    muxed_BL[i] / muxed_BR[i]  — per bit (= num_pairs / mux_ratio)
    sel[k]                     — one per mux level, k = 0..mux_ratio-1

At 1.310 µm pair pitch the cell has some DRC violations (poly/diff
inter-pair spacing below 0.27/0.21 minimums) — inherent to the bitcell
pitch; same class of violations the foundry sp_cell has. Closes via
the global SRAM area-marker / waiver flow, not per-cell fixes.
"""
from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS


# --- device + rule constants ------------------------------------------------
_W: float = 0.42
_L: float = 0.15
_SD_EXT: float = 0.35
_POLY_OVH: float = 0.13
_LICON: float = 0.17
_LI_ENC: float = 0.08
_NSDM_ENC: float = 0.125
_MCON: float = 0.17
_MET1_WIDTH: float = 0.14

_VIA1: float = 0.15
_VIA1_ENC: float = 0.055
_VIA2: float = 0.20
_VIA2_ENC_MET2_OTHER: float = 0.085
_VIA2_ENC_MET3: float = 0.065

_POLY_LICON_ENC: float = 0.08
_BAR_H: float = _LICON + 2 * _POLY_LICON_ENC     # 0.33 — wide enough to
                                                 # host a licon with encl

_BL_X: float = 0.0425
_BR_X: float = 1.1575
_MP1_X: float = 0.350    # BL pg diff at x=[0.14, 0.56]
_MP2_X: float = 0.800    # BR pg diff at x=[0.59, 1.01]; gap 0.03 to BL
                          # pg diff (no overlap; DRC-dirty diff.3). mcon
                          # pads clear BL and BR bitline stubs with ≥0.12
                          # µm (DRC-dirty met1.2 but no merge).

_MIN_PAIR_PITCH: float = 1.31

_DIFF_HALF: float = _W / 2.0
_DIFF_Y_HALF: float = _L / 2.0 + _SD_EXT
_INTER_ROW_GAP: float = 0.40
_POLY_DIFF_SPACE: float = 0.075

_RAIL_W: float = 0.40       # met3 rail width (same as precharge)

# Muxed bus exit x positions — coincide with the first pair's pg source
# stub x so the exit stub merges with the pair-0 pg source stub
# (same net: muxed_BL[bit] / muxed_BR[bit]). Any other x causes the
# full-height muxed exit stub to pass through some other pair's BL or
# BR pg source stub, shorting different bits together.
_MUX_BL_X: float = 0.350    # = _MP1_X — first pair BL pg source x
_MUX_BR_X: float = 0.800    # = _MP2_X — first pair BR pg source x

# Layer shortcuts
_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MCON_L = LAYERS.MCON.as_tuple
_MET1 = LAYERS.MET1.as_tuple
_VIA_L = LAYERS.VIA.as_tuple
_MET2 = LAYERS.MET2.as_tuple
_VIA2_L = LAYERS.VIA2.as_tuple
_MET3 = LAYERS.MET3.as_tuple
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


def _rect_hw(cell: gdstk.Cell, layer: tuple[int, int],
             cx: float, cy: float, w: float, h: float) -> None:
    _rect(cell, layer, cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2)


def _diff_contact(cell: gdstk.Cell, cx: float, cy: float) -> None:
    _sq(cell, _LICON1, cx, cy, _LICON)
    _sq(cell, _LI1, cx, cy, _LICON + 2 * _LI_ENC)


def _mcon_pad(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Mcon + asymmetric met1 pad (0.23 wide × 0.29 tall)."""
    _sq(cell, _MCON_L, cx, cy, _MCON)
    _rect_hw(cell, _MET1, cx, cy, _MCON + 2 * 0.03, _MCON + 2 * 0.06)


def _via1_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Via1 with 0.30 µm met1 and met2 pads (via.4a + via.5a compliant)."""
    _sq(cell, _MET1, cx, cy, 0.30)
    _sq(cell, _VIA_L, cx, cy, _VIA1)
    _sq(cell, _MET2, cx, cy, 0.30)


def _via1_stack_narrow(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Via1 with asymmetric 0.23×0.29 met1/met2 pads — for use at
    locations where a full 0.30 square pad would overlap an adjacent
    BL/BR bitline stub (e.g., at muxed_BL/BR vertical stub positions
    near BR). Violates via.4a (0.03 < 0.055 in x direction) but the
    non-overlap with the adjacent stub is the first-order requirement.
    """
    _rect_hw(cell, _MET1, cx, cy, 0.23, 0.29)
    _sq(cell, _VIA_L, cx, cy, _VIA1)
    _rect_hw(cell, _MET2, cx, cy, 0.23, 0.29)


def _via2_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    _sq(cell, _MET2, cx, cy, _VIA2 + 2 * _VIA2_ENC_MET2_OTHER)
    _sq(cell, _VIA2_L, cx, cy, _VIA2)
    _sq(cell, _MET3, cx, cy, _VIA2 + 2 * _VIA2_ENC_MET3)


def _tap_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Full licon→li1→mcon→met1→via1→met2 stack, single 0.30 µm pad."""
    _sq(cell, _LICON1, cx, cy, _LICON)
    _sq(cell, _LI1, cx, cy, _LICON + 2 * _LI_ENC)
    _sq(cell, _MCON_L, cx, cy, _MCON)
    _sq(cell, _MET1, cx, cy, 0.30)
    _sq(cell, _VIA_L, cx, cy, _VIA1)
    _sq(cell, _MET2, cx, cy, 0.30)


def _vertical_nmos(cell: gdstk.Cell, x_center: float, y_center: float):
    """Emit a vertical-current NMOS. Returns (bot_y, top_y) for the
    two contact positions (caller chooses which is source/drain)."""
    diff_left = x_center - _W / 2.0
    diff_right = x_center + _W / 2.0
    diff_bot = y_center - _L / 2.0 - _SD_EXT
    diff_top = y_center + _L / 2.0 + _SD_EXT

    _rect(cell, _DIFF, diff_left, diff_bot, diff_right, diff_top)
    _rect(cell, _NSDM,
          diff_left - _NSDM_ENC, diff_bot - _NSDM_ENC,
          diff_right + _NSDM_ENC, diff_top + _NSDM_ENC)

    top_y = diff_top - _SD_EXT / 2.0
    bot_y = diff_bot + _SD_EXT / 2.0
    _diff_contact(cell, x_center, top_y)
    _diff_contact(cell, x_center, bot_y)
    return bot_y, top_y


def generate_column_mux(
    num_pairs: int,
    mux_ratio: int = 2,
    pair_pitch: float = _MIN_PAIR_PITCH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Emit an M:1 column mux cell.

    num_pairs must be a multiple of mux_ratio (mux groups line up
    cleanly with bit boundaries).
    """
    if mux_ratio not in (2, 4, 8):
        raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
    if pair_pitch < _MIN_PAIR_PITCH - 1e-9:
        raise ValueError(f"pair_pitch {pair_pitch} < min {_MIN_PAIR_PITCH}")
    if num_pairs < mux_ratio or num_pairs % mux_ratio != 0:
        raise ValueError(
            f"num_pairs ({num_pairs}) must be a positive multiple of "
            f"mux_ratio ({mux_ratio})"
        )

    name = cell_name or (
        f"column_mux_{num_pairs}pairs_mux{mux_ratio}_p{int(pair_pitch*1000)}nm"
    )
    cell_w = _snap(num_pairs * pair_pitch)
    num_bits = num_pairs // mux_ratio
    group_width = _snap(mux_ratio * pair_pitch)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    # --- vertical layout plan -------------------------------------------
    # Bottom → top:
    #   exit region: per-pair muxed_BL/BR vertical met1 stubs from y=0
    #     up to mux_band_BL_y (for BL) or mux_band_BR_y (for BR).
    #   y = mux_band_BL_y : met2 horizontal tie band (per mux group),
    #     joins every pair's muxed_BL stub into one muxed_BL[bit] net.
    #   y = mux_band_BR_y : met2 horizontal tie band (per mux group),
    #     joins every pair's muxed_BR stub. Higher than BL band so BL
    #     and BR bands don't merge on met2.
    #   pg row: single row of vertical NMOS (BL pg at x_mp1, BR pg at
    #     x_mp2) per pair, sharing a horizontal gate poly.
    #   drain row: met1 jogs from pg top terminals to BL/BR stubs.
    #   sel[0..M-1] horizontal met3 rails above the pg row.
    #   cell top.
    # BL and BR tie bands need 0.14 µm met2 spacing; band rects are
    # 0.30 µm tall so center-to-center ≥ 0.30 + 0.14 = 0.44 µm.
    mux_band_BL_y: float = 0.28
    mux_band_BR_y: float = 0.78
    pg_y: float = mux_band_BR_y + _INTER_ROW_GAP + _DIFF_Y_HALF    # 1.605
    pg_bot_y: float = pg_y - _L / 2.0 - _SD_EXT / 2.0               # 1.325
    pg_top_y: float = pg_y + _L / 2.0 + _SD_EXT / 2.0               # 1.825
    sel_first_y: float = pg_top_y + _INTER_ROW_GAP + _RAIL_W / 2    # 2.425
    sel_pitch: float = _RAIL_W + _INTER_ROW_GAP                    # 0.80
    cell_h = _snap(sel_first_y + (mux_ratio - 1) * sel_pitch
                   + _RAIL_W / 2 + 0.14)

    _rect(cell, _BOUNDARY, 0, 0, cell_w, cell_h)

    # sel[k] horizontal met3 rails
    sel_y: list[float] = []
    for k in range(mux_ratio):
        y = sel_first_y + k * sel_pitch
        sel_y.append(y)
        _rect(cell, _MET3, 0, y - _RAIL_W / 2, cell_w, y + _RAIL_W / 2)
        cell.add(gdstk.Label(
            f"col_sel_{k}",
            (_snap(min(0.5, cell_w / 2)), _snap(y)),
            layer=_MET3[0], texttype=_MET3[1]))

    met1_half = _MET1_WIDTH / 2.0

    # --- Per pair features (BL/BR stubs, 2 pg each) ---------------------
    for i in range(num_pairs):
        x_offset = i * pair_pitch
        x_bl = x_offset + _BL_X
        x_br = x_offset + _BR_X
        x_mp1 = x_offset + _MP1_X
        x_mp2 = x_offset + _MP2_X
        k = i % mux_ratio                       # sel level for this pair
        bit = i // mux_ratio

        # Full-height BL[i] / BR[i] met1 stubs.
        _rect(cell, _MET1, x_bl - met1_half, 0.0, x_bl + met1_half, cell_h)
        cell.add(gdstk.Label(
            f"bl_{i}", (_snap(x_bl), _snap(cell_h - 0.15)),
            layer=_MET1[0], texttype=_MET1[1]))
        _rect(cell, _MET1, x_br - met1_half, 0.0, x_br + met1_half, cell_h)
        cell.add(gdstk.Label(
            f"br_{i}", (_snap(x_br), _snap(cell_h - 0.15)),
            layer=_MET1[0], texttype=_MET1[1]))

        # Adapter jogs at cell TOP: bridge the internal BL/BR stubs
        # (at x_bl=0.0425, x_br=1.1575) to the foundry bitcell's real
        # met1 BL/BR positions (at x_bl_real=0.420, x_br_real=0.780).
        # Without these jogs, the mux's BL at the cell top doesn't abut
        # the array's BL at x=0.420 (they'd be at different x). A short
        # horizontal met1 rect in the top 0.14 µm of the cell reaches
        # to the bitcell BL/BR x.
        x_bl_real = x_offset + 0.420
        x_br_real = x_offset + 0.780
        _rect(cell, _MET1,
              x_bl - met1_half, cell_h - 0.14,
              x_bl_real + met1_half, cell_h)
        _rect(cell, _MET1,
              x_br_real - met1_half, cell_h - 0.14,
              x_br + met1_half, cell_h)

        # BL pass-gate at x_mp1, BR pass-gate at x_mp2. Vertical NMOS.
        bl_bot, bl_top = _vertical_nmos(cell, x_mp1, pg_y)
        br_bot, br_top = _vertical_nmos(cell, x_mp2, pg_y)

        # Drain (top) → BL / BR via met1 jog.
        _mcon_pad(cell, x_mp1, bl_top)
        _rect(cell, _MET1,
              x_bl - met1_half, bl_top - met1_half,
              x_mp1 + met1_half, bl_top + met1_half)
        _mcon_pad(cell, x_mp2, br_top)
        _rect(cell, _MET1,
              x_mp2 - met1_half, br_top - met1_half,
              x_br + met1_half, br_top + met1_half)

        # Source (bottom) → muxed tie band via1. Met1 vertical stub
        # connects pg source mcon (at pg_bot_y) down to the tie-band y
        # (where the via1 lands). No per-pair exit stub — the cell has
        # one exit per mux group, at a safe x in the first pair's
        # BL-BR gap (emitted below in the per-bit loop).
        _mcon_pad(cell, x_mp1, bl_bot)
        _rect(cell, _MET1,
              x_mp1 - met1_half, mux_band_BL_y,
              x_mp1 + met1_half, bl_bot)
        _via1_stack_narrow(cell, x_mp1, mux_band_BL_y)

        _mcon_pad(cell, x_mp2, br_bot)
        _rect(cell, _MET1,
              x_mp2 - met1_half, mux_band_BR_y,
              x_mp2 + met1_half, br_bot)
        _via1_stack_narrow(cell, x_mp2, mux_band_BR_y)

        # Shared horizontal gate poly across both pg (BL + BR) for the
        # pair. Spans from BL pg diff left - POLY_OVH to BR pg diff
        # right + POLY_OVH.
        poly_left = x_mp1 - _W / 2.0 - _POLY_OVH
        poly_right = x_mp2 + _W / 2.0 + _POLY_OVH
        _rect(cell, _POLY,
              poly_left, pg_y - _L / 2.0,
              poly_right, pg_y + _L / 2.0)

        # Gate tap for this pair: vertical poly stub from gate poly up
        # to the appropriate sel[k] rail, then licon+stack.
        tap_rel_x = (_MP1_X + _MP2_X) / 2.0     # 0.655 — between pgs
        tap_x = x_offset + tap_rel_x
        stub_half = _BAR_H / 2.0                 # 0.165
        # Poly stub from gate poly top up to sel[k] tap y + margin.
        tap_y = sel_y[k]
        _rect(cell, _POLY,
              tap_x - stub_half, pg_y + _L / 2.0,
              tap_x + stub_half, tap_y + stub_half + 0.01)
        _tap_stack(cell, tap_x, tap_y)
        # Met2 riser from (tap_x, tap_y) up through nothing — just a
        # short piece linking via1 met2 pad to via2 pad at sel rail.
        _via2_stack(cell, tap_x, tap_y)

    # --- Per mux group (bit): muxed_BL / muxed_BR tie bands + exits ------
    for bit in range(num_bits):
        bit_x0 = bit * group_width
        first_x_mp1 = bit_x0 + _MP1_X
        last_x_mp1 = bit_x0 + (mux_ratio - 1) * pair_pitch + _MP1_X
        first_x_mp2 = bit_x0 + _MP2_X
        last_x_mp2 = bit_x0 + (mux_ratio - 1) * pair_pitch + _MP2_X
        mbl_x = bit_x0 + _MUX_BL_X
        mbr_x = bit_x0 + _MUX_BR_X
        band_half = 0.15            # match via1 pad half

        # BL tie band — spans from the exit x (left of first pair's
        # x_mp1) to the last pair's x_mp1, so every pair's via1 pad and
        # the exit via1 all land on the same met2 polygon.
        bl_band_left = min(first_x_mp1, mbl_x) - band_half
        bl_band_right = max(last_x_mp1, mbl_x) + band_half
        _rect(cell, _MET2,
              bl_band_left, mux_band_BL_y - band_half,
              bl_band_right, mux_band_BL_y + band_half)
        br_band_left = min(first_x_mp2, mbr_x) - band_half
        br_band_right = max(last_x_mp2, mbr_x) + band_half
        _rect(cell, _MET2,
              br_band_left, mux_band_BR_y - band_half,
              br_band_right, mux_band_BR_y + band_half)

        # muxed_BL / muxed_BR exit stubs (met1 vertical from y=0 up to
        # their respective band y), labeled at the cell-bottom edge.
        _rect(cell, _MET1,
              mbl_x - met1_half, 0.0,
              mbl_x + met1_half, mux_band_BL_y)
        _via1_stack(cell, mbl_x, mux_band_BL_y)
        cell.add(gdstk.Label(
            f"muxed_bl_{bit}", (_snap(mbl_x), _snap(0.14)),
            layer=_MET1[0], texttype=_MET1[1]))

        _rect(cell, _MET1,
              mbr_x - met1_half, 0.0,
              mbr_x + met1_half, mux_band_BR_y)
        _via1_stack(cell, mbr_x, mux_band_BR_y)
        cell.add(gdstk.Label(
            f"muxed_br_{bit}", (_snap(mbr_x), _snap(0.14)),
            layer=_MET1[0], texttype=_MET1[1]))

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))
    return cell, lib
