"""Run LVS on production CIM macros.

Mirrors `run_lvs_production.py` but for the CIM family.  Defaults to
the smallest variant (SRAM-D, 64×64) for fast turnaround when
debugging; pass variant name(s) to run others.

Usage::

    python3 scripts/run_lvs_cim.py SRAM-D
    python3 scripts/run_lvs_cim.py -j 2 SRAM-A SRAM-D
"""
from __future__ import annotations

import argparse
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams
from rekolektion.verify.lvs import run_lvs


_ROOT = Path(__file__).parent.parent
_DEFAULT_INPUT = _ROOT / "output" / "cim_macros"
_DEFAULT_OUTPUT = _ROOT / "output" / "lvs_cim"


def _flatten_gds(src_gds: Path, dst_gds: Path, top_cell: str) -> Path:
    """Flatten the top cell's hierarchy in src_gds and write to dst_gds.

    Magic's hierarchical extraction strips bitcell ports that abut
    between cells (BL columns, WL/MWL rows, MBL columns), so the
    macro-extracted bitcell sub-cell has fewer ports than the
    reference.  Flattening the entire macro before extraction
    eliminates the sub-cell hierarchy and lets Magic produce a flat
    transistor-level netlist that we compare against the (also-
    flattened by netgen) reference.
    """
    import gdstk
    src = gdstk.read_gds(str(src_gds))
    top = next(c for c in src.cells if c.name == top_cell)
    top.flatten()
    out_lib = gdstk.Library(name=f"{top_cell}_flat", unit=src.unit, precision=src.precision)
    out_lib.add(top)
    dst_gds.parent.mkdir(parents=True, exist_ok=True)
    out_lib.write_gds(str(dst_gds))
    return dst_gds


def _lvs_one(variant: str, input_root: Path, output_root: Path) -> dict:
    p = CIMMacroParams.from_variant(variant)
    cell_dir = input_root / p.top_cell_name
    gds = cell_dir / f"{p.top_cell_name}.gds"
    ref_sp = cell_dir / f"{p.top_cell_name}.sp"
    if not gds.exists() or not ref_sp.exists():
        raise SystemExit(
            f"Missing inputs for {variant}: {gds} or {ref_sp}.  "
            f"Run `python3 scripts/generate_cim_production.py` first."
        )
    out_dir = output_root / p.top_cell_name
    out_dir.mkdir(parents=True, exist_ok=True)

    # Flatten the macro hierarchy before extraction so Magic produces
    # a flat transistor-level netlist (no sub-cell port stripping).
    flat_gds = out_dir / f"{p.top_cell_name}_flat.gds"
    print(f"[{variant}] flattening macro GDS hierarchy → {flat_gds.name} ...")
    _flatten_gds(gds, flat_gds, p.top_cell_name)

    print(f"[{variant}] running full LVS: extract GDS + netgen ...")
    result = run_lvs(
        gds_path=flat_gds,
        schematic_path=ref_sp,
        cell_name=p.top_cell_name,
        output_dir=out_dir,
    )
    return {
        "variant": variant,
        "match": result.match,
        "log": str(out_dir / "lvs_results.log"),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("variants", nargs="*", default=["SRAM-D"],
                        help="CIM variants to run (default: SRAM-D)")
    parser.add_argument("-j", "--jobs", type=int, default=1,
                        help="Parallel workers (default: 1)")
    parser.add_argument("--input", type=Path, default=_DEFAULT_INPUT)
    parser.add_argument("--output", type=Path, default=_DEFAULT_OUTPUT)
    args = parser.parse_args()

    for v in args.variants:
        if v not in CIM_VARIANTS:
            parser.error(
                f"Unknown variant {v!r}. Valid: {sorted(CIM_VARIANTS)}"
            )

    args.output.mkdir(parents=True, exist_ok=True)

    results: list[dict] = []
    if args.jobs <= 1:
        for v in args.variants:
            results.append(_lvs_one(v, args.input, args.output))
    else:
        print(f"[parallel] running LVS on {len(args.variants)} "
              f"macros with {args.jobs} workers")
        with ProcessPoolExecutor(max_workers=args.jobs) as ex:
            fut_map = {
                ex.submit(_lvs_one, v, args.input, args.output): v
                for v in args.variants
            }
            for fut in as_completed(fut_map):
                results.append(fut.result())

    print("\n=== CIM LVS Summary ===")
    for r in sorted(results, key=lambda x: x["variant"]):
        status = "PASS" if r["match"] else "FAIL"
        print(f"  {r['variant']:<10} {status}  log: {r['log']}")

    return 0 if all(r["match"] for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
