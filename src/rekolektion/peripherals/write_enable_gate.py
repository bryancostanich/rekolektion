"""Byte-enable AND gate generator for write driver gating.

Generates a row of 2-input AND gates (implemented as NAND + inverter)
that mask the global write-enable (WE) with per-byte enable signals
(BEN[N-1:0]).  One AND gate per output bit — all bits in the same byte
share the same BEN input.

Output: WE_byte[i] = WE & BEN[i // 8]

Layout: NAND2 (2 series NMOS + 2 parallel PMOS) followed by inverter
(1 NMOS + 1 PMOS), tiled once per output bit, with shared BEN bus on
met1 horizontal and per-gate WE_byte output on met2 vertical stubs.

Usage::

    from rekolektion.peripherals.write_enable_gate import generate_write_enable_gates
    cell, lib = generate_write_enable_gates(num_bits=32, ben_bits=4)
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES


# ---------------------------------------------------------------------------
# Design-rule-derived constants (matching column_mux.py conventions)
# ---------------------------------------------------------------------------

_W_N = 0.42      # NMOS channel width
_W_P = 0.55      # PMOS channel width (wider for balanced drive)
_L = 0.15        # gate length
_SD_EXT = 0.36   # source/drain diff past gate
_POLY_OVH = 0.14
_GATE_EXT = 0.52
_LICON = 0.17
_LI_ENC = 0.08
_NSDM_ENC = 0.125
_PSDM_ENC = 0.125
_MCON = 0.17

_LI_PAD = _LICON + 2 * _LI_ENC       # 0.33
_POLY_PAD_W = _LICON + 2 * 0.06      # 0.29
_POLY_PAD_H = _LICON + 2 * 0.09      # 0.35

# Gate cell dimensions
_GATE_WIDTH = 3.0    # X pitch per AND gate (one per output bit)
_GATE_HEIGHT = 6.0   # Y height for NAND + INV stack

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
_NSDM = LAYERS.NSDM.as_tuple
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
    x0 = _snap(cx - size / 2)
    y0 = _snap(cy - size / 2)
    cell.add(gdstk.rectangle(
        (x0, y0), (x0 + size, y0 + size),
        layer=layer[0], datatype=layer[1],
    ))


def _draw_nmos(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Draw a minimal NMOS transistor at (cx, cy)."""
    hw = _W_N / 2.0
    hl = _L / 2.0

    # Diffusion
    _rect(cell, _DIFF,
          cx - hw, cy - hl - _SD_EXT,
          cx + hw, cy + hl + _SD_EXT)
    # NSDM
    _rect(cell, _NSDM,
          cx - hw - _NSDM_ENC, cy - hl - _SD_EXT - _NSDM_ENC,
          cx + hw + _NSDM_ENC, cy + hl + _SD_EXT + _NSDM_ENC)
    # Poly gate
    _rect(cell, _POLY,
          cx - hw - _POLY_OVH, cy - hl,
          cx + hw + _POLY_OVH, cy + hl)
    # Source contact (bottom)
    src_y = cy - hl - _SD_EXT / 2.0
    _sq_contact(cell, _LICON1, cx, src_y, _LICON)
    _rect(cell, _LI1,
          cx - _LI_PAD / 2, src_y - _LI_PAD / 2,
          cx + _LI_PAD / 2, src_y + _LI_PAD / 2)
    # Drain contact (top)
    drn_y = cy + hl + _SD_EXT / 2.0
    _sq_contact(cell, _LICON1, cx, drn_y, _LICON)
    _rect(cell, _LI1,
          cx - _LI_PAD / 2, drn_y - _LI_PAD / 2,
          cx + _LI_PAD / 2, drn_y + _LI_PAD / 2)


def _draw_pmos(cell: gdstk.Cell, cx: float, cy: float) -> None:
    """Draw a minimal PMOS transistor at (cx, cy) (inside n-well)."""
    hw = _W_P / 2.0
    hl = _L / 2.0

    # Diffusion
    _rect(cell, _DIFF,
          cx - hw, cy - hl - _SD_EXT,
          cx + hw, cy + hl + _SD_EXT)
    # PSDM
    _rect(cell, _PSDM,
          cx - hw - _PSDM_ENC, cy - hl - _SD_EXT - _PSDM_ENC,
          cx + hw + _PSDM_ENC, cy + hl + _SD_EXT + _PSDM_ENC)
    # Poly gate
    _rect(cell, _POLY,
          cx - hw - _POLY_OVH, cy - hl,
          cx + hw + _POLY_OVH, cy + hl)
    # Source contact (top — closer to VDD)
    src_y = cy + hl + _SD_EXT / 2.0
    _sq_contact(cell, _LICON1, cx, src_y, _LICON)
    _rect(cell, _LI1,
          cx - _LI_PAD / 2, src_y - _LI_PAD / 2,
          cx + _LI_PAD / 2, src_y + _LI_PAD / 2)
    # Drain contact (bottom)
    drn_y = cy - hl - _SD_EXT / 2.0
    _sq_contact(cell, _LICON1, cx, drn_y, _LICON)
    _rect(cell, _LI1,
          cx - _LI_PAD / 2, drn_y - _LI_PAD / 2,
          cx + _LI_PAD / 2, drn_y + _LI_PAD / 2)


