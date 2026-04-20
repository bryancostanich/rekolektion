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


def _pn(name: str, upper: bool) -> str:
    """Convert pin name to uppercase if requested.

    Handles bus notation: ``addr[3]`` → ``ADDR[3]``.
    Already-uppercase names (``VPWR``, ``VGND``) pass through unchanged.
    """
    if not upper:
        return name
    idx = name.find('[')
    if idx >= 0:
        return name[:idx].upper() + name[idx:]
    if name == name.upper():
        return name
    return name.upper()


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
    *,
    uppercase_ports: bool = False,
    include_power_pins: bool = True,
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
    uppercase_ports : bool
        Use UPPERCASE pin names (CLK, ADDR, etc.) instead of lowercase.
    include_power_pins : bool
        Include VPWR/VGND in Verilog outputs.

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
    paths["sp"] = generate_spice(params, out_dir / f"{stem}.sp", macro_name=mn,
                                 uppercase_ports=uppercase_ports)
    paths["v"] = generate_verilog(params, out_dir / f"{stem}.v", macro_name=mn,
                                  uppercase_ports=uppercase_ports,
                                  include_power_pins=include_power_pins)
    paths["bb_v"] = generate_verilog_blackbox(
        params, out_dir / f"{stem}_bb.v", macro_name=mn,
        uppercase_ports=uppercase_ports,
        include_power_pins=include_power_pins,
    )
    paths["lef"] = generate_lef(params, out_dir / f"{stem}.lef", macro_name=mn,
                                uppercase_ports=uppercase_ports)
    paths["lib"] = generate_liberty(params, out_dir / f"{stem}.lib", macro_name=mn,
                                    uppercase_ports=uppercase_ports)
    return paths


