#!/usr/bin/env python3
"""Generate V1 production SRAM macros.

V1 chip configuration (parallel weight bank architecture):
  - 1x weight macro type: sram_weight_bank_small  512 words x 32 bits, mux4 (2 KB)
    Instantiated 32 times in RTL (2 bank sets x 16 banks per set)
  - 1x activation macro type: sram_activation_bank  256 words x 64 bits, mux2 (2 KB)
    Instantiated 128 times in RTL (16 groups x 8 banks per group)

Port names use UPPERCASE to match khalkulo RTL convention:
  CLK, ADDR, DIN, DOUT, WE, CS

Each macro gets GDS, Verilog (.v), blackbox (.bb.v), SPICE (.sp),
LEF (.lef), and Liberty (.lib) output files.
All outputs are written to output/v1_macros/.
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

# Ensure the project source is importable
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.macro.assembler import generate_sram_macro, MacroParams
from rekolektion.macro.outputs import generate_spice, generate_verilog, generate_verilog_blackbox
from rekolektion.macro.lef_generator import generate_lef
from rekolektion.macro.liberty_generator import generate_liberty

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "output" / "v1_macros"

# All V1 macros use uppercase ports and exclude VPWR/VGND from Verilog
# (power pins are connected via OpenLane power grid, not RTL instantiation)
UPPERCASE_PORTS = True
INCLUDE_POWER_PINS = False

# ---------------------------------------------------------------------------
# Macro definitions
# ---------------------------------------------------------------------------

WEIGHT_MACROS = [
    {
        "filename": "sram_weight_bank_small",
        "macro_name": "sram_weight_bank_small",
        "words": 512,
        "bits": 32,
        "mux_ratio": 4,
        "description": "Weight bank small (512x32, 2 KB)",
    },
]

ACTIVATION_MACROS = [
    {
        "filename": "sram_activation_bank",
        "macro_name": "sram_activation_bank",
        "words": 256,
        "bits": 64,
        "mux_ratio": 2,
        "description": "Activation bank (256x64, 2 KB)",
    },
]

TEST_MACROS = [
    {
        "filename": "sram_test_tiny",
        "macro_name": "sram_test_tiny",
        "words": 32,
        "bits": 8,
        "mux_ratio": 1,
        "description": "Tiny dev macro (32x8, 32 B) for fast LVS iteration",
    },
]

ALL_MACROS = WEIGHT_MACROS + ACTIVATION_MACROS + TEST_MACROS


# ---------------------------------------------------------------------------
# Report helpers
# ---------------------------------------------------------------------------

def _bits_to_kb(total_bits: int) -> float:
    return total_bits / 8 / 1024


def _format_area(area_um2: float) -> str:
    if area_um2 > 1e6:
        return f"{area_um2 / 1e6:.4f} mm^2"
    return f"{area_um2:,.1f} um^2"


# ---------------------------------------------------------------------------
# Main generator
# ---------------------------------------------------------------------------

def generate_all() -> list[dict]:
    """Generate all V1 macros and return a list of result dicts."""
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    results = []
    total = len(ALL_MACROS)

    for idx, cfg in enumerate(ALL_MACROS, 1):
        name = cfg["filename"]
        gds_path = OUTPUT_DIR / f"{name}.gds"

        print(f"[{idx:2d}/{total}] {cfg['description']:40s} ", end="", flush=True)
        t0 = time.time()

        lib, params = generate_sram_macro(
            words=cfg["words"],
            bits=cfg["bits"],
            mux_ratio=cfg["mux_ratio"],
            output_path=gds_path,
            macro_name=cfg["macro_name"],
        )

        mn = cfg["macro_name"]

        # Verilog
        v_path = generate_verilog(params, OUTPUT_DIR / f"{name}.v", macro_name=mn,
                                  uppercase_ports=UPPERCASE_PORTS,
                                  include_power_pins=INCLUDE_POWER_PINS)

        # Blackbox Verilog
        bb_v_path = generate_verilog_blackbox(params, OUTPUT_DIR / f"{name}_bb.v", macro_name=mn,
                                              uppercase_ports=UPPERCASE_PORTS,
                                              include_power_pins=INCLUDE_POWER_PINS)

        # SPICE
        sp_path = generate_spice(params, OUTPUT_DIR / f"{name}.sp", macro_name=mn,
                                 uppercase_ports=UPPERCASE_PORTS)

        # LEF (with GDS-based OBS generation)
        lef_path = generate_lef(params, OUTPUT_DIR / f"{name}.lef", macro_name=mn,
                                uppercase_ports=UPPERCASE_PORTS,
                                gds_path=gds_path)

        # Liberty
        lib_path = generate_liberty(params, OUTPUT_DIR / f"{name}.lib", macro_name=mn,
                                    uppercase_ports=UPPERCASE_PORTS)

        elapsed = time.time() - t0

        total_bits = cfg["words"] * cfg["bits"]
        area_um2 = params.macro_width * params.macro_height
        area_mm2 = area_um2 / 1e6
        density = total_bits / area_mm2 if area_mm2 > 0 else 0.0

        result = {
            "name": name,
            "description": cfg["description"],
            "words": cfg["words"],
            "bits": cfg["bits"],
            "mux_ratio": cfg["mux_ratio"],
            "rows": params.rows,
            "cols": params.cols,
            "macro_width_um": params.macro_width,
            "macro_height_um": params.macro_height,
            "area_um2": area_um2,
            "area_mm2": area_mm2,
            "total_bits": total_bits,
            "capacity_kb": _bits_to_kb(total_bits),
            "density_bits_per_mm2": density,
            "gds_path": str(gds_path),
            "v_path": str(v_path),
            "bb_v_path": str(bb_v_path),
            "sp_path": str(sp_path),
            "lef_path": str(lef_path),
            "lib_path": str(lib_path),
            "elapsed_s": elapsed,
        }
        results.append(result)

        print(
            f"{params.macro_width:8.1f} x {params.macro_height:7.1f} um  "
            f"{area_mm2:.6f} mm^2  "
            f"{density:>10,.0f} b/mm^2  "
            f"({elapsed:.1f}s)"
        )

    return results


def write_manifest(results: list[dict]) -> Path:
    """Write output/v1_macros/manifest.md summarising all macros."""
    manifest_path = OUTPUT_DIR / "manifest.md"

    total_bits = sum(r["total_bits"] for r in results)
    total_area_mm2 = sum(r["area_mm2"] for r in results)
    total_kb = total_bits / 8 / 1024

    weight_results = [r for r in results if "weight" in r["name"]]
    act_results = [r for r in results if "activation" in r["name"]]
    weight_area = sum(r["area_mm2"] for r in weight_results)
    act_area = sum(r["area_mm2"] for r in act_results)

    lines = [
        "# V1 Production SRAM Macro Manifest",
        "",
        "## Summary",
        "",
        f"- **Total macro types**: {len(results)}",
        f"- **Weight macro types**: {len(weight_results)}",
        f"- **Activation macro types**: {len(act_results)}",
        f"- **RTL instances**: 32 weight (2 bank sets x 16) + 128 activation (16 groups x 8) = 160",
        f"- **Total capacity**: {total_kb:.1f} KB ({total_bits:,} bits)",
        f"- **Total SRAM area**: {total_area_mm2:.6f} mm^2",
        "",
        "## Area Breakdown",
        "",
        f"| Category    | Count | Per-macro area (mm^2) | Total area (mm^2) |",
        f"|-------------|------:|----------------------:|-------------------:|",
    ]

    if weight_results:
        per_w = weight_area / len(weight_results)
        lines.append(
            f"| Weight      | {len(weight_results):5d} | {per_w:.6f}             | {weight_area:.6f}          |"
        )
    if act_results:
        per_a = act_area / len(act_results)
        lines.append(
            f"| Activation  | {len(act_results):5d} | {per_a:.6f}             | {act_area:.6f}          |"
        )
    lines.append(
        f"| **Total**   | **{len(results)}** |                       | **{total_area_mm2:.6f}**    |"
    )

    lines += [
        "",
        "## Comparison to Area Budget",
        "",
        "Estimated V1 area budget for SRAM: ~1.5 mm^2 (typical SKY130 density).",
        "",
        f"- Actual total SRAM area: {total_area_mm2:.6f} mm^2",
        f"- Budget utilisation: {total_area_mm2 / 1.5 * 100:.1f}%",
        "",
        "## Macro Details",
        "",
        "| # | Name | Config | Rows x Cols | Width (um) | Height (um) | Area (mm^2) | Capacity | Density (b/mm^2) |",
        "|--:|------|--------|-------------|------------|-------------|-------------|----------|------------------|",
    ]

    for i, r in enumerate(results, 1):
        config = f"{r['words']}x{r['bits']} mux{r['mux_ratio']}"
        rc = f"{r['rows']}x{r['cols']}"
        lines.append(
            f"| {i} | {r['name']} | {config} | {rc} | "
            f"{r['macro_width_um']:.1f} | {r['macro_height_um']:.1f} | "
            f"{r['area_mm2']:.6f} | {r['capacity_kb']:.1f} KB | "
            f"{r['density_bits_per_mm2']:,.0f} |"
        )

    lines += [
        "",
        "## Output Files",
        "",
        "Each macro produces six files:",
        "- `.gds` -- GDS-II layout",
        "- `.v` -- Behavioral Verilog model",
        "- `_bb.v` -- Blackbox Verilog stub (for OpenSTA / synthesis)",
        "- `.sp` -- SPICE subcircuit stub",
        "- `.lef` -- LEF abstract for place-and-route",
        "- `.lib` -- Liberty timing model for STA",
        "",
    ]

    manifest_path.write_text("\n".join(lines))
    return manifest_path


def main() -> None:
    print("=" * 72)
    print("V1 Production SRAM Macro Generator")
    print("=" * 72)
    print(f"Output directory: {OUTPUT_DIR}")
    print(f"Generating {len(ALL_MACROS)} macros...\n")

    t0 = time.time()
    results = generate_all()
    total_time = time.time() - t0

    manifest = write_manifest(results)

    total_bits = sum(r["total_bits"] for r in results)
    total_area = sum(r["area_mm2"] for r in results)
    total_kb = total_bits / 8 / 1024

    print(f"\n{'=' * 72}")
    print(f"Generation complete in {total_time:.1f}s")
    print(f"  Macros: {len(results)}")
    print(f"  Total capacity: {total_kb:.1f} KB ({total_bits:,} bits)")
    print(f"  Total SRAM area: {total_area:.6f} mm^2")
    print(f"  Manifest: {manifest}")
    print(f"  Output: {OUTPUT_DIR}")
    print(f"{'=' * 72}")


if __name__ == "__main__":
    main()
