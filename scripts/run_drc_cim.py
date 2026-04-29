"""Run Magic DRC on each CIM macro's flattened GDS and report real
(non-waiver) violations.

The hierarchical macro GDS produces many "can't abut between subcells"
false positives that don't exist post-tapeout (the fab streams a flat
layout).  This script flattens each macro into its own DRC working
directory and runs Magic DRC against that, so the result reflects what
the foundry tool will actually see and is independent of whichever
flat copy the LVS pipeline last cached.

Usage::

    python3 scripts/run_drc_cim.py [SRAM-A SRAM-B SRAM-C SRAM-D]

Defaults to all four if no variants are given.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

import gdstk

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams
from rekolektion.verify.drc import run_drc, find_pdk_root


_MACRO_ROOT = Path("output/cim_macros")
_DRC_ROOT = Path("output/drc_cim")


def _flatten(macro_gds: Path, dst: Path, top_cell: str) -> Path:
    """Copy macro_gds into a flattened single-cell GDS at dst.

    Magic gives the cleanest DRC against a single fully-flat top cell;
    hierarchical input creates spurious "abut between subcells" tiles.
    """
    src = gdstk.read_gds(str(macro_gds))
    top = next(c for c in src.cells if c.name == top_cell)
    top.flatten()
    out_lib = gdstk.Library(name=f"{top_cell}_flat",
                            unit=src.unit, precision=src.precision)
    out_lib.add(top)
    dst.parent.mkdir(parents=True, exist_ok=True)
    out_lib.write_gds(str(dst))
    return dst


def _drc_one(variant: str) -> tuple[str, int, int, str]:
    """Returns (variant, total_violations, real_violations, log_path)."""
    p = CIMMacroParams.from_variant(variant)
    macro_gds = _MACRO_ROOT / p.top_cell_name / f"{p.top_cell_name}.gds"
    if not macro_gds.exists():
        return (variant, -1, -1,
                "<no GDS — run generate_cim_production.py first>")

    out_dir = _DRC_ROOT / p.top_cell_name
    flat_gds = out_dir / f"{p.top_cell_name}_flat.gds"
    print(f"[{variant}] flattening {macro_gds.name} ...", flush=True)
    _flatten(macro_gds, flat_gds, p.top_cell_name)
    print(f"[{variant}] running DRC on {flat_gds.name} ...", flush=True)
    res = run_drc(
        flat_gds,
        cell_name=p.top_cell_name,
        pdk_root=find_pdk_root(),
        output_dir=out_dir,
    )
    return (variant, res.error_count, res.real_error_count, str(res.log_path))


def main(argv: list[str]) -> int:
    variants = argv[1:] if len(argv) > 1 else list(CIM_VARIANTS.keys())
    for v in variants:
        if v not in CIM_VARIANTS:
            raise SystemExit(f"Unknown variant {v!r}. "
                             f"Valid: {sorted(CIM_VARIANTS)}")
    results = [_drc_one(v) for v in variants]
    print("\n=== CIM DRC Summary (flat GDS) ===")
    print(f"{'variant':<10} {'total':>10} {'real':>10}  log")
    for variant, total, real, log in results:
        status = "CLEAN" if real == 0 and total >= 0 else f"{real}"
        print(f"{variant:<10} {total:>10} {status:>10}  {log}")
    return 0 if all(real == 0 for _, _, real, _ in results if real >= 0) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
