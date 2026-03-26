"""Output generation for SRAM macros: behavioral SPICE and Verilog models,
LEF abstracts, and Liberty timing models.

These are simplified behavioral models for simulation — not transistor-level
netlists.  They provide the correct port interface so that the SRAM macro
can be instantiated in a larger design.

The LEF and Liberty generators produce files needed by OpenLane for
place-and-route and static timing analysis.

Usage::

    from rekolektion.macro.assembler import compute_macro_params
    from rekolektion.macro.outputs import generate_spice, generate_verilog
    from rekolektion.macro.outputs import generate_all_outputs

    params = compute_macro_params(words=1024, bits=32, mux_ratio=8)
    generate_spice(params, "output/sram_1024x32.sp")
    generate_verilog(params, "output/sram_1024x32.v")

    # Or generate all outputs at once:
    params.macro_width = 500.0
    params.macro_height = 400.0
    paths = generate_all_outputs(params, "output", "sram_1024x32")
"""

from __future__ import annotations

import math
from pathlib import Path

from rekolektion.macro.assembler import MacroParams


def generate_all_outputs(
    params: MacroParams,
    output_dir: str | Path,
    stem: str,
) -> dict[str, Path]:
    """Generate all output files (SPICE, Verilog, LEF, Liberty) for a macro.

    Parameters
    ----------
    params : MacroParams
        Macro parameters (must have macro_width/macro_height set for LEF/lib).
    output_dir : path
        Directory for output files.
    stem : str
        Base filename (without extension).

    Returns
    -------
    dict[str, Path]
        Mapping of output type to file path.
    """
    from rekolektion.macro.lef_generator import generate_lef
    from rekolektion.macro.liberty_generator import generate_liberty

    out_dir = Path(output_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    paths: dict[str, Path] = {}
    paths["sp"] = generate_spice(params, out_dir / f"{stem}.sp")
    paths["v"] = generate_verilog(params, out_dir / f"{stem}.v")
    paths["lef"] = generate_lef(params, out_dir / f"{stem}.lef")
    paths["lib"] = generate_liberty(params, out_dir / f"{stem}.lib")
    return paths


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
