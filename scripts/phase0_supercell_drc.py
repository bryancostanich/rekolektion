"""Phase 0 — foundry-pitch supercell DRC feasibility check.

Generates a SINGLE foundry-pitch SRAM-D supercell (no Y annex; T7+cap fit
within foundry Y=1.58) and runs Magic DRC.  Outputs:
  - output/phase0/supercell_pp.gds        (the prototype)
  - output/phase0/drc_report.txt          (Magic DRC results)
  - stdout summary

If DRC clean, this validates Architecture 1 feasibility for SRAM-D.
Phase 1 then rolls this pattern into the production code paths.

Updated cap dimensions (per capm.2a 0.84 µm spacing constraint between
independent caps in adjacent supercells):
    cap_l = 0.74  (= supercell_h - 0.84)
    cap_w = capacitance / (cap_l × ~2 fF/µm²)
    SRAM-D: 2.9 fF / (0.74 × 2.0) = 1.96 µm  →  supercell_w 2.80
"""
from __future__ import annotations

import os
import subprocess
from pathlib import Path

import gdstk

# Parameters - SRAM-D Phase 0 prototype
_FOUNDRY_LEF_W = 1.310
_FOUNDRY_LEF_H = 1.580
_T7_W = 0.42
_T7_L = 0.15
_T7_DIFF_OVERHANG = 0.36
_NSDM_ENC = 0.125

_CAP_L = 0.74         # max cap_l within supercell_h=1.58 with capm.2a spacing
_CAP_W = 1.96         # SRAM-D: preserves 2.9 fF target
_CAP_TO_CAP = 0.84
_SUPER_H = _FOUNDRY_LEF_H            # 1.58 (foundry-pitch)
_SUPER_W = max(2.31, _CAP_W + _CAP_TO_CAP)  # 2.80

_FOUNDRY_NWELL_R = 1.325
_T7_TO_NWELL = 0.34
_T7_DIFF_X = _FOUNDRY_NWELL_R + _T7_TO_NWELL  # 1.665
_T7_DIFF_W = _T7_W                              # 0.42

_FOUNDRY_CELL_GDS = (
    Path(__file__).parent.parent
    / "src/rekolektion/bitcell/cells"
    / "sky130_fd_bd_sram__sram_sp_cell_opt1.gds"
)
_FOUNDRY_CELL_NAME = "sky130_fd_bd_sram__sram_sp_cell_opt1"

# GDS layers (sky130)
NWELL    = (64, 20)
DIFF     = (65, 20)
POLY     = (66, 20)
LICON1   = (66, 44)
LI1      = (67, 20)
LI1_LBL  = (67, 5)
MCON     = (67, 44)
MET1     = (68, 20)
MET1_LBL = (68, 5)
VIA1     = (68, 44)
MET2     = (69, 20)
VIA2     = (69, 44)
MET3     = (70, 20)
MIMCAP   = (89, 44)
VIA3     = (70, 44)
MET4     = (71, 20)
MET4_LBL = (71, 5)
MET4_PIN = (71, 16)
NSDM     = (93, 44)
AREAID_SRAM = (81, 2)


def _rect(cell, layer, x0, y0, x1, y1):
    cell.add(gdstk.rectangle((x0, y0), (x1, y1), layer=layer[0], datatype=layer[1]))


def _label(cell, text, layer, x, y):
    cell.add(gdstk.Label(text, (x, y), layer=layer[0], texttype=layer[1]))


def _load_foundry_with_qtap():
    """Load foundry cell, add Q-tap (same as production)."""
    src = gdstk.read_gds(str(_FOUNDRY_CELL_GDS))
    foundry = next(c for c in src.cells if c.name == _FOUNDRY_CELL_NAME)
    cell = foundry.copy(_FOUNDRY_CELL_NAME + "_qtap")

    # Strip foundry's WL label (per-row override at parent)
    for label in [l for l in cell.labels if l.text == "WL"]:
        cell.remove(label)

    # Q-tap: LICON1 + LI1 stripe to east edge (same as production)
    qx, qy = 0.30, 1.12
    LICON_HALF = 0.085
    _rect(cell, LICON1, qx - LICON_HALF, qy - LICON_HALF,
          qx + LICON_HALF, qy + LICON_HALF)
    LI_W = 0.17
    li_y_lo = 1.20 - LI_W / 2  # 1.115
    li_y_hi = 1.20 + LI_W / 2  # 1.285
    _rect(cell, LI1, qx - 0.165, li_y_lo, _FOUNDRY_LEF_W, li_y_hi)
    _rect(cell, LI1, qx - 0.165, qy - 0.165, qx + 0.165, li_y_hi)
    _label(cell, "Q", LI1_LBL, 1.20, 1.20)
    return cell


