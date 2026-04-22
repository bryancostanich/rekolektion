"""SRAM precharge cell generator — Option II: shared-diffusion N=2 blocks.

Extraction-clean per-column precharge at bitcell pitch (1.310 µm per
BL/BR pair), using shared source diffusion across 2-column blocks to
relieve the geometric pressure that makes a fully per-pair layout
infeasible at sky130 design rules.

Per N=2 block (2 pairs = 2.62 µm):
  Row A — shared-source PMOS pair (horizontal diff strip):
    MP1[2k], MP1[2k+1] — drains at BL[2k], BL[2k+1]; shared VDD source
    between them. One source via-stack per block, centred at x=0.6975
    (well clear of every BL/BR stub).
  Row B — same pattern for MP2s (BR precharge), drains at BR[2k],
    BR[2k+1]; shared VDD source at x=1.8125.
  Row C — per-pair MP3 equalizers (BL ↔ BR), gates tap precharge_en
    through a poly stub below each MP3.

Gates:
  Row A and Row B gate lines are continuous horizontal poly back-bars
  above their diff strips, with vertical stems crossing each transistor
  diff. Each back-bar is tapped to precharge_en once per cell.

Rails (canonical sky130 / OpenRAM):
  VDD — horizontal met3 at the top
  precharge_en — horizontal met3 at the bottom
  BL/BR vertical met1 stubs cross rails without shorting (different
  masks; via stacks only where intentional taps land).

Why N=2:
  At 1.310 µm pair pitch, per-pair via stacks for MP1/MP2 source AND
  per-pair gate taps fight for the same narrow inter-pair gap (~22 nm
  valid x window once BL/BR 0.14 met1 spacing is enforced). Shared
  diffusion moves source tapping to block centers, freeing the gap.
  N=2 is the smallest refactor that achieves this — one diff break per
  two pairs is the minimum novelty.
"""
from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS


# --- device + rule constants ------------------------------------------------
_W: float = 0.42            # PMOS channel width (extent along y for horiz PMOS)
_L: float = 0.15            # channel length (poly width)
_SD_EXT: float = 0.35       # diff past gate (licon.11 + li.3 satisfied)
_POLY_OVH: float = 0.13     # poly overhang past diff (poly.8)
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

# Bitline / block geometry.
_BL_X: float = 0.0425
_BR_X: float = 1.1575
_MIN_PAIR_PITCH: float = 1.31

# Row A shared-source block x (block 0, pair 0 = BL[0], pair 1 = BL[1]):
#   Drain MP1[0]  at x = 0.0425          (BL[0])
#   Gate  MP1[0]  at x = 0.0425 + 0.25 = 0.2925
#   Gate  MP1[1]  at x = 1.3525 - 0.25 = 1.1025
#   Drain MP1[1]  at x = 1.3525          (BL[1])
#   Shared source contact centred at (0.2925 + 1.1025)/2 = 0.6975
_MP1_GATE_REL: tuple[float, float] = (0.2925, 1.1025)
_MP1_SRC_TAP_X: float = 0.6975

# Row B (MP2): same topology shifted by 1.115 µm (BL → BR offset within a pair).
_MP2_GATE_REL: tuple[float, float] = (1.4075, 2.2175)
_MP2_SRC_TAP_X: float = 1.8125

_DIFF_HALF: float = _W / 2.0          # 0.21 — horizontal-PMOS y half-extent
_INTER_ROW_GAP: float = 0.40
_POLY_DIFF_SPACE: float = 0.075        # poly.5 — poly end to diff edge

_RAIL_W: float = 0.40                  # met3 rail width

# Back-bar thickness (widened enough to enclose a 0.17 µm licon with
# 0.08 µm poly-encl on all sides).
_BAR_H: float = _LICON + 2 * _POLY_LICON_ENC    # 0.33

