#!/usr/bin/env python3
"""SRAM macro sizing study — Track 15a Phase 0.

Generates alternative macro configurations and compares total SRAM area
to find the optimal dimensions for V1. The goal is to minimize total
area (macro area × instance count) for the required capacity.

Current V1:
  - Weight: 32 × (512×32, mux4) = 64 KB,  per-macro 0.0396 mm²
  - Activation: 128 × (256×64, mux2) = 192 KB, per-macro 0.0400 mm²

Constraints:
  - Weight port: 32-bit read data (parallel bank delivers 512 bits via 16 banks)
  - Activation port: 64-bit read data (8 bytes per bank, 8 banks = 64 bytes/group)
  - Weight bank count: 16 per set (fixed by parallel delivery architecture)
  - Activation bank count: variable (audit says 4-8 per group)
"""

from __future__ import annotations

import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.macro.assembler import generate_sram_macro, MacroParams

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "output" / "sizing_study"


def measure_macro(words: int, bits: int, mux_ratio: int, name: str) -> dict:
    """Generate a macro and measure its area. Returns result dict."""
    gds_path = OUTPUT_DIR / f"{name}.gds"

    try:
        lib, params = generate_sram_macro(
            words=words,
            bits=bits,
            mux_ratio=mux_ratio,
            output_path=gds_path,
            macro_name=name,
        )

        area_um2 = params.macro_width * params.macro_height
        area_mm2 = area_um2 / 1e6
        total_bits = words * bits
        density = total_bits / area_mm2 if area_mm2 > 0 else 0

        return {
            "name": name,
            "words": words,
            "bits": bits,
            "mux_ratio": mux_ratio,
            "rows": params.rows,
            "cols": params.cols,
            "width_um": params.macro_width,
            "height_um": params.macro_height,
            "area_mm2": area_mm2,
            "total_bits": total_bits,
            "capacity_kb": total_bits / 8 / 1024,
            "density_bpmm2": density,
        }
    except Exception as e:
        return {
            "name": name,
            "words": words,
            "bits": bits,
            "mux_ratio": mux_ratio,
            "error": str(e),
        }


def weight_bank_study():
    """Study alternative weight bank macro dimensions.

    Current: 512×32 mux4 (2 KB per macro, 16 macros per set, 32 total).
    Parallel delivery requires 16 banks per set, each providing 32 bits.
    Port width is fixed at 32 bits.

    We can change words (depth) and mux_ratio while keeping bits=32.
    More words per macro = fewer words wasted if capacity > needed.
    Different mux ratios trade row count for column mux complexity.
    """
    configs = [
        # (words, bits, mux, description)
        (256, 32, 2, "wgt_256x32_m2"),
        (256, 32, 4, "wgt_256x32_m4"),
        (512, 32, 2, "wgt_512x32_m2"),
        (512, 32, 4, "wgt_512x32_m4"),   # current
        (512, 32, 8, "wgt_512x32_m8"),
        (1024, 32, 4, "wgt_1024x32_m4"),
        (1024, 32, 8, "wgt_1024x32_m8"),
        (2048, 32, 8, "wgt_2048x32_m8"),
    ]
    return configs


def activation_bank_study():
    """Study alternative activation bank macro dimensions.

    Current: 256×64 mux2 (2 KB per macro, 8 per group, 128 total).
    Port width is 64 bits (8 bytes per read).

    If we reduce to 4 banks/group, we need same capacity in fewer macros.
    Options: bigger macros (more words), different port widths, different mux.
    Port width must stay 64 for DW mode compatibility.
    """
    configs = [
        # (words, bits, mux, description)
        (128, 64, 2, "act_128x64_m2"),
        (192, 64, 2, "act_192x64_m2"),
        (256, 64, 2, "act_256x64_m2"),   # current
        (256, 64, 4, "act_256x64_m4"),
        (384, 64, 2, "act_384x64_m2"),
        (384, 64, 4, "act_384x64_m4"),
        (512, 64, 2, "act_512x64_m2"),
        (512, 64, 4, "act_512x64_m4"),
        (768, 64, 2, "act_768x64_m2"),
        (1024, 64, 2, "act_1024x64_m2"),
        (1024, 64, 4, "act_1024x64_m4"),
        (1024, 64, 8, "act_1024x64_m8"),
    ]
    return configs


