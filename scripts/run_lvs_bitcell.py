"""Run strict LVS on the LR bitcell and the CIM bitcell against their
hand-written schematic references.

Background: track 05 phase-1 SPICE characterisation surfaced that
the LR-derived bitcells extract with 8 transistors / 14 nets while
the schematic only declares 6 transistors / 7 nets — a real layout
defect that the existing LVS infrastructure was missing.  Root cause:
`run_lvs_cim.py` and friends compare extracted-macro against a
reference SPICE *built from the same extracted cells*, so a broken
cell agrees with itself and LVS reports "match".  This script runs
LVS against *independently authored* schematics in `output/`.

Usage::

    # Run all bitcell LVS checks (LR + CIM).
    python3 scripts/run_lvs_bitcell.py

    # Run a specific cell only.
    python3 scripts/run_lvs_bitcell.py lr
    python3 scripts/run_lvs_bitcell.py cim

The schematic references live alongside the GDS in `output/`:
  - LR  : output/sky130_6t_lr.spice (hand-written, 6T standard)
  - CIM : output/sky130_6t_cim_lr.spice (hand-written, 6T+1T+1C C3SRAM)

Exit code: 0 if all selected LVS runs match, 1 otherwise.
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.verify.lvs import run_lvs


_ROOT = Path(__file__).parent.parent
_OUT = _ROOT / "output"


@dataclass(frozen=True)
class BitcellLVSConfig:
    label: str                 # short name shown in summary
    gds: Path                  # layout
    schematic: Path            # hand-written reference
    cell_name: str             # .subckt name


_CONFIGS: dict[str, BitcellLVSConfig] = {
    "lr": BitcellLVSConfig(
        label="LR (6T)",
        gds=_OUT / "sky130_6t_lr.gds",
        schematic=_OUT / "sky130_6t_lr.spice",
        cell_name="sky130_sram_6t_bitcell_lr",
    ),
    "cim": BitcellLVSConfig(
        label="CIM (6T+1T+1C)",
        gds=_OUT / "sky130_6t_cim_lr.gds",
        schematic=_OUT / "sky130_6t_cim_lr.spice",
        cell_name="sky130_sram_6t_cim_lr",
    ),
}


def _run_one(cfg: BitcellLVSConfig) -> tuple[bool, Path]:
    """Returns (match, log_path)."""
    if not cfg.gds.exists():
        raise FileNotFoundError(f"Missing GDS: {cfg.gds}")
    if not cfg.schematic.exists():
        raise FileNotFoundError(f"Missing schematic ref: {cfg.schematic}")

    out_dir = _OUT / "lvs_bitcell" / cfg.label.replace(" ", "_").replace("(", "").replace(")", "")
    out_dir.mkdir(parents=True, exist_ok=True)

    print(f"[{cfg.label}] LVS: layout={cfg.gds.name} "
          f"vs schematic={cfg.schematic.name}", flush=True)
    result = run_lvs(
        gds_path=cfg.gds,
        schematic_path=cfg.schematic,
        cell_name=cfg.cell_name,
        output_dir=out_dir,
    )
    return result.match, out_dir / "lvs_results.log"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument(
        "cells", nargs="*",
        help=f"Cell(s) to LVS (one of {sorted(_CONFIGS)}). Default: all.",
    )
    args = ap.parse_args(argv[1:])
    cells = args.cells or sorted(_CONFIGS.keys())
    for c in cells:
        if c not in _CONFIGS:
            raise SystemExit(
                f"Unknown cell {c!r}. Valid: {sorted(_CONFIGS)}"
            )

    print(f"Running strict LVS on {len(cells)} bitcell(s)\n", flush=True)
    results: list[tuple[BitcellLVSConfig, bool, Path]] = []
    for key in cells:
        cfg = _CONFIGS[key]
        try:
            match, log = _run_one(cfg)
            results.append((cfg, match, log))
        except FileNotFoundError as e:
            print(f"[{cfg.label}] SKIP: {e}", flush=True)
            results.append((cfg, False, Path(str(e))))

    print("\n=== Bitcell LVS Summary ===")
    print(f"{'cell':<22} {'match':<8}  log")
    any_fail = False
    for cfg, match, log in results:
        status = "PASS" if match else "FAIL"
        if not match:
            any_fail = True
        print(f"{cfg.label:<22} {status:<8}  {log}")
    if any_fail:
        print("\n*** One or more bitcells fail strict LVS — layout does not "
              "match its hand-written schematic.  See logs above. ***")
    return 1 if any_fail else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
