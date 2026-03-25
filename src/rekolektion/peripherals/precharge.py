"""Precharge circuit placeholder generator.

Generates a simple placeholder precharge cell that equalizes BL and BR
before a read operation.  The placeholder has the correct pin interface
but does not contain transistor-level circuitry.

Interface:
    BL[0..N-1], BR[0..N-1] — bit-line pairs
    precharge_en            — active-low precharge enable
    VDD                     — supply

Usage::

    from rekolektion.peripherals.precharge import generate_precharge
    cell, lib = generate_precharge(num_cols=64)
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

# SKY130 layers
LAYER_MET1 = (68, 20)
LAYER_MET2 = (69, 20)
LAYER_BOUNDARY = (235, 0)

_DEFAULT_BL_PITCH = 1.2  # microns


def generate_precharge(
    num_cols: int,
    bl_pitch: float = _DEFAULT_BL_PITCH,
    cell_name: str | None = None,
    output_path: str | Path | None = None,
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a precharge placeholder cell.

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

    # Bit-line stubs (met2, bottom — connects to array top)
    for i in range(num_cols):
        x_bl = i * bl_pitch + bl_pitch * 0.35
        x_br = i * bl_pitch + bl_pitch * 0.65
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

    # VDD rail (met1, horizontal at top)
    cell.add(gdstk.rectangle(
        (0, height - 0.5), (width, height - 0.22),
        layer=LAYER_MET1[0], datatype=LAYER_MET1[1],
    ))
    cell.add(gdstk.Label(
        "VDD", (width / 2, height - 0.36),
        layer=LAYER_MET1[0], texttype=LAYER_MET1[1],
    ))

    # precharge_en signal (met1, left edge)
    cell.add(gdstk.rectangle(
        (0, 1.0), (0.5, 1.28),
        layer=LAYER_MET1[0], datatype=LAYER_MET1[1],
    ))
    cell.add(gdstk.Label(
        "precharge_en", (0.25, 1.14),
        layer=LAYER_MET1[0], texttype=LAYER_MET1[1],
    ))

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))

    return cell, lib
