"""Liberty (.lib) timing model generator for SRAM macros.

Generates Liberty timing files with analytically-computed timing values
based on array dimensions and SKY130 device/interconnect parameters.

Timing model:
  Read path:  CLK → decoder → word line → bit-line develop → sense amp → dout
  Write path: CLK → decoder → word line → write driver → cell flip

Key parameters are derived from:
  - Array size (rows, cols) → BL capacitance, WL RC delay
  - Mux ratio → column decoder contribution
  - Foundry cell I_read (~5 μA at VDD=1.8V, W/L=0.42/0.15)
  - Sense amp trip voltage (~100 mV differential)
  - NAND decoder delay per stage (~0.15 ns in SKY130)

Usage::

    from rekolektion.macro.assembler import MacroParams, build_floorplan
    from rekolektion.macro.liberty_generator import generate_liberty

    p = MacroParams(words=256, bits=64, mux_ratio=2)
    generate_liberty(p, "output/sram_256x64.lib")
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path


# ---------------------------------------------------------------------------
# Internal liberty params dataclass
# ---------------------------------------------------------------------------
# Carved out of the deleted V1 `assembler.py`.  The Liberty emission
# engine below works against this flat dataclass; the public
# `generate_liberty(MacroParams, ...)` API at the bottom of this file
# adapts the modern V2 MacroParams (foundry-bitcell, mux-aware) into
# this internal shape.

@dataclass
class _LibertyMacroParams:
    """Internal flat parameter shape for the liberty emission engine."""
    words: int
    bits: int
    mux_ratio: int
    rows: int
    cols: int
    num_addr_bits: int
    num_row_bits: int
    num_col_bits: int
    cell_name: str = ""
    cell_width: float = 0.0
    cell_height: float = 0.0
    macro_width: float = 0.0
    macro_height: float = 0.0
    write_enable: bool = False
    scan_chain: bool = False
    clock_gating: bool = False
    power_gating: bool = False
    wl_switchoff: bool = False
    burn_in: bool = False

    @property
    def num_ben_bits(self) -> int:
        if not self.write_enable:
            return 0
        return max(1, self.bits // 8)

    @property
    def num_scan_flops(self) -> int:
        if not self.scan_chain:
            return 0
        return self.num_addr_bits + 2 + self.bits + self.num_ben_bits


# Backwards-compatible alias for code that still references the
# legacy `MacroParams` symbol from this module.
MacroParams = _LibertyMacroParams


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


# ---------------------------------------------------------------------------
# SKY130 device / interconnect parameters for timing estimation
# ---------------------------------------------------------------------------

# Bitcell read current: access transistor in series with pull-down, NMOS W=0.42
# L=0.15 at VDD=1.8V.  Typical ~20-30 μA at TT, ~10 μA at SS corner.
# Use SS worst-case for timing margin.
_I_READ_UA = 10.0

# Bit-line capacitance per row: foundry cell at 0.48 μm pitch contributes
# ~0.4 fF junction + ~0.1 fF routing = ~0.5-1.0 fF per cell.
# Use 1.0 fF as conservative estimate including met2 coupling.
_C_BL_PER_ROW_FF = 1.0  # fF per cell on the bit line

# Sense amplifier trip voltage (minimum BL differential to sense correctly)
_SA_TRIP_MV = 100.0

# Sense amplifier internal delay (latch + output buffer)
_SA_DELAY_NS = 0.5

# NAND decoder delay per stage (sky130 NAND2 FO4 ~0.15 ns)
_DECODER_STAGE_NS = 0.15

# Word-line RC delay per column (poly + contact resistance)
_WL_RC_PER_COL_PS = 0.5  # ps per column

# Write driver pull-down time (time to develop enough BL swing to flip cell)
# Faster than read because write driver is stronger than bitcell
_WRITE_DRIVER_NS = 0.3

# Column mux transistor delay (pass gate RC)
_MUX_DELAY_NS = 0.1

# Output buffer delay (sense amp output to pad)
_OUTPUT_BUFFER_NS = 0.15

# Clock distribution / setup overhead
_CLK_SKEW_NS = 0.1

# Capacitance estimates
_INPUT_CAP_PF = 0.005   # input pin capacitance (gate load of decoder)
_CLK_CAP_PF = 0.010     # clock pin capacitance (drives decoder + control)
_OUTPUT_CAP_PF = 0.470   # max output capacitance (sense amp DOUT driver)
# Computed from OpenRAM sense amp extraction (sky130_fd_bd_sram__openram_sense_amp):
#   DOUT PFET: W=1.26um L=0.15um → Id_sat=452uA → C_max=0.471pF (rise-limited)
#   DOUT NFET: W=0.65um L=0.15um → Id_sat=1004uA → C_max=1.046pF (fall)
# Conservative value: PFET-limited = 0.471 pF (comparable to SKY130 buf_4 at 0.561 pF).
# Previous value (0.020 pF) was 23x too low, causing 120K+ unnecessary buffer insertions.

# Operating conditions
_NOM_PROCESS = 1.0
_NOM_VOLTAGE = 1.8
_NOM_TEMPERATURE = 25.0


# ---------------------------------------------------------------------------
# Timing computation
# ---------------------------------------------------------------------------

@dataclass
class SRAMTiming:
    """Computed timing parameters for an SRAM macro."""
    # Read path
    t_decoder_ns: float      # address decoder delay
    t_wl_ns: float           # word-line RC delay
    t_bl_develop_ns: float   # bit-line differential development
    t_mux_ns: float          # column mux delay
    t_sa_ns: float           # sense amplifier delay
    t_output_ns: float       # output buffer delay
    t_clk_to_q_ns: float     # total clock-to-Q (read)

    # Write path
    t_write_ns: float        # total write time

    # Constraints
    t_setup_ns: float        # setup time (addr/din before CLK)
    t_hold_ns: float         # hold time (addr/din after CLK)

    # Transitions
    t_rise_ns: float         # output rise transition
    t_fall_ns: float         # output fall transition

    def summary(self) -> str:
        return (
            f"  Read path breakdown:\n"
            f"    Decoder:     {self.t_decoder_ns:.3f} ns\n"
            f"    Word line:   {self.t_wl_ns:.3f} ns\n"
            f"    BL develop:  {self.t_bl_develop_ns:.3f} ns\n"
            f"    Column mux:  {self.t_mux_ns:.3f} ns\n"
            f"    Sense amp:   {self.t_sa_ns:.3f} ns\n"
            f"    Output buf:  {self.t_output_ns:.3f} ns\n"
            f"    ----------------------------\n"
            f"    CLK-to-Q:    {self.t_clk_to_q_ns:.3f} ns\n"
            f"\n"
            f"  Write time:    {self.t_write_ns:.3f} ns\n"
            f"  Setup:         {self.t_setup_ns:.3f} ns\n"
            f"  Hold:          {self.t_hold_ns:.3f} ns\n"
        )


def compute_timing(params: MacroParams) -> SRAMTiming:
    """Compute SRAM timing from array dimensions.

    Uses analytical models based on SKY130 device parameters.
    All values include ~20% margin over nominal estimates.
    """
    rows = params.rows
    cols = params.cols
    mux_ratio = params.mux_ratio

    # Number of decoder stages: ceil(log2(rows)) NAND gates in series
    # Pre-decode reduces this, but conservatively assume full decode
    num_row_bits = params.num_row_bits
    num_decoder_stages = max(1, math.ceil(num_row_bits / 2))  # 2-input NAND tree
    t_decoder = num_decoder_stages * _DECODER_STAGE_NS

    # Word-line RC delay across the row
    t_wl = cols * _WL_RC_PER_COL_PS / 1000.0  # convert ps to ns

    # Bit-line development time: t = C_BL * V_trip / I_read
    c_bl_ff = rows * _C_BL_PER_ROW_FF
    c_bl_pf = c_bl_ff / 1000.0
    t_bl_develop = c_bl_pf * (_SA_TRIP_MV / 1000.0) / (_I_READ_UA / 1000.0)  # ns

    # Column mux delay (only if mux_ratio > 1)
    t_mux = _MUX_DELAY_NS if mux_ratio > 1 else 0.0

    # Sense amp + output
    t_sa = _SA_DELAY_NS
    t_output = _OUTPUT_BUFFER_NS

    # Total read path (clock-to-Q) with 20% margin
    t_clk_to_q_raw = t_decoder + t_wl + t_bl_develop + t_mux + t_sa + t_output
    t_clk_to_q = t_clk_to_q_raw * 1.2

    # Write time: decoder + WL + write driver (cell flip is fast)
    t_write_raw = t_decoder + t_wl + _WRITE_DRIVER_NS + t_mux
    t_write = t_write_raw * 1.2

    # Setup time: address must be stable before CLK so decoder output is valid
    # when WL rises.  Conservative: full decoder delay + margin.
    t_setup = t_decoder * 1.2 + _CLK_SKEW_NS

    # Hold time: address must remain stable briefly after CLK edge.
    # Typically very short — just need WL to be asserted.
    t_hold = 0.1 + _CLK_SKEW_NS

    # Output transitions (depends on sense amp drive strength and load)
    t_rise = 0.10  # ns — fast for lightly-loaded output
    t_fall = 0.10

    return SRAMTiming(
        t_decoder_ns=t_decoder,
        t_wl_ns=t_wl,
        t_bl_develop_ns=t_bl_develop,
        t_mux_ns=t_mux,
        t_sa_ns=t_sa,
        t_output_ns=t_output,
        t_clk_to_q_ns=round(t_clk_to_q, 4),
        t_write_ns=round(t_write, 4),
        t_setup_ns=round(t_setup, 4),
        t_hold_ns=round(t_hold, 4),
        t_rise_ns=t_rise,
        t_fall_ns=t_fall,
    )


# ---------------------------------------------------------------------------
# Liberty emission engine (operates on the internal _LibertyMacroParams)
# ---------------------------------------------------------------------------

def _emit_liberty(
    params: MacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    uppercase_ports: bool = False,
) -> Path:
    """Generate a Liberty timing model for the SRAM macro.

    Timing values are computed analytically from array dimensions
    using SKY130 device parameters.  See compute_timing() for details.

    Parameters
    ----------
    params : MacroParams
        Macro parameters (words, bits, dimensions, etc.).
    output_path : path
        Write .lib to this file.

    Returns
    -------
    Path
        The output file path.
    """
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    timing = compute_timing(params)

    cell_name = macro_name or f"sram_{params.words}x{params.bits}_mux{params.mux_ratio}"
    addr_bits = params.num_addr_bits
    data_bits = params.bits
    area_um2 = params.macro_width * params.macro_height

    lines: list[str] = []

    # Library header
    lines += [
        f'library ({cell_name}_lib) {{',
        f'  comment : "Liberty timing model for {cell_name}";',
        f'  comment : "Timing from analytical model — see compute_timing()";',
        f'  delay_model : table_lookup ;',
        f'  time_unit : "1ns" ;',
        f'  voltage_unit : "1V" ;',
        f'  current_unit : "1mA" ;',
        f'  capacitive_load_unit (1,pf) ;',
        f'  pulling_resistance_unit : "1kohm" ;',
        f'  leakage_power_unit : "1nW" ;',
        f'',
        f'  /* Threshold parameters required by OpenSTA */',
        f'  input_threshold_pct_rise : 50 ;',
        f'  input_threshold_pct_fall : 50 ;',
        f'  output_threshold_pct_rise : 50 ;',
        f'  output_threshold_pct_fall : 50 ;',
        f'  slew_lower_threshold_pct_rise : 20 ;',
        f'  slew_upper_threshold_pct_rise : 80 ;',
        f'  slew_lower_threshold_pct_fall : 20 ;',
        f'  slew_upper_threshold_pct_fall : 80 ;',
        f'',
        f'  nom_process : {_NOM_PROCESS:.1f} ;',
        f'  nom_voltage : {_NOM_VOLTAGE:.1f} ;',
        f'  nom_temperature : {_NOM_TEMPERATURE:.1f} ;',
        f'',
        f'  operating_conditions (nom) {{',
        f'    process : {_NOM_PROCESS:.1f} ;',
        f'    voltage : {_NOM_VOLTAGE:.1f} ;',
        f'    temperature : {_NOM_TEMPERATURE:.1f} ;',
        f'  }}',
        f'  default_operating_conditions : nom ;',
        f'',
    ]

    up = uppercase_ports
    CLK = _pn('clk', up)
    ADDR = _pn('addr', up)
    DIN = _pn('din', up)
    DOUT = _pn('dout', up)

    # Bus type definitions (required before cell for bus() groups)
    bus_types = [
        (ADDR, addr_bits),
        (DIN, data_bits),
        (DOUT, data_bits),
    ]
    ben_bits_val = params.num_ben_bits
    if ben_bits_val:
        BEN = _pn('ben', up)
        bus_types.append((BEN, ben_bits_val))
    for bname, bwidth in bus_types:
        lines += [
            f'  type ({bname}_type) {{',
            f'    base_type : array ;',
            f'    data_type : bit ;',
            f'    bit_width : {bwidth} ;',
            f'    bit_from : 0 ;',
            f'    bit_to : {bwidth - 1} ;',
            f'  }}',
            f'',
        ]

    # Cell definition
    lines += [
        f'  cell ({cell_name}) {{',
        f'    area : {area_um2:.3f} ;',
        f'    dont_touch : true ;',
        f'    dont_use : true ;',
        f'',
    ]

    # clk pin
    lines += [
        f'    pin ({CLK}) {{',
        f'      direction : input ;',
        f'      capacitance : {_CLK_CAP_PF} ;',
        f'      clock : true ;',
        f'    }}',
        f'',
    ]

    # we pin
    lines += _input_pin_with_timing(_pn("we", up), timing.t_setup_ns, timing.t_hold_ns, clk_pin=CLK)
    lines.append('')

    # cs pin
    lines += _input_pin_with_timing(_pn("cs", up), timing.t_setup_ns, timing.t_hold_ns, clk_pin=CLK)
    lines.append('')

    # Address bus
    ADDR = _pn("addr", up)
    lines += _input_bus_with_timing(ADDR, addr_bits, timing.t_setup_ns, timing.t_hold_ns, clk_pin=CLK)
    lines.append('')

    # Data-in bus
    DIN = _pn("din", up)
    lines += _input_bus_with_timing(DIN, data_bits, timing.t_setup_ns, timing.t_hold_ns, clk_pin=CLK)
    lines.append('')

    # Byte-enable bus
    ben_bits = params.num_ben_bits
    if ben_bits:
        BEN = _pn("ben", up)
        lines += _input_bus_with_timing(BEN, ben_bits, timing.t_setup_ns, timing.t_hold_ns, clk_pin=CLK)
        lines.append('')

    # Scan chain pins
    if params.scan_chain:
        lines += _input_pin_with_timing(_pn("scan_in", up), timing.t_setup_ns, timing.t_hold_ns, clk_pin=CLK)
        lines.append('')
        lines += _input_pin_with_timing(_pn("scan_en", up), timing.t_setup_ns, timing.t_hold_ns, clk_pin=CLK)
        lines.append('')
        lines += _output_pin_with_timing(
            _pn("scan_out", up), timing.t_clk_to_q_ns,
            timing.t_rise_ns, timing.t_fall_ns, clk_pin=CLK,
        )
        lines.append('')

    # Feature control pins
    for feat_flag, pin_name in [
        (params.clock_gating, "cen"),
        (params.power_gating, "sleep"),
        (params.wl_switchoff, "wl_off"),
        (params.burn_in, "tm"),
    ]:
        if feat_flag:
            lines += _input_pin_with_timing(_pn(pin_name, up), timing.t_setup_ns, timing.t_hold_ns, clk_pin=CLK)
            lines.append('')

    # Data-out bus
    DOUT = _pn("dout", up)
    lines += _output_bus_with_timing(DOUT, data_bits, timing.t_clk_to_q_ns,
                                     timing.t_rise_ns, timing.t_fall_ns, clk_pin=CLK)
    lines.append('')

    # Power pins
    lines += [
        f'    pin (VPWR) {{',
        f'      direction : inout ;',
        f'      always_on : true ;',
        f'    }}',
        f'',
        f'    pin (VGND) {{',
        f'      direction : inout ;',
        f'      always_on : true ;',
        f'    }}',
        f'',
    ]

    # Close cell and library
    lines += [
        f'  }}',  # end cell
        f'}}',    # end library
        '',
    ]

    out.write_text("\n".join(lines))
    return out


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _input_pin_with_timing(name: str, setup_ns: float, hold_ns: float, *, clk_pin: str = "clk") -> list[str]:
    return [
        f'    pin ({name}) {{',
        f'      direction : input ;',
        f'      capacitance : {_INPUT_CAP_PF} ;',
        f'      timing () {{',
        f'        related_pin : "{clk_pin}" ;',
        f'        timing_type : setup_rising ;',
        f'        rise_constraint (scalar) {{',
        f'          values ("{setup_ns:.4f}") ;',
        f'        }}',
        f'        fall_constraint (scalar) {{',
        f'          values ("{setup_ns:.4f}") ;',
        f'        }}',
        f'      }}',
        f'      timing () {{',
        f'        related_pin : "{clk_pin}" ;',
        f'        timing_type : hold_rising ;',
        f'        rise_constraint (scalar) {{',
        f'          values ("{hold_ns:.4f}") ;',
        f'        }}',
        f'        fall_constraint (scalar) {{',
        f'          values ("{hold_ns:.4f}") ;',
        f'        }}',
        f'      }}',
        f'    }}',
    ]


def _output_pin_with_timing(
    name: str, clk_to_q_ns: float,
    rise_ns: float, fall_ns: float,
    *, clk_pin: str = "clk",
) -> list[str]:
    return [
        f'    pin ({name}) {{',
        f'      direction : output ;',
        f'      max_capacitance : {_OUTPUT_CAP_PF} ;',
        f'      timing () {{',
        f'        related_pin : "{clk_pin}" ;',
        f'        timing_type : rising_edge ;',
        f'        cell_rise (scalar) {{',
        f'          values ("{clk_to_q_ns:.4f}") ;',
        f'        }}',
        f'        cell_fall (scalar) {{',
        f'          values ("{clk_to_q_ns:.4f}") ;',
        f'        }}',
        f'        rise_transition (scalar) {{',
        f'          values ("{rise_ns:.4f}") ;',
        f'        }}',
        f'        fall_transition (scalar) {{',
        f'          values ("{fall_ns:.4f}") ;',
        f'        }}',
        f'      }}',
        f'    }}',
    ]


def _input_bus_with_timing(
    name: str, width: int, setup_ns: float, hold_ns: float, *, clk_pin: str = "clk",
) -> list[str]:
    """Generate a Liberty bus() group for an input bus with timing."""
    lines = [
        f'    bus ({name}) {{',
        f'      bus_type : {name}_type ;',
        f'      direction : input ;',
        f'      capacitance : {_INPUT_CAP_PF} ;',
        f'      timing () {{',
        f'        related_pin : "{clk_pin}" ;',
        f'        timing_type : setup_rising ;',
        f'        rise_constraint (scalar) {{ values ("{setup_ns:.4f}") ; }}',
        f'        fall_constraint (scalar) {{ values ("{setup_ns:.4f}") ; }}',
        f'      }}',
        f'      timing () {{',
        f'        related_pin : "{clk_pin}" ;',
        f'        timing_type : hold_rising ;',
        f'        rise_constraint (scalar) {{ values ("{hold_ns:.4f}") ; }}',
        f'        fall_constraint (scalar) {{ values ("{hold_ns:.4f}") ; }}',
        f'      }}',
    ]
    for i in range(width):
        lines.append(f'      pin ({name}[{i}]) {{ }}')
    lines.append(f'    }}')
    return lines


def _output_bus_with_timing(
    name: str, width: int, clk_to_q_ns: float,
    rise_ns: float, fall_ns: float, *, clk_pin: str = "clk",
) -> list[str]:
    """Generate a Liberty bus() group for an output bus with timing."""
    lines = [
        f'    bus ({name}) {{',
        f'      bus_type : {name}_type ;',
        f'      direction : output ;',
        f'      max_capacitance : {_OUTPUT_CAP_PF} ;',
        f'      timing () {{',
        f'        related_pin : "{clk_pin}" ;',
        f'        timing_type : rising_edge ;',
        f'        cell_rise (scalar) {{ values ("{clk_to_q_ns:.4f}") ; }}',
        f'        cell_fall (scalar) {{ values ("{clk_to_q_ns:.4f}") ; }}',
        f'        rise_transition (scalar) {{ values ("{rise_ns:.4f}") ; }}',
        f'        fall_transition (scalar) {{ values ("{fall_ns:.4f}") ; }}',
        f'      }}',
    ]
    for i in range(width):
        lines.append(f'      pin ({name}[{i}]) {{ }}')
    lines.append(f'    }}')
    return lines


# ---------------------------------------------------------------------------
# Public API: V2 (foundry-cell, mux-aware) MacroParams adapter
# ---------------------------------------------------------------------------

def generate_liberty(
    p,                     # rekolektion.macro.assembler.MacroParams (v2)
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    uppercase_ports: bool = True,
) -> Path:
    """Write a Liberty (.lib) file for a v2 SRAM macro.

    Builds an internal `_LibertyMacroParams` from the v2 params + the
    floorplan + the foundry bitcell info, then dispatches to the
    analytical emission engine `_emit_liberty`.

    Defaults to ``uppercase_ports=True`` to match the v2 LEF
    convention.
    """
    # Imported here to avoid a circular import (assembler -> ... ->
    # liberty_generator at module load time).
    from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
    from rekolektion.macro.assembler import build_floorplan

    fp = build_floorplan(p)
    bc = load_foundry_sp_bitcell()
    macro_w, macro_h = fp.macro_size
    num_row_bits = int(math.log2(p.rows))
    num_col_bits = int(math.log2(p.mux_ratio))
    legacy = _LibertyMacroParams(
        words=p.words,
        bits=p.bits,
        mux_ratio=p.mux_ratio,
        rows=p.rows,
        cols=p.cols,
        num_addr_bits=p.num_addr_bits,
        num_row_bits=num_row_bits,
        num_col_bits=num_col_bits,
        cell_name=bc.cell_name,
        cell_width=bc.cell_width,
        cell_height=bc.cell_height,
        macro_width=macro_w,
        macro_height=macro_h,
    )
    return _emit_liberty(
        legacy,
        output_path,
        macro_name=macro_name or p.top_cell_name,
        uppercase_ports=uppercase_ports,
    )
