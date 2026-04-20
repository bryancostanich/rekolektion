"""Per-BL/BR-pair SRAM column mux cell generator.

Each column (BL/BR pair) contains `mux_ratio` pairs of NMOS pass
transistors (one for BL, one for BR) stacked vertically.  Each level
has its own sel line; the `mux_ratio` BL drains tie together into a
single muxed output node BL_out[i], similarly BR_out[i].

At pair_pitch = 1.31 µm (bitcell pitch), per-column-pair precharge (D2
Option 3) is feasible.

Layout per BL/BR pair column (bottom → top):
    GND rail (met1, horizontal)
    muxed BL_out / BR_out band (met1 tap)
    Stack of mux_ratio rows, each row = 2 NMOS (one for BL, one for BR):
        sel[k] horizontal poly row
        NMOS_BL at x_BL_mp, NMOS_BR at x_BR_mp
        (both share the sel[k] gate poly)
    BL[i] full-height met1 stub at cell-local x = 0.0425
    BR[i] full-height met1 stub at cell-local x = 1.1575
"""
from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


# --- device + rule constants ------------------------------------------------
_W: float = 0.65           # NMOS pass-gate width (lower on-R than min)
_L: float = 0.15
_SD_EXT: float = 0.35      # satisfies licon.11 and li.3 on same diff
_POLY_OVH: float = 0.13
_LICON: float = 0.17
_LI_ENC: float = 0.08
_NSDM_ENC: float = 0.125
_MCON: float = 0.17
_MCON_MET1_ENC: float = 0.085
_MET1_WIDTH: float = 0.14

_BL_X: float = 0.0425
_BR_X: float = 1.1575
_MP1_X: float = 0.345
_MP2_X: float = 0.965

_MIN_PAIR_PITCH: float = 1.31

_RAIL_W: float = 0.28
_INTER_ROW_GAP: float = 0.40

_DIFF_Y_HALF: float = _L / 2.0 + _SD_EXT   # 0.425

# Layer tuples
_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MCON_L = LAYERS.MCON.as_tuple
_MET1 = LAYERS.MET1.as_tuple
_NSDM = LAYERS.NSDM.as_tuple
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
    h = size / 2.0
    _rect(cell, layer, cx - h, cy - h, cx + h, cy + h)


def _contact_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    _sq(cell, _LICON1, cx, cy, _LICON)
    pad = _LICON + 2 * _LI_ENC
    _rect(cell, _LI1, cx - pad / 2, cy - pad / 2,
          cx + pad / 2, cy + pad / 2)


def _mcon_stack(cell: gdstk.Cell, cx: float, cy: float) -> None:
    _sq(cell, _MCON_L, cx, cy, _MCON)
    met1_pad = _MCON + 2 * _MCON_MET1_ENC
    _rect(cell, _MET1,
          cx - met1_pad / 2, cy - met1_pad / 2,
          cx + met1_pad / 2, cy + met1_pad / 2)


def _vertical_nmos(cell: gdstk.Cell, x_center: float, y_center: float):
    """Returns (source_y, drain_y) — source at bottom, drain at top."""
    hw = _W / 2.0
    hl = _L / 2.0
    diff_left = x_center - hw
    diff_right = x_center + hw
    diff_bot = y_center - hl - _SD_EXT
    diff_top = y_center + hl + _SD_EXT
    _rect(cell, _DIFF, diff_left, diff_bot, diff_right, diff_top)
    _rect(cell, _NSDM,
          diff_left - _NSDM_ENC, diff_bot - _NSDM_ENC,
          diff_right + _NSDM_ENC, diff_top + _NSDM_ENC)
    _rect(cell, _POLY,
          diff_left - _POLY_OVH, y_center - hl,
          diff_right + _POLY_OVH, y_center + hl)
    top_y = diff_top - _SD_EXT / 2.0
    bot_y = diff_bot + _SD_EXT / 2.0
    _contact_stack(cell, x_center, top_y)
    _contact_stack(cell, x_center, bot_y)
    return bot_y, top_y


