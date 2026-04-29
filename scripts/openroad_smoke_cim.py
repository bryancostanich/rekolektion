"""OpenROAD integration smoke test for CIM SRAM macros.

For each variant:
1. Synthesize a wrapper Verilog that instantiates the macro and pulls
   every macro pin to a top-level port, via Yosys.
2. Run OpenROAD: read tech LEF + std-cell LEF + macro LEF + Liberty,
   link the netlist, floorplan a die that fits the macro + halo, place
   the macro, place I/O pins, run global + detailed route.
3. Report pass/fail per variant.

Catches integration issues that the isolated format checks (parse-test
LEF, parse-test Liberty, parse-test Verilog) can't see — e.g. LEF OBS
that blocks routing, Liberty pin definitions that don't match LEF,
power/ground net classification mismatches.

Usage::

    python3 scripts/openroad_smoke_cim.py [SRAM-A SRAM-B SRAM-C SRAM-D]

Defaults to SRAM-D only because the 256-row variants take a long time
to detail-route a wrapper around.
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams


_PDK_VERSION = "7519dfb04400f224f140749cda44ee7de6f5e095"
_PROC_TOP = Path(
    f"~/.volare/volare/sky130/versions/{_PDK_VERSION}/sky130B/libs.ref/sky130_fd_sc_hd"
).expanduser()
_OUT_ROOT = Path("output/openroad_smoke")


def _wrapper_verilog(top: str, macro: str, rows: int, cols: int) -> str:
    return f"""\
// Smoke-test wrapper for {macro}.  Pulls every macro pin to a
// top-level port so OpenROAD has to place + route the connection
// during the integration smoke test.
module {top} (
    input  [{rows - 1}:0] mwl_en,
    input         mbl_pre,
    input         vbias,
    inout         vref,
    inout         vpwr,
    inout         vgnd,
    output [{cols - 1}:0] mbl_out
);
    {macro} u_macro (
        .MWL_EN  (mwl_en),
        .MBL_OUT (mbl_out),
        .MBL_PRE (mbl_pre),
        .VREF    (vref),
        .VBIAS   (vbias),
        .VPWR    (vpwr),
        .VGND    (vgnd)
    );
endmodule
"""


def _yosys_synth(work: Path, top: str, macro: str, bb_v: Path) -> Path:
    wrapper = work / "wrapper.v"
    netlist = work / f"{top}.netlist.v"
    ys = work / "syn.ys"
    ys.write_text(
        f"read_verilog -sv {bb_v}\n"
        f"read_verilog -sv {wrapper}\n"
        f"hierarchy -check -top {top}\n"
        f"proc; opt\n"
        f"write_verilog -noattr {netlist}\n"
    )
    res = subprocess.run(["yosys", "-q", str(ys)], capture_output=True, text=True)
    if res.returncode != 0:
        raise RuntimeError(f"yosys failed:\n{res.stderr}")
    return netlist


def _openroad_tcl(work: Path, macro_name: str, macro_dir: Path,
                  netlist: Path, top: str,
                  die_w: float, die_h: float,
                  macro_x: float, macro_y: float) -> Path:
    tcl = work / "smoke.tcl"
    # Halo of 10 µm around the macro, core inset of 10 µm from die.
    tcl.write_text(f"""\
read_lef {_PROC_TOP}/techlef/sky130_fd_sc_hd.tlef
read_lef {_PROC_TOP}/lef/sky130_fd_sc_hd.lef
read_lef {macro_dir}/{macro_name}.lef

read_liberty {_PROC_TOP}/lib/sky130_fd_sc_hd__tt_025C_1v80.lib
read_liberty {macro_dir}/{macro_name}.lib

read_verilog {netlist}
link_design {top}

initialize_floorplan \\
    -die_area  "0 0 {die_w} {die_h}" \\
    -core_area "10 10 {die_w - 10} {die_h - 10}" \\
    -site unithd

make_tracks li1  -x_offset 0.230 -x_pitch 0.460 -y_offset 0.170 -y_pitch 0.340
make_tracks met1 -x_offset 0.170 -x_pitch 0.340 -y_offset 0.170 -y_pitch 0.340
make_tracks met2 -x_offset 0.230 -x_pitch 0.460 -y_offset 0.230 -y_pitch 0.460
make_tracks met3 -x_offset 0.340 -x_pitch 0.680 -y_offset 0.340 -y_pitch 0.680
make_tracks met4 -x_offset 0.460 -x_pitch 0.920 -y_offset 0.460 -y_pitch 0.920
make_tracks met5 -x_offset 1.700 -x_pitch 3.400 -y_offset 1.700 -y_pitch 3.400