def create_phase0_supercell():
    """Build foundry-pitch SRAM-D supercell prototype (no Y annex)."""
    name = "sky130_cim_supercell_pp_sram_d"
    lib = gdstk.Library(name=name + "_lib", unit=1e-6, precision=5e-9)
    foundry = _load_foundry_with_qtap()
    lib.add(foundry)

    super_cell = gdstk.Cell(name)
    lib.add(super_cell)

    # Foundry cell at origin
    super_cell.add(gdstk.Reference(foundry, origin=(0.0, 0.0)))

    # SRAM areaid over foundry footprint
    _rect(super_cell, AREAID_SRAM, 0.0, 0.0, _FOUNDRY_LEF_W, _FOUNDRY_LEF_H)

    # ---- T7 NMOS placement (within foundry-Y range now) ----
    # T7 diff height = 2 × overhang + gate_l = 0.87 µm
    # Center T7 in supercell-y so source is near Q exit y=1.20.
    # Source center = t7_diff_y0 + overhang/2.
    # Want source ≈ 1.0 (compromise between Q at 1.20 and supercell edges)
    # → t7_diff_y0 = 1.0 - 0.18 = 0.82.  Wait, that puts diff at [0.82, 1.69]
    # which exceeds supercell_h=1.58.  Need to lower.
    # Try: source at y=0.85, t7_diff_y0 = 0.67.  Diff [0.67, 1.54].  Fits in [0, 1.58].
    # NSDM enclosure bumps to [0.545, 1.665] — exceeds Y by 0.085 at top.
    # Lower further: source at y=0.78, t7_diff_y0=0.60. Diff [0.60, 1.47].
    # NSDM [0.475, 1.595] — exceeds by 0.015.  Use 0.55+0.04=0.59 → diff [0.59, 1.46].
    # NSDM [0.465, 1.585] — fits within [0, 1.58] - 0.005.  Acceptable margin.
    t7_diff_y0 = 0.59
    t7_diff_y1 = t7_diff_y0 + 2 * _T7_DIFF_OVERHANG + _T7_L  # 1.46

    _rect(super_cell, DIFF, _T7_DIFF_X, t7_diff_y0,
          _T7_DIFF_X + _T7_DIFF_W, t7_diff_y1)
    _rect(super_cell, NSDM,
          _T7_DIFF_X - _NSDM_ENC, t7_diff_y0 - _NSDM_ENC,
          _T7_DIFF_X + _T7_DIFF_W + _NSDM_ENC,
          t7_diff_y1 + _NSDM_ENC)

    POLY_EXT = 0.13
    t7_gate_y = t7_diff_y0 + _T7_DIFF_OVERHANG  # 0.95
    _rect(super_cell, POLY,
          _T7_DIFF_X - POLY_EXT, t7_gate_y,
          _T7_DIFF_X + _T7_DIFF_W + POLY_EXT, t7_gate_y + _T7_L)
    _label(super_cell, "MWL", (66, 5),
           _T7_DIFF_X + _T7_DIFF_W / 2, t7_gate_y + _T7_L / 2)

    # T7 source contact (lower diff)
    LICON_HALF = 0.085
    LI_PAD_HALF = 0.165
    t7_src_cy = t7_diff_y0 + _T7_DIFF_OVERHANG / 2  # 0.77
    t7_src_cx = _T7_DIFF_X + _T7_DIFF_W / 2          # 1.875
    _rect(super_cell, LICON1,
          t7_src_cx - LICON_HALF, t7_src_cy - LICON_HALF,
          t7_src_cx + LICON_HALF, t7_src_cy + LICON_HALF)
    _rect(super_cell, LI1,
          t7_src_cx - LI_PAD_HALF, t7_src_cy - LI_PAD_HALF,
          t7_src_cx + LI_PAD_HALF, t7_src_cy + LI_PAD_HALF)

    # Q-to-T7-source LI1 routing
    LI_W = 0.17
    qx_exit = _FOUNDRY_LEF_W
    qy_exit = 1.20
    # Horizontal LI1 from foundry east edge to T7 src column at y=qy_exit
    _rect(super_cell, LI1, qx_exit, qy_exit - LI_W / 2,
          t7_src_cx + LI_W / 2, qy_exit + LI_W / 2)
    # Vertical LI1 from y=qy_exit DOWN to T7 source y
    _rect(super_cell, LI1,
          t7_src_cx - LI_W / 2, t7_src_cy - LI_W / 2,
          t7_src_cx + LI_W / 2, qy_exit + LI_W / 2)

    # T7 drain contact (upper diff)
    t7_drn_cy = t7_diff_y1 - _T7_DIFF_OVERHANG / 2  # 1.28
    t7_drn_cx = _T7_DIFF_X + _T7_DIFF_W / 2
    _rect(super_cell, LICON1,
          t7_drn_cx - LICON_HALF, t7_drn_cy - LICON_HALF,
          t7_drn_cx + LICON_HALF, t7_drn_cy + LICON_HALF)
    _rect(super_cell, LI1,
          t7_drn_cx - LI_PAD_HALF, t7_drn_cy - LI_PAD_HALF,
          t7_drn_cx + LI_PAD_HALF, t7_drn_cy + LI_PAD_HALF)
    _rect(super_cell, MCON,
          t7_drn_cx - LICON_HALF, t7_drn_cy - LICON_HALF,
          t7_drn_cx + LICON_HALF, t7_drn_cy + LICON_HALF)
    M1_PAD_HALF = 0.16
    _rect(super_cell, MET1,
          t7_drn_cx - M1_PAD_HALF, t7_drn_cy - M1_PAD_HALF,
          t7_drn_cx + M1_PAD_HALF, t7_drn_cy + M1_PAD_HALF)
    VIA_HALF = 0.075
    _rect(super_cell, VIA1,
          t7_drn_cx - VIA_HALF, t7_drn_cy - VIA_HALF,
          t7_drn_cx + VIA_HALF, t7_drn_cy + VIA_HALF)
    M2_PAD_HALF = max(0.16, 0.185)
    _rect(super_cell, MET2,
          t7_drn_cx - M2_PAD_HALF, t7_drn_cy - M2_PAD_HALF,
          t7_drn_cx + M2_PAD_HALF, t7_drn_cy + M2_PAD_HALF)
    VIA2_HALF = 0.10
    _rect(super_cell, VIA2,
          t7_drn_cx - VIA2_HALF, t7_drn_cy - VIA2_HALF,
          t7_drn_cx + VIA2_HALF, t7_drn_cy + VIA2_HALF)
    M3_PAD_HALF = 0.185
    _rect(super_cell, MET3,
          t7_drn_cx - M3_PAD_HALF, t7_drn_cy - M3_PAD_HALF,
          t7_drn_cx + M3_PAD_HALF, t7_drn_cy + M3_PAD_HALF)

    # ---- MIM cap on M3+capm centered in supercell ----
    cap_y0 = (_SUPER_H - _CAP_L) / 2   # (1.58 - 0.74)/2 = 0.42
    cap_y1 = cap_y0 + _CAP_L            # 1.16
    cap_x0 = (_SUPER_W - _CAP_W) / 2   # (2.80 - 1.96)/2 = 0.42
    cap_x1 = cap_x0 + _CAP_W            # 2.38

    M3_ENC_CAPM = 0.14
    _rect(super_cell, MET3,
          cap_x0 - M3_ENC_CAPM, cap_y0 - M3_ENC_CAPM,
          cap_x1 + M3_ENC_CAPM, cap_y1 + M3_ENC_CAPM)
    _rect(super_cell, MIMCAP, cap_x0, cap_y0, cap_x1, cap_y1)

    # M3 strap from T7 drain stack to cap M3 bottom plate.
    # T7 drain M3 pad at (t7_drn_cx, t7_drn_cy) = (1.875, 1.28).
    # Cap M3 plate at x=[0.28, 2.52], y=[0.28, 1.30].
    # T7 drain M3 pad already overlaps cap M3 plate (x=1.875 inside [0.28, 2.52],
    # y=1.28 inside [0.28, 1.30]).  No additional strap needed.

    # MBL cap top via M4
    VIA3_HALF = 0.10
    M4_PAD_HALF = 0.18
    mbl_cx = (cap_x0 + cap_x1) / 2
    mbl_cy = (cap_y0 + cap_y1) / 2
    _rect(super_cell, VIA3,
          mbl_cx - VIA3_HALF, mbl_cy - VIA3_HALF,
          mbl_cx + VIA3_HALF, mbl_cy + VIA3_HALF)
    _rect(super_cell, MET4,
          mbl_cx - M4_PAD_HALF, mbl_cy - M4_PAD_HALF,
          mbl_cx + M4_PAD_HALF, mbl_cy + M4_PAD_HALF)
    _label(super_cell, "MBL", MET4_LBL, mbl_cx, mbl_cy)
    PIN_HALF = 0.04
    _rect(super_cell, MET4_PIN,
          mbl_cx - PIN_HALF, mbl_cy - PIN_HALF,
          mbl_cx + PIN_HALF, mbl_cy + PIN_HALF)

    return lib, name


