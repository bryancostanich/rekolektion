"""Regenerate sram_test_tiny GDS + reference SPICE and run LVS.

Used while iterating on the reference netlist (D3 LVS closure).
Writes output to output/lvs_tiny/.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

from rekolektion.macro_v2.assembler import MacroV2Params, assemble
from rekolektion.macro_v2.spice_generator import generate_reference_spice
from rekolektion.verify.lvs import extract_netlist, run_lvs


_ROOT = Path(__file__).parent.parent
_OUT = _ROOT / "output/lvs_tiny"


def _count_devices(sp: Path) -> dict[str, int]:
    """Count X-lines (subckt instances) by the last token on the line."""
    counts: dict[str, int] = {}
    for line in sp.read_text().splitlines():
        s = line.strip()
        if not s.startswith("X") and not s.startswith("x"):
            continue
        # Skip continuation lines
        toks = s.split()
        if len(toks) < 2:
            continue
        name = toks[-1]
        counts[name] = counts.get(name, 0) + 1
    return counts


def main() -> int:
    _OUT.mkdir(parents=True, exist_ok=True)
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)

    print(f"Assembling {p.top_cell_name} ...", flush=True)
    lib = assemble(p)
    gds = _OUT / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))

    print(f"Generating reference SPICE ...", flush=True)
    ref_sp = _OUT / f"{p.top_cell_name}.sp"
    generate_reference_spice(p, ref_sp)

    print(f"Extracting netlist from GDS via Magic ...", flush=True)
    extracted = extract_netlist(gds, cell_name=p.top_cell_name, output_dir=_OUT)

    ref_counts = _count_devices(ref_sp)
    ext_counts = _count_devices(extracted)
    print("\n=== Device counts ===")
    all_names = sorted(set(ref_counts) | set(ext_counts))
    print(f"{'cell':<50s} {'ref':>6s} {'ext':>6s} {'diff':>6s}")
    for n in all_names:
        r = ref_counts.get(n, 0)
        e = ext_counts.get(n, 0)
        mark = "" if r == e else " <<"
        print(f"{n:<50s} {r:>6d} {e:>6d} {e-r:>+6d}{mark}")
    ref_total = sum(ref_counts.values())
    ext_total = sum(ext_counts.values())
    print(f"\nTOTAL ref={ref_total} ext={ext_total} diff={ext_total - ref_total:+d}")

    print("\nRunning netgen LVS ...", flush=True)
    result = run_lvs(gds, ref_sp, cell_name=p.top_cell_name, output_dir=_OUT)
    print(result.summary())
    print(f"Log: {result.log_path}")
    return 0 if result.match else 1


if __name__ == "__main__":
    sys.exit(main())
