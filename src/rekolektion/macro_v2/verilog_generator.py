"""Verilog top-level netlist generator for v2 SRAM macros (Option Y).

Emits a structural Verilog module matching the per-instance LEF pins
declared in `sub_lef.py`.  Sub-blocks expose every cell pin
individually (e.g. dff0_clk, dff1_clk, dff2_clk, dff3_clk); the top
module wires same-net pins together so OpenROAD's router connects
them at the top level without the macro needing any internal signal
routing.

Pre-routed nets (WL, BL/BR, PDN) are NOT wired at the top — they're
physically connected via block-boundary overlap in the assembler.
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
    path = Path(output_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w") as f:
        _write_header(f, p)
        _write_blackboxes(f, p)
        _write_top_module(f, p)
    return path


def _tag(p: MacroV2Params) -> str:
    return f"m{p.mux_ratio}_{p.words}x{p.bits}"


# ---------------------------------------------------------------------------
# Header + blackbox declarations
# ---------------------------------------------------------------------------


def _write_header(f: TextIO, p: MacroV2Params) -> None:
    f.write(
        f"// Auto-generated Verilog for rekolektion v2 SRAM macro.\n"
        f"// Top module: {p.top_cell_name}\n"
        f"// Words x Bits x Mux: {p.words} x {p.bits} x mux{p.mux_ratio}\n"
        f"`default_nettype none\n\n"
    )


def _emit_bb(f: TextIO, name: str, ports: list[tuple[str, str]]) -> None:
    """ports: list of (port_name, direction) where direction is
    "input", "output", or "inout"."""
    f.write(f"(* blackbox *)\n")
    f.write(f"module {name} (\n")
    for i, (port, _) in enumerate(ports):
        comma = "," if i < len(ports) - 1 else ""
        f.write(f"    {port}{comma}\n")
    f.write(f");\n")
    for port, dir_ in ports:
        f.write(f"    {dir_} {port};\n")
    f.write(f"endmodule\n\n")


def _ctrl_logic_ports() -> list[tuple[str, str]]:
    ports: list[tuple[str, str]] = []
    for i in range(4):
        ports += [(f"dff{i}_clk", "input"), (f"dff{i}_d", "input"), (f"dff{i}_q", "output")]
    for i in range(2):
        ports += [(f"nand{i}_a", "input"), (f"nand{i}_b", "input"), (f"nand{i}_z", "output")]
    ports += [("VPWR", "inout"), ("VGND", "inout")]
    return ports


def _row_decoder_ports(p: MacroV2Params) -> list[tuple[str, str]]:
    split = _SPLIT_TABLE[p.rows]
    if len(split) != 1:
        return [("VPWR", "inout"), ("VGND", "inout")]
    k = split[0]
    ports: list[tuple[str, str]] = []
    for r in range(p.rows):
        for pn in ["a", "b", "c"][:k]:
            ports.append((f"nand{r}_{pn}", "input"))
        ports.append((f"nand{r}_z", "output"))
    ports += [("VPWR", "inout"), ("VGND", "inout")]
    return ports


def _wl_driver_ports(p: MacroV2Params) -> list[tuple[str, str]]:
    ports: list[tuple[str, str]] = []
    for r in range(p.rows):
        ports.append((f"nand{r}_a", "input"))
        ports.append((f"nand{r}_z", "output"))
    ports += [("VPWR", "inout"), ("VGND", "inout")]
    return ports


def _sa_ports(p: MacroV2Params) -> list[tuple[str, str]]:
    ports: list[tuple[str, str]] = []
    for i in range(p.bits):
        ports += [(f"sa{i}_en", "input"), (f"sa{i}_dout", "output")]
    ports += [("VPWR", "inout"), ("VGND", "inout")]
    return ports


def _wd_ports(p: MacroV2Params) -> list[tuple[str, str]]:
    ports: list[tuple[str, str]] = []
    for i in range(p.bits):
        ports += [(f"wd{i}_en", "input"), (f"wd{i}_din", "input")]
    ports += [("VPWR", "inout"), ("VGND", "inout")]
    return ports


def _precharge_ports() -> list[tuple[str, str]]:
    return [("p_en_bar", "input"), ("VPWR", "inout")]


def _column_mux_ports(p: MacroV2Params) -> list[tuple[str, str]]:
    return (
        [(f"col_sel_{s}", "input") for s in range(p.mux_ratio)]
        + [("VPWR", "inout"), ("VGND", "inout")]
    )


def _write_blackboxes(f: TextIO, p: MacroV2Params) -> None:
    tag = _tag(p)
    _emit_bb(f, f"sram_array_{tag}", [("VPWR", "inout"), ("VGND", "inout")])
    _emit_bb(f, f"pre_{tag}", _precharge_ports())
    _emit_bb(f, f"mux_{tag}", _column_mux_ports(p))
    _emit_bb(f, f"sa_{tag}", _sa_ports(p))
    _emit_bb(f, f"wd_{tag}", _wd_ports(p))
    _emit_bb(f, f"row_decoder_{tag}", _row_decoder_ports(p))
    _emit_bb(f, f"wl_driver_{tag}", _wl_driver_ports(p))
    _emit_bb(f, f"ctrl_logic_{tag}", _ctrl_logic_ports())


# ---------------------------------------------------------------------------
# Top module
# ---------------------------------------------------------------------------


def _write_top_module(f: TextIO, p: MacroV2Params) -> None:
    tag = _tag(p)

    top_ports: list[str] = ["clk", "we", "cs"]
    top_ports += [f"addr_{i}" for i in range(p.num_addr_bits)]
    top_ports += [f"din_{i}" for i in range(p.bits)]
    top_ports += [f"dout_{i}" for i in range(p.bits)]
    # col_sel is exposed as top-level input pins (one per mux position)
    # so external decode logic drives them at chip level.
    top_ports += [f"col_sel_{s}" for s in range(p.mux_ratio)]
    top_ports += ["VPWR", "VGND"]

    f.write(f"module {p.top_cell_name} (\n")
    for i, port in enumerate(top_ports):
        comma = "," if i < len(top_ports) - 1 else ""
        f.write(f"    {port}{comma}\n")
    f.write(f");\n")
    f.write(f"    input  clk, we, cs;\n")
    f.write(f"    input  {', '.join(f'addr_{i}' for i in range(p.num_addr_bits))};\n")
    f.write(f"    input  {', '.join(f'din_{i}' for i in range(p.bits))};\n")
    f.write(f"    output {', '.join(f'dout_{i}' for i in range(p.bits))};\n")
    f.write(f"    input  {', '.join(f'col_sel_{s}' for s in range(p.mux_ratio))};\n")
    f.write(f"    inout  VPWR, VGND;\n\n")

    # Internal signal wires
    f.write(f"    wire nand0_z, nand1_z;\n")
    f.write(f"    wire clk_buf, wl_en, p_en_bar, s_en, w_en;\n\n")

    # --- ctrl_logic: wire each DFF CLK to clk, D to shared Z net, Q
    #     to named output; NAND A/B to we/cs, Z to the shared net.
    ctrl_conns: list[tuple[str, str]] = []
    dff_q_outputs = ("clk_buf", "p_en_bar", "s_en", "w_en")
    for i in range(4):
        ctrl_conns.append((f"dff{i}_clk", "clk"))
        ctrl_conns.append((f"dff{i}_d", "nand0_z" if i < 2 else "nand1_z"))
        ctrl_conns.append((f"dff{i}_q", dff_q_outputs[i]))
    for i in range(2):
        ctrl_conns.append((f"nand{i}_a", "we"))
        ctrl_conns.append((f"nand{i}_b", "cs"))
        ctrl_conns.append((f"nand{i}_z", f"nand{i}_z"))
    ctrl_conns += [("VPWR", "VPWR"), ("VGND", "VGND")]
    _inst(f, f"ctrl_logic_{tag}", "u_ctrl", ctrl_conns)

    # --- row_decoder: each NAND's A/B/C → shared addr net, Z →
    #     per-row dec_out (connected to wl_driver at top).
    split = _SPLIT_TABLE[p.rows]
    dec_conns: list[tuple[str, str]] = []
    if len(split) == 1:
        k = split[0]
        pin_names = ["a", "b", "c"][:k]
        for r in range(p.rows):
            # For a simplified decoder, all NANDs share the same k
            # address lines (matches the spice_generator's reference).
            for idx, pn in enumerate(pin_names):
                dec_conns.append((f"nand{r}_{pn}", f"addr_{idx}"))
            dec_conns.append((f"nand{r}_z", f"dec_out_{r}"))
    dec_conns += [("VPWR", "VPWR"), ("VGND", "VGND")]
    # Add dec_out declaration
    f.write(f"    wire {', '.join(f'dec_out_{r}' for r in range(p.rows))};\n\n")
    _inst(f, f"row_decoder_{tag}", "u_decoder", dec_conns)

    # --- wl_driver: NAND3 A = dec_out, Z = array WL.  Since WL is
    #     pre-routed (not a top-level net), we just tie A and Z to
    #     explicit named wires; OpenROAD connects A to dec_out_r and
    #     leaves Z terminating at the (pre-routed) array edge.
    wl_conns: list[tuple[str, str]] = []
    f.write(f"    wire {', '.join(f'wl_{r}' for r in range(p.rows))};\n\n")
    for r in range(p.rows):
        wl_conns.append((f"nand{r}_a", f"dec_out_{r}"))
        wl_conns.append((f"nand{r}_z", f"wl_{r}"))
    wl_conns += [("VPWR", "VPWR"), ("VGND", "VGND")]
    _inst(f, f"wl_driver_{tag}", "u_wl_driver", wl_conns)

    # --- sram_array: power-only, WL/BL/BR pre-routed
    _inst(f, f"sram_array_{tag}", "u_array", [("VPWR", "VPWR"), ("VGND", "VGND")])

    # --- precharge: p_en_bar
    _inst(f, f"pre_{tag}", "u_precharge",
          [("p_en_bar", "p_en_bar"), ("VPWR", "VPWR")])

    # --- column_mux: col_sel[*]
    mux_conns: list[tuple[str, str]] = []
    for s in range(p.mux_ratio):
        mux_conns.append((f"col_sel_{s}", f"col_sel_{s}"))
    mux_conns += [("VPWR", "VPWR"), ("VGND", "VGND")]
    _inst(f, f"mux_{tag}", "u_colmux", mux_conns)

    # --- sense_amp: s_en, dout per bit
    sa_conns: list[tuple[str, str]] = []
    for i in range(p.bits):
        sa_conns.append((f"sa{i}_en", "s_en"))
        sa_conns.append((f"sa{i}_dout", f"dout_{i}"))
    sa_conns += [("VPWR", "VPWR"), ("VGND", "VGND")]
    _inst(f, f"sa_{tag}", "u_sense_amp", sa_conns)

    # --- write_driver: w_en, din per bit
    wd_conns: list[tuple[str, str]] = []
    for i in range(p.bits):
        wd_conns.append((f"wd{i}_en", "w_en"))
        wd_conns.append((f"wd{i}_din", f"din_{i}"))
    wd_conns += [("VPWR", "VPWR"), ("VGND", "VGND")]
    _inst(f, f"wd_{tag}", "u_write_driver", wd_conns)

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
