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

Both macros assemble via `rekolektion.macro_v2.assembler.assemble()`
with different MacroV2Params. Per autonomous_decisions.md D4, the
activation_bank runs at mux=4 (not mux=2 as originally spec'd).
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path

from rekolektion.macro_v2.assembler import MacroV2Params, assemble
from rekolektion.macro_v2.lef_generator import generate_lef
from rekolektion.macro_v2.liberty_generator import generate_liberty
from rekolektion.macro_v2.spice_generator import generate_reference_spice


@dataclass(frozen=True)
class ProductionMacro:
    macro_name: str
    words: int
    bits: int
    mux_ratio: int


PRODUCTION_MACROS: tuple[ProductionMacro, ...] = (
    ProductionMacro("sram_weight_bank_small", 512, 32, 4),
    ProductionMacro("sram_activation_bank", 256, 64, 4),
)


def generate_all(output_root: Path) -> None:
    output_root.mkdir(parents=True, exist_ok=True)
    for m in PRODUCTION_MACROS:
        _generate_one(m, output_root)


def _generate_one(m: ProductionMacro, output_root: Path) -> None:
    print(
        f"\n[{m.macro_name}] {m.words} words × {m.bits} bits × mux={m.mux_ratio}"
    )
    p = MacroV2Params(words=m.words, bits=m.bits, mux_ratio=m.mux_ratio)
    rows = p.rows
    cols = p.cols
    print(f"  rows={rows}  cols={cols}  addr_bits={p.num_addr_bits}")

    cell_dir = output_root / m.macro_name
    cell_dir.mkdir(parents=True, exist_ok=True)
    gds_path = cell_dir / f"{m.macro_name}.gds"
    sp_path = cell_dir / f"{m.macro_name}.sp"

    lib = assemble(p)
    # Override top cell name to the production macro name by creating a
    # thin wrapper. Assembler uses `p.top_cell_name` which is
    # deterministic; instead we rename here for LEF-compat.
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    top.name = m.macro_name
    lib.name = f"{m.macro_name}_lib"
    lib.write_gds(str(gds_path))
    print(f"  wrote {gds_path}  ({gds_path.stat().st_size // 1024} KB)")

    generate_reference_spice(p, sp_path)
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
    args = ap.parse_args(argv)
    generate_all(args.output_dir)
    print("\nDone.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
