"""Output generation for SRAM macros: behavioral SPICE and Verilog models.

These are simplified behavioral models for simulation — not transistor-level
netlists.  They provide the correct port interface so that the SRAM macro
can be instantiated in a larger design.

Usage::

    from rekolektion.macro.assembler import compute_macro_params
    from rekolektion.macro.outputs import generate_spice, generate_verilog

    params = compute_macro_params(words=1024, bits=32, mux_ratio=8)
    generate_spice(params, "output/sram_1024x32.sp")
    generate_verilog(params, "output/sram_1024x32.v")
"""

from __future__ import annotations

import math
from pathlib import Path

from rekolektion.macro.assembler import MacroParams


def generate_spice(
    params: MacroParams,
    output_path: str | Path,
) -> Path:
    """Generate a behavioral SPICE model for the SRAM macro.

    This is a stub model that defines the port interface.  A real
    transistor-level netlist would be extracted from the GDS.

    Parameters
    ----------
    params : MacroParams
        Macro parameters (words, bits, address width, etc.).
    output_path : path
        Write SPICE to this file.

    Returns
    -------
    Path
        The output file path.
    """
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    addr_bits = params.num_addr_bits
    data_bits = params.bits

    addr_pins = " ".join(f"A[{i}]" for i in range(addr_bits))
    din_pins = " ".join(f"DIN[{i}]" for i in range(data_bits))
    dout_pins = " ".join(f"DOUT[{i}]" for i in range(data_bits))

    lines = [
        f"* Behavioral SPICE model for SRAM {params.words}x{params.bits}",
        f"* Mux ratio: {params.mux_ratio}",
        f"* Array: {params.rows} rows x {params.cols} columns",
        f"* Address bits: {addr_bits} ({params.num_row_bits} row + {params.num_col_bits} col)",
        f"*",
        f".subckt sram_{params.words}x{params.bits}",
        f"+  CLK WE CS",
        f"+  {addr_pins}",
        f"+  {din_pins}",
        f"+  {dout_pins}",
        f"+  VDD GND",
        f"*",
        f"* This is a behavioral stub.  For transistor-level simulation,",
        f"* extract the netlist from the GDS layout.",
        f"*",
        f".ends sram_{params.words}x{params.bits}",
        "",
    ]

    out.write_text("\n".join(lines))
    return out


def generate_verilog(
    params: MacroParams,
    output_path: str | Path,
) -> Path:
    """Generate a behavioral Verilog model for the SRAM macro.

    The model implements a simple synchronous read/write memory with
    the correct port interface.

    Parameters
    ----------
    params : MacroParams
        Macro parameters.
    output_path : path
        Write Verilog to this file.

    Returns
    -------
    Path
        The output file path.
    """
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    w = params.words
    b = params.bits
    addr_bits = params.num_addr_bits

    lines = [
        f"// Behavioral Verilog model for SRAM {w}x{b}",
        f"// Mux ratio: {params.mux_ratio}",
        f"// Array: {params.rows} rows x {params.cols} columns",
        f"// Address bits: {addr_bits}",
        f"",
        f"module sram_{w}x{b} (",
        f"    input  wire               CLK,",
        f"    input  wire               WE,",
        f"    input  wire               CS,",
        f"    input  wire [{addr_bits-1}:0]  ADDR,",
        f"    input  wire [{b-1}:0]  DIN,",
        f"    output reg  [{b-1}:0]  DOUT",
        f");",
        f"",
        f"    // Memory array",
        f"    reg [{b-1}:0] mem [0:{w-1}];",
        f"",
        f"    always @(posedge CLK) begin",
        f"        if (CS) begin",
        f"            if (WE) begin",
        f"                mem[ADDR] <= DIN;",
        f"            end else begin",
        f"                DOUT <= mem[ADDR];",
        f"            end",
        f"        end",
        f"    end",
        f"",
        f"endmodule",
        "",
    ]

    out.write_text("\n".join(lines))
    return out