set blk [ord::get_db_block]
set vpwr_net [$blk findNet "vpwr"]
set vgnd_net [$blk findNet "vgnd"]
$vpwr_net setSpecial
$vpwr_net setSigType POWER
$vgnd_net setSpecial
$vgnd_net setSigType GROUND

set inst [$blk findInst "u_macro"]
$inst setOrigin {int(macro_x * 1000)} {int(macro_y * 1000)}
$inst setPlacementStatus PLACED

place_pins -hor_layers met3 -ver_layers met2
set_routing_layers -signal met1-met5
set_thread_count 4

if {{[catch {{global_route}} err]}} {{
    puts "GLOBAL_ROUTE_FAILED: $err"
    exit 1
}}
puts "GLOBAL_ROUTE_OK"

if {{[catch {{detailed_route -bottom_routing_layer met1 -top_routing_layer met5 \\
                            -or_seed 42 -verbose 0}} err]}} {{
    puts "DETAILED_ROUTE_FAILED: $err"
    exit 1
}}
puts "DETAILED_ROUTE_OK"

write_def {work}/{top}.routed.def

set unc 0
foreach iterm [$inst getITerms] {{
    set net [$iterm getNet]
    if {{$net == "NULL"}} {{ incr unc }}
}}
puts "UNCONNECTED_PINS: $unc"
puts "SMOKE_TEST_DONE"
exit 0
""")
    return tcl


def _smoke_one(variant: str) -> tuple[str, bool, str]:
    p = CIMMacroParams.from_variant(variant)
    macro = p.top_cell_name
    macro_dir = Path("output/cim_macros") / macro
    bb_v = macro_dir / f"{macro}_bb.v"
    if not bb_v.exists():
        return (variant, False, f"missing {bb_v} — run generate_cim_production.py first")

    work = _OUT_ROOT / macro
    work.mkdir(parents=True, exist_ok=True)
    top = f"{macro}_smoke"

    (work / "wrapper.v").write_text(_wrapper_verilog(top, macro, p.rows, p.cols))
    netlist = _yosys_synth(work, top, macro, bb_v.resolve())

    # Die: macro size + 40 µm halo on each side.  Read the size out of
    # the LEF rather than the params dataclass — `p.macro_width` is 0
    # until `assemble_cim()` populates it, and we don't want to redo
    # the assembly here.
    lef = macro_dir / f"{macro}.lef"
    macro_w = macro_h = 0.0
    for line in lef.read_text().splitlines():
        s = line.strip()
        if s.startswith("SIZE "):
            parts = s.split()
            macro_w = float(parts[1])
            macro_h = float(parts[3])
            break
    if macro_w == 0:
        return (variant, False, f"could not parse SIZE from {lef}")
    die_w = macro_w + 40.0
    die_h = macro_h + 40.0
    macro_x = 20.0
    macro_y = 20.0

    tcl = _openroad_tcl(work, macro, macro_dir.resolve(), netlist.resolve(), top,
                        die_w, die_h, macro_x, macro_y)

    log = work / "openroad.log"
    env = os.environ.copy()
    if "/Users/bryancostanich/.local/bin" not in env.get("PATH", ""):
        env["PATH"] = "/Users/bryancostanich/.local/bin:" + env.get("PATH", "")
    res = subprocess.run(
        ["openroad", "-no_init", "-exit", str(tcl.resolve())],
        capture_output=True, text=True, timeout=3600, env=env,
    )
    log.write_text(res.stdout + "\n--- STDERR ---\n" + res.stderr)
    ok = "SMOKE_TEST_DONE" in res.stdout and "UNCONNECTED_PINS: 0" in res.stdout \
         and "DETAILED_ROUTE_OK" in res.stdout
    return (variant, ok, str(log))


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("variants", nargs="*", help="Variants (default: SRAM-D)")
    args = ap.parse_args(argv[1:])
    variants = args.variants if args.variants else ["SRAM-D"]
    for v in variants:
        if v not in CIM_VARIANTS:
            raise SystemExit(f"Unknown variant {v!r}.")
    if not shutil.which("openroad"):
        raise SystemExit("openroad not on PATH")
    if not shutil.which("yosys"):
        raise SystemExit("yosys not on PATH")
    results: list[tuple[str, bool, str]] = []
    for v in variants:
        print(f"[{v}] running OpenROAD smoke test...", flush=True)
        results.append(_smoke_one(v))
    print("\n=== OpenROAD Integration Smoke Summary ===")
    print(f"{'variant':<10} {'result':>8}  log")
    for variant, ok, log in results:
        print(f"{variant:<10} {('PASS' if ok else 'FAIL'):>8}  {log}")
    return 0 if all(ok for _, ok, _ in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
