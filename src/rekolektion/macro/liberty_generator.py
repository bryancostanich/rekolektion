"""Liberty (.lib) timing model generator for SRAM macros.

Generates Liberty timing files with conservative placeholder values
for use in OpenLane synthesis and STA.  Real timing will come from
SPICE characterisation; these placeholders allow initial P&R to proceed.

Usage::

    from rekolektion.macro.assembler import compute_macro_params
    from rekolektion.macro.liberty_generator import generate_liberty

    params = compute_macro_params(words=1024, bits=32, mux_ratio=8)
    params.macro_width = 500.0
    params.macro_height = 400.0
    generate_liberty(params, "output/sram_1024x32.lib")
"""

from __future__ import annotations

from pathlib import Path

from rekolektion.macro.assembler import MacroParams


# ---------------------------------------------------------------------------
# Placeholder timing / capacitance values
# ---------------------------------------------------------------------------

_SETUP_NS = 1.0       # setup time (addr/din to clk rising)
_HOLD_NS = 0.5        # hold time (addr/din to clk rising)
_CLK_TO_Q_NS = 2.0    # clock-to-Q delay (read path)
_WE_SETUP_NS = 1.0    # write-enable setup to clk

_INPUT_CAP_PF = 0.005  # placeholder input pin capacitance
_OUTPUT_CAP_PF = 0.010  # placeholder output max capacitance

_NOM_PROCESS = 1.0
_NOM_VOLTAGE = 1.8
_NOM_TEMPERATURE = 25.0


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def generate_liberty(
    params: MacroParams,
    output_path: str | Path,
) -> Path:
    """Generate a Liberty timing model for the SRAM macro.

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

    cell_name = f"sram_{params.words}x{params.bits}_mux{params.mux_ratio}"
    addr_bits = params.num_addr_bits
    data_bits = params.bits
    area_um2 = params.macro_width * params.macro_height

    lines: list[str] = []

    # Library header
    lines += [
        f'library ({cell_name}_lib) {{',
        f'  comment : "Placeholder Liberty model for {cell_name}";',
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
        f'      capacitance : {_INPUT_CAP_PF} ;',
        f'      clock : true ;',
        f'    }}',
        f'',
    ]

    # we pin with setup/hold to clk
    lines += _input_pin_with_timing("we", _WE_SETUP_NS, _HOLD_NS)
    lines.append('')

    # Address pins with setup/hold to clk
    for i in range(addr_bits):
        lines += _input_pin_with_timing(f"addr[{i}]", _SETUP_NS, _HOLD_NS)
        lines.append('')

    # Data-in pins with setup/hold to clk
    for i in range(data_bits):
        lines += _input_pin_with_timing(f"din[{i}]", _SETUP_NS, _HOLD_NS)
        lines.append('')

    # Data-out pins with clock-to-Q timing
    for i in range(data_bits):
        lines += _output_pin_with_timing(f"dout[{i}]", _CLK_TO_Q_NS)
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
    """Generate an input pin block with setup/hold constraints to clk."""
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


def _output_pin_with_timing(name: str, clk_to_q_ns: float) -> list[str]:
    """Generate an output pin block with clock-to-Q delay."""
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
        f'          values ("0.1000") ;',
        f'        }}',
        f'        fall_transition (scalar) {{',
        f'          values ("0.1000") ;',
        f'        }}',
        f'      }}',
        f'    }}',
    ]
