"""Generate v2 production SRAM macros (GDS + structural SPICE).

Invocation:
    python3 scripts/generate_v2_production.py [--output-dir path]

Produces (relative to --output-dir, default output/v2_macros/):
    sram_weight_bank_small/
        sram_weight_bank_small.gds
        sram_weight_bank_small.sp
    sram_activation_bank/
        sram_activation_bank.gds
        sram_activation_bank.sp

Both macros assemble via `rekolektion.macro.assembler.assemble()`
with different MacroParams. Per autonomous_decisions.md D4, the
activation_bank runs at mux=4 (not mux=2 as originally spec'd).
"""
from __future__ import annotations

import argparse
import os
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path

from rekolektion.macro.assembler import MacroParams, assemble
from rekolektion.macro.lef_generator import generate_lef
from rekolektion.macro.liberty_generator import generate_liberty
from rekolektion.macro.spice_generator import generate_reference_spice


@dataclass(frozen=True)
class ProductionMacro:
    macro_name: str
    words: int
    bits: int
    mux_ratio: int


PRODUCTION_MACROS: tuple[ProductionMacro, ...] = (
    ProductionMacro("sram_weight_bank_small", 512, 32, 4),
    # activation_bank reverts to the spec's mux=2 now that D4 Option B
    # landed (per-pair peripheral generators at bitcell pitch fit).
    ProductionMacro("sram_activation_bank", 256, 64, 2),
)


def generate_all(output_root: Path, workers: int = 0) -> None:
    """Generate every macro in PRODUCTION_MACROS.

    workers=0 (default): run one macro at a time, in-process — easiest
    to debug, preserves live stdout.
    workers>=2:          run up to N macros concurrently via a
    ProcessPoolExecutor.  Each worker spawns its own Magic subprocess
    for the refspice extraction, so N parallel workers = N parallel
    Magic processes.  Capped at len(PRODUCTION_MACROS) since there's
    no benefit beyond one worker per macro.
    """
    output_root.mkdir(parents=True, exist_ok=True)
    if workers <= 1 or len(PRODUCTION_MACROS) <= 1:
        for m in PRODUCTION_MACROS:
            _generate_one(m, output_root)
        return

    effective = min(workers, len(PRODUCTION_MACROS))
    print(
        f"[parallel] running {len(PRODUCTION_MACROS)} macros with "
        f"{effective} worker processes"
    )
    with ProcessPoolExecutor(max_workers=effective) as pool:
        futures = {
            pool.submit(_generate_one, m, output_root): m
            for m in PRODUCTION_MACROS
        }
        for fut in as_completed(futures):
            m = futures[fut]
            try:
                fut.result()
            except Exception as exc:
                print(f"[{m.macro_name}] FAILED: {exc}", file=sys.stderr)
                raise


def _generate_one(m: ProductionMacro, output_root: Path) -> None:
    print(
        f"\n[{m.macro_name}] {m.words} words × {m.bits} bits × mux={m.mux_ratio}"
    )
    p = MacroParams(words=m.words, bits=m.bits, mux_ratio=m.mux_ratio)
    rows = p.rows
    cols = p.cols
    print(f"  rows={rows}  cols={cols}  addr_bits={p.num_addr_bits}")

    cell_dir = output_root / m.macro_name
    cell_dir.mkdir(parents=True, exist_ok=True)
    gds_path = cell_dir / f"{m.macro_name}.gds"
    sp_path = cell_dir / f"{m.macro_name}.sp"

    lib, tracker = assemble(p)
    # Override top cell name to the production macro name by creating a
    # thin wrapper. Assembler uses `p.top_cell_name` which is
    # deterministic; instead we rename here for LEF-compat.
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    top.name = m.macro_name
    lib.name = f"{m.macro_name}_lib"
    lib.write_gds(str(gds_path))
    print(f"  wrote {gds_path}  ({gds_path.stat().st_size // 1024} KB)")

    # Emit `<gds>.nets.json` sidecar consumed by the F# rekolektion-viz
    # tool to highlight nets in the rendered macro.
    sidecar_path = tracker.write(gds_path, m.macro_name)
    print(f"  wrote {sidecar_path}  ({sidecar_path.stat().st_size} bytes)")

    # Pass macro_name as the top-subckt name so the refspice's .subckt
    # header matches the GDS top-cell name we renamed above — netgen
    # needs both sides to agree on the top-level subckt name.
    generate_reference_spice(p, sp_path, top_subckt_name=m.macro_name)
    print(f"  wrote {sp_path}  ({sp_path.stat().st_size} bytes)")

    lef_path = cell_dir / f"{m.macro_name}.lef"
    # uppercase_ports=True to match rekolektion's existing v1 Liberty
    # files, which OpenLane reads alongside the LEF during P&R.
    generate_lef(p, lef_path, macro_name=m.macro_name, uppercase_ports=True)
    print(f"  wrote {lef_path}  ({lef_path.stat().st_size} bytes)")

    lib_path = cell_dir / f"{m.macro_name}.lib"
    generate_liberty(p, lib_path, macro_name=m.macro_name)
    print(f"  wrote {lib_path}  ({lib_path.stat().st_size} bytes)")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    default_output = Path(__file__).parent.parent / "output" / "v2_macros"
    ap.add_argument(
        "--output-dir", type=Path, default=default_output,
        help=f"output root (default: {default_output})",
    )
    ap.add_argument(
        "--workers", "-j", type=int, default=0,
        help=(
            "parallel worker processes.  Default 0 = serial (easier to "
            "debug).  Pass -j <N> (>=2) to run up to N macros in parallel; "
            "capped at the number of macros.  Each worker still spawns its "
            "own Magic subprocess — Magic itself is single-threaded, so the "
            "wall-clock speedup is at most min(workers, len(macros))."
        ),
    )
    args = ap.parse_args(argv)
    generate_all(args.output_dir, workers=args.workers)
    print("\nDone.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