# Y layout, bottom → top. Back-bars are 0.33 µm tall (LICON + 2·encl),
# not the 0.15 µm poly min-width; otherwise licon doesn't fit on the bar.
_EN_RAIL_Y: float = 0.28
# Row C (MP3 equalizers — per-pair; gates tapped below their diff, no
# back-bar needed for Row C).
_ROW_C_Y: float = _EN_RAIL_Y + _RAIL_W / 2 + _INTER_ROW_GAP + _DIFF_HALF    # 1.09
_ROW_C_POLY_TOP: float = _ROW_C_Y + _DIFF_HALF + _POLY_OVH                  # 1.43
# Row B (MP2 shared-source)
_ROW_B_DIFF_BOT: float = _ROW_C_POLY_TOP + _POLY_DIFF_SPACE                 # 1.505
_ROW_B_Y: float = _ROW_B_DIFF_BOT + _DIFF_HALF                               # 1.715
_ROW_B_POLY_TOP: float = _ROW_B_Y + _DIFF_HALF + _POLY_OVH                   # 2.055
_ROW_B_BAR_Y: float = _ROW_B_POLY_TOP + _BAR_H / 2                           # 2.22
_ROW_B_BAR_TOP: float = _ROW_B_BAR_Y + _BAR_H / 2                            # 2.385
# Row A (MP1 shared-source)
_ROW_A_DIFF_BOT: float = _ROW_B_BAR_TOP + _POLY_DIFF_SPACE                   # 2.46
_ROW_A_Y: float = _ROW_A_DIFF_BOT + _DIFF_HALF                               # 2.67
_ROW_A_POLY_TOP: float = _ROW_A_Y + _DIFF_HALF + _POLY_OVH                   # 3.01
_ROW_A_BAR_Y: float = _ROW_A_POLY_TOP + _BAR_H / 2                           # 3.175
_ROW_A_BAR_TOP: float = _ROW_A_BAR_Y + _BAR_H / 2                            # 3.34
# VDD rail above Row A back-bar.
_VDD_RAIL_Y: float = _ROW_A_BAR_TOP + 0.10 + _RAIL_W / 2                      # 3.64
_CELL_H: float = _VDD_RAIL_Y + _RAIL_W / 2 + 0.14                             # 3.98

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
_NWELL = LAYERS.NWELL.as_tuple
_PSDM = LAYERS.PSDM.as_tuple
_BOUNDARY = LAYERS.BOUNDARY.as_tuple
_COREID = LAYERS.COREID.as_tuple      # (81, 2) — sky130 SRAM core marker:
                                       # relaxes met1/li1/poly spacing to the
                                       # sub-min values needed at 1.310 µm
                                       # pair pitch (adjacent BL/BR stubs sit
                                       # 0.055 µm apart — way below the 0.14
                                       # µm general met1.2 rule, but allowed
                                       # under the SRAM core exception).


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


def _poly_contact(cell: gdstk.Cell, cx: float, cy: float) -> None:
    _sq(cell, _LICON1, cx, cy, _LICON)
    _sq(cell, _LI1, cx, cy, _LICON + 2 * _LI_ENC)


def _mcon_pad(cell: gdstk.Cell, cx: float, cy: float,
              met1_w: float = None, met1_h: float = None) -> None:
    """Mcon + met1 pad with asymmetric enclosure by default (0.23 wide
    in x, 0.29 tall in y — the sky130 asymmetric mcon encl rule)."""
    _sq(cell, _MCON_L, cx, cy, _MCON)
    w = met1_w if met1_w is not None else (_MCON + 2 * 0.03)
    h = met1_h if met1_h is not None else (_MCON + 2 * 0.06)
    _rect_hw(cell, _MET1, cx, cy, w, h)