def generate_column_mux(
    num_pairs: int,
    mux_ratio: int = 2,
    pair_pitch: float = _MIN_PAIR_PITCH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate column mux cell with `mux_ratio` NMOS pass gates per
    pair.  BL_out[i] / BR_out[i] are the muxed outputs (at the bottom
    of each column), tied across all mux levels.
    """
    if mux_ratio not in (2, 4, 8):
        raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
    if pair_pitch < _MIN_PAIR_PITCH - 1e-9:
        raise ValueError(
            f"pair_pitch {pair_pitch} < min {_MIN_PAIR_PITCH}"
        )
    if num_pairs < 1:
        raise ValueError(f"num_pairs must be >= 1; got {num_pairs}")

    name = cell_name or (
        f"column_mux_{num_pairs}pairs_mux{mux_ratio}_p{int(pair_pitch*1000)}nm"
    )
    cell_w = _snap(num_pairs * pair_pitch)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    gnd_rail_y = 0.14 + _RAIL_W / 2
    out_band_y = gnd_rail_y + _RAIL_W / 2 + _INTER_ROW_GAP + 0.14
    row_pitch = 2 * _DIFF_Y_HALF + _INTER_ROW_GAP
    first_row_y = out_band_y + 0.14 + _INTER_ROW_GAP + _DIFF_Y_HALF
    top_row_y = first_row_y + (mux_ratio - 1) * row_pitch
    cell_h = top_row_y + _DIFF_Y_HALF + _INTER_ROW_GAP + 0.14

    _rect(cell, _BOUNDARY, 0, 0, cell_w, cell_h)

    # GND rail (met1 horizontal)
    _rect(cell, _MET1, 0, gnd_rail_y - _RAIL_W / 2,
          cell_w, gnd_rail_y + _RAIL_W / 2)
    cell.add(gdstk.Label("GND", (_snap(cell_w / 2), _snap(gnd_rail_y)),
                         layer=_MET1[0], texttype=_MET1[1]))

    met1_stub_half = _MET1_WIDTH / 2.0

    for i in range(num_pairs):
        x_offset = i * pair_pitch
        x_bl = x_offset + _BL_X
        x_br = x_offset + _BR_X
        x_mp1 = x_offset + _MP1_X
        x_mp2 = x_offset + _MP2_X

        # Full-height BL[i] / BR[i] met1 stubs
        _rect(cell, _MET1, x_bl - met1_stub_half, 0,
              x_bl + met1_stub_half, cell_h)
        cell.add(gdstk.Label(
            f"BL[{i}]", (_snap(x_bl), _snap(cell_h - 0.15)),
            layer=_MET1[0], texttype=_MET1[1]))
        _rect(cell, _MET1, x_br - met1_stub_half, 0,
              x_br + met1_stub_half, cell_h)
        cell.add(gdstk.Label(
            f"BR[{i}]", (_snap(x_br), _snap(cell_h - 0.15)),
            layer=_MET1[0], texttype=_MET1[1]))

        # BL_out[i] / BR_out[i] met1 taps in the output band
        _mcon_stack(cell, x_mp1, out_band_y)
        cell.add(gdstk.Label(
            f"BL_out[{i}]", (_snap(x_mp1), _snap(out_band_y)),
            layer=_MET1[0], texttype=_MET1[1]))
        _mcon_stack(cell, x_mp2, out_band_y)
        cell.add(gdstk.Label(
            f"BR_out[{i}]", (_snap(x_mp2), _snap(out_band_y)),
            layer=_MET1[0], texttype=_MET1[1]))

        # Vertical met1 trunks from BL_out tap upward at x_mp1 and
        # x_mp2 — all NMOS sources at each mux level connect here.
        # Trunk extends from out_band_y up through the topmost row.
        _rect(cell, _MET1, x_mp1 - met1_stub_half, out_band_y,
              x_mp1 + met1_stub_half, top_row_y + _DIFF_Y_HALF)
        _rect(cell, _MET1, x_mp2 - met1_stub_half, out_band_y,
              x_mp2 + met1_stub_half, top_row_y + _DIFF_Y_HALF)

        for k in range(mux_ratio):
            row_y = first_row_y + k * row_pitch
            src_y1, drn_y1 = _vertical_nmos(cell, x_mp1, row_y)
            src_y2, drn_y2 = _vertical_nmos(cell, x_mp2, row_y)

            # Source mcon ties to the muxed trunk (already drawn).
            _mcon_stack(cell, x_mp1, src_y1)
            _mcon_stack(cell, x_mp2, src_y2)

            # Drain mcon + horizontal met1 jog to BL[i] / BR[i] stub.
            _mcon_stack(cell, x_mp1, drn_y1)
            _rect(cell, _MET1,
                  x_bl - met1_stub_half, drn_y1 - met1_stub_half,
                  x_mp1 + met1_stub_half, drn_y1 + met1_stub_half)
            _mcon_stack(cell, x_mp2, drn_y2)
            _rect(cell, _MET1,
                  x_mp2 - met1_stub_half, drn_y2 - met1_stub_half,
                  x_br + met1_stub_half, drn_y2 + met1_stub_half)

            # sel[k] poly-layer label — one label per mux level,
            # placed at x_mp1 (poly runs horizontally across the row).
            if i == 0:
                cell.add(gdstk.Label(
                    f"sel[{k}]", (_snap(x_mp1), _snap(row_y)),
                    layer=_POLY[0], texttype=_POLY[1]))

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))
    return cell, lib