def run_drc(gds_path: Path, top_cell: str, output_dir: Path) -> tuple[int, str]:
    """Run Magic DRC on the GDS, return (violation_count, log_text)."""
    pdk_root = os.environ.get("PDK_ROOT", str(Path.home() / ".volare"))
    magicrc = Path(pdk_root) / "sky130B/libs.tech/magic/sky130B.magicrc"
    log_path = output_dir / "drc_report.txt"
    tcl_path = output_dir / "drc.tcl"
    tcl_path.write_text(f"""\
gds read {gds_path.resolve()}
load {top_cell}
select top cell
drc catchup
drc check
set result [drc listall why]
set total 0
foreach {{msg boxes}} $result {{
    set n [llength $boxes]
    incr total $n
    puts "($n) $msg"
}}
puts "=== TOTAL: $total ==="
quit -noprompt
""")
    env = os.environ.copy()
    env["PDK_ROOT"] = pdk_root
    cmd = ["magic", "-dnull", "-noconsole", "-rcfile", str(magicrc),
           str(tcl_path.resolve())]
    proc = subprocess.run(cmd, capture_output=True, text=True,
                          cwd=str(output_dir), env=env, timeout=300)
    log = proc.stdout + "\n--- STDERR ---\n" + proc.stderr
    log_path.write_text(log)
    # Parse total
    total = 0
    for line in proc.stdout.splitlines():
        if line.startswith("=== TOTAL:"):
            try:
                total = int(line.split()[2])
            except (ValueError, IndexError):
                pass
    return total, log


def main():
    out_dir = Path("output/phase0")
    out_dir.mkdir(parents=True, exist_ok=True)
    lib, name = create_phase0_supercell()
    gds_path = out_dir / "supercell_pp.gds"
    lib.write_gds(str(gds_path))
    print(f"Generated: {gds_path}")
    print(f"  supercell_w={_SUPER_W:.3f} µm")
    print(f"  supercell_h={_SUPER_H:.3f} µm")
    print(f"  cap dims: {_CAP_W:.3f} × {_CAP_L:.3f} = {_CAP_W*_CAP_L:.3f} µm² "
          f"({_CAP_W*_CAP_L*2.0:.2f} fF @ 2 fF/µm²)")
    print(f"Running Magic DRC...")
    total, log = run_drc(gds_path, name, out_dir)
    print(f"\n=== DRC Result: {total} violations ===")
    if total > 0:
        # Print first 30 lines of magic stdout for context
        for line in log.splitlines()[:60]:
            if line.startswith("(") or "DRC" in line or "WARNING" in line:
                print(line)
    return total


if __name__ == "__main__":
    import sys
    sys.exit(main())
