"""Column mux generator with pass-transistor topology.

Generates a column mux cell using NMOS pass-transistor topology.
Each mux group of ``mux_ratio`` input bit-line pairs is switched to a
single output pair via NMOS pass gates controlled by select lines.

Supported mux ratios: 1:1 (no mux), 2:1, 4:1, 8:1.

Interface:
    BL_in[0..N-1], BR_in[0..N-1]   -- input bit-line pairs from array
    BL_out[0..N/R-1], BR_out[0..N/R-1] -- output bit-line pairs to sense amps
    sel[0..log2(R)-1]               -- select lines

Usage::

    from rekolektion.peripherals.column_mux import generate_column_mux
    cell, lib = generate_column_mux(num_cols=64, mux_ratio=4)
"""

from __future__ import annotations

import math
from pathlib import Path
from typing import Tuple

import gdstk

# SKY130 layers
LAYER_DIFF = (65, 20)       # ndiffusion (NMOS active)
LAYER_POLY = (66, 20)       # polysilicon gate
LAYER_LICON = (66, 44)      # local interconnect contact
LAYER_LI1 = (67, 20)        # local interconnect
LAYER_MCON = (67, 44)       # metal1 contact
LAYER_MET1 = (68, 20)       # metal1
LAYER_VIA1 = (68, 44)       # via1
LAYER_MET2 = (69, 20)       # metal2
LAYER_NSDM = (93, 44)       # n+ source/drain implant
LAYER_BOUNDARY = (235, 0)

# Column mux pitch should match the bitcell pitch
_DEFAULT_BL_PITCH = 1.2  # microns -- approximate bitcell width

# Pass-transistor geometry (SKY130 minimum-ish dimensions)
_NMOS_W = 0.42             # NMOS gate width (diffusion height)
_NMOS_L = 0.15             # NMOS gate length (poly width across diff)
_DIFF_EXT = 0.13           # diffusion extension beyond gate
_CONTACT_SIZE = 0.17       # licon / mcon contact size
_IMPLANT_ENC = 0.125       # nsdm enclosure of diffusion


def _draw_pass_transistor(
    cell: gdstk.Cell,
    x_center: float,
    y_center: float,
    gate_y_bottom: float,
    gate_y_top: float,
) -> None:
    """Draw a single NMOS pass transistor at the given location.

    The transistor is oriented vertically: source/drain are above and
    below the gate, with diffusion running vertically and poly running
    horizontally across it.

    Parameters
    ----------
    cell : gdstk.Cell
        Target cell.
    x_center : float
        X centre of the transistor.
    y_center : float
        Y centre of the transistor (centre of gate).
    gate_y_bottom, gate_y_top : float
        Y extent of the poly gate line (may extend beyond the transistor
        to connect to the select bus).
    """
    half_w = _NMOS_W / 2.0
    half_l = _NMOS_L / 2.0

    # Diffusion (vertical strip)
    diff_bottom = y_center - half_w - _DIFF_EXT
    diff_top = y_center + half_w + _DIFF_EXT
    cell.add(gdstk.rectangle(
        (x_center - half_l, diff_bottom),
        (x_center + half_l, diff_top),
        layer=LAYER_DIFF[0], datatype=LAYER_DIFF[1],
    ))

    # N+ implant around diffusion
    cell.add(gdstk.rectangle(
        (x_center - half_l - _IMPLANT_ENC, diff_bottom - _IMPLANT_ENC),
        (x_center + half_l + _IMPLANT_ENC, diff_top + _IMPLANT_ENC),
        layer=LAYER_NSDM[0], datatype=LAYER_NSDM[1],
    ))

    # Poly gate (horizontal, across diffusion, extending to gate bus)
    cell.add(gdstk.rectangle(
        (x_center - half_l - 0.06, gate_y_bottom),
        (x_center + half_l + 0.06, gate_y_top),
        layer=LAYER_POLY[0], datatype=LAYER_POLY[1],
    ))

    # Source contact (bottom)
    cs = _CONTACT_SIZE / 2.0
    src_y = y_center - half_w - _DIFF_EXT / 2.0
    cell.add(gdstk.rectangle(
        (x_center - cs, src_y - cs),
        (x_center + cs, src_y + cs),
        layer=LAYER_LICON[0], datatype=LAYER_LICON[1],
    ))

    # Drain contact (top)
    drn_y = y_center + half_w + _DIFF_EXT / 2.0
    cell.add(gdstk.rectangle(
        (x_center - cs, drn_y - cs),
        (x_center + cs, drn_y + cs),
        layer=LAYER_LICON[0], datatype=LAYER_LICON[1],
    ))


