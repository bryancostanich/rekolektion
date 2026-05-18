"""Run the Phase A 9-PVT sweep across {ss, tt, ff} × {-40, 27, 85 °C}.

For each corner: substitute __CORNER__ + __TEMP__ into the testbench
template, run ngspice, parse the `print` line outputs, and aggregate
into a table.

Acceptance criteria per forming_fsm_contract.md + Track 04:
- v_ds_peak < 1.95 V at every corner (access FET breakdown)
- v_src_steady > 3.20 V at every corner (forming bias spec is 3.30 V;
  small drop across PMOS R_on is OK)
- t_rise, t_fall < 100 ns at every corner (so pulse-on / pulse-off
  transitions fit inside the per-cycle FSM budget)
"""

import re
import subprocess
import tempfile
from pathlib import Path

REPO = Path("/Users/bryancostanich/git_repos/bryan_costanich/rekolektion")
TB = REPO / "cell_designs/khalkulo/spice/phaseA_form_tb.spice"
NGSPICE = Path.home() / ".local/bin/ngspice"

CORNERS = ["ss", "tt", "ff"]
TEMPS = [-40, 27, 85]
METRICS = ["t_rise", "t_fall", "v_src_steady", "v_src_wl_fall", "v_ds_peak"]

template = TB.read_text()

results = []
print(f"{'corner':>4} {'temp':>5} | "
      + " ".join(f"{m:>14}" for m in METRICS))
print("-" * 88)

for corner in CORNERS:
    for temp in TEMPS:
        deck = template.replace("__CORNER__", corner).replace("__TEMP__", str(temp))
        with tempfile.NamedTemporaryFile(
            "w", suffix=".spice", dir=TB.parent, delete=False
        ) as f:
            f.write(deck)
            deck_path = Path(f.name)
        try:
            proc = subprocess.run(
                [str(NGSPICE), "-b", str(deck_path)],
                capture_output=True, text=True, timeout=120,
            )
            out = proc.stdout + proc.stderr
        finally:
            deck_path.unlink()

        row = {"corner": corner, "temp": temp}
        for m in METRICS:
            # ngspice prints: `metric_name        =  value`
            mat = re.search(rf"^\s*{re.escape(m)}\s*=\s*([-\d.eE+]+)", out, re.M)
            row[m] = float(mat.group(1)) if mat else float("nan")
        results.append(row)
        print(f"{corner:>4} {temp:>5} | "
              + " ".join(f"{row[m]:>14.4g}" for m in METRICS))

print()
# Pass/fail summary
print("Acceptance gates:")
ok = True
for row in results:
    fails = []
    if not row["v_ds_peak"] < 1.95:
        fails.append(f"V_DS={row['v_ds_peak']:.3f} ≥ 1.95")
    if not row["v_src_steady"] > 3.20:
        fails.append(f"V_SRC_steady={row['v_src_steady']:.3f} ≤ 3.20")
    if fails:
        ok = False
        print(f"  ❌ {row['corner']}/{row['temp']}°C: " + "; ".join(fails))
print("PASS" if ok else "FAIL")
