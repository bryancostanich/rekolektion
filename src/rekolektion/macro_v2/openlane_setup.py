"""Prepare an OpenLane run directory for the v2 macro flow (Option Y).

Emits all inputs OpenLane needs to place + route the SRAM macro as
a single hardened-macro flow where the rekolektion sub-blocks are
pre-placed macros with their own LEF+GDS, and OpenROAD routes
signals between them at the top level.

Inputs produced:
  - {run_dir}/
      src/{top_cell_name}.v                — top Verilog netlist
      macros/{sub_block}.gds, .lef         — sub-block abstracts
      macros/{top_cell_name}_placeholder.* — full-macro placement GDS
                                              (pre-routed WL/BL/PDN)
      macro_placement.cfg                   — sub-block x/y/orient
      config.json                           — OpenLane config

Design philosophy:
  - The assembler already produces a single GDS with every sub-block
    placed AND pre-routed signals (WL, BL/BR, PDN).  We split that
    output by handing OpenLane the sub-block GDSes separately and
    letting it re-assemble them at the floorplan positions.
  - The "pre-routed" top-level wires (WL/BL/BR/PDN) are carried as
    top-level geometry in the macro's own GDS, which OpenLane
    preserves and doesn't re-route.
"""
from __future__ import annotations

import json
import shutil
from dataclasses import dataclass
from pathlib import Path

import gdstk

from rekolektion.macro_v2.assembler import (
    MacroV2Params,
    assemble,
    build_floorplan,
)
from rekolektion.macro_v2.verilog_generator import generate_verilog
from rekolektion.macro_v2.sub_lef import (
    generate_sub_block_lefs,
    pin_escape_rects,
    power_stub_positions,
    _POWER_STUB_W,
    _POWER_STUB_LEN,
)


# Mapping from floorplan block name -> gdstk cell name (the assembler
# renames each sub-block during placement to include the tag).
def _sub_block_cell_names(p: MacroV2Params) -> dict[str, str]:
    tag = f"m{p.mux_ratio}_{p.words}x{p.bits}"
    return {
        "array":         f"sram_array_{tag}",
        "precharge":     f"pre_{tag}",
        "col_mux":       f"mux_{tag}",
        "sense_amp":     f"sa_{tag}",
        "write_driver":  f"wd_{tag}",
        "row_decoder":   f"row_decoder_{tag}",
        "wl_driver":     f"wl_driver_{tag}",
        "control_logic": f"ctrl_logic_{tag}",
    }


# Map floorplan key -> logical "macros/" filename prefix
_FP_TO_LEF_KEY = {
    "array":         "sram_array",
    "precharge":     "pre",
    "col_mux":       "mux",
    "sense_amp":     "sa",
    "write_driver":  "wd",
    "row_decoder":   "row_decoder",
    "wl_driver":     "wl_driver",
    "control_logic": "ctrl_logic",
}


