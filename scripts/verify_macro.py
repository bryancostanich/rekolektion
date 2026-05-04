#!/usr/bin/env python3
"""Verify a rekolektion macro — DRC + LVS against generated GDS and SPICE.

Usage:
    python3 scripts/verify_macro.py <macro_name> [--output-dir output/v1_macros] [--work-dir output/verify]

Exit code: 0 if both DRC and LVS clean, 1 otherwise, 2 on setup error.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

# Make rekolektion importable from repo root
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.verify.drc import run_drc
from rekolektion.verify.lvs import run_lvs


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("macro_name", help="Macro filename stem in --output-dir (no extension)")
    ap.add_argument("--output-dir", default="output/v1_macros",
                    help="Directory containing <macro>.gds and <macro>.sp")
    ap.add_argument("--work-dir", default="output/verify",
                    help="Directory for Magic/netgen work files and logs")
    ap.add_argument("--skip-lvs", action="store_true",
                    help="Run DRC only (useful while building up the flow)")
    args = ap.parse_args()

    out = Path(args.output_dir)
    work = Path(args.work_dir) / args.macro_name
    work.mkdir(parents=True, exist_ok=True)
    gds = out / f"{args.macro_name}.gds"
    sp = out / f"{args.macro_name}.sp"
    if not gds.exists():
        print(f"ERROR: {gds} not found", file=sys.stderr)
        return 2
    if not args.skip_lvs and not sp.exists():
        print(f"ERROR: {sp} not found", file=sys.stderr)
        return 2

    print(f"=== DRC — {args.macro_name} ===", flush=True)
    # task #111: opt-in to legacy global waiver filter for this generic
    # verifier.  This script verifies arbitrary user macros without
    # knowing their foundry-cell footprints, so spatial filtering isn't
    # available; the legacy filter preserves prior behavior.  Migrate
    # callers that DO know their footprints to pass them via
    # `waiver_footprints=...` instead — see scripts/run_drc_cim.py.
    drc = run_drc(gds, cell_name=args.macro_name, output_dir=work,
                  allow_global_waivers=True)
    print(drc.summary())
    if not drc.clean:
        seen = set()
        for err in drc.errors[:20]:
            if err not in seen:
                print(f"  {err}")
                seen.add(err)
    print(f"  log: {drc.log_path}")

    lvs_ok = True
    if not args.skip_lvs:
        print(f"\n=== LVS — {args.macro_name} ===", flush=True)
        lvs = run_lvs(gds, sp, cell_name=args.macro_name, output_dir=work)
        print(lvs.summary())
        print(f"  log: {lvs.log_path}")
        print(f"  extracted: {lvs.extracted_netlist_path}")
        lvs_ok = lvs.match

    return 0 if (drc.clean and lvs_ok) else 1


if __name__ == "__main__":
    sys.exit(main())
