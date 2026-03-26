"""Column mux generator with pass-transistor topology.

Generates a column mux cell using NMOS pass-transistor topology.
Each mux group of ``mux_ratio`` input bit-line pairs is switched to a
single output pair via NMOS pass gates controlled by select lines.

Supported mux ratios: 1:1 (no mux), 2:1, 4:1, 8:1.

Layout: one NMOS pass transistor per column per mux input, stacked
vertically.  Gate contacts on horizontal poly extension to the LEFT
of the diffusion (outside active area, avoids poly.11 bends).
Minimum bl_pitch = 1.9 um.

Usage::

    from rekolektion.peripherals.column_mux import generate_column_mux
    cell, lib = generate_column_mux(num_cols=64, mux_ratio=4)
"""

from __future__ import annotations

import math
from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


# ---------------------------------------------------------------------------
# Design-rule-derived constants
# ---------------------------------------------------------------------------

_W = 0.42       # transistor channel width (diff extent in X)
_L = 0.15       # gate length (poly extent in Y)
_SD_EXT = 0.36  # source/drain diff past gate (need S/D li1 pads 0.17 apart)
_POLY_OVH = 0.14          # poly extension past diff edge (poly.8 = 0.13 + margin)
_GATE_EXT = 0.52           # poly extension past diff for gate contact pad
_LICON = 0.17              # contact size
_LI_ENC = 0.08             # li1 enclosure of licon
_NSDM_ENC = 0.125          # nsdm enclosure of diff
_MCON = 0.17               # mcon size

_LI_PAD = _LICON + 2 * _LI_ENC       # 0.33 — li1 pad side
_POLY_PAD_W = _LICON + 2 * 0.06      # 0.29 — poly pad width (licon enc 0.05 + margin)
_POLY_PAD_H = _LICON + 2 * 0.09      # 0.35 — poly pad height (licon enc 0.08 + margin)

# Transistor total heights
_DIFF_H = _L + 2 * _SD_EXT           # 0.75
_TRANS_PITCH = 1.60                   # Y pitch between stacked transistors

# X extent per transistor: gate_ext + diff_half + poly_ovh
_TRANS_X = _GATE_EXT + _W + _POLY_OVH  # 1.05

# Minimum column pitch: transistor X + poly spacing
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


def _sq_contact(cell: gdstk.Cell, layer: tuple[int, int],
                cx: float, cy: float, size: float) -> None:
    """Draw a square contact guaranteed to be exactly `size` after snapping."""
    x0 = _snap(cx - size / 2)
    y0 = _snap(cy - size / 2)
    cell.add(gdstk.rectangle(
        (x0, y0), (x0 + size, y0 + size),
        layer=layer[0], datatype=layer[1],
    ))


def _draw_nmos_pass_transistor(
    cell: gdstk.Cell,
    x_center: float,
    y_center: float,
) -> tuple[float, float, float]:
    """Draw one NMOS pass transistor with gate contact on the left.

    Diffusion runs vertically (source at bottom, drain at top).
    Poly gate is horizontal (Y width = L = 0.15).
    Gate contact pad on extended poly to the LEFT of diff.

    Returns (gate_pad_cx, gate_pad_cy, gate_pad_right_x) for routing.
    """
    hw = _W / 2.0       # 0.21
    hl = _L / 2.0       # 0.075

    diff_left = x_center - hw
    diff_right = x_center + hw
    diff_bot = y_center - hl - _SD_EXT
    diff_top = y_center + hl + _SD_EXT

    # 1. Diffusion
    _rect(cell, _DIFF, diff_left, diff_bot, diff_right, diff_top)

    # 2. NSDM implant
    _rect(cell, _NSDM,
          diff_left - _NSDM_ENC, diff_bot - _NSDM_ENC,
          diff_right + _NSDM_ENC, diff_top + _NSDM_ENC)

    # 3. Poly gate — extends far left for gate contact, normal right overhang
    poly_left = diff_left - _GATE_EXT  # 0.50 past diff for contact pad
    poly_right = diff_right + _POLY_OVH  # 0.13 past diff (minimum)
    _rect(cell, _POLY, poly_left, y_center - hl, poly_right, y_center + hl)

    # 4. Poly contact pad — widen poly at far left (outside diff, no poly.11)
    pad_left = poly_left
    pad_right = poly_left + _POLY_PAD_W  # 0.27
    _rect(cell, _POLY,
          pad_left, y_center - _POLY_PAD_H / 2,
          pad_right, y_center + _POLY_PAD_H / 2)

    # 5. Gate licon + li1 on poly pad
    gate_cx = pad_left + _POLY_PAD_W / 2  # pad center X
    gate_cy = y_center
    _sq_contact(cell, _LICON1, gate_cx, gate_cy, _LICON)
    _rect(cell, _LI1,
          gate_cx - _LI_PAD / 2, gate_cy - _LI_PAD / 2,
          gate_cx + _LI_PAD / 2, gate_cy + _LI_PAD / 2)

    # 6. Source contact (bottom of diff)
    src_y = y_center - hl - _SD_EXT / 2.0
    _sq_contact(cell, _LICON1, x_center, src_y, _LICON)
    _rect(cell, _LI1,
          x_center - _LI_PAD / 2, src_y - _LI_PAD / 2,
          x_center + _LI_PAD / 2, src_y + _LI_PAD / 2)

    # 7. Drain contact (top of diff)
    drn_y = y_center + hl + _SD_EXT / 2.0
    _sq_contact(cell, _LICON1, x_center, drn_y, _LICON)
    _rect(cell, _LI1,
          x_center - _LI_PAD / 2, drn_y - _LI_PAD / 2,
          x_center + _LI_PAD / 2, drn_y + _LI_PAD / 2)

    return gate_cx, gate_cy, pad_right