def _write_sub_block_gds(
    assembled_lib: gdstk.Library,
    cell_name: str,
    output_path: Path,
    pin_escape_rects: list[tuple[float, float, float, float]] | None = None,
) -> None:
    """Extract a single sub-block's cell (and its dependents) from the
    assembled library and write it as a standalone GDS.

    Adds top-level met2 VPWR/VGND strips matching the LEF abstracts
    so OpenROAD's PDN generator sees real metal at the power pin
    locations it was told to expect.
    """
    out_lib = gdstk.Library(name=f"{cell_name}_lib")
    cell = next(c for c in assembled_lib.cells if c.name == cell_name)
    # Walk dependents so all referenced cells come along.
    queue = [cell]
    seen: set[str] = set()
    cell_copies: dict[str, gdstk.Cell] = {}
    while queue:
        c = queue.pop()
        if c.name in seen:
            continue
        seen.add(c.name)
        copy = c.copy(c.name)
        cell_copies[c.name] = copy
        out_lib.add(copy)
        for ref in c.references:
            if ref.cell.name not in seen:
                queue.append(ref.cell)

    # Power geometry for OpenROAD PDN:
    #   - Full-width met2 rails at the block's top and bottom (these
    #     are the "real" internal power rails).
    #   - Met3 stubs at each LEF power-pin x, stacked via2 down to
    #     met2.  The met3 stubs are what OpenROAD's PDN contacts;
    #     the via2 stacks make sure every stub is electrically tied
    #     to the full-width met2 rail so stray fragments can't form.
    top_copy = cell_copies[cell_name]
    bb = top_copy.bounding_box()
    if bb is not None:
        (x0, y0), (x1, y1) = bb
        w = x1 - x0
        h = y1 - y0
        MET2 = (69, 20)
        MET3 = (70, 20)
        VIA2 = (69, 44)  # via between met2 and met3
        # Full-width met2 rails
        top_copy.add(gdstk.rectangle(
            (x0, y0), (x1, min(y1, y0 + 1.0)),
            layer=MET2[0], datatype=MET2[1],
        ))
        top_copy.add(gdstk.rectangle(
            (x0, max(y0, y1 - 1.0)), (x1, y1),
            layer=MET2[0], datatype=MET2[1],
        ))
        # Per-stub met3 + via2 stacks at the same positions as the
        # LEF VPWR/VGND pins.  Positions are in *sub-block-local*
        # coordinates; add x0 / y0 to translate to the GDS frame.
        for cx_local in power_stub_positions(w):
            cx = x0 + cx_local
            for at_top in (True, False):
                if at_top:
                    sy1 = max(y0, y1 - _POWER_STUB_LEN)
                    sy2 = y1
                else:
                    sy1 = y0
                    sy2 = min(y1, y0 + _POWER_STUB_LEN)
                sx1 = cx - _POWER_STUB_W / 2
                sx2 = cx + _POWER_STUB_W / 2
                # met3 stub
                top_copy.add(gdstk.rectangle(
                    (sx1, sy1), (sx2, sy2),
                    layer=MET3[0], datatype=MET3[1],
                ))
                # via2 cut (centered at the stub centre, inside the
                # overlap between met3 stub and met2 rail)
                via_y_c = y1 - 0.5 if at_top else y0 + 0.5
                top_copy.add(gdstk.rectangle(
                    (cx - 0.10, via_y_c - 0.10),
                    (cx + 0.10, via_y_c + 0.10),
                    layer=VIA2[0], datatype=VIA2[1],
                ))

    # Add met1 landing pads for each signal pin so OpenROAD's router
    # can land on met1 at each declared LEF pin.  Each pad is drawn
    # at the same (enlarged) RECT used in the LEF, with a stack of
    # li1 + mcon + met1 so the existing li1 pin geometry beneath is
    # electrically connected to the met1 pad.
    if pin_escape_rects:
        # layer ids
        LI1 = (67, 20)
        MCON = (67, 44)
        MET1 = (68, 20)
        for x1p, y1p, x2p, y2p in pin_escape_rects:
            top_copy.add(gdstk.rectangle(
                (x1p, y1p), (x2p, y2p), layer=LI1[0], datatype=LI1[1],
            ))
            # mcon array: minimum 0.17 x 0.17 cut
            mcx = (x1p + x2p) / 2
            mcy = (y1p + y2p) / 2
            top_copy.add(gdstk.rectangle(
                (mcx - 0.085, mcy - 0.085), (mcx + 0.085, mcy + 0.085),
                layer=MCON[0], datatype=MCON[1],
            ))
            top_copy.add(gdstk.rectangle(
                (x1p, y1p), (x2p, y2p), layer=MET1[0], datatype=MET1[1],
            ))

    out_lib.write_gds(str(output_path))


def _write_macro_placement_cfg(
    p: MacroV2Params,
    output_path: Path,
    sub_block_cell_names: dict[str, str],
    margin: float = 15.0,
) -> None:
    """OpenLane macro_placement.cfg format:
       instance_name x y orient
    Coordinates are in the macro's DIE_AREA (lower-left = 0,0),
    plus `margin` to leave space for std cell rows at the perimeter.

    Placements are snapped to the sky130 std-cell site grid
    (0.46 x 2.72) so that macro-interior pin positions fall on met1
    routing tracks (met1 has tracks on a 0.34 pitch which is a
    factor of 0.46; y tracks on 0.34 which is a factor of 2.72).
    Un-snapped placements cause DRT-0418 / DRT-0419 "no routing
    tracks pass through pin" warnings that leave pins unconnectable.
    """
    site_w = 0.46
    site_h = 2.72

    fp = build_floorplan(p)
    xs_lo = min(x for x, _ in fp.positions.values()) - margin
    ys_lo = min(y for _, y in fp.positions.values()) - margin

    lines: list[str] = []
    # Map floorplan block key -> top-level instance name used in Verilog
    instance_by_fp_key = {
        "array":         "u_array",
        "precharge":     "u_precharge",
        "col_mux":       "u_colmux",
        "sense_amp":     "u_sense_amp",
        "write_driver":  "u_write_driver",
        "row_decoder":   "u_decoder",
        "wl_driver":     "u_wl_driver",
        "control_logic": "u_ctrl",
    }
    for fp_key, (x, y) in fp.positions.items():
        inst = instance_by_fp_key[fp_key]
        xl = x - xs_lo
        yl = y - ys_lo
        # Snap to site grid
        xl = round(xl / site_w) * site_w
        yl = round(yl / site_h) * site_h
        lines.append(f"{inst} {xl:.3f} {yl:.3f} N")

    output_path.write_text("\n".join(lines) + "\n")


