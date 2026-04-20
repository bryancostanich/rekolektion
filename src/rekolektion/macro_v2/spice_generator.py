"""Reference SPICE netlist generator for v2 SRAM macros.

Emits a structural SPICE netlist that mirrors the hierarchy produced
by `assembler.assemble()`: a top-level .subckt with the macro's IO
ports, composing block-level subckts (row_decoder, control_logic,
bitcell_array, precharge, column_mux, sense_amp, write_driver).

C6.6 scope — STRUCTURAL ONLY. Block .subckts declare their ports and
comment on which foundry cell bodies they would compose. Full
transistor-level expansion is deferred until the foundry-cell
extraction issue (see autonomous_decisions.md D3) is resolved.
"""
from __future__ import annotations

from pathlib import Path
from typing import TextIO

from rekolektion.macro_v2.assembler import MacroV2Params


def generate_reference_spice(
    p: MacroV2Params,
    output_path: str | Path,
) -> Path:
    """Emit a structural SPICE netlist for the assembled macro."""
    path = Path(output_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w") as f:
        _write_header(f, p)
        _write_top_subckt(f, p)
        _write_block_subckts(f, p)
    return path


def _write_header(f: TextIO, p: MacroV2Params) -> None:
    f.write(
        "* Structural reference SPICE for rekolektion v2 SRAM macro.\n"
        f"* Top cell: {p.top_cell_name}\n"
        f"* Words x Bits x Mux: {p.words} x {p.bits} x mux{p.mux_ratio}\n"
        f"* Rows: {p.rows}, Cols: {p.cols}, Addr bits: {p.num_addr_bits}\n"
        "*\n"
        "* NOTE: C6.6 produces STRUCTURAL hierarchy only. Block .subckts\n"
        "* declare ports and reference foundry cells by name; transistor\n"
        "* bodies for foundry cells are expected from PDK .include files\n"
        "* resolved by the LVS runner. See conductor autonomous_decisions.md D3.\n"
        "\n"
    )


def _top_port_list(p: MacroV2Params) -> list[str]:
    ports: list[str] = []
    # clock/control
    ports += ["clk", "we", "cs"]
    # address
    for i in range(p.num_addr_bits):
        ports.append(f"addr{i}")
    # data in / data out
    for i in range(p.bits):
        ports.append(f"din{i}")
    for i in range(p.bits):
        ports.append(f"dout{i}")
    # power
    ports += ["VPWR", "VGND"]
    return ports


def _write_top_subckt(f: TextIO, p: MacroV2Params) -> None:
    ports = _top_port_list(p)
    f.write(f".subckt {p.top_cell_name}\n")
    # Wrap port list for readability
    _write_wrapped_ports(f, ports)

    # Internal nets
    wl_nets = [f"wl_{r}" for r in range(p.rows)]
    bl_nets = [f"bl_{c}" for c in range(p.cols)]
    br_nets = [f"br_{c}" for c in range(p.cols)]
    enable_nets = ["clk_buf", "p_en_bar", "s_en", "w_en", "wl_en"]

    f.write("*\n* Internal enable signals\n")
    for n in enable_nets:
        f.write(f"* .net {n}\n")

    f.write("\n* Row decoder\n")
    addr_args = " ".join(f"addr{i}" for i in range(p.num_addr_bits))
    f.write(
        f"Xdecoder {addr_args} {' '.join(wl_nets)} wl_en "
        f"VPWR VGND row_decoder_{p.rows}\n"
    )

    f.write("\n* Control logic\n")
    f.write(
        "Xcontrol clk we cs clk_buf wl_en p_en_bar s_en w_en "
        "VPWR VGND ctrl_logic\n"
    )

    f.write("\n* Bitcell array\n")
    f.write(
        f"Xarray {' '.join(wl_nets)} "
        f"{' '.join(bl_nets)} {' '.join(br_nets)} "
        f"VPWR VGND sram_array_{p.rows}x{p.cols}\n"
    )

    f.write("\n* Precharge row\n")
    f.write(
        f"Xprecharge {' '.join(bl_nets)} {' '.join(br_nets)} "
        f"p_en_bar VPWR precharge_row_{p.bits}_mux{p.mux_ratio}\n"
    )

    f.write("\n* Column mux\n")
    muxed_bl = [f"muxed_bl_{i}" for i in range(p.bits)]
    muxed_br = [f"muxed_br_{i}" for i in range(p.bits)]
    f.write(
        f"Xcolmux {' '.join(bl_nets)} {' '.join(br_nets)} "
        f"{' '.join(muxed_bl)} {' '.join(muxed_br)} "
        f"col_sel VPWR VGND column_mux_row_{p.bits}_mux{p.mux_ratio}\n"
    )

    f.write("\n* Sense amplifier row\n")
    dout_args = " ".join(f"dout{i}" for i in range(p.bits))
    f.write(
        f"Xsa {' '.join(muxed_bl)} {' '.join(muxed_br)} "
        f"s_en {dout_args} VPWR VGND "
        f"sense_amp_row_{p.bits}_mux{p.mux_ratio}\n"
    )

    f.write("\n* Write driver row\n")
    din_args = " ".join(f"din{i}" for i in range(p.bits))
    f.write(
        f"Xwd {din_args} w_en {' '.join(muxed_bl)} {' '.join(muxed_br)} "
        f"VPWR VGND write_driver_row_{p.bits}_mux{p.mux_ratio}\n"
    )

    f.write(f"\n.ends {p.top_cell_name}\n\n")


def _write_wrapped_ports(f: TextIO, ports: list[str], width: int = 78) -> None:
    line = "+"
    for port in ports:
        if len(line) + 1 + len(port) > width:
            f.write(line + "\n")
            line = "+ " + port
        else:
            line = (line + " " + port) if line.strip() != "+" else (
                "+ " + port
            )
    if line.strip():
        f.write(line + "\n")


def _write_block_subckts(f: TextIO, p: MacroV2Params) -> None:
    """Emit stub .subckt declarations for each sub-block."""

    # --- row_decoder ---
    wl_ports = " ".join(f"wl{r}" for r in range(p.rows))
    addr_ports = " ".join(f"addr{i}" for i in range(p.num_addr_bits))
    f.write(
        f".subckt row_decoder_{p.rows} {addr_ports} {wl_ports} wl_en "
        "VPWR VGND\n"
    )
    f.write(
        "* Structural stub: composes foundry NAND_dec and (for larger\n"
        "* N) Predecoder blocks. See macro_v2/row_decoder.py.\n"
    )
    f.write(f".ends row_decoder_{p.rows}\n\n")

    # --- control logic ---
    f.write(
        ".subckt ctrl_logic clk we cs clk_buf wl_en p_en_bar s_en w_en "
        "VPWR VGND\n"
    )
    f.write(
        "* Structural stub: 4 DFFs + 2 NAND2 cells. Internal enable\n"
        "* generation logic TBD (C7/C8 work).\n"
    )
    f.write(".ends ctrl_logic\n\n")

    # --- bitcell array ---
    wl_ports = " ".join(f"wl{r}" for r in range(p.rows))
    bl_ports = " ".join(f"bl{c}" for c in range(p.cols))
    br_ports = " ".join(f"br{c}" for c in range(p.cols))
    f.write(
        f".subckt sram_array_{p.rows}x{p.cols} {wl_ports} "
        f"{bl_ports} {br_ports} VPWR VGND\n"
    )
    f.write(
        f"* {p.rows*p.cols} foundry bitcell instances tiled at "
        "1.31 x 1.58 um pitch.\n"
    )
    f.write(f".ends sram_array_{p.rows}x{p.cols}\n\n")

    # --- precharge row ---
    bl_ports = " ".join(f"bl{c}" for c in range(p.cols))
    br_ports = " ".join(f"br{c}" for c in range(p.cols))
    f.write(
        f".subckt precharge_row_{p.bits}_mux{p.mux_ratio} "
        f"{bl_ports} {br_ports} p_en_bar VPWR\n"
    )
    f.write(
        f"* {p.bits} precharge_0 instances at "
        f"{p.mux_ratio * 1.31:.2f} um pitch.\n"
    )
    f.write(
        f"* NOTE (D2): at mux={p.mux_ratio}, only col 0 of each mux\n"
        "* group actually connects to its precharge cell's BL pin.\n"
    )
    f.write(f".ends precharge_row_{p.bits}_mux{p.mux_ratio}\n\n")

    # --- column mux row ---
    muxed_bl = " ".join(f"muxed_bl{i}" for i in range(p.bits))
    muxed_br = " ".join(f"muxed_br{i}" for i in range(p.bits))
    f.write(
        f".subckt column_mux_row_{p.bits}_mux{p.mux_ratio} "
        f"{bl_ports} {br_ports} {muxed_bl} {muxed_br} "
        "col_sel VPWR VGND\n"
    )
    f.write(
        f"* {p.bits} single_level_column_mux instances. Structural "
        "limitation (D2): only col 0 of each mux group routed.\n"
    )
    f.write(f".ends column_mux_row_{p.bits}_mux{p.mux_ratio}\n\n")

    # --- sense amp row ---
    dout_ports = " ".join(f"dout{i}" for i in range(p.bits))
    f.write(
        f".subckt sense_amp_row_{p.bits}_mux{p.mux_ratio} "
        f"{muxed_bl} {muxed_br} s_en {dout_ports} VPWR VGND\n"
    )
    f.write(
        f"* {p.bits} foundry sense_amp instances.\n"
    )
    f.write(f".ends sense_amp_row_{p.bits}_mux{p.mux_ratio}\n\n")

    # --- write driver row ---
    din_ports = " ".join(f"din{i}" for i in range(p.bits))
    f.write(
        f".subckt write_driver_row_{p.bits}_mux{p.mux_ratio} "
        f"{din_ports} w_en {muxed_bl} {muxed_br} VPWR VGND\n"
    )
    f.write(
        f"* {p.bits} foundry write_driver instances.\n"
    )
    f.write(f".ends write_driver_row_{p.bits}_mux{p.mux_ratio}\n\n")
