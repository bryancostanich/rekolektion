#!/usr/bin/env python3
"""Generate V1 target SRAM macros for verification.

Produces three macros matching the V1 target configurations:
  - Weight macro:     1024 words x 32 bits, 8:1 mux  (32 KB)
  - Activation macro: 384 words x 64 bits, 2:1 mux   (~3 KB)
  - Small test macro: 64 words x 8 bits, 2:1 mux

Each macro gets GDS, Verilog, and SPICE output.
"""

from __future__ import annotations

import sys
from pathlib import Path

# Ensure the project source is importable
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.macro.assembler import generate_sram_macro
from rekolektion.macro.outputs import generate_spice, generate_verilog

MACROS = [
    {
        "name": "weight_32kb",
        "words": 1024,
        "bits": 32,
        "mux_ratio": 8,
    },
    {
        "name": "activation_3kb",
        "words": 384,
        "bits": 64,
        "mux_ratio": 2,
    },
    {
        "name": "test_64x8",
        "words": 64,
        "bits": 8,
        "mux_ratio": 2,
    },
]

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "output" / "macros"


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    for cfg in MACROS:
        name = cfg["name"]
        gds_path = OUTPUT_DIR / f"{name}.gds"

        print(f"\n{'='*60}")
        print(f"Generating {name}: {cfg['words']}w x {cfg['bits']}b, mux {cfg['mux_ratio']}")
        print(f"{'='*60}")

        lib, params = generate_sram_macro(
            words=cfg["words"],
            bits=cfg["bits"],
            mux_ratio=cfg["mux_ratio"],
            output_path=gds_path,
        )

        print(f"  Array: {params.rows} rows x {params.cols} cols")
        print(f"  Address bits: {params.num_addr_bits} ({params.num_row_bits} row + {params.num_col_bits} col)")
        print(f"  Macro size: {params.macro_width:.3f} x {params.macro_height:.3f} um")
        print(f"  GDS: {gds_path}")

        # Generate Verilog model
        v_path = generate_verilog(params, OUTPUT_DIR / f"{name}.v")
        print(f"  Verilog: {v_path}")

        # Generate SPICE model
        sp_path = generate_spice(params, OUTPUT_DIR / f"{name}.sp")
        print(f"  SPICE: {sp_path}")

        # Summary stats
        total_bits = cfg["words"] * cfg["bits"]
        area_um2 = params.macro_width * params.macro_height
        area_mm2 = area_um2 / 1e6
        density = total_bits / area_mm2 if area_mm2 > 0 else 0
        print(f"  Total bits: {total_bits:,}")
        print(f"  Area: {area_um2:,.1f} um^2 = {area_mm2:.6f} mm^2")
        print(f"  Density: {density:,.0f} bits/mm^2")

    print(f"\nAll macros written to {OUTPUT_DIR}")


if __name__ == "__main__":
    main()