_CUSTOM_PDN_CFG = """\
# Custom PDN config for rekolektion macro-flow runs.
# Sub-block macros expose VPWR/VGND on met2 (top/bottom strips); the
# stdcell_grid below lays tight met3 stripes that contact each macro
# at several points, plus met1 follow-pins rails on std-cell rows,
# so the perimeter std-cell area and the macro-internal area are on
# a single connected VPWR / VGND net.
source $::env(SCRIPTS_DIR)/openroad/common/set_global_connections.tcl
set_global_connections

set_voltage_domain -name CORE -power $::env(VDD_NET) -ground $::env(GND_NET)

define_pdn_grid \\
    -name stdcell_grid \\
    -starts_with POWER \\
    -voltage_domain CORE \\
    -pins "met2 met3 met4"

# Met1 follow-pins on std-cell rows so decap/fill/tap cells get
# power from the same net as the macros.
add_pdn_stripe \\
    -grid stdcell_grid \\
    -layer met1 \\
    -width 0.48 -followpins \\
    -starts_with POWER

# Very dense met3 stripes so every macro gets many connections
# into its met2 VPWR/VGND abutment strips.  VPWR/VGND pair takes
# 2 * (width + spacing) = 6 um per pitch; smaller pitch increases
# grid redundancy and reduces VPWR fragmentation.
add_pdn_stripe \\
    -grid stdcell_grid \\
    -layer met3 \\
    -width 1.6 -pitch 5.0 -offset 1.0 -spacing 0.8 \\
    -starts_with POWER -extend_to_core_ring

# Met4 perpendicular stripes — denser pitch for better stitching.
add_pdn_stripe \\
    -grid stdcell_grid \\
    -layer met4 \\
    -width 1.6 -pitch 6.0 -offset 1.0 -spacing 0.8 \\
    -starts_with POWER -extend_to_core_ring

add_pdn_connect -grid stdcell_grid -layers "met1 met2"
add_pdn_connect -grid stdcell_grid -layers "met2 met3"
add_pdn_connect -grid stdcell_grid -layers "met3 met4"
"""


def _write_openlane_config(
    p: MacroV2Params,
    output_path: Path,
    die_size: tuple[float, float],
    lef_files: list[str],
    gds_files: list[str],
) -> None:
    cfg = {
        "DESIGN_NAME": p.top_cell_name,
        "VERILOG_FILES": [f"dir::src/{p.top_cell_name}.v"],
        "EXTRA_LEFS": lef_files,
        "EXTRA_GDS_FILES": gds_files,
        "CLOCK_PORT": "clk",
        "CLOCK_PERIOD": 20.0,
        "FP_SIZING": "absolute",
        "DIE_AREA": f"0 0 {die_size[0]:.3f} {die_size[1]:.3f}",
        "FP_CORE_UTIL": 30,
        "MACRO_PLACEMENT_CFG": "dir::macro_placement.cfg",
        "PDK": "sky130B",
        "STD_CELL_LIBRARY": "sky130_fd_sc_hd",
        "RUN_KLAYOUT_XOR": False,
        # Skip Magic DRC at the macro level — the foundry cells we
        # compose emit DRC rules that require SRAM-COREID waivers
        # (applied by rekolektion.verify.drc but not OpenLane's
        # vanilla Magic flow).  Run rekolektion.verify.drc on the
        # produced GDS separately.
        "RUN_MAGIC_DRC": False,
        "RUN_LVS": True,
        # Custom PDN — macros expose met2 power pins, connect via
        # met3 stripes.  See _CUSTOM_PDN_CFG at top of this module.
        "FP_PDN_CFG": "dir::pdn.tcl",
        "RUN_CTS": False,
        # The sram_* top module exposes VPWR/VGND as `inout` ports,
        # which OpenLane's power-pin checker doesn't always recognise
        # on a macro-flow module (it expects std-cell-style USE POWER
        # annotations).  Disable the connected-pins checker entirely
        # since we're generating a hardened macro, not a top-level
        # chip.
        "IGNORE_DISCONNECTED_MODULES": [p.top_cell_name],
        "ERROR_ON_DISCONNECTED_PINS": False,
        "RUN_IRDROP_REPORT": False,
    }
    output_path.write_text(json.dumps(cfg, indent=2) + "\n")


