"""Run LVS on production v2 SRAM macros (weight_bank, activation_bank).

Assumes generate_v2_production.py has already produced the GDS + refspice
in output/v2_macros/<macro_name>/.  This script:
  1. Extracts the assembled GDS via Magic (`extract all` + `ext2spice`).
  2. Compares the extracted netlist against the reference SPICE via netgen.

Magic extract on a 128x128 bitcell array takes ~10 min single-threaded.
Supports -j N to run multiple macros in parallel (each worker spawns its
own Magic subprocess).
"""
from __future__ import annotations

import argparse
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path

from rekolektion.verify.lvs import extract_netlist, run_lvs


_ROOT = Path(__file__).parent.parent
_DEFAULT_INPUT = _ROOT / "output" / "v2_macros"
_DEFAULT_OUTPUT = _ROOT / "output" / "lvs_production"


@dataclass(frozen=True)
class ProductionMacro:
    name: str
    # The top_cell_name inside the GDS.  Production regen renames it to
    # the macro_name at build time, so they agree.


MACROS: tuple[ProductionMacro, ...] = (
    ProductionMacro("sram_weight_bank_small"),
    ProductionMacro("sram_activation_bank"),
)


def _count_devices(sp: Path) -> dict[str, int]:
    """Count X-lines (subckt instances) by the last token on the line."""
    counts: dict[str, int] = {}
    for line in sp.read_text().splitlines():
        s = line.strip()
        if not s.startswith("X") and not s.startswith("x"):
            continue
        toks = s.split()
        if len(toks) < 2:
            continue
        name = toks[-1]
        counts[name] = counts.get(name, 0) + 1
    return counts


def _lvs_one(m: ProductionMacro, input_root: Path, output_root: Path) -> dict:
    """Run LVS on a single production macro.  Returns a result dict."""
    cell_dir = input_root / m.name
    gds = cell_dir / f"{m.name}.gds"
    ref_sp = cell_dir / f"{m.name}.sp"
    if not gds.exists() or not ref_sp.exists():
        return {
            "macro": m.name, "ok": False,
            "error": f"missing GDS or refspice in {cell_dir}",
        }

    out_dir = output_root / m.name
    out_dir.mkdir(parents=True, exist_ok=True)

    # run_lvs does the Magic extraction internally.  Don't pre-extract
    # here — that duplicates the work (Magic runs twice, taking 2x
    # wall time).
    print(
        f"[{m.name}] running full LVS: extract GDS (~10-15 min) + netgen ...",
        flush=True,
    )
    try:
        result = run_lvs(gds, ref_sp, cell_name=m.name, output_dir=out_dir)
    except Exception as exc:
        return {"macro": m.name, "ok": False, "error": f"LVS failed: {exc}"}

    # Device counts for the summary table.
    ref_counts = _count_devices(ref_sp)
    ext_path = result.extracted_netlist_path
    ext_counts = _count_devices(ext_path) if ext_path else {}
    ref_total = sum(ref_counts.values())
    ext_total = sum(ext_counts.values())

    return {
        "macro": m.name,
        "ok": result.match,
        "devices_ref": ref_total,
        "devices_ext": ext_total,
        "log": str(result.log_path),
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--input-dir", type=Path, default=_DEFAULT_INPUT,
                    help=f"where to find GDS+refspice (default: {_DEFAULT_INPUT})")
    ap.add_argument("--output-dir", type=Path, default=_DEFAULT_OUTPUT,
                    help=f"where to write extraction+LVS logs (default: {_DEFAULT_OUTPUT})")
    ap.add_argument("--workers", "-j", type=int, default=0,
                    help="parallel worker processes (0=serial, default)")
    args = ap.parse_args(argv)

    args.output_dir.mkdir(parents=True, exist_ok=True)

    results: list[dict] = []
    if args.workers <= 1 or len(MACROS) <= 1:
        for m in MACROS:
            results.append(_lvs_one(m, args.input_dir, args.output_dir))
    else:
        effective = min(args.workers, len(MACROS))
        print(f"[parallel] running LVS on {len(MACROS)} macros with {effective} workers")
        with ProcessPoolExecutor(max_workers=effective) as pool:
            futures = {
                pool.submit(_lvs_one, m, args.input_dir, args.output_dir): m
                for m in MACROS
            }
            for fut in as_completed(futures):
                results.append(fut.result())

    print("\n=== LVS Summary ===")
    all_ok = True
    for r in sorted(results, key=lambda x: x["macro"]):
        if not r["ok"]:
            all_ok = False
            print(f"  {r['macro']:<30s} FAIL  {r.get('error', 'see log')}")
            if "log" in r:
                print(f"    log: {r['log']}")
        else:
            print(f"  {r['macro']:<30s} MATCH  "
                  f"devices ref={r['devices_ref']} ext={r['devices_ext']}")

    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
