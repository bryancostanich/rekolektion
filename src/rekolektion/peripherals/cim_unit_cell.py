"""Unit cell test structure for CIM post-silicon characterization.

Wraps a single CIM bitcell with all ports (BL, BLB, WL, MWL, MBL, VDD, VSS)
routed to macro-level pins. Enables direct measurement of:
- SRAM read/write margins
- CIM coupling voltage (write weight, assert MWL, measure MBL)
- MIM cap value extraction
- T7 pass transistor characterization

One test structure per CIM variant (different cap sizes).

Usage::

    from rekolektion.peripherals.cim_unit_cell import generate_unit_cell
    cell, lib = generate_unit_cell("SRAM-A")
"""

from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.bitcell.sky130_6t_lr_cim import (
    CIM_VARIANTS, generate_cim_bitcell, load_cim_bitcell,
)


def generate_unit_cell(
    variant: str = "SRAM-A",
) -> Tuple[gdstk.Cell, gdstk.Library]:
    """Generate a unit cell test structure for one CIM variant.

    The test structure is the CIM bitcell itself with LEF-compatible
    port labels. No additional layout — the bitcell already has all
    ports labeled (BL, BLB, WL, MWL, MBL, VDD, VSS).

    Returns (cell, library).
    """
    if variant not in CIM_VARIANTS:
        raise ValueError(f"Unknown variant: {variant}")

    v = CIM_VARIANTS[variant]
    vname = variant.lower().replace("-", "_")

    # Generate the CIM bitcell
    gds_dir = Path("output/cim_variants")
    gds_dir.mkdir(parents=True, exist_ok=True)
    gds_path = gds_dir / f"sky130_6t_cim_lr_{vname}.gds"

    if not gds_path.exists():
        generate_cim_bitcell(str(gds_path), mim_w=v["mim_w"], mim_l=v["mim_l"])

    # Read the bitcell GDS
    src_lib = gdstk.read_gds(str(gds_path))
    src_cell = src_lib.cells[0]

    # Create test structure cell (copy of bitcell with new name)
    cell_name = f"cim_unit_cell_{vname}"
    cell = gdstk.Cell(cell_name)

    for poly in src_cell.polygons:
        cell.add(poly.copy())
    for lbl in src_cell.labels:
        cell.add(lbl.copy())

    lib = gdstk.Library(name=f"{cell_name}_lib", unit=1e-6, precision=5e-9)
    lib.add(cell)
    return cell, lib


def generate_all_unit_cells(output_dir: str = "output/cim_test_structures") -> None:
    """Generate unit cell test structures for all 4 CIM variants."""
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    for variant in CIM_VARIANTS:
        cell, lib = generate_unit_cell(variant)
        gds_path = out / f"{cell.name}.gds"
        lib.write_gds(str(gds_path))
        bb = cell.bounding_box()
        w = bb[1][0] - bb[0][0] if bb else 0
        h = bb[1][1] - bb[0][1] if bb else 0
        print(f"{variant}: {cell.name} ({w:.2f} x {h:.2f} um) → {gds_path}")
