#!/usr/bin/env python3
"""Measure bit density of generated SRAM macros.

Reads each generated macro GDS, measures bounding box area,
computes bits/mm^2, and compares against the area budget target
of 290,000 bits/mm^2.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

import gdstk

TARGET_DENSITY = 290_000  # bits/mm^2 from area budget

MACROS = [
    {"name": "weight_32kb", "words": 1024, "bits": 32, "mux_ratio": 8},
    {"name": "activation_3kb", "words": 384, "bits": 64, "mux_ratio": 2},
    {"name": "test_64x8", "words": 64, "bits": 8, "mux_ratio": 2},
]

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "output" / "macros"


def measure_macro(gds_path: Path, words: int, bits: int) -> dict:
    """Measure a single macro and return density info."""
    lib = gdstk.read_gds(str(gds_path))

    # Find top cell (usually the one not referenced by others)
    referenced = set()
    for cell in lib.cells:
        for ref in cell.references:
            referenced.add(ref.cell.name if hasattr(ref.cell, "name") else ref.cell)
    top_cells = [c for c in lib.cells if c.name not in referenced]
    top = top_cells[0] if top_cells else lib.cells[0]

    bb = top.bounding_box()
    if bb is None:
        return {
            "cell": top.name,
            "width_um": 0,
            "height_um": 0,
            "area_um2": 0,
            "area_mm2": 0,
            "total_bits": words * bits,
            "density": 0,
            "vs_target_pct": 0,
        }

    width = bb[1][0] - bb[0][0]
    height = bb[1][1] - bb[0][1]
    area_um2 = width * height
    area_mm2 = area_um2 / 1e6
    total_bits = words * bits
    density = total_bits / area_mm2 if area_mm2 > 0 else 0
    vs_target = (density / TARGET_DENSITY) * 100 if TARGET_DENSITY > 0 else 0

    return {
        "cell": top.name,
        "width_um": width,
        "height_um": height,
        "area_um2": area_um2,
        "area_mm2": area_mm2,
        "total_bits": total_bits,
        "density": density,
        "vs_target_pct": vs_target,
    }


def main() -> None:
    print(f"\n{'='*70}")
    print("SRAM Macro Density Measurement")
    print(f"Target density: {TARGET_DENSITY:,} bits/mm^2")
    print(f"{'='*70}\n")

    results = []
    for cfg in MACROS:
        gds_path = OUTPUT_DIR / f"{cfg['name']}.gds"
        if not gds_path.exists():
            print(f"SKIP: {gds_path} not found")
            continue

        info = measure_macro(gds_path, cfg["words"], cfg["bits"])
        results.append((cfg, info))

        print(f"Macro: {cfg['name']}")
        print(f"  Config: {cfg['words']}w x {cfg['bits']}b, mux {cfg['mux_ratio']}")
        print(f"  Top cell: {info['cell']}")
        print(f"  Dimensions: {info['width_um']:.3f} x {info['height_um']:.3f} um")
        print(f"  Area: {info['area_um2']:,.1f} um^2 ({info['area_mm2']:.6f} mm^2)")
        print(f"  Total bits: {info['total_bits']:,}")
        print(f"  Density: {info['density']:,.0f} bits/mm^2")
        print(f"  vs target: {info['vs_target_pct']:.1f}%")
        print()

    # Summary table
    if results:
        print(f"\n{'='*70}")
        print("Summary")
        print(f"{'='*70}")
        print(f"{'Macro':<20} {'Bits':>10} {'Area (mm2)':>12} {'Density':>15} {'vs Target':>10}")
        print(f"{'-'*20} {'-'*10} {'-'*12} {'-'*15} {'-'*10}")
        for cfg, info in results:
            print(
                f"{cfg['name']:<20} {info['total_bits']:>10,} "
                f"{info['area_mm2']:>12.6f} "
                f"{info['density']:>15,.0f} "
                f"{info['vs_target_pct']:>9.1f}%"
            )
        print(f"\nTarget: {TARGET_DENSITY:,} bits/mm^2")
        print("NOTE: Density below target is expected — placeholder peripheral")
        print("cells are oversized. Real cells will improve density.")


if __name__ == "__main__":
    main()