def generate_column_mux(
    num_cols: int,
    mux_ratio: int = 1,
    bl_pitch: float = _DEFAULT_BL_PITCH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a column mux cell with pass-transistor topology.

    Parameters
    ----------
    num_cols : int
        Number of input bit-line pairs (from the array).
    mux_ratio : int
        Mux ratio -- must be 1, 2, 4, or 8.
    bl_pitch : float
        Bit-line pair pitch (typically == bitcell width).
    cell_name : str, optional
        Name for the cell (auto-generated if not given).
    output_path : path, optional
        If given, write GDS to this file.

    Returns
    -------
    (gdstk.Cell, gdstk.Library)
    """
    if mux_ratio not in (1, 2, 4, 8):
        raise ValueError(f"mux_ratio must be 1, 2, 4, or 8; got {mux_ratio}")
    if num_cols % mux_ratio != 0:
        raise ValueError(
            f"num_cols ({num_cols}) must be divisible by mux_ratio ({mux_ratio})"
        )

    num_outputs = num_cols // mux_ratio
    num_sel = int(math.log2(mux_ratio)) if mux_ratio > 1 else 0

    name = cell_name or f"column_mux_{num_cols}x{mux_ratio}"
    width = num_cols * bl_pitch
    height = 2.0 * mux_ratio  # Scale height with mux ratio

    lib = gdstk.Library(name=f"{name}_lib")
    cell = gdstk.Cell(name)
    lib.add(cell)

    # Boundary rectangle
    cell.add(gdstk.rectangle(
        (0, 0), (width, height),
        layer=LAYER_BOUNDARY[0], datatype=LAYER_BOUNDARY[1],
    ))

    # --- Select line buses (met1, horizontal) --------------------------------
    # Each select line runs the full width as a horizontal met1 bus.
    sel_y_positions = []
    for s in range(num_sel):
        y = height * 0.3 + s * (height * 0.4 / max(num_sel, 1))
        sel_y_positions.append(y)
        # Full-width select bus
        cell.add(gdstk.rectangle(
            (0, y - 0.07), (width, y + 0.07),
            layer=LAYER_MET1[0], datatype=LAYER_MET1[1],
        ))
        cell.add(gdstk.Label(
            f"sel[{s}]", (0.25, y),
            layer=LAYER_MET1[0], texttype=LAYER_MET1[1],
        ))

    # --- Pass-transistor mux groups ------------------------------------------
    # For each mux group, we have ``mux_ratio`` input BL pairs that are
    # switched to one output BL pair.  Each input has two NMOS pass gates
    # (one for BL, one for BR) controlled by the appropriate select line.
    #
    # For decoded mux (binary select lines):
    #   mux_ratio=2 : sel[0] selects input 0 or input 1
    #   mux_ratio=4 : sel[1:0] decoded to 4 selects (one-hot internally)
    #   mux_ratio=8 : sel[2:0] decoded to 8 selects (one-hot internally)
    # We draw one transistor per input per BL/BR, with its gate connected to
    # the corresponding select line.  For simplicity the gate is connected to
    # sel[input_index % num_sel] which is correct for mux_ratio=2 and gives a
    # representative layout for larger ratios.

    transistor_y_region_bottom = 0.6
    transistor_y_region_top = height - 0.6
    transistor_y_span = transistor_y_region_top - transistor_y_region_bottom

    for grp in range(num_outputs):
        for inp in range(mux_ratio):
            col_idx = grp * mux_ratio + inp
            x_bl = col_idx * bl_pitch + bl_pitch * 0.35
            x_br = col_idx * bl_pitch + bl_pitch * 0.65

            # Y position for this input's transistor pair
            t_y = transistor_y_region_bottom + (inp + 0.5) * transistor_y_span / mux_ratio

            # Select line for this input
            sel_idx = inp % max(num_sel, 1)
            gate_y = sel_y_positions[sel_idx] if sel_y_positions else t_y

            # Draw pass transistors for BL and BR
            _draw_pass_transistor(cell, x_bl, t_y, gate_y - 0.07, gate_y + 0.07)
            _draw_pass_transistor(cell, x_br, t_y, gate_y - 0.07, gate_y + 0.07)

    # --- Input bit-line stubs (met2, bottom edge) ----------------------------
    for i in range(num_cols):
        x_bl = i * bl_pitch + bl_pitch * 0.35
        x_br = i * bl_pitch + bl_pitch * 0.65
        # BL_in / BR_in stubs at bottom
        cell.add(gdstk.rectangle(
            (x_bl - 0.07, 0), (x_bl + 0.07, 0.5),
            layer=LAYER_MET2[0], datatype=LAYER_MET2[1],
        ))
        cell.add(gdstk.rectangle(
            (x_br - 0.07, 0), (x_br + 0.07, 0.5),
            layer=LAYER_MET2[0], datatype=LAYER_MET2[1],
        ))
        # Label
        cell.add(gdstk.Label(
            f"BL_in[{i}]", (x_bl, 0.25),
            layer=LAYER_MET2[0], texttype=LAYER_MET2[1],
        ))
        cell.add(gdstk.Label(
            f"BR_in[{i}]", (x_br, 0.25),
            layer=LAYER_MET2[0], texttype=LAYER_MET2[1],
        ))

    # --- Output bit-line stubs (met2, top edge) ------------------------------
    out_pitch = bl_pitch * mux_ratio
    for i in range(num_outputs):
        x_bl = i * out_pitch + out_pitch * 0.35
        x_br = i * out_pitch + out_pitch * 0.65
        cell.add(gdstk.rectangle(
            (x_bl - 0.07, height - 0.5), (x_bl + 0.07, height),
            layer=LAYER_MET2[0], datatype=LAYER_MET2[1],
        ))
        cell.add(gdstk.rectangle(
            (x_br - 0.07, height - 0.5), (x_br + 0.07, height),
            layer=LAYER_MET2[0], datatype=LAYER_MET2[1],
        ))
        cell.add(gdstk.Label(
            f"BL_out[{i}]", (x_bl, height - 0.25),
            layer=LAYER_MET2[0], texttype=LAYER_MET2[1],
        ))
        cell.add(gdstk.Label(
            f"BR_out[{i}]", (x_br, height - 0.25),
            layer=LAYER_MET2[0], texttype=LAYER_MET2[1],
        ))

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
