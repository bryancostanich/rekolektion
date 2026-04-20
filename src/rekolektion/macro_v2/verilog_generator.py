"""Verilog top-level netlist generator for v2 SRAM macros.

Emits a synthesisable Verilog module that matches the SPICE topology
in `spice_generator.py`.  Used as input to OpenLane/OpenROAD for
macro-level P&R (Option Y): sub-blocks are declared as blackbox
modules with their LEF abstracts; the top module wires them together.

The top-level Verilog has:
  - ports: clk, we, cs, addr[0..num_addr_bits-1], din[0..bits-1],
           dout[0..bits-1], VPWR, VGND
  - one instance each of: control_logic, row_decoder, wl_driver_row,
    bitcell_array, precharge_row, column_mux_row, sense_amp_row,
    write_driver_row
  - sub-block names match the assembler's name_tag convention
    (e.g. `ctrl_logic_m4_32x8`) so the LEF + GDS names line up.
"""
from __future__ import annotations

from pathlib import Path
from typing import TextIO

from rekolektion.macro_v2.assembler import MacroV2Params
from rekolektion.macro_v2.row_decoder import _SPLIT_TABLE


def generate_verilog(
    p: MacroV2Params,
    output_path: str | Path,
) -> Path:
    """Emit the macro-top Verilog module to `output_path`."""
    path = Path(output_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w") as f:
        _write_header(f, p)
        _write_blackboxes(f, p)
        _write_top_module(f, p)
    return path


# ---------------------------------------------------------------------------
# Header + blackbox declarations
# ---------------------------------------------------------------------------


def _write_header(f: TextIO, p: MacroV2Params) -> None:
    f.write(
        f"// Auto-generated Verilog for rekolektion v2 SRAM macro.\n"
        f"// Top module: {p.top_cell_name}\n"
        f"// Words x Bits x Mux: {p.words} x {p.bits} x mux{p.mux_ratio}\n"
        f"// Rows: {p.rows}, Cols: {p.cols}, Addr bits: {p.num_addr_bits}\n"
        f"\n"
        f"`default_nettype none\n\n"
    )


def _tag(p: MacroV2Params) -> str:
    return f"m{p.mux_ratio}_{p.words}x{p.bits}"


def _write_blackboxes(f: TextIO, p: MacroV2Params) -> None:
    """Emit `module ... endmodule` blackboxes for each sub-block.

    Sub-block modules are blackbox-only here — their implementation
    is provided to the tool via GDS + LEF + (optional) behavioural
    Verilog.  Port lists must match the LEF pin declarations exactly.
    """
    tag = _tag(p)
    _emit_bb(f, f"sram_array_{tag}", [
        *[f"wl_{r}" for r in range(p.rows)],
        *[f"bl_{c}" for c in range(p.cols)],
        *[f"br_{c}" for c in range(p.cols)],
        "VPWR", "VGND",
    ])
    _emit_bb(f, f"pre_{tag}", [
        *[f"bl_{c}" for c in range(p.cols)],
        *[f"br_{c}" for c in range(p.cols)],
        "p_en_bar", "VPWR",
    ])
    _emit_bb(f, f"mux_{tag}", [
        *[f"bl_{c}" for c in range(p.cols)],
        *[f"br_{c}" for c in range(p.cols)],
        *[f"muxed_bl_{i}" for i in range(p.bits)],
        *[f"muxed_br_{i}" for i in range(p.bits)],
        *[f"col_sel_{s}" for s in range(p.mux_ratio)],
        "VPWR", "VGND",
    ])
    _emit_bb(f, f"sa_{tag}", [
        *[f"muxed_bl_{i}" for i in range(p.bits)],
        *[f"muxed_br_{i}" for i in range(p.bits)],
        "s_en",
        *[f"dout_{i}" for i in range(p.bits)],
        "VPWR", "VGND",
    ])
    _emit_bb(f, f"wd_{tag}", [
        *[f"din_{i}" for i in range(p.bits)],
        "w_en",
        *[f"muxed_bl_{i}" for i in range(p.bits)],
        *[f"muxed_br_{i}" for i in range(p.bits)],
        "VPWR", "VGND",
    ])
    _emit_bb(f, f"row_decoder_{tag}", [
        *[f"addr_{i}" for i in range(p.num_addr_bits)],
        *[f"dec_out_{r}" for r in range(p.rows)],
        "wl_en", "VPWR", "VGND",
    ])
    _emit_bb(f, f"wl_driver_{tag}", [
        *[f"dec_out_{r}" for r in range(p.rows)],
        *[f"wl_{r}" for r in range(p.rows)],
        "VPWR", "VGND",
    ])
    _emit_bb(f, f"ctrl_logic_{tag}", [
        "clk", "we", "cs",
        "clk_buf", "wl_en", "p_en_bar", "s_en", "w_en",
        "VPWR", "VGND",
    ])


def _emit_bb(f: TextIO, name: str, ports: list[str]) -> None:
    f.write(f"(* blackbox *)\n")
    f.write(f"module {name} (\n")
    for i, port in enumerate(ports):
        comma = "," if i < len(ports) - 1 else ""
        f.write(f"    {port}{comma}\n")
    f.write(f");\n")
    for port in ports:
        f.write(f"    inout {port};\n")
    f.write(f"endmodule\n\n")


# ---------------------------------------------------------------------------
# Top module with instantiation + net names that match the SPICE netlist
# ---------------------------------------------------------------------------


def _write_top_module(f: TextIO, p: MacroV2Params) -> None:
    tag = _tag(p)
    # Port list
    ports: list[str] = ["clk", "we", "cs"]
    ports += [f"addr_{i}" for i in range(p.num_addr_bits)]
    ports += [f"din_{i}" for i in range(p.bits)]
    ports += [f"dout_{i}" for i in range(p.bits)]
    ports += ["VPWR", "VGND"]

    f.write(f"module {p.top_cell_name} (\n")
    for i, port in enumerate(ports):
        comma = "," if i < len(ports) - 1 else ""
        f.write(f"    {port}{comma}\n")
    f.write(f");\n")

    # Port directions
    f.write(f"    input  clk, we, cs;\n")
    addr_list = ", ".join(f"addr_{i}" for i in range(p.num_addr_bits))
    f.write(f"    input  {addr_list};\n")
    din_list = ", ".join(f"din_{i}" for i in range(p.bits))
    f.write(f"    input  {din_list};\n")
    dout_list = ", ".join(f"dout_{i}" for i in range(p.bits))
    f.write(f"    output {dout_list};\n")
    f.write(f"    inout  VPWR, VGND;\n\n")

    # Internal wires
    wl_names = [f"wl_{r}" for r in range(p.rows)]
    dec_names = [f"dec_out_{r}" for r in range(p.rows)]
    bl_names = [f"bl_{c}" for c in range(p.cols)]
    br_names = [f"br_{c}" for c in range(p.cols)]
    muxed_bl = [f"muxed_bl_{i}" for i in range(p.bits)]
    muxed_br = [f"muxed_br_{i}" for i in range(p.bits)]
    col_sel = [f"col_sel_{s}" for s in range(p.mux_ratio)]
    ctrl_outs = ["clk_buf", "wl_en", "p_en_bar", "s_en", "w_en"]

    f.write(f"    wire {', '.join(wl_names)};\n")
    f.write(f"    wire {', '.join(dec_names)};\n")
    f.write(f"    wire {', '.join(bl_names)};\n")
    f.write(f"    wire {', '.join(br_names)};\n")
    f.write(f"    wire {', '.join(muxed_bl)};\n")
    f.write(f"    wire {', '.join(muxed_br)};\n")
    f.write(f"    wire {', '.join(col_sel)};\n")
    f.write(f"    wire {', '.join(ctrl_outs)};\n\n")

    # Instantiations
    _inst(f, f"ctrl_logic_{tag}", "u_ctrl", [
        ("clk", "clk"), ("we", "we"), ("cs", "cs"),
        ("clk_buf", "clk_buf"), ("wl_en", "wl_en"),
        ("p_en_bar", "p_en_bar"), ("s_en", "s_en"), ("w_en", "w_en"),
        ("VPWR", "VPWR"), ("VGND", "VGND"),
    ])
    _inst(f, f"row_decoder_{tag}", "u_decoder",
          [(f"addr_{i}", f"addr_{i}") for i in range(p.num_addr_bits)]
          + [(f"dec_out_{r}", f"dec_out_{r}") for r in range(p.rows)]
          + [("wl_en", "wl_en"), ("VPWR", "VPWR"), ("VGND", "VGND")])
    _inst(f, f"wl_driver_{tag}", "u_wl_driver",
          [(f"dec_out_{r}", f"dec_out_{r}") for r in range(p.rows)]
          + [(f"wl_{r}", f"wl_{r}") for r in range(p.rows)]
          + [("VPWR", "VPWR"), ("VGND", "VGND")])
    _inst(f, f"sram_array_{tag}", "u_array",
          [(f"wl_{r}", f"wl_{r}") for r in range(p.rows)]
          + [(f"bl_{c}", f"bl_{c}") for c in range(p.cols)]
          + [(f"br_{c}", f"br_{c}") for c in range(p.cols)]
          + [("VPWR", "VPWR"), ("VGND", "VGND")])
    _inst(f, f"pre_{tag}", "u_precharge",
          [(f"bl_{c}", f"bl_{c}") for c in range(p.cols)]
          + [(f"br_{c}", f"br_{c}") for c in range(p.cols)]
          + [("p_en_bar", "p_en_bar"), ("VPWR", "VPWR")])
    _inst(f, f"mux_{tag}", "u_colmux",
          [(f"bl_{c}", f"bl_{c}") for c in range(p.cols)]
          + [(f"br_{c}", f"br_{c}") for c in range(p.cols)]
          + [(f"muxed_bl_{i}", f"muxed_bl_{i}") for i in range(p.bits)]
          + [(f"muxed_br_{i}", f"muxed_br_{i}") for i in range(p.bits)]
          + [(f"col_sel_{s}", f"col_sel_{s}") for s in range(p.mux_ratio)]
          + [("VPWR", "VPWR"), ("VGND", "VGND")])
    _inst(f, f"sa_{tag}", "u_sense_amp",
          [(f"muxed_bl_{i}", f"muxed_bl_{i}") for i in range(p.bits)]
          + [(f"muxed_br_{i}", f"muxed_br_{i}") for i in range(p.bits)]
          + [("s_en", "s_en")]
          + [(f"dout_{i}", f"dout_{i}") for i in range(p.bits)]
          + [("VPWR", "VPWR"), ("VGND", "VGND")])
    _inst(f, f"wd_{tag}", "u_write_driver",
          [(f"din_{i}", f"din_{i}") for i in range(p.bits)]
          + [("w_en", "w_en")]
          + [(f"muxed_bl_{i}", f"muxed_bl_{i}") for i in range(p.bits)]
          + [(f"muxed_br_{i}", f"muxed_br_{i}") for i in range(p.bits)]
          + [("VPWR", "VPWR"), ("VGND", "VGND")])

    f.write(f"endmodule\n")


def _inst(
    f: TextIO,
    module: str,
    inst_name: str,
    connections: list[tuple[str, str]],
) -> None:
    f.write(f"    {module} {inst_name} (\n")
    for i, (port, net) in enumerate(connections):
        comma = "," if i < len(connections) - 1 else ""
        f.write(f"        .{port}({net}){comma}\n")
    f.write(f"    );\n\n")