def generate_spice(
    params: MacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    uppercase_ports: bool = False,
) -> Path:
    """Generate a behavioral SPICE model for the SRAM macro."""
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    mn = _macro_name(params, macro_name)
    p = uppercase_ports
    addr_bits = params.num_addr_bits
    data_bits = params.bits
    ben_bits = params.num_ben_bits
    scan = params.scan_chain

    addr_pins = " ".join(_pn(f"addr[{i}]", p) for i in range(addr_bits))
    din_pins = " ".join(_pn(f"din[{i}]", p) for i in range(data_bits))
    dout_pins = " ".join(_pn(f"dout[{i}]", p) for i in range(data_bits))

    lines = [
        f"* Behavioral SPICE model for {mn}",
        f"* {params.words} words x {params.bits} bits, mux {params.mux_ratio}",
        f"* Array: {params.rows} rows x {params.cols} columns",
        f"*",
        f".subckt {mn}",
        f"+  {_pn('clk', p)} {_pn('we', p)} {_pn('cs', p)}",
        f"+  {addr_pins}",
        f"+  {din_pins}",
        f"+  {dout_pins}",
    ]
    if ben_bits:
        ben_pins = " ".join(_pn(f"ben[{i}]", p) for i in range(ben_bits))
        lines.append(f"+  {ben_pins}")
    if scan:
        lines.append(f"+  {_pn('scan_in', p)} {_pn('scan_out', p)} {_pn('scan_en', p)}")
    extra_pins = []
    if params.clock_gating:
        extra_pins.append(_pn("cen", p))
    if params.power_gating:
        extra_pins.append(_pn("sleep", p))
    if params.wl_switchoff:
        extra_pins.append(_pn("wl_off", p))
    if params.burn_in:
        extra_pins.append(_pn("tm", p))
    if extra_pins:
        lines.append(f"+  {' '.join(extra_pins)}")
    lines += [
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
    *,
    uppercase_ports: bool = False,
    include_power_pins: bool = True,
) -> Path:
    """Generate a behavioral Verilog model for the SRAM macro."""
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    mn = _macro_name(params, macro_name)
    up = uppercase_ports
    w = params.words
    b = params.bits
    addr_bits = params.num_addr_bits
    ben_bits = params.num_ben_bits
    scan = params.scan_chain
    num_scan = params.num_scan_flops

    # Pin names
    CLK = _pn('clk', up); WE = _pn('we', up); CS = _pn('cs', up)
    ADDR = _pn('addr', up); DIN = _pn('din', up); DOUT = _pn('dout', up)

    lines = [
        f"// Behavioral Verilog model for {mn}",
        f"// {w} words x {b} bits, mux {params.mux_ratio}",
        f"// Array: {params.rows} rows x {params.cols} columns",
        f"// Address bits: {addr_bits}",
    ]
    if ben_bits:
        lines.append(f"// Byte-enable bits: {ben_bits}")
    if scan:
        lines.append(f"// Scan chain: {num_scan} flops (addr -> we -> cs -> din{' -> ben' if ben_bits else ''})")
    lines += [
        f"",
        f"module {mn} (",
        f"    input  wire               {CLK},",
        f"    input  wire               {WE},",
        f"    input  wire               {CS},",
        f"    input  wire [{addr_bits-1}:0]  {ADDR},",
        f"    input  wire [{b-1}:0]  {DIN},",
        f"    output reg  [{b-1}:0]  {DOUT},",
    ]
    if ben_bits:
        lines.append(f"    input  wire [{ben_bits-1}:0]  {_pn('ben', up)},")
    if scan:
        lines += [
            f"    input  wire              {_pn('scan_in', up)},",
            f"    output wire              {_pn('scan_out', up)},",
            f"    input  wire              {_pn('scan_en', up)},",
        ]
    if params.clock_gating:
        lines.append(f"    input  wire              {_pn('cen', up)},")
    if params.power_gating:
        lines.append(f"    input  wire              {_pn('sleep', up)},")
    if params.wl_switchoff:
        lines.append(f"    input  wire              {_pn('wl_off', up)},")
    if params.burn_in:
        lines.append(f"    input  wire              {_pn('tm', up)},  // physical stress mode — no behavioral effect")
    if include_power_pins:
        lines += [
            f"    inout  wire              VPWR,",
            f"    inout  wire              VGND",
        ]
    else:
        # Remove trailing comma from last port
        lines[-1] = lines[-1].rstrip(',')
    lines += [
        f");",
        f"",
    ]
    if params.burn_in:
        lines += [
            f"    // tm (test mode) controls physical wordline stress — no behavioral model needed",
            f"    /* verilator lint_off UNUSEDSIGNAL */",
            f"    wire _unused_tm = {_pn('tm', up)};",
            f"    /* verilator lint_on UNUSEDSIGNAL */",
            f"",
        ]

    if scan:
        SCAN_IN = _pn('scan_in', up); SCAN_OUT = _pn('scan_out', up); SCAN_EN = _pn('scan_en', up)
        # Scan flop chain: addr[0..N-1], we, cs, din[0..B-1], [ben[0..M-1]]
        lines += [
            f"    // Scan chain registers ({num_scan} flops)",
            f"    reg [{num_scan-1}:0] scan_chain;",
            f"    assign {SCAN_OUT} = scan_chain[{num_scan-1}];",
            f"",
            f"    // Muxed functional inputs",
            f"    wire [{addr_bits-1}:0] addr_int;",
            f"    wire              we_int;",
            f"    wire              cs_int;",
            f"    wire [{b-1}:0]  din_int;",
        ]
        if ben_bits:
            lines.append(f"    wire [{ben_bits-1}:0]  ben_int;")
        lines.append(f"")

        # Assign internal signals from scan chain or functional inputs
        # Chain bit mapping: addr[0..N-1], we, cs, din[0..B-1], [ben[0..M-1]]
        offset = 0
        lines.append(f"    assign addr_int = {SCAN_EN} ? scan_chain[{offset + addr_bits - 1}:{offset}] : {ADDR};")
        offset += addr_bits
        lines.append(f"    assign we_int   = {SCAN_EN} ? scan_chain[{offset}] : {WE};")
        offset += 1
        lines.append(f"    assign cs_int   = {SCAN_EN} ? scan_chain[{offset}] : {CS};")
        offset += 1
        lines.append(f"    assign din_int  = {SCAN_EN} ? scan_chain[{offset + b - 1}:{offset}] : {DIN};")
        offset += b
        if ben_bits:
            BEN = _pn('ben', up)
            lines.append(f"    assign ben_int  = {SCAN_EN} ? scan_chain[{offset + ben_bits - 1}:{offset}] : {BEN};")
        lines += [
            f"",
            f"    // Scan shift register",
            f"    always @(posedge {CLK}) begin",
            f"        if ({SCAN_EN})",
            f"            scan_chain <= {{scan_chain[{num_scan-2}:0], {SCAN_IN}}};",
            f"    end",
            f"",
        ]
        # Use _int signals for memory logic
        addr_sig = "addr_int"
        we_sig = "we_int"
        cs_sig = "cs_int"
        din_sig = "din_int"
        ben_sig = "ben_int"
    else:
        addr_sig = ADDR
        we_sig = WE
        cs_sig = CS
        din_sig = DIN
        ben_sig = _pn('ben', up)

    # Clock gating: ICG (latch-based, glitch-free)
    CEN = _pn('cen', up)
    if params.clock_gating:
        lines += [
            f"    // ICG — latch CEN on CLK low, AND with CLK",
            f"    wire clk_gated;",
            f"    reg cen_latched;",
            f"    /* verilator lint_off LATCH */",
            f"    always_latch if (!{CLK}) cen_latched = {CEN};",
            f"    /* verilator lint_on LATCH */",
            f"    assign clk_gated = {CLK} & cen_latched;",
            f"",
        ]
        clk_sig = "clk_gated"
    else:
        clk_sig = CLK

    # Build active condition: cs [&& !wl_off] [&& !sleep]
    active_conds = ["cs_reg"]
    if params.wl_switchoff:
        active_conds.append("!wl_off_reg")
    if params.power_gating:
        active_conds.append("!sleep_reg")
    active_expr = " && ".join(active_conds)

    # --- Three-block OpenRAM pattern (sram_behavioral_model_pattern.md) ---
    # Block 1: posedge — register inputs with blocking assignment
    # Block 2: negedge — write to memory
    # Block 3: negedge — read from memory, update DOUT

    # Registered input declarations
    lines += [
        f"    reg [{b-1}:0] mem [0:{w-1}];",
        f"",
        f"    // Registered inputs (captured at posedge)",
        f"    reg [{addr_bits-1}:0] addr_reg;",
        f"    reg              we_reg;",
        f"    reg              cs_reg;",
        f"    reg [{b-1}:0]  din_reg;",
    ]
    if ben_bits:
        lines.append(f"    reg [{ben_bits-1}:0]  ben_reg;")
    if params.wl_switchoff:
        lines.append(f"    reg              wl_off_reg;")
    if params.power_gating:
        lines.append(f"    reg              sleep_reg;")

    # Block 1: posedge — capture inputs (blocking = for immediate availability)
    lines += [
        f"",
        f"    // Block 1: Register inputs at posedge (blocking — OpenRAM pattern)",
        f"    /* verilator lint_off BLKSEQ */",
        f"    always @(posedge {clk_sig}) begin",
        f"        addr_reg = {addr_sig};",
        f"        we_reg   = {we_sig};",
        f"        cs_reg   = {cs_sig};",
        f"        din_reg  = {din_sig};",
    ]
    if ben_bits:
        lines.append(f"        ben_reg  = {ben_sig};")
    if params.wl_switchoff:
        lines.append(f"        wl_off_reg = {_pn('wl_off', up)};")
    if params.power_gating:
        lines.append(f"        sleep_reg = {_pn('sleep', up)};")
    lines += [
        f"    end",
        f"    /* verilator lint_on BLKSEQ */",
        f"",
    ]

    # Block 2: negedge — write (blocking — per OpenRAM pattern)
    lines.append(f"    // Block 2: Write at negedge")
    lines.append(f"    /* verilator lint_off BLKSEQ */")
    lines.append(f"    always @(negedge {clk_sig}) begin")
    lines.append(f"        if ({active_expr} && we_reg) begin")
    if ben_bits:
        bytes_per_word = max(1, b // 8)
        for byte_idx in range(bytes_per_word):
            hi = min(byte_idx * 8 + 7, b - 1)
            lo = byte_idx * 8
            ben_idx = min(byte_idx, ben_bits - 1)
            lines.append(f"            if (ben_reg[{ben_idx}]) mem[addr_reg][{hi}:{lo}] = din_reg[{hi}:{lo}];")
    else:
        lines.append(f"            mem[addr_reg] = din_reg;")
    lines += [
        f"        end",
        f"    end",
        f"    /* verilator lint_on BLKSEQ */",
        f"",
    ]

    # Block 3: negedge — read
    lines += [
        f"    // Block 3: Read at negedge (DOUT valid before next posedge)",
        f"    always @(negedge {clk_sig}) begin",
        f"        if ({active_expr} && !we_reg)",
        f"            {DOUT} <= mem[addr_reg];",
        f"    end",
        f"",
        f"endmodule",
        "",
    ]

    out.write_text("\n".join(lines))
    return out


def generate_verilog_blackbox(
    params: MacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    uppercase_ports: bool = False,
    include_power_pins: bool = True,
) -> Path:
    """Generate a blackbox Verilog stub for the SRAM macro.

    This produces a module declaration with ports but no implementation,
    suitable for OpenSTA and synthesis tools that cannot parse behavioral
    Verilog (``reg``, ``always``, etc.).
    """
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    mn = _macro_name(params, macro_name)
    up = uppercase_ports
    b = params.bits
    addr_bits = params.num_addr_bits
    ben_bits = params.num_ben_bits
    scan = params.scan_chain

    lines = [
        f"// Blackbox Verilog stub for {mn}",
        f"// {params.words} words x {b} bits, mux {params.mux_ratio}",
        f"",
        f"(* blackbox *)",
        f"module {mn} (",
        f"    input  wire               {_pn('clk', up)},",
        f"    input  wire               {_pn('we', up)},",
        f"    input  wire               {_pn('cs', up)},",
        f"    input  wire [{addr_bits-1}:0]  {_pn('addr', up)},",
        f"    input  wire [{b-1}:0]  {_pn('din', up)},",
        f"    output wire [{b-1}:0]  {_pn('dout', up)},",
    ]
    if ben_bits:
        lines.append(f"    input  wire [{ben_bits-1}:0]  {_pn('ben', up)},")
    if scan:
        lines += [
            f"    input  wire              {_pn('scan_in', up)},",
            f"    output wire              {_pn('scan_out', up)},",
            f"    input  wire              {_pn('scan_en', up)},",
        ]
    if params.clock_gating:
        lines.append(f"    input  wire              {_pn('cen', up)},")
    if params.power_gating:
        lines.append(f"    input  wire              {_pn('sleep', up)},")
    if params.wl_switchoff:
        lines.append(f"    input  wire              {_pn('wl_off', up)},")
    if params.burn_in:
        lines.append(f"    input  wire              {_pn('tm', up)},")
    if include_power_pins:
        lines += [
            f"    inout  wire              VPWR,",
            f"    inout  wire              VGND",
        ]
    else:
        # Remove trailing comma from last port
        lines[-1] = lines[-1].rstrip(',')
    lines += [
        f");",
        f"endmodule",
        "",
    ]

    out.write_text("\n".join(lines))
    return out
