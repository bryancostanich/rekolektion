"""Column mux placeholder generator.

No foundry column mux cell exists, so we generate a placeholder with
the correct pin interface.  The placeholder is a pass-through — it does
not contain actual transistor-level mux circuitry.

Supported mux ratios: 1:1 (no mux), 2:1, 4:1, 8:1.

Interface:
    BL_in[0..N-1], BR_in[0..N-1]   — input bit-line pairs from array
    BL_out[0..N/R-1], BR_out[0..N/R-1] — output bit-line pairs to sense amps
    sel[0..log2(R)-1]               — select lines

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
LAYER_MET1 = (68, 20)
LAYER_MET2 = (69, 20)
LAYER_BOUNDARY = (235, 0)

# Column mux pitch should match the bitcell pitch
_DEFAULT_BL_PITCH = 1.2  # microns — approximate bitcell width


def generate_column_mux(
    num_cols: int,
    mux_ratio: int = 1,
    bl_pitch: float = _DEFAULT_BL_PITCH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a column mux placeholder cell.

    Parameters
    ----------
    num_cols : int
        Number of input bit-line pairs (from the array).
    mux_ratio : int
        Mux ratio — must be 1, 2, 4, or 8.
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

    # Input bit-line stubs (met2, bottom edge)
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

    # Output bit-line stubs (met2, top edge)
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

    # Select line stubs (met1, left edge)
    for s in range(num_sel):
        y = height * 0.3 + s * 1.0
        cell.add(gdstk.rectangle(
            (0, y - 0.07), (0.5, y + 0.07),
            layer=LAYER_MET1[0], datatype=LAYER_MET1[1],
        ))
        cell.add(gdstk.Label(
            f"sel[{s}]", (0.25, y),
            layer=LAYER_MET1[0], texttype=LAYER_MET1[1],
        ))

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
