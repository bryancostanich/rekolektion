"""Precharge circuit generator with PMOS transistor geometry.

Generates a precharge cell with three PMOS transistors per bit-line pair:
  - MP1: connects BL to VDD  (precharge BL)
  - MP2: connects BR to VDD  (precharge BR)
  - MP3: connects BL to BR   (equalization)

All gates are driven by the active-low ``precharge_en`` signal.

Interface:
    BL[0..N-1], BR[0..N-1] -- bit-line pairs
    precharge_en            -- active-low precharge enable
    VDD                     -- supply

Usage::

    from rekolektion.peripherals.precharge import generate_precharge
    cell, lib = generate_precharge(num_cols=64)
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

# SKY130 layers
LAYER_DIFF = (65, 20)       # pdiffusion (PMOS active)
LAYER_POLY = (66, 20)       # polysilicon gate
LAYER_LICON = (66, 44)      # local interconnect contact
LAYER_LI1 = (67, 20)        # local interconnect
LAYER_MCON = (67, 44)       # metal1 contact
LAYER_MET1 = (68, 20)       # metal1
LAYER_VIA1 = (68, 44)       # via1
LAYER_MET2 = (69, 20)       # metal2
LAYER_NWELL = (64, 20)      # n-well (for PMOS)
LAYER_PSDM = (94, 20)       # p+ source/drain implant
LAYER_BOUNDARY = (235, 0)

_DEFAULT_BL_PITCH = 1.2  # microns

# PMOS transistor geometry (SKY130 minimum-ish dimensions)
_PMOS_W = 0.42              # gate width (diffusion extent)
_PMOS_L = 0.15              # gate length (poly width across diff)
_DIFF_EXT = 0.13            # diffusion extension beyond gate
_CONTACT_SIZE = 0.17        # licon contact size
_IMPLANT_ENC = 0.125        # psdm enclosure of diffusion
_NWELL_ENC = 0.18           # nwell enclosure of diffusion


def _draw_pmos(
    cell: gdstk.Cell,
    x_center: float,
    y_center: float,
    gate_x_left: float,
    gate_x_right: float,
) -> None:
    """Draw a single PMOS transistor (vertical diffusion, horizontal gate).

    Parameters
    ----------
    cell : gdstk.Cell
        Target cell.
    x_center : float
        X centre of the diffusion.
    y_center : float
        Y centre of the gate.
    gate_x_left, gate_x_right : float
        X extent of the poly gate line (to reach the precharge_en bus).
    """
    half_w = _PMOS_W / 2.0
    half_l = _PMOS_L / 2.0

    # Diffusion (vertical strip: source at top, drain at bottom)
    diff_bottom = y_center - half_w - _DIFF_EXT
    diff_top = y_center + half_w + _DIFF_EXT
    cell.add(gdstk.rectangle(
        (x_center - half_l, diff_bottom),
        (x_center + half_l, diff_top),
        layer=LAYER_DIFF[0], datatype=LAYER_DIFF[1],
    ))

    # P+ implant
    cell.add(gdstk.rectangle(
        (x_center - half_l - _IMPLANT_ENC, diff_bottom - _IMPLANT_ENC),
        (x_center + half_l + _IMPLANT_ENC, diff_top + _IMPLANT_ENC),
        layer=LAYER_PSDM[0], datatype=LAYER_PSDM[1],
    ))

    # N-well
    cell.add(gdstk.rectangle(
        (x_center - half_l - _NWELL_ENC, diff_bottom - _NWELL_ENC),
        (x_center + half_l + _NWELL_ENC, diff_top + _NWELL_ENC),
        layer=LAYER_NWELL[0], datatype=LAYER_NWELL[1],
    ))

    # Poly gate (horizontal, spanning to gate bus)
    cell.add(gdstk.rectangle(
        (gate_x_left, y_center - half_l),
        (gate_x_right, y_center + half_l),
        layer=LAYER_POLY[0], datatype=LAYER_POLY[1],
    ))

    # Source contact (top of diffusion)
    cs = _CONTACT_SIZE / 2.0
    src_y = y_center + half_w + _DIFF_EXT / 2.0
    cell.add(gdstk.rectangle(
        (x_center - cs, src_y - cs),
        (x_center + cs, src_y + cs),
        layer=LAYER_LICON[0], datatype=LAYER_LICON[1],
    ))

    # Drain contact (bottom of diffusion)
    drn_y = y_center - half_w - _DIFF_EXT / 2.0
    cell.add(gdstk.rectangle(
        (x_center - cs, drn_y - cs),
        (x_center + cs, drn_y + cs),
        layer=LAYER_LICON[0], datatype=LAYER_LICON[1],
    ))


def generate_precharge(
    num_cols: int,
    bl_pitch: float = _DEFAULT_BL_PITCH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a precharge cell with PMOS transistor geometry.

    For each bit-line pair the cell contains:
      - MP1 (BL-to-VDD precharge PMOS)
      - MP2 (BR-to-VDD precharge PMOS)
      - MP3 (BL-to-BR equalization PMOS)

    Parameters
    ----------
    num_cols : int
        Number of bit-line pairs.
    bl_pitch : float
        Bit-line pair pitch (typically == bitcell width).
    cell_name : str, optional
        Name for the cell.
    output_path : path, optional
        If given, write GDS to this file.

    Returns
    -------
    (gdstk.Cell, gdstk.Library)
    """
    name = cell_name or f"precharge_{num_cols}"
    width = num_cols * bl_pitch
    height = 3.0  # Fixed height for precharge row

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    # Boundary
    cell.add(gdstk.rectangle(
        (0, 0), (width, height),
        layer=LAYER_BOUNDARY[0], datatype=LAYER_BOUNDARY[1],
    ))

    # --- VDD rail (met1, horizontal at top) -----------------------------------
    cell.add(gdstk.rectangle(
        (0, height - 0.5), (width, height - 0.22),
        layer=LAYER_MET1[0], datatype=LAYER_MET1[1],
    ))
    cell.add(gdstk.Label(
        "VDD", (width / 2, height - 0.36),
        layer=LAYER_MET1[0], texttype=LAYER_MET1[1],
    ))

    # --- precharge_en bus (met1, horizontal, runs full width) -----------------
    pch_en_y = 1.14
    cell.add(gdstk.rectangle(
        (0, pch_en_y - 0.07), (width, pch_en_y + 0.07),
        layer=LAYER_MET1[0], datatype=LAYER_MET1[1],
    ))
    cell.add(gdstk.Label(
        "precharge_en", (0.25, pch_en_y),
        layer=LAYER_MET1[0], texttype=LAYER_MET1[1],
    ))

    # --- Per-column PMOS transistors and bit-line stubs -----------------------
    # Layout per column (bottom to top):
    #   0.0 -- 0.5  : BL/BR input stubs (met2)
    #   ~1.1        : precharge_en bus
    #   ~1.5        : equalization PMOS (MP3, BL-to-BR)
    #   ~2.0        : precharge PMOS MP1 (BL-to-VDD) and MP2 (BR-to-VDD)
    #   2.5 -- 3.0  : VDD rail

    eq_y = 1.6    # equalization transistor y-centre
    pch_y = 2.2   # precharge transistor y-centre

    for i in range(num_cols):
        x_bl = i * bl_pitch + bl_pitch * 0.35
        x_br = i * bl_pitch + bl_pitch * 0.65
        x_mid = i * bl_pitch + bl_pitch * 0.50

        # --- BL/BR met2 stubs (full height for connectivity) ----------------
        cell.add(gdstk.rectangle(
            (x_bl - 0.07, 0), (x_bl + 0.07, height),
            layer=LAYER_MET2[0], datatype=LAYER_MET2[1],
        ))
        cell.add(gdstk.rectangle(
            (x_br - 0.07, 0), (x_br + 0.07, height),
            layer=LAYER_MET2[0], datatype=LAYER_MET2[1],
        ))
        cell.add(gdstk.Label(
            f"BL[{i}]", (x_bl, 0.25),
            layer=LAYER_MET2[0], texttype=LAYER_MET2[1],
        ))
        cell.add(gdstk.Label(
            f"BR[{i}]", (x_br, 0.25),
            layer=LAYER_MET2[0], texttype=LAYER_MET2[1],
        ))

        # --- MP1: BL precharge PMOS (source=VDD, drain=BL) ------------------
        _draw_pmos(cell, x_bl, pch_y,
                   gate_x_left=x_bl - 0.15, gate_x_right=x_bl + 0.15)

        # --- MP2: BR precharge PMOS (source=VDD, drain=BR) ------------------
        _draw_pmos(cell, x_br, pch_y,
                   gate_x_left=x_br - 0.15, gate_x_right=x_br + 0.15)

        # --- MP3: Equalization PMOS (BL-to-BR) ------------------------------
        # Place at midpoint between BL and BR
        _draw_pmos(cell, x_mid, eq_y,
                   gate_x_left=x_mid - 0.15, gate_x_right=x_mid + 0.15)

        # --- Gate connections to precharge_en bus (li1 vertical straps) ------
        for x_gate in (x_bl, x_br, x_mid):
            cell.add(gdstk.rectangle(
                (x_gate - 0.04, pch_en_y - 0.07),
                (x_gate + 0.04, eq_y),
                layer=LAYER_LI1[0], datatype=LAYER_LI1[1],
            ))

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
