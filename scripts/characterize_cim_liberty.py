"""SPICE characterisation harness for the CIM macro Liberty file.

Generates an ngspice testbench around the macro, runs a transient
sim, and reports timing measurements (precharge → MBL_OUT settling).
Intended drop-in replacement for the analytical numbers in
`cim_liberty_generator.py` — once the testbench fidelity issues
documented below are resolved.

KNOWN LIMITATIONS (first cut):

- The 6T bitcells store Q/QB on cross-coupled inverter nodes that
  ngspice cannot resolve at DC without explicit `.ic` initialisation
  or a pre-conditioning write through BL/BLB + WL.  Currently
  produces "singular matrix" warnings during DC OP, falls through to
  source-stepping, and the transient still runs but bitcell state is
  random.  CIM compute output therefore reflects only the precharge
  / MWL_EN edge — not a meaningful binary input pattern × weight
  product.  TODO: prepend a 5 ns write phase that drives BL/BLB
  + WL_<r> for each row to a known weight, then transitions to the
  CIM compute phase.

- `t50` / `t90` Liberty measures expect a digital-edge output that
  crosses 0.5·VDD / 0.9·VDD.  MBL_OUT[c] is ANALOG (NMOS source
  follower of MBL voltage); on the smoke run it settled to ~1.17 V
  and stayed there, so the standard rise-edge measure failed.  For
  CIM Liberty arcs, replace with:
    - V_quiescent (MBL_PRE precharged, no MWL_EN)
    - V_compute   (MWL_EN[r] asserted with known weight pattern)
    - t_settle    (time for V to be within 5% of V_compute)
  These three together give the timing model STA needs.

- A single-point sim already takes ~30 min on the 64-row variant;
  256-row variants will be 4×.  A full NLDM table (4 input slews ×
  4 output loads = 16 points × 4 variants = 64 sims) is roughly a
  day of compute. Plan to run overnight per characterisation pass.


The macro reference SPICE exposes BL/BLB/WL/MWL/MBL as ports for
LVS matchability; for simulation those nets need stable biases:
  - BL/BLB :  VPWR  (precharged-high SRAM state, no read access)
  - WL     :  VGND  (access transistors off — Q/QB held in cell)
  - MWL_<r>:  internally driven by the buf_2 in row r; tied to GND
              through a high-Z 1 MΩ resistor so ngspice doesn't flag
              floating nodes (the buf_2's ~5 kΩ output impedance
              dominates).
  - MBL_<c>:  internally connected between precharge, bitcells, and
              sense; same high-Z tie.

Sim measurements (single point, default slew + load):
  - t_compute  =  time from MBL_PRE rising edge to MBL_OUT[0]
                  crossing 50% of its precharged-vs-asserted swing
  - i_in_avg   =  average current through MBL_PRE during the
                  precharge ramp (input cap × dV/dt approximation)

Usage::

    python3 scripts/characterize_cim_liberty.py SRAM-D
    python3 scripts/characterize_cim_liberty.py --all
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams


_PDK_NGSPICE = Path(
    "~/.volare/volare/sky130/versions/0fe599b2afb6708d281543108caf8310912f54af/sky130B/libs.tech/ngspice/sky130.lib.spice"
).expanduser()
_OUT_ROOT = Path("output/spice_char")


def _build_testbench(p: CIMMacroParams, work: Path) -> Path:
    """Write the ngspice testbench file and return its path."""
    macro = p.top_cell_name
    macro_sp = (Path("output/cim_macros") / macro / f"{macro}.sp").resolve()
    if not macro_sp.exists():
        raise FileNotFoundError(f"missing {macro_sp} — run generate_cim_production.py")

    # Build the macro's port list as it appears in the .subckt header.
    # Order matches what cim_spice_generator emits.
    rows = p.rows
    cols = p.cols
    ports: list[str] = []
    ports += [f"MWL_EN[{i}]" for i in range(rows)]
    ports += [f"MBL_OUT[{i}]" for i in range(cols)]
    ports += ["MBL_PRE", "VREF", "VBIAS"]
    ports += [f"BL_{i}" for i in range(cols)]
    ports += [f"BLB_{i}" for i in range(cols)]
    ports += [f"WL_{i}" for i in range(rows)]
    ports += [f"MWL_{i}" for i in range(rows)]
    ports += [f"MBL_{i}" for i in range(cols)]
    ports += ["VPWR", "VGND"]

    tb = work / "tb.sp"
    lines: list[str] = []
    lines.append(f"* CIM characterisation testbench for {macro}")
    lines.append(f".lib {_PDK_NGSPICE} tt")
    lines.append(f".include {macro_sp}")
    lines.append("")
    lines.append("* Supplies")
    lines.append("Vpwr  vpwr  0  1.8")
    lines.append("Vgnd  vgnd  0  0")
    lines.append("Vvref vref  0  1.5")
    lines.append("Vvbias vbias 0 0.7")
    lines.append("")
    lines.append("* Stimulus: MBL_PRE held HIGH for 5 ns precharge, then falling.")
    lines.append("Vmpre mbl_pre 0 PWL(0 1.8 5n 1.8 5.1n 0)")
    lines.append("")
    lines.append("* MWL_EN[0] asserts simultaneously with MBL_PRE falling.")
    lines.append("Vmwl0 mwl_en_0 0 PWL(0 0 5n 0 5.1n 1.8)")
    # Other MWL_EN[r]: held LOW
    lines.append("* Other MWL_EN[r] tied LOW so only row 0 contributes.")
    for i in range(1, rows):
        lines.append(f"Vmwl{i} mwl_en_{i} 0 0")
    lines.append("")
    lines.append("* BL_<c> / BLB_<c>: precharged HIGH (no SRAM access).")
    for i in range(cols):
        lines.append(f"Vbl{i}  bl_{i}  0 1.8")
        lines.append(f"Vblb{i} blb_{i} 0 1.8")
    lines.append("")
    lines.append("* WL_<r>: held LOW so 6T access transistors are off.")
    for i in range(rows):
        lines.append(f"Vwl{i} wl_{i} 0 0")
    lines.append("")
    lines.append("* MWL_<r> and MBL_<c> are internally driven by the macro;")
    lines.append("* tie via 1 MΩ high-Z to ground so ngspice has a node.")
    for i in range(rows):
        lines.append(f"Rmwl{i}_tie mwl_{i} 0 1Meg")
    for i in range(cols):
        lines.append(f"Rmbl{i}_tie mbl_{i} 0 1Meg")
    lines.append("")
    lines.append("* MBL_OUT loads — small (10 fF) so we measure intrinsic delay.")
    for i in range(cols):
        lines.append(f"Cmblo{i} mbl_out_{i} 0 10f")
    lines.append("")

    # Build .subckt instantiation in port order.  Map our testbench
    # node names to the port list using underscored variants of the
    # bracketed forms (ngspice flattens [] to _).
    def _node(port: str) -> str:
        return port.replace("[", "_").replace("]", "").lower()
    inst_nodes = " ".join(_node(p) for p in ports)
    lines.append(f"Xdut {inst_nodes} {macro}")
    lines.append("")
    lines.append("* --- analyses ---")
    lines.append(".tran 0.05n 30n")
    lines.append(".measure tran t50 WHEN v(mbl_out_0)=0.9 RISE=1 TD=5n")
    lines.append(".measure tran t90 WHEN v(mbl_out_0)=1.62 RISE=1 TD=5n")
    lines.append(".measure tran v_settle FIND v(mbl_out_0) AT=25n")
    lines.append(".end")
    tb.write_text("\n".join(lines))
    return tb


def _run_ngspice(tb: Path, work: Path, timeout: int = 1800) -> tuple[bool, str]:
    log = work / "ngspice.log"
    res = subprocess.run(
        ["ngspice", "-b", str(tb.resolve())],
        capture_output=True, text=True, timeout=timeout, cwd=str(work),
    )
    log.write_text(res.stdout + "\n--- STDERR ---\n" + res.stderr)
    return (res.returncode == 0, res.stdout)


def _parse_measurements(stdout: str) -> dict[str, float]:
    """Pull `.measure` results out of ngspice stdout."""
    out: dict[str, float] = {}
    pat = re.compile(r"^\s*(t50|t90|v_settle)\s*=\s*([0-9.eE+\-]+)", re.M)
    for m in pat.finditer(stdout):
        out[m.group(1)] = float(m.group(2))
    return out


def _char_one(variant: str) -> dict:
    p = CIMMacroParams.from_variant(variant)
    work = _OUT_ROOT / p.top_cell_name
    work.mkdir(parents=True, exist_ok=True)
    tb = _build_testbench(p, work)
    print(f"[{variant}] running ngspice on {tb} ...", flush=True)
    ok, stdout = _run_ngspice(tb, work)
    if not ok:
        return {"variant": variant, "ok": False,
                "log": str(work / "ngspice.log")}
    meas = _parse_measurements(stdout)
    return {"variant": variant, "ok": True, **meas,
            "log": str(work / "ngspice.log")}


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("variants", nargs="*", help="Variants (default: SRAM-D)")
    ap.add_argument("--all", action="store_true",
                    help="Run all four variants (slow on 256-row).")
    args = ap.parse_args(argv[1:])
    if args.all:
        variants = list(CIM_VARIANTS.keys())
    elif args.variants:
        variants = args.variants
    else:
        variants = ["SRAM-D"]
    for v in variants:
        if v not in CIM_VARIANTS:
            raise SystemExit(f"Unknown variant {v!r}.")
    if not shutil.which("ngspice"):
        raise SystemExit("ngspice not on PATH")
    if not _PDK_NGSPICE.exists():
        raise SystemExit(f"sky130 ngspice models not at {_PDK_NGSPICE}")
    results = [_char_one(v) for v in variants]
    print("\n=== CIM SPICE Characterisation ===")
    print(f"{'variant':<10} {'t50 (ns)':>10} {'t90 (ns)':>10} {'V@25ns':>10}  log")
    for r in results:
        if not r["ok"]:
            print(f"{r['variant']:<10}  FAILED  {r['log']}")
            continue
        t50 = r.get("t50", 0) * 1e9
        t90 = r.get("t90", 0) * 1e9
        v25 = r.get("v_settle", 0)
        print(f"{r['variant']:<10} {t50:>10.3f} {t90:>10.3f} {v25:>10.4f}  {r['log']}")
    return 0 if all(r["ok"] for r in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
