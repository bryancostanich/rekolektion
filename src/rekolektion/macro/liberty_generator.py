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

    from rekolektion.macro.assembler import compute_macro_params
    from rekolektion.macro.liberty_generator import generate_liberty

    params = compute_macro_params(words=1024, bits=32, mux_ratio=8)
    params.macro_width = 500.0
    params.macro_height = 400.0
    generate_liberty(params, "output/sram_1024x32.lib")
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path

from rekolektion.macro.assembler import MacroParams


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
_OUTPUT_CAP_PF = 0.020   # max output capacitance (sense amp drive strength)

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
# Public API
# ---------------------------------------------------------------------------

def generate_liberty(
    params: MacroParams,
    output_path: str | Path,
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

    cell_name = f"sram_{params.words}x{params.bits}_mux{params.mux_ratio}"
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
        f'    pin (clk) {{',
        f'      direction : input ;',
        f'      capacitance : {_CLK_CAP_PF} ;',
        f'      clock : true ;',
        f'    }}',
        f'',
    ]

    # we pin
    lines += _input_pin_with_timing("we", timing.t_setup_ns, timing.t_hold_ns)
    lines.append('')

    # Address pins
    for i in range(addr_bits):
        lines += _input_pin_with_timing(f"addr[{i}]", timing.t_setup_ns, timing.t_hold_ns)
        lines.append('')

    # Data-in pins
    for i in range(data_bits):
        lines += _input_pin_with_timing(f"din[{i}]", timing.t_setup_ns, timing.t_hold_ns)
        lines.append('')

    # Data-out pins
    for i in range(data_bits):
        lines += _output_pin_with_timing(
            f"dout[{i}]", timing.t_clk_to_q_ns,
            timing.t_rise_ns, timing.t_fall_ns,
        )
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

def _input_pin_with_timing(name: str, setup_ns: float, hold_ns: float) -> list[str]:
    return [
        f'    pin ({name}) {{',
        f'      direction : input ;',
        f'      capacitance : {_INPUT_CAP_PF} ;',
        f'      timing () {{',
        f'        related_pin : "clk" ;',
        f'        timing_type : setup_rising ;',
        f'        rise_constraint (scalar) {{',
        f'          values ("{setup_ns:.4f}") ;',
        f'        }}',
        f'        fall_constraint (scalar) {{',
        f'          values ("{setup_ns:.4f}") ;',
        f'        }}',
        f'      }}',
        f'      timing () {{',
        f'        related_pin : "clk" ;',
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
) -> list[str]:
    return [
        f'    pin ({name}) {{',
        f'      direction : output ;',
        f'      max_capacitance : {_OUTPUT_CAP_PF} ;',
        f'      timing () {{',
        f'        related_pin : "clk" ;',
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