def print_weight_analysis(results):
    """Analyze weight bank results for total system area."""
    print("\n" + "=" * 90)
    print("WEIGHT BANK ANALYSIS")
    print("=" * 90)
    print(f"\nConstraint: 16 banks per set, 2 sets = 32 instances. Port width = 32 bits.")
    print(f"Current: 512×32 mux4, 0.0396 mm²/macro, 32 × 0.0396 = 1.27 mm² total\n")

    print(f"{'Config':<20s} {'Rows×Cols':>10s} {'Area/macro':>12s} {'Density':>12s} "
          f"{'Cap/macro':>10s} {'32× total':>12s} {'vs current':>12s}")
    print("-" * 90)

    current_total = None
    for r in results:
        if "error" in r:
            print(f"{r['name']:<20s} ERROR: {r['error']}")
            continue

        total_32 = r["area_mm2"] * 32
        config = f"{r['words']}×{r['bits']} m{r['mux_ratio']}"
        rc = f"{r['rows']}×{r['cols']}"

        if r["name"] == "wgt_512x32_m4":
            current_total = total_32
            marker = " <-- CURRENT"
        else:
            marker = ""

        if current_total:
            delta = ((total_32 - current_total) / current_total) * 100
            delta_str = f"{delta:+.1f}%"
        else:
            delta_str = "--"

        print(f"{config:<20s} {rc:>10s} {r['area_mm2']:>12.6f} {r['density_bpmm2']:>12,.0f} "
              f"{r['capacity_kb']:>8.1f} KB {total_32:>12.4f} {delta_str:>12s}{marker}")


def print_activation_analysis(results):
    """Analyze activation bank results for different bank-per-group counts."""
    print("\n" + "=" * 90)
    print("ACTIVATION BANK ANALYSIS")
    print("=" * 90)
    print(f"\nPort width = 64 bits. Analyzing at 8, 6, and 4 banks per group (9 groups).")
    print(f"Current: 256×64 mux2, 0.0400 mm²/macro, 128 instances (8/group), 5.12 mm² total")
    print(f"Target: same or similar per-group capacity with fewer, denser macros.\n")

    # Required per-group capacity for different bank counts:
    # Current: 8 banks × 256 words × 64 bits = 8 × 2 KB = 16 KB/group
    # Need enough capacity for activation tiles

    current_total = 128 * 0.0400  # 5.12 mm²

    for n_banks in [8, 6, 4]:
        print(f"\n--- {n_banks} banks/group ({n_banks * 9} total instances) ---")
        print(f"{'Config':<20s} {'Rows×Cols':>10s} {'Area/macro':>12s} {'Density':>12s} "
              f"{'Cap/macro':>10s} {'Cap/group':>10s} {'Total area':>12s} {'vs current':>12s}")
        print("-" * 110)

        for r in results:
            if "error" in r:
                print(f"{r['name']:<20s} ERROR: {r['error']}")
                continue

            n_instances = n_banks * 9
            total_area = r["area_mm2"] * n_instances
            cap_per_group = r["capacity_kb"] * n_banks
            config = f"{r['words']}×{r['bits']} m{r['mux_ratio']}"
            rc = f"{r['rows']}×{r['cols']}"
            delta = ((total_area - current_total) / current_total) * 100

            marker = ""
            if r["name"] == "act_256x64_m2" and n_banks == 8:
                marker = " <-- CURRENT"

            print(f"{config:<20s} {rc:>10s} {r['area_mm2']:>12.6f} {r['density_bpmm2']:>12,.0f} "
                  f"{r['capacity_kb']:>8.1f} KB {cap_per_group:>8.1f} KB "
                  f"{total_area:>12.4f} {delta:>+11.1f}%{marker}")


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print("=" * 90)
    print("SRAM MACRO SIZING STUDY — Track 15a Phase 0")
    print("=" * 90)

    # Generate weight bank alternatives
    print("\nGenerating weight bank macros...")
    wgt_configs = weight_bank_study()
    wgt_results = []
    for words, bits, mux, name in wgt_configs:
        print(f"  {name:30s} ", end="", flush=True)
        t0 = time.time()
        r = measure_macro(words, bits, mux, name)
        elapsed = time.time() - t0
        if "error" in r:
            print(f"ERROR: {r['error']}")
        else:
            print(f"{r['width_um']:7.1f} × {r['height_um']:7.1f} um  "
                  f"{r['area_mm2']:.6f} mm²  ({elapsed:.1f}s)")
        wgt_results.append(r)

    # Generate activation bank alternatives
    print("\nGenerating activation bank macros...")
    act_configs = activation_bank_study()
    act_results = []
    for words, bits, mux, name in act_configs:
        print(f"  {name:30s} ", end="", flush=True)
        t0 = time.time()
        r = measure_macro(words, bits, mux, name)
        elapsed = time.time() - t0
        if "error" in r:
            print(f"ERROR: {r['error']}")
        else:
            print(f"{r['width_um']:7.1f} × {r['height_um']:7.1f} um  "
                  f"{r['area_mm2']:.6f} mm²  ({elapsed:.1f}s)")
        act_results.append(r)

    # Analysis
    print_weight_analysis(wgt_results)
    print_activation_analysis(act_results)

    # Save raw results
    all_results = {"weight": wgt_results, "activation": act_results}
    results_path = OUTPUT_DIR / "sizing_study_results.json"
    with open(results_path, "w") as f:
        json.dump(all_results, f, indent=2, default=str)
    print(f"\nRaw results saved to {results_path}")


if __name__ == "__main__":
    main()
