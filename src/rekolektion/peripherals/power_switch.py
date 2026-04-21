"""Power gating switch generator for SRAM macros.

Generates a header switch array using PMOS transistors that gate the
macro's VDD supply.  When SLEEP is asserted, the header switches turn
off, isolating the macro from VDD and reducing leakage to sub-threshold
levels.

The generator produces:
  - A row of parallel PMOS header switches (sized for low Rdson)
  - SLEEP input on met1 horizontal bus
  - VDD_REAL (always-on supply) and VDD (virtual, gated supply) rails

SKY130 power gating approach:
  - Header switches: sky130_fd_pr__pfet_01v8 (W=5µm for low Rdson)
  - Output isolation: sky130_fd_sc_hd__lpflow_isobufsrc (placed by assembler)
  - Bleeder: sky130_fd_sc_hd__lpflow_bleeder (maintains virtual VDD)
  - Always-on clock: sky130_fd_sc_hd__lpflow_clkbufkapwr (if needed)

Usage::

    from rekolektion.peripherals.power_switch import generate_power_switches
    cell, lib = generate_power_switches(num_switches=8, macro_width=30.0)
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.tech.sky130 import LAYERS


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_W_P = 5.0        # PMOS header switch width (large for low Rdson)
_L = 0.15         # gate length
_SD_EXT = 0.50    # source/drain extension (larger for high-current device)
_POLY_OVH = 0.14
_LICON = 0.17
_LI_ENC = 0.08
_NSDM_ENC = 0.125
_PSDM_ENC = 0.125

_LI_PAD = _LICON + 2 * _LI_ENC
_SWITCH_PITCH = 6.0   # X pitch per switch (needs room for wide PMOS)
_SWITCH_HEIGHT = 4.0   # Y height of switch row

# Layer shortcuts
_DIFF = LAYERS.DIFF.as_tuple
_POLY = LAYERS.POLY.as_tuple
_LICON1 = LAYERS.LICON1.as_tuple
_LI1 = LAYERS.LI1.as_tuple
_MCON = LAYERS.MCON.as_tuple
_MET1 = LAYERS.MET1.as_tuple
_VIA = LAYERS.VIA.as_tuple
_MET2 = LAYERS.MET2.as_tuple
_NWELL = LAYERS.NWELL.as_tuple
_PSDM = LAYERS.PSDM.as_tuple
_BOUNDARY = LAYERS.BOUNDARY.as_tuple  # (235, 4) — sky130 prBoundary


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


def generate_power_switches(
    num_switches: int = 4,
    macro_width: float = 30.0,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a header switch array for power gating.

    Creates a row of PMOS header switches between VDD_REAL (always-on)
    and VDD (virtual, gated).  SLEEP pin controls the gates.

    Parameters
    ----------
    num_switches : int
        Number of parallel PMOS switches (more = lower Rdson).
    macro_width : float
        Target width to match macro dimensions (switches are spread).
    cell_name : str, optional
        Name for the cell.
    output_path : path, optional
        Write GDS to this file.
    """
    if num_switches < 1:
        raise ValueError("num_switches must be >= 1")

    name = cell_name or f"power_switch_{num_switches}x"
    width = _snap(max(macro_width, num_switches * _SWITCH_PITCH))
    height = _snap(_SWITCH_HEIGHT)

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    # Boundary
    _rect(cell, _BOUNDARY, 0, 0, width, height)

    # Full-width N-well (PMOS devices)
    _rect(cell, _NWELL, 0, 0, width, height)

    # VDD_REAL rail (met1, top) — always-on supply
    vdd_real_y = height - 0.3
    _rect(cell, _MET1, 0, vdd_real_y - 0.14, width, vdd_real_y + 0.14)
    cell.add(gdstk.Label(
        "VDD_REAL", (_snap(0.5), _snap(vdd_real_y)),
        layer=_MET1[0], texttype=_MET1[1],
    ))

    # VDD (virtual) rail (met1, bottom) — gated supply to macro
    vdd_virt_y = 0.3
    _rect(cell, _MET1, 0, vdd_virt_y - 0.14, width, vdd_virt_y + 0.14)
    cell.add(gdstk.Label(
        "VDD", (_snap(0.5), _snap(vdd_virt_y)),
        layer=_MET1[0], texttype=_MET1[1],
    ))

    # SLEEP bus (met2, horizontal, middle)
    sleep_y = height / 2
    _rect(cell, _MET2, 0, sleep_y - 0.07, width, sleep_y + 0.07)
    cell.add(gdstk.Label(
        "SLEEP", (_snap(0.2), _snap(sleep_y)),
        layer=_MET2[0], texttype=_MET2[1],
    ))

    # Place PMOS header switches evenly across width
    switch_spacing = width / (num_switches + 1)

    for i in range(num_switches):
        cx = switch_spacing * (i + 1)
        cy = height / 2

        hw = _W_P / 2.0
        hl = _L / 2.0

        # PMOS diffusion (vertical, source at top → VDD_REAL, drain at bottom → VDD)
        _rect(cell, _DIFF,
              cx - hw, cy - hl - _SD_EXT,
              cx + hw, cy + hl + _SD_EXT)

        # PSDM implant
        _rect(cell, _PSDM,
              cx - hw - _PSDM_ENC, cy - hl - _SD_EXT - _PSDM_ENC,
              cx + hw + _PSDM_ENC, cy + hl + _SD_EXT + _PSDM_ENC)

        # Poly gate (horizontal)
        _rect(cell, _POLY,
              cx - hw - _POLY_OVH, cy - hl,
              cx + hw + _POLY_OVH, cy + hl)

        # Gate contact to SLEEP bus (via met2)
        gate_x = cx - hw - _POLY_OVH - 0.3
        _sq_contact(cell, _LICON1, gate_x, cy, _LICON)
        _rect(cell, _LI1,
              gate_x - _LI_PAD / 2, cy - _LI_PAD / 2,
              gate_x + _LI_PAD / 2, cy + _LI_PAD / 2)
        _sq_contact(cell, _MCON, gate_x, cy, 0.17)
        pad = 0.34
        _rect(cell, _MET1,
              gate_x - pad / 2, cy - pad / 2,
              gate_x + pad / 2, cy + pad / 2)
        _sq_contact(cell, _VIA, gate_x, cy, 0.15)
        _rect(cell, _MET2,
              gate_x - pad / 2, cy - pad / 2,
              gate_x + pad / 2, cy + pad / 2)

        # Source contact (top → VDD_REAL rail)
        src_y = cy + hl + _SD_EXT / 2
        _sq_contact(cell, _LICON1, cx, src_y, _LICON)
        _rect(cell, _LI1,
              cx - _LI_PAD / 2, src_y - _LI_PAD / 2,
              cx + _LI_PAD / 2, src_y + _LI_PAD / 2)
        # Connect to VDD_REAL via mcon + met1
        _sq_contact(cell, _MCON, cx, src_y, 0.17)
        _rect(cell, _MET1,
              cx - pad / 2, src_y - pad / 2,
              cx + pad / 2, vdd_real_y + 0.14)

        # Drain contact (bottom → VDD virtual rail)
        drn_y = cy - hl - _SD_EXT / 2
        _sq_contact(cell, _LICON1, cx, drn_y, _LICON)
        _rect(cell, _LI1,
              cx - _LI_PAD / 2, drn_y - _LI_PAD / 2,
              cx + _LI_PAD / 2, drn_y + _LI_PAD / 2)
        _sq_contact(cell, _MCON, cx, drn_y, 0.17)
        _rect(cell, _MET1,
              cx - pad / 2, vdd_virt_y - 0.14,
              cx + pad / 2, drn_y + pad / 2)

    if output_path:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
