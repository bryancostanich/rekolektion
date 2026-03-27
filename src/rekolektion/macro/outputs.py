"""Output generation for SRAM macros: behavioral SPICE and Verilog models,
LEF abstracts, and Liberty timing models.

These are simplified behavioral models for simulation — not transistor-level
netlists.  They provide the correct port interface so that the SRAM macro
can be instantiated in a larger design.

Pin names are standardized across all outputs (Verilog, LEF, Liberty, SPICE):
  clk, we, cs, addr[N:0], din[N:0], dout[N:0], VPWR, VGND

Usage::

    from rekolektion.macro.assembler import compute_macro_params
    from rekolektion.macro.outputs import generate_all_outputs

    params = compute_macro_params(words=1024, bits=32, mux_ratio=8)
    params.macro_width = 500.0
    params.macro_height = 400.0
    paths = generate_all_outputs(params, "output", "my_sram")
"""

from __future__ import annotations

from pathlib import Path

from rekolektion.macro.assembler import MacroParams


def _macro_name(params: MacroParams, name: str | None = None) -> str:
    """Derive a consistent macro/module name."""
    if name:
        return name
    return f"sram_{params.words}x{params.bits}_mux{params.mux_ratio}"


def generate_all_outputs(
    params: MacroParams,
    output_dir: str | Path,
    stem: str,
    macro_name: str | None = None,
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
    macro_name : str, optional
        Override macro/cell name (default: sram_{words}x{bits}_mux{mux}).

    Returns
    -------
    dict[str, Path]
        Mapping of output type to file path.
    """
    from rekolektion.macro.lef_generator import generate_lef
    from rekolektion.macro.liberty_generator import generate_liberty

    out_dir = Path(output_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    mn = _macro_name(params, macro_name)

    paths: dict[str, Path] = {}
    paths["sp"] = generate_spice(params, out_dir / f"{stem}.sp", macro_name=mn)
    paths["v"] = generate_verilog(params, out_dir / f"{stem}.v", macro_name=mn)
    paths["lef"] = generate_lef(params, out_dir / f"{stem}.lef", macro_name=mn)
    paths["lib"] = generate_liberty(params, out_dir / f"{stem}.lib", macro_name=mn)
    return paths


def generate_spice(
    params: MacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
) -> Path:
    """Generate a behavioral SPICE model for the SRAM macro."""
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    mn = _macro_name(params, macro_name)
    addr_bits = params.num_addr_bits
    data_bits = params.bits

    addr_pins = " ".join(f"addr[{i}]" for i in range(addr_bits))
    din_pins = " ".join(f"din[{i}]" for i in range(data_bits))
    dout_pins = " ".join(f"dout[{i}]" for i in range(data_bits))

    lines = [
        f"* Behavioral SPICE model for {mn}",
        f"* {params.words} words x {params.bits} bits, mux {params.mux_ratio}",
        f"* Array: {params.rows} rows x {params.cols} columns",
        f"*",
        f".subckt {mn}",
        f"+  clk we cs",
        f"+  {addr_pins}",
        f"+  {din_pins}",
        f"+  {dout_pins}",
        f"+  VPWR VGND",
        f"*",
        f"* Behavioral stub — extract from GDS for transistor-level simulation.",
        f"*",
        f".ends {mn}",
        "",
    ]

    out.write_text("\n".join(lines))
    return out


def generate_verilog(
    params: MacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
) -> Path:
    """Generate a behavioral Verilog model for the SRAM macro."""
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    mn = _macro_name(params, macro_name)
    w = params.words
    b = params.bits
    addr_bits = params.num_addr_bits

    lines = [
        f"// Behavioral Verilog model for {mn}",
        f"// {w} words x {b} bits, mux {params.mux_ratio}",
        f"// Array: {params.rows} rows x {params.cols} columns",
        f"// Address bits: {addr_bits}",
        f"",
        f"module {mn} (",
        f"    input  wire               clk,",
        f"    input  wire               we,",
        f"    input  wire               cs,",
        f"    input  wire [{addr_bits-1}:0]  addr,",
        f"    input  wire [{b-1}:0]  din,",
        f"    output reg  [{b-1}:0]  dout,",
        f"    inout  wire              VPWR,",
        f"    inout  wire              VGND",
        f");",
        f"",
        f"    reg [{b-1}:0] mem [0:{w-1}];",
        f"",
        f"    always @(posedge clk) begin",
        f"        if (cs) begin",
        f"            if (we)",
        f"                mem[addr] <= din;",
        f"            else",
        f"                dout <= mem[addr];",
        f"        end",
        f"    end",
        f"",
        f"endmodule",
        "",
    ]

    out.write_text("\n".join(lines))
    return out