def generate_write_enable_gates(
    num_bits: int,
    ben_bits: int,
    gate_pitch: float = _GATE_WIDTH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate byte-enable AND gates for write driver masking.

    Creates one AND gate per output bit.  Each AND gate computes:
        WE_byte[i] = WE & BEN[i * ben_bits / num_bits]

    Parameters
    ----------
    num_bits : int
        Number of data bits (= number of AND gates).
    ben_bits : int
        Number of byte-enable signals.
    gate_pitch : float
        X pitch per gate (default 3.0 um).
    cell_name : str, optional
        Name for the cell.
    output_path : path, optional
        Write GDS to this file.
    """
    if num_bits < 1 or ben_bits < 1:
        raise ValueError("num_bits and ben_bits must be >= 1")

    name = cell_name or f"write_enable_gates_{num_bits}b_{ben_bits}ben"
    width = _snap(num_bits * gate_pitch)
    height = _snap(_GATE_HEIGHT)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    # Boundary
    _rect(cell, _BOUNDARY, 0, 0, width, height)

    # N-well for PMOS region (upper half)
    nwell_bot = height * 0.45
    _rect(cell, _NWELL, 0, nwell_bot, width, height)

    # Horizontal buses
    # WE input bus (met1, near bottom)
    we_bus_y = 0.5
    _rect(cell, _MET1, 0, we_bus_y - 0.07, width, we_bus_y + 0.07)
    cell.add(gdstk.Label(
        "WE", (_snap(0.2), _snap(we_bus_y)),
        layer=_MET1[0], texttype=_MET1[1],
    ))

    # BEN buses (met1, horizontal, one per ben_bits near bottom)
    ben_bus_base_y = 1.1
    ben_bus_spacing = 0.40
    ben_bus_ys = []
    for b in range(ben_bits):
        y = ben_bus_base_y + b * ben_bus_spacing
        ben_bus_ys.append(y)
        _rect(cell, _MET1, 0, y - 0.07, width, y + 0.07)
        cell.add(gdstk.Label(
            f"BEN[{b}]", (_snap(0.2), _snap(y)),
            layer=_MET1[0], texttype=_MET1[1],
        ))

    # NMOS Y center (lower region)
    nmos_y = 2.8
    # PMOS Y center (upper region)
    pmos_y = 4.8

    # VDD bus (met1, top)
    vdd_y = height - 0.3
    _rect(cell, _MET1, 0, vdd_y - 0.07, width, vdd_y + 0.07)

    # VSS bus (met1, bottom)
    vss_y = 0.15
    _rect(cell, _MET1, 0, vss_y - 0.07, width, vss_y + 0.07)

    bits_per_ben = max(1, num_bits // ben_bits)

    for i in range(num_bits):
        x_base = i * gate_pitch + gate_pitch / 2
        ben_idx = min(i // bits_per_ben, ben_bits - 1)

        # Draw NMOS pair (series — for NAND2)
        _draw_nmos(cell, x_base - 0.5, nmos_y)
        _draw_nmos(cell, x_base + 0.5, nmos_y)

        # Draw PMOS pair (parallel — for NAND2)
        _draw_pmos(cell, x_base - 0.5, pmos_y)
        _draw_pmos(cell, x_base + 0.5, pmos_y)

        # Connect to BEN bus via met2 vertical drop
        ben_y = ben_bus_ys[ben_idx]
        via_sz = 0.15
        pad_sz = 0.34

        # Via from BEN bus met1 → met2 at gate X position
        gate_x = x_base - 0.5
        _sq_contact(cell, _VIA, gate_x, ben_y, via_sz)
        _rect(cell, _MET2,
              gate_x - pad_sz / 2, ben_y - pad_sz / 2,
              gate_x + pad_sz / 2, ben_y + pad_sz / 2)

        # Met2 stub up to NMOS gate region
        _rect(cell, _MET2,
              gate_x - 0.07, ben_y,
              gate_x + 0.07, nmos_y)

        # Output stub (met2, from PMOS drain area up to top)
        out_x = x_base
        _rect(cell, _MET2, out_x - 0.07, pmos_y - 0.5, out_x + 0.07, 0)

        # Output label
        cell.add(gdstk.Label(
            f"WE_byte[{i}]", (_snap(out_x), _snap(0.15)),
            layer=_MET2[0], texttype=_MET2[1],
        ))

    if output_path:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