def generate_column_mux(
    num_cols: int,
    mux_ratio: int = 1,
    bl_pitch: float = 1.925,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a column mux cell with pass-transistor topology.

    Each column has ``mux_ratio`` pass transistors stacked vertically
    (one per mux input).  Each transistor is a standalone NMOS with
    gate contact on the left.  Separate BL and BR transistors per column
    would double the transistor count; for simplicity this generator
    creates one transistor per column that switches the BL line.  A
    separate instance can be generated for BR if needed.

    Parameters
    ----------
    num_cols : int
        Number of input bit-line pairs (from the array).
    mux_ratio : int
        Mux ratio -- must be 1, 2, 4, or 8.
    bl_pitch : float
        Bit-line pair pitch (clamped to minimum 1.9 um).
    cell_name : str, optional
        Name for the cell.
    output_path : path, optional
        Write GDS to this file.
    """
    if mux_ratio not in (1, 2, 4, 8):
        raise ValueError(f"mux_ratio must be 1, 2, 4, or 8; got {mux_ratio}")
    if num_cols % mux_ratio != 0:
        raise ValueError(
            f"num_cols ({num_cols}) must be divisible by mux_ratio ({mux_ratio})"
        )

    eff_pitch = max(bl_pitch, _MIN_BL_PITCH)
    num_outputs = num_cols // mux_ratio
    # One-hot select: each mux input gets its own select line
    num_sel = mux_ratio if mux_ratio > 1 else 0

    name = cell_name or f"column_mux_{num_cols}x{mux_ratio}"
    width = _snap(num_cols * eff_pitch)

    # Height: bottom margin + select buses + transistor slots + top margin
    bot_margin = 0.5
    sel_spacing = 0.50
    sel_region = max(num_sel * sel_spacing + 0.20, 0.3) if mux_ratio > 1 else 0.3
    trans_region = mux_ratio * _TRANS_PITCH
    top_margin = 0.5
    height = _snap(bot_margin + sel_region + trans_region + top_margin)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    _rect(cell, _BOUNDARY, 0, 0, width, height)

    if mux_ratio <= 1:
        # Pass-through: just met2 stubs
        for i in range(num_cols):
            xc = i * eff_pitch + eff_pitch / 2
            _rect(cell, _MET2, xc - 0.07, 0, xc + 0.07, height)
        if output_path:
            Path(output_path).parent.mkdir(parents=True, exist_ok=True)
            lib.write_gds(str(output_path))
        return cell, lib

    # --- Select buses (met1, horizontal) ------------------------------------
    sel_y_positions = []
    for s in range(num_sel):
        y = bot_margin + 0.20 + s * sel_spacing
        sel_y_positions.append(y)
        _rect(cell, _MET1, 0, y - 0.07, width, y + 0.07)  # 0.14 wide met1

    trans_base_y = bot_margin + sel_region + 0.3  # first transistor Y center

    # --- Per-column pass transistors ----------------------------------------
    for col in range(num_cols):
        x_center = col * eff_pitch + eff_pitch / 2
        inp = col % mux_ratio

        y_center = trans_base_y + inp * _TRANS_PITCH

        # Draw transistor
        gate_cx, gate_cy, _ = _draw_nmos_pass_transistor(cell, x_center, y_center)

        # Connect gate to select bus via mcon -> met1 -> via -> met2 -> via -> met1
        # (met2 vertical jump avoids shorting to other select buses)
        sel_idx = inp  # one-hot: each input has its own select line
        sel_y = sel_y_positions[sel_idx]

        # mcon on gate li1
        _sq_contact(cell, _MCON_L, gate_cx, gate_cy, _MCON)

        # met1 pad at gate mcon (large enough for mcon + via enclosure via.5a)
        pad_sz = 0.34  # via(0.15) + 2*0.095 enclosure (via.5a needs 0.085)
        _rect(cell, _MET1,
              gate_cx - pad_sz / 2, gate_cy - pad_sz / 2,
              gate_cx + pad_sz / 2, gate_cy + pad_sz / 2)

        # Via up to met2 at gate position
        via_sz = 0.15
        _sq_contact(cell, _VIA, gate_cx, gate_cy, via_sz)
        _rect(cell, _MET2,
              gate_cx - pad_sz / 2, gate_cy - pad_sz / 2,
              gate_cx + pad_sz / 2, gate_cy + pad_sz / 2)

        # Met2 vertical strip from gate down to select bus Y
        _rect(cell, _MET2, gate_cx - pad_sz / 2, sel_y - pad_sz / 2,
              gate_cx + pad_sz / 2, gate_cy + pad_sz / 2)

        # Via back to met1 at select bus Y
        _sq_contact(cell, _VIA, gate_cx, sel_y, via_sz)
        _rect(cell, _MET1,
              gate_cx - pad_sz / 2, sel_y - pad_sz / 2,
              gate_cx + pad_sz / 2, sel_y + pad_sz / 2)

        # --- Met2 stubs for bit-line connectivity ---------------------------
        # Input stub (bottom)
        _rect(cell, _MET2, x_center - 0.07, 0, x_center + 0.07, y_center - 0.3)
        # Output stub (top, only for first column of each mux group)
        if inp == 0:
            _rect(cell, _MET2, x_center - 0.07, y_center + 0.3, x_center + 0.07, height)

        # Labels
        cell.add(gdstk.Label(
            f"BL_in[{col}]", (_snap(x_center), 0.15),
            layer=_MET2[0], texttype=_MET2[1],
        ))

    if output_path:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
