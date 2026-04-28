"""Generate production CIM macros (GDS + SPICE reference).

Mirrors `generate_v2_production.py` for the CIM family.  Builds each
variant under `output/cim_macros/<variant>/` with both the assembled
GDS and the LVS reference SPICE.

Usage::

    python3 scripts/generate_cim_production.py [SRAM-A SRAM-B SRAM-C SRAM-D]

If no variants are passed on the CLI, all four are built.
"""
from __future__ import annotations

import sys
from pathlib import Path

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import (
    CIMMacroParams, generate_cim_macro,
)
from rekolektion.macro.cim_spice_generator import generate_cim_reference_spice


_OUT_ROOT = Path("output/cim_macros")


def build_variant(variant: str) -> CIMMacroParams:
    p = CIMMacroParams.from_variant(variant)
    out_dir = _OUT_ROOT / p.top_cell_name
    out_dir.mkdir(parents=True, exist_ok=True)

    gds_path = out_dir / f"{p.top_cell_name}.gds"
    sp_path = out_dir / f"{p.top_cell_name}.sp"

    print(f"\n[{variant}] {p.rows} rows × {p.cols} cols × {p.cap_fF:.1f} fF")
    _, p = generate_cim_macro(variant, output_path=gds_path)
    print(f"  wrote {gds_path}  ({p.macro_width:.1f} × {p.macro_height:.1f} um)")

    generate_cim_reference_spice(p, sp_path)
    print(f"  wrote {sp_path}")
    return p


def main(argv: list[str]) -> int:
    variants = argv[1:] if len(argv) > 1 else list(CIM_VARIANTS.keys())
    for v in variants:
        if v not in CIM_VARIANTS:
            raise SystemExit(f"Unknown variant {v!r}. "
                             f"Valid: {sorted(CIM_VARIANTS)}")
    for v in variants:
        build_variant(v)
    print("\nDone.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
