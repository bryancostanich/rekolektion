"""Flat-extract LVS for cim_reram_bl_swap_mux.

The hierarchical extract triggers Magic's port-promotion bug
(rekolektion CLAUDE.md trap): the W=25 MV PMOS primitive's autonamed
nwell node merges with the parent's gate label because the W25 PMOS
primitive doesn't expose its nwell as a labeled port. Flat-extract
collapses the SRef, then `port makeall` promotes only the labels
attached to actual top-level geometry — no auto-port misattribution.

Usage:
    .venv/bin/python scripts/lvs_flat_bl_swap_mux.py
"""
import os
import subprocess
import tempfile
from pathlib import Path

RKT = Path("cell_designs/reram_drv/cim_reram_bl_swap_mux.rkt").resolve()
SCH = Path("/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/"
           "source/cim/reram/spice/cim_reram_drv_phaseB_bl.spice").resolve()
CELL = "cim_reram_bl_swap_mux"
PDK_ROOT = Path(os.environ.get("PDK_ROOT", str(Path.home() / ".volare")))

# Step 1: .rkt → GDS via viz CLI to-gds
out_dir = Path(tempfile.mkdtemp(prefix="lvs_flat_bl_swap_"))
gds_path = out_dir / f"{CELL}.gds"
viz_proj = Path("tools/viz/src/Rekolektion.Viz.Cli").resolve()
subprocess.run(
    ["dotnet", "run", "--project", str(viz_proj), "--",
     "to-gds", str(RKT), str(gds_path)],
    check=True,
)
print(f"GDS (hier): {gds_path}")

# Step 1b: physically flatten the GDS so Magic sees a single cell with
# no SRefs. This bypasses Magic's `flatten -doinplace` quirks and
# guarantees no hierarchical extraction step can run.
import gdstk
lib = gdstk.read_gds(str(gds_path))
top = next(c for c in lib.cells if c.name == CELL)
top.flatten()        # inline all polygon/path/label content from SRefs
# Drop every other cell from the library — Magic will see only one cell.
flat_lib = gdstk.Library(name="flat")
flat_lib.add(top)
flat_gds = out_dir / f"{CELL}_flat.gds"
flat_lib.write_gds(str(flat_gds))
gds_path = flat_gds
print(f"GDS (flat): {gds_path}")

# Step 2: Magic — flatten then extract with port makeall
extracted = out_dir / f"{CELL}_flat_extracted.spice"
magicrc = PDK_ROOT / "sky130B" / "libs.tech" / "magic" / "sky130B.magicrc"
tcl = f"""\
gds read {gds_path}
load {CELL}
select top cell
puts "=== Children of top: [cellname list children {CELL}] ==="
port makeall
extract all
ext2spice lvs
ext2spice -o {extracted}
quit -noprompt
"""
tcl_path = out_dir / "extract_flat.tcl"
tcl_path.write_text(tcl)
env = os.environ.copy()
env["PDK_ROOT"] = str(PDK_ROOT)
mr = subprocess.run(["magic", "-dnull", "-noconsole", "-rcfile", str(magicrc),
                     str(tcl_path)], env=env, cwd=out_dir,
                    capture_output=True, text=True)
print("--- Magic stdout (tail) ---")
print("\n".join(mr.stdout.splitlines()[-40:]))
if mr.returncode != 0:
    print("--- Magic stderr ---")
    print(mr.stderr[-2000:])

# Step 3: netgen LVS
log_path = out_dir / "lvs_flat.log"
comp = out_dir / "comp_flat.out"
ng_tcl = out_dir / "netgen.tcl"
ng_tcl.write_text(f"""\
set f_layout {{{extracted} {CELL}}}
set f_sch    {{{SCH} {CELL}}}
lvs $f_layout $f_sch {PDK_ROOT}/sky130B/libs.tech/netgen/sky130B_setup.tcl {comp}
""")
ngr = subprocess.run(["netgen", "-batch", "source", str(ng_tcl)],
                     env=env, cwd=out_dir, capture_output=True, text=True)
log_path.write_text(ngr.stdout + "\n--- STDERR ---\n" + ngr.stderr)
print("\n--- netgen (tail) ---")
print("\n".join(ngr.stdout.splitlines()[-30:]))
match = ("Netlists match uniquely" in ngr.stdout
         or "Cells have no differences" in ngr.stdout)
print(f"\n=== FLAT LVS: {'MATCH' if match else 'MISMATCH'} ===")
print(f"extracted: {extracted}")
print(f"comp:      {comp}")