def _via1_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Via1 with 0.30 µm symmetric pads on met1 and met2.

    Pad 0.30 µm meets via.4a (≥0.055 symmetric), via.5a (≥0.06 in one
    direction), and met1.6 min area (0.09 > 0.083 µm²) standalone.
    Caller must ensure any overlapping met1/met2 already present at
    (cx, cy) either matches the 0.30 µm footprint or is contained in
    it — Magic checks encl on each source polygon, not just the union.
    """
    pad = 0.30
    _sq(cell, _MET1, cx, cy, pad)
    _sq(cell, _VIA_L, cx, cy, _VIA1)
    _sq(cell, _MET2, cx, cy, pad)


def _tap_stack(cell: gdstk.Cell, cx: float, cy: float, poly: bool = False) -> None:
    """Full stack for a tap point on a poly or diff terminal:
    licon → li1 → mcon → met1 → via1 → met2, with a single 0.30 µm
    met1 pad so Magic sees one polygon for via1 encl.
    """
    _sq(cell, _LICON1, cx, cy, _LICON)
    _sq(cell, _LI1, cx, cy, _LICON + 2 * _LI_ENC)
    _sq(cell, _MCON_L, cx, cy, _MCON)
    _sq(cell, _MET1, cx, cy, 0.30)
    _sq(cell, _VIA_L, cx, cy, _VIA1)
    _sq(cell, _MET2, cx, cy, 0.30)


def _via2_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    _sq(cell, _MET2, cx, cy, _VIA2 + 2 * _VIA2_ENC_MET2_OTHER)
    _sq(cell, _VIA2_L, cx, cy, _VIA2)
    _sq(cell, _MET3, cx, cy, _VIA2 + 2 * _VIA2_ENC_MET3)


def _shared_source_pair(
    cell: gdstk.Cell, y_center: float, x_offset: float,
    gate_xs: tuple[float, float], drain_xs: tuple[float, float],
    src_tap_x: float,
) -> None:
    """Emit a shared-source horizontal PMOS pair at row y_center.

    x_offset is the block's cell-origin offset; all x parameters are
    interpreted relative to x_offset.

    Diff: continuous horizontal strip from just left of drain[0] to just
    right of drain[1]. Poly stems at each gate x. Drain contacts at
    drain_xs; one shared source contact at src_tap_x (between gates).
    """
    xg0 = x_offset + gate_xs[0]
    xg1 = x_offset + gate_xs[1]
    xd0 = x_offset + drain_xs[0]
    xd1 = x_offset + drain_xs[1]
    xs = x_offset + src_tap_x

    diff_left = xd0 - _SD_EXT / 2.0
    diff_right = xd1 + _SD_EXT / 2.0
    diff_bot = y_center - _W / 2.0
    diff_top = y_center + _W / 2.0

    _rect(cell, _DIFF, diff_left, diff_bot, diff_right, diff_top)
    _rect(cell, _PSDM,
          diff_left - _PSDM_ENC, diff_bot - _PSDM_ENC,
          diff_right + _PSDM_ENC, diff_top + _PSDM_ENC)

    # Vertical poly stems at each gate x (poly overhangs past diff).
    poly_bot = diff_bot - _POLY_OVH
    poly_top = diff_top + _POLY_OVH
    for xg in (xg0, xg1):
        _rect(cell, _POLY,
              xg - _L / 2.0, poly_bot,
              xg + _L / 2.0, poly_top)

    # Drain contacts at each BL/BR x.
    _diff_contact(cell, xd0, y_center)
    _diff_contact(cell, xd1, y_center)
    # Shared source contact in the middle.
    _diff_contact(cell, xs, y_center)


def _row_back_bar(
    cell: gdstk.Cell, y_center: float, x_left: float, x_right: float,
    gate_xs_list: list[float],
) -> None:
    """Horizontal poly back-bar connecting every gate stem's upper end,
    plus vertical extensions from each gate-stem top into the bar so
    poly stays one connected polygon.

    gate_xs_list: list of absolute (cell-level) x positions of every
    vertical gate stem that must merge into this bar.
    """
    # Back-bar (horizontal poly strip).
    _rect(cell, _POLY, x_left, y_center - _L / 2.0,
          x_right, y_center + _L / 2.0)
    # (The per-stem poly rectangles already reach y = diff_top + POLY_OVH,
    # which is exactly y_center - _L/2; they merge with the bar at the
    # bottom edge of the bar by construction.)
    # (Nothing else to draw here if stem tops align with bar bottom.)
    _ = gate_xs_list


def _gate_tap_to_rail(
    cell: gdstk.Cell, tap_x: float, bar_y: float, rail_y: float,
) -> None:
    """From a poly back-bar at (tap_x, bar_y), drop a stack
    licon → li1 → mcon → met1 → via1 → met2 → ... → via2 → met3
    landing on the precharge_en rail at y=rail_y.

    Assumes tap_x lies on a poly back-bar that is already drawn.
    """
    _tap_stack(cell, tap_x, bar_y, poly=True)
    # Met2 riser from (tap_x, bar_y) down to rail_y. Width 0.30 µm to
    # match via1/via2 met2 pads at the stack and via2 pad at the rail.
    half = 0.15
    _rect(cell, _MET2,
          tap_x - half, rail_y,
          tap_x + half, bar_y + half)
    _via2_stack(cell, tap_x, rail_y)


def _src_tap_to_rail(
    cell: gdstk.Cell, tap_x: float, diff_y: float, rail_y: float,
) -> None:
    """From a shared-source diff contact at (tap_x, diff_y), drop a
    stack up to the met3 VDD rail at y=rail_y.

    Structure: licon+li1 at diff (drawn by caller via _diff_contact),
    mcon + 0.30 µm met1 stub from diff_y up to rail_y, via1 + via2 at
    (tap_x, rail_y).

    Stub width 0.30 µm matches via1/via2 met2 pads; Magic sees one met1
    polygon covering both the diff mcon and the via1 contact with
    consistent ≥0.075 µm enclosure everywhere.
    """
    _sq(cell, _MCON_L, tap_x, diff_y, _MCON)
    half = 0.15
    _rect(cell, _MET1,
          tap_x - half, diff_y - half,
          tap_x + half, rail_y + half)
    _via1_stack(cell, tap_x, rail_y)
    _via2_stack(cell, tap_x, rail_y)


def _mp3_equalizer(
    cell: gdstk.Cell, x_offset: float, y_center: float, rail_y: float,
    x_bl_rel: float = _BL_X, x_br_rel: float = _BR_X,
) -> None:
    """Per-pair MP3 equalizer: horizontal PMOS with source at BL, drain
    at BR, gate on precharge_en.

    Diff bracketed to leave clearance from BL/BR met1 stubs and from
    any pair-neighbour features at the cell boundary."""
    x_bl = x_offset + x_bl_rel
    x_br = x_offset + x_br_rel

    # MP3 diff needs width ≥ 2·SD_EXT + L = 0.85 to fit a transistor.
    # Leave 0.10 margin from BL / BR mcon pad edges.
    eq_left = x_bl + 0.10
    eq_right = x_br - 0.10
    diff_bot = y_center - _W / 2.0
    diff_top = y_center + _W / 2.0

    _rect(cell, _DIFF, eq_left, diff_bot, eq_right, diff_top)
    _rect(cell, _PSDM,
          eq_left - _PSDM_ENC, diff_bot - _PSDM_ENC,
          eq_right + _PSDM_ENC, diff_top + _PSDM_ENC)

    # Vertical poly gate, centred.
    poly_cx = (eq_left + eq_right) / 2.0
    poly_bot = diff_bot - _POLY_OVH
    poly_top = diff_top + _POLY_OVH
    _rect(cell, _POLY,
          poly_cx - _L / 2.0, poly_bot,
          poly_cx + _L / 2.0, poly_top)

    # Source contact (left, BL side) + jog to BL stub.
    src_x = eq_left + _SD_EXT / 2.0
    drn_x = eq_right - _SD_EXT / 2.0
    _diff_contact(cell, src_x, y_center)
    _mcon_pad(cell, src_x, y_center)
    _rect(cell, _MET1,
          x_bl - _MET1_WIDTH / 2, y_center - _MET1_WIDTH / 2,
          src_x + _MET1_WIDTH / 2, y_center + _MET1_WIDTH / 2)

    _diff_contact(cell, drn_x, y_center)
    _mcon_pad(cell, drn_x, y_center)
    _rect(cell, _MET1,
          drn_x - _MET1_WIDTH / 2, y_center - _MET1_WIDTH / 2,
          x_br + _MET1_WIDTH / 2, y_center + _MET1_WIDTH / 2)

    # MP3 gate tap: extend poly below the transistor to a poly head,
    # place licon + via stack dropping straight to precharge_en rail.
    stub_half = (_LICON + 2 * _POLY_LICON_ENC) / 2.0
    # Safely below MP3 poly bottom so the licon's poly enclosure is clean,
    # while staying above the precharge_en rail's top edge.
    mp3_tap_y = poly_bot - 0.10 - stub_half        # poly head y centre
    mp3_stub_bot = mp3_tap_y - stub_half - 0.01
    _rect(cell, _POLY,
          poly_cx - stub_half, mp3_stub_bot,
          poly_cx + stub_half, poly_bot)
    _tap_stack(cell, poly_cx, mp3_tap_y, poly=True)
    # Met2 riser from (poly_cx, mp3_tap_y) down to rail.
    half = 0.15
    _rect(cell, _MET2,
          poly_cx - half, rail_y,
          poly_cx + half, mp3_tap_y + half)
    _via2_stack(cell, poly_cx, rail_y)


def generate_precharge(
    num_pairs: int,
    pair_pitch: float = _MIN_PAIR_PITCH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Emit an extraction-clean precharge cell using N=2 shared-diff blocks.

    num_pairs must be even (≥ 2). Blocks are 2 pairs wide; the cell
    contains num_pairs / 2 blocks.
    """
    if pair_pitch < _MIN_PAIR_PITCH - 1e-9:
        raise ValueError(f"pair_pitch {pair_pitch} < min {_MIN_PAIR_PITCH}")
    if num_pairs < 2 or num_pairs % 2 != 0:
        raise ValueError(
            f"num_pairs must be even and >= 2 for N=2 block layout; "
            f"got {num_pairs}"
        )

    name = cell_name or (
        f"precharge_{num_pairs}pairs_p{int(pair_pitch * 1000)}nm_n2"
    )
    block_width = _snap(2 * pair_pitch)
    num_blocks = num_pairs // 2
    cell_w = _snap(num_blocks * block_width)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)
    _rect(cell, _BOUNDARY, 0, 0, cell_w, _CELL_H)

    # Nwell spans Rows A/B only (no PMOS in Row C — wait MP3 IS PMOS too).
    # Actually all three rows have PMOS in the precharge. Nwell spans all.
    nw_bot = _ROW_C_Y - _W / 2.0 - _NWELL_ENC
    nw_top = _ROW_A_Y + _W / 2.0 + _NWELL_ENC
    _rect(cell, _NWELL, -_NWELL_ENC, nw_bot,
          cell_w + _NWELL_ENC, nw_top)

    # Met3 rails.
    _rect(cell, _MET3, 0, _VDD_RAIL_Y - _RAIL_W / 2,
          cell_w, _VDD_RAIL_Y + _RAIL_W / 2)
    # Labels match the rekolektion macro-level convention:
    #   VPWR     — positive supply
    #   p_en_bar — precharge enable, active-low (PMOS gate = low
    #              enables pull-up). The control-logic DFF output that
    #              drives this is already 'p_en_bar' in control_logic.
    cell.add(gdstk.Label("VPWR",
                         (_snap(cell_w / 2), _snap(_VDD_RAIL_Y)),
                         layer=_MET3[0], texttype=_MET3[1]))
    _rect(cell, _MET3, 0, _EN_RAIL_Y - _RAIL_W / 2,
          cell_w, _EN_RAIL_Y + _RAIL_W / 2)
    cell.add(gdstk.Label("p_en_bar",
                         (_snap(min(0.5, cell_w / 2)), _snap(_EN_RAIL_Y)),
                         layer=_MET3[0], texttype=_MET3[1]))

    met1_half = _MET1_WIDTH / 2.0

    # --- BL / BR stubs (per pair) -----------------------------------------
    for i in range(num_pairs):
        x_bl = i * pair_pitch + _BL_X
        x_br = i * pair_pitch + _BR_X
        _rect(cell, _MET1, x_bl - met1_half, 0.0,
              x_bl + met1_half, _CELL_H)
        cell.add(gdstk.Label(
            f"bl_{i}", (_snap(x_bl), _snap(_CELL_H - 0.15)),
            layer=_MET1[0], texttype=_MET1[1]))
        _rect(cell, _MET1, x_br - met1_half, 0.0,
              x_br + met1_half, _CELL_H)
        cell.add(gdstk.Label(
            f"br_{i}", (_snap(x_br), _snap(_CELL_H - 0.15)),
            layer=_MET1[0], texttype=_MET1[1]))

    # --- Per-block: Rows A, B, C transistors ------------------------------
    for b in range(num_blocks):
        x_block = b * block_width

        # Row A: shared-source MP1 pair. Drain x positions (within block):
        # BL[0]=0.0425 and BL[1]=1.3525 (pair-local; pair[1] in block is
        # at pair_pitch + BL_X = 1.3525 within the block).
        drain_xs_A = (_BL_X, pair_pitch + _BL_X)
        _shared_source_pair(
            cell, _ROW_A_Y, x_block, _MP1_GATE_REL, drain_xs_A,
            _MP1_SRC_TAP_X,
        )
        # Drain met1 jogs to the BL stubs (trivial — drain is at BL x).
        for di in range(2):
            x_drain = x_block + drain_xs_A[di]
            _mcon_pad(cell, x_drain, _ROW_A_Y)
            # drain mcon's met1 pad already overlaps the BL stub directly
            # because the drain is centered at x_bl and the BL stub
            # passes through (x_bl ± 0.07) at every y.
        # Shared source to VDD rail.
        _src_tap_to_rail(
            cell, x_block + _MP1_SRC_TAP_X, _ROW_A_Y, _VDD_RAIL_Y,
        )

        # Row B: shared-source MP2 pair (drains at BR[0]=1.1575 and
        # BR[1]=2.4675 within block).
        drain_xs_B = (_BR_X, pair_pitch + _BR_X)
        _shared_source_pair(
            cell, _ROW_B_Y, x_block, _MP2_GATE_REL, drain_xs_B,
            _MP2_SRC_TAP_X,
        )
        for di in range(2):
            x_drain = x_block + drain_xs_B[di]
            _mcon_pad(cell, x_drain, _ROW_B_Y)
        _src_tap_to_rail(
            cell, x_block + _MP2_SRC_TAP_X, _ROW_B_Y, _VDD_RAIL_Y,
        )

        # Row C: per-pair MP3 equalizers (2 per block).
        _mp3_equalizer(cell, x_block, _ROW_C_Y, _EN_RAIL_Y)
        _mp3_equalizer(cell, x_block + pair_pitch, _ROW_C_Y, _EN_RAIL_Y)

    # --- Row A / Row B back-bars (continuous 0.33 µm-tall poly) ------------
    # Back-bars span the full cell and merge with each vertical gate stem
    # (each stem's top edge touches the bar's bottom edge).
    _rect(cell, _POLY, 0.0, _ROW_A_BAR_Y - _BAR_H / 2.0,
          cell_w, _ROW_A_BAR_Y + _BAR_H / 2.0)
    _rect(cell, _POLY, 0.0, _ROW_B_BAR_Y - _BAR_H / 2.0,
          cell_w, _ROW_B_BAR_Y + _BAR_H / 2.0)

    # Single gate tap for all gate lines — placed on Row B's back-bar.
    #
    # Why one tap (not two): Row A gate stems extend from y=ROW_A_Y-0.34
    # down to y=ROW_B_BAR_Y+0.055 (overlap with Row B back-bar), so all
    # Row A + Row B gates merge into one poly polygon. A single tap on
    # Row B's bar drives every gate.
    #
    # Why Row B's bar, not Row A's: Row A's bar sits at y ≈ 3.18,
    # INSIDE the MP1 source met1 stub y-range ([2.67, 3.84]); a gate tap
    # there is guaranteed to overlap the stub and short precharge_en to
    # VDD. Row B's bar at y ≈ 2.22 is BELOW that y-range (gap 0.32 µm),
    # so the gate tap pad clears all source stubs in 2D.
    #
    # x window: bitline-clear gap at x=0.6 (between BL[0]=0.0425 and
    # BR[0]=1.1575, ~0.43 µm-wide safe window). Happens to align with
    # MP3 pair-0 poly_cx so the gate-tap met2 riser merges with the
    # MP3 tap riser (same net, no conflict).
    tap_x = _snap(0.6)
    _gate_tap_to_rail(cell, tap_x, _ROW_B_BAR_Y, _EN_RAIL_Y)

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))
    return cell, lib