@dataclass
class OpenLanePrepResult:
    run_dir: Path
    verilog_path: Path
    config_path: Path
    placement_cfg_path: Path
    sub_block_lefs: dict[str, Path]
    sub_block_gds: dict[str, Path]


def prepare_openlane_run(
    p: MacroV2Params,
    run_dir: str | Path,
) -> OpenLanePrepResult:
    """Set up run_dir/ with everything OpenLane needs to P&R the macro."""
    run_dir = Path(run_dir)
    (run_dir / "src").mkdir(parents=True, exist_ok=True)
    (run_dir / "macros").mkdir(parents=True, exist_ok=True)

    # 1. Top Verilog netlist.
    verilog_path = run_dir / "src" / f"{p.top_cell_name}.v"
    generate_verilog(p, verilog_path)

    # 2. Sub-block LEFs.
    sub_lef_paths, pins_by_block = generate_sub_block_lefs(
        p, run_dir / "macros", return_pins=True,
    )
    escapes = pin_escape_rects(pins_by_block)

    # 3. Sub-block GDSes — assemble the macro once and extract each
    #    sub-block cell.  Add li1 + mcon + met1 landing pads at each
    #    LEF-declared li1 pin location so the OpenROAD router can
    #    land on met1.
    assembled, _ = assemble(p)
    cell_names = _sub_block_cell_names(p)
    sub_gds_paths: dict[str, Path] = {}
    for fp_key, lef_key in _FP_TO_LEF_KEY.items():
        cell_name = cell_names[fp_key]
        gds_path = run_dir / "macros" / f"{cell_name}.gds"
        _write_sub_block_gds(
            assembled, cell_name, gds_path,
            pin_escape_rects=escapes.get(lef_key, []),
        )
        sub_gds_paths[fp_key] = gds_path

    # 4. Macro placement cfg.
    placement_cfg_path = run_dir / "macro_placement.cfg"
    _write_macro_placement_cfg(p, placement_cfg_path, cell_names)

    # 5. Custom PDN tcl config (macros use met2 power pins, not the
    # default met4/met5 — hence a custom PDN template).
    pdn_cfg_path = run_dir / "pdn.tcl"
    pdn_cfg_path.write_text(_CUSTOM_PDN_CFG)

    # 6. OpenLane config.
    fp = build_floorplan(p)
    xs_lo = min(x for x, _ in fp.positions.values())
    ys_lo = min(y for _, y in fp.positions.values())
    xs_hi = max(x + fp.sizes[n][0] for n, (x, _) in fp.positions.items())
    ys_hi = max(y + fp.sizes[n][1] for n, (_, y) in fp.positions.items())
    # Add generous margin around macros — OpenROAD needs std cell
    # rows for PDN generation, so leave empty area at the perimeter
    # that CutRows can tile with std cell rows.
    margin = 15.0
    die_w = xs_hi - xs_lo + 2 * margin
    die_h = ys_hi - ys_lo + 2 * margin

    config_path = run_dir / "config.json"
    lef_rels = [f"dir::macros/{lp.name}" for lp in sub_lef_paths.values()]
    gds_rels = [f"dir::macros/{gp.name}" for gp in sub_gds_paths.values()]
    _write_openlane_config(p, config_path, (die_w, die_h), lef_rels, gds_rels)

    return OpenLanePrepResult(
        run_dir=run_dir,
        verilog_path=verilog_path,
        config_path=config_path,
        placement_cfg_path=placement_cfg_path,
        sub_block_lefs=sub_lef_paths,
        sub_block_gds=sub_gds_paths,
    )
