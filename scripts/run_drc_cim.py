"""Run Magic DRC on each CIM macro and report real (non-waiver) violations.

Two modes:
- flat (default) — flatten the macro into a single-cell GDS first.
  Matches what the foundry tool sees post-tapeout (fab streams flat).
  Independent of whichever flat copy LVS last cached.
- hier (`--hier`) — DRC on the hierarchical macro GDS as-is.  Matches
  what an integrator running hierarchical DRC will see.  Picks up the
  "abut between subcells" / "Can't overlap those layers" tile patterns
  (handled via _KNOWN_WAIVER_MESSAGES).

Usage::

    python3 scripts/run_drc_cim.py [SRAM-A SRAM-B SRAM-C SRAM-D]
    python3 scripts/run_drc_cim.py --hier SRAM-D
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

import gdstk

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams, build_cim_floorplan
from rekolektion.verify.drc import run_drc, find_pdk_root


# Inflate every foundry-cell footprint by this much to absorb cell
# overhangs (each cell's GDS bbox extends past its tiling pitch by
# 0.17–0.5 µm to allow shared nwell / SDM with mirror-tiled neighbors).
# Without this margin tiles inside an overhang region are misclassified
# as outside.
_OVERHANG_MARGIN: float = 0.6


def _foundry_footprints(p: CIMMacroParams) -> list[tuple[str, float, float, float, float]]:
    """Returns [(name, x0, y0, x1, y1), ...] for each region whose
    interior is allowed to host foundry-density-pattern waivers."""
    fp = build_cim_floorplan(p)
    out: list[tuple[str, float, float, float, float]] = []
    m = _OVERHANG_MARGIN
    for name in ("array", "mwl_driver", "mbl_sense", "mbl_precharge"):
        x, y = fp.positions[name]
        w, h = fp.sizes[name]
        out.append((name, x - m, y - m, x + w + m, y + h + m))
    return out


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


def _drc_one(variant: str, *, hier: bool) -> tuple[str, int, int, str]:
    """Returns (variant, total_violations, real_violations, log_path)."""
    p = CIMMacroParams.from_variant(variant)
    macro_gds = _MACRO_ROOT / p.top_cell_name / f"{p.top_cell_name}.gds"
    if not macro_gds.exists():
        return (variant, -1, -1,
                "<no GDS — run generate_cim_production.py first>")

    suffix = "hier" if hier else "flat"
    out_dir = _DRC_ROOT / suffix / p.top_cell_name
    if hier:
        gds_for_drc = macro_gds
    else:
        gds_for_drc = out_dir / f"{p.top_cell_name}_flat.gds"
        print(f"[{variant}] flattening {macro_gds.name} ...", flush=True)
        _flatten(macro_gds, gds_for_drc, p.top_cell_name)
    print(f"[{variant}] running {suffix} DRC on {gds_for_drc.name} ...", flush=True)
    # Pass per-variant foundry footprints so the spatial waiver check
    # in run_drc() can flag any rule-id-on-the-waiver-list tile that
    # falls OUTSIDE a foundry cell — those would have been silently
    # waived under the legacy global filter and might be real bugs.
    res = run_drc(
        gds_for_drc,
        cell_name=p.top_cell_name,
        pdk_root=find_pdk_root(),
        output_dir=out_dir,
        waiver_footprints=_foundry_footprints(p),
    )
    return (variant, res.error_count, res.real_error_count, str(res.log_path))


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("variants", nargs="*",
                    help="CIM variants to run (default: all four)")
    ap.add_argument("--hier", action="store_true",
                    help="DRC on hierarchical macro GDS (default: flatten first)")
    args = ap.parse_args(argv[1:])
    variants = args.variants if args.variants else list(CIM_VARIANTS.keys())
    for v in variants:
        if v not in CIM_VARIANTS:
            raise SystemExit(f"Unknown variant {v!r}. "
                             f"Valid: {sorted(CIM_VARIANTS)}")
    results = [_drc_one(v, hier=args.hier) for v in variants]
    mode = "hierarchical" if args.hier else "flat"
    print(f"\n=== CIM DRC Summary ({mode} GDS) ===")
    print(f"{'variant':<10} {'total':>10} {'real':>10}  log")
    for variant, total, real, log in results:
        status = "CLEAN" if real == 0 and total >= 0 else f"{real}"
        print(f"{variant:<10} {total:>10} {status:>10}  {log}")
    return 0 if all(real == 0 for _, _, real, _ in results if real >= 0) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
