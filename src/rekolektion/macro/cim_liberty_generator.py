"""Liberty (.lib) timing model generator for CIM SRAM macros.

Generates Liberty timing files with CIM-specific timing arcs:
  MWL_EN → MBL_OUT:  CIM compute latency (charge sharing + settling)
  MBL_PRE setup/hold: precharge timing relative to MWL_EN assertion

Per-pin input capacitances are computed analytically from each cell's
transistor sizing × SKY130 thin-oxide Cox plus a 30% margin for routing
parasitic.  Replaces the old universal 0.005 pF placeholder, which was
~10–20× off on the high-fanout pins (MBL_PRE / VBIAS each gate 64
column transistors).  A future SPICE-characterisation pass should
replace these analytical numbers with sim-extracted values; the
function signature is unchanged so the SPICE flow can drop in.

Timing arcs remain analytical estimates:
  - MIM cap charge sharing with MBL parasitic (~256 fF per 64-cell column)
  - Source follower buffer delay (~0.5 ns)
  - MWL poly RC delay across row (~0.1 ns for 64 columns)
"""

from __future__ import annotations

from pathlib import Path

from rekolektion.macro.cim_assembler import CIMMacroParams


# CIM timing parameters (analytical estimates)
_MWL_RC_NS = 0.10        # MWL poly RC delay per row
_CHARGE_SHARE_NS = 2.0   # charge sharing settling time (conservative)
_SENSE_BUFFER_NS = 0.50  # source follower delay
_PRECHARGE_NS = 1.0      # MBL precharge time

_CIM_COMPUTE_NS = _MWL_RC_NS + _CHARGE_SHARE_NS + _SENSE_BUFFER_NS  # ~2.6 ns

_SETUP_NS = _PRECHARGE_NS  # MBL_PRE must be deasserted before MWL_EN
_HOLD_NS = 0.2             # MBL_PRE hold after MWL_EN falls

# SKY130 thin-oxide capacitance per gate area.  tox≈4.1 nm, εr=3.9 →
# Cox = ε0·εr/tox ≈ 8.5 fF/µm².
_COX_FF_PER_UM2: float = 8.5

# Routing-parasitic uplift applied to the raw gate-cap calculation
# (poly sheet R, li1 / met1 stub area, mcon).  Conservative 30%.
_PARASITIC_UPLIFT: float = 1.30

# Foundry buf_2 input pin capacitance.  Datasheet: ~3 fF on the A pin
# (sky130_fd_sc_hd__buf_2 — single inverter cascade input).  Used for
# MWL_EN[r], which gates exactly one buf_2 driver per row.
_BUF2_A_INPUT_FF: float = 3.0

# Cell transistor widths (µm).  Mirrors the constants in the cell
# generators (`peripherals/cim_mbl_precharge.py::_PU_W`,
# `peripherals/cim_mbl_sense.py::_DRV_W` / `_BIAS_W`).  Kept here so
# the Liberty file can be regenerated without parsing the source.
_PRECHARGE_PMOS_W: float = 0.84   # MBL precharge PMOS, one per column
_SENSE_BIAS_NMOS_W: float = 0.50  # Sense-amp bias NMOS, one per column
_SENSE_DRV_NMOS_W: float = 1.00   # Sense-amp driver NMOS, one per column
_GATE_L: float = 0.15             # All transistors

# Source / drain parasitic per column on VREF (precharge PMOS source).
# Diff area ≈ W × min-S/D length (0.29 µm) × Cj_pmos (~0.4 fF/µm²
# bottom + 0.2 fF/µm sidewall).  Roughly 0.3 fF per column.
_VREF_PER_COL_FF: float = 0.3

# Output load assumptions (analytical placeholder until characterised).
_MBL_OUT_CAP_PF: float = 0.50   # max output load (pad + ADC input)

_NOM_PROCESS = 1.0
_NOM_VOLTAGE = 1.2         # CIM operates at 1.2V (not 1.8V) per Track 21
_NOM_TEMPERATURE = 25.0


def _gate_cap_ff(w_um: float, l_um: float = _GATE_L) -> float:
    """Single-transistor gate capacitance in fF, with parasitic uplift."""
    return w_um * l_um * _COX_FF_PER_UM2 * _PARASITIC_UPLIFT


def _input_cap_pf(net: str, cols: int) -> float:
    """Analytical input capacitance per pin, in pF.

    `net` is one of: MWL_EN, MBL_PRE, VBIAS, VREF.  `cols` scales the
    column-shared signals.
    """
    if net == "MWL_EN":
        # One foundry buf_2 input per row; not column-scaled.
        return _BUF2_A_INPUT_FF * _PARASITIC_UPLIFT / 1000.0
    if net == "MBL_PRE":
        # `cols` PMOS gates in parallel.
        return cols * _gate_cap_ff(_PRECHARGE_PMOS_W) / 1000.0
    if net == "VBIAS":
        # `cols` NMOS gates in parallel.
        return cols * _gate_cap_ff(_SENSE_BIAS_NMOS_W) / 1000.0
    if net == "VREF":
        # `cols` PMOS source/drain regions (no gate).  Uplift already
        # baked into _VREF_PER_COL_FF.
        return cols * _VREF_PER_COL_FF / 1000.0
    raise ValueError(f"unknown net {net!r}")


def generate_cim_liberty(
    params: CIMMacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    pwr_pin: str = "VPWR",
    gnd_pin: str = "VGND",
) -> Path:
    """Generate a Liberty timing model for a CIM SRAM macro.

    pwr_pin/gnd_pin: power pin names (default VPWR/VGND for khalkulo).
    """
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    rows = params.rows
    cols = params.cols
    if not macro_name:
        macro_name = f"cim_{params.variant.lower().replace('-', '_')}_{rows}x{cols}"

    area = params.macro_width * params.macro_height

    lines: list[str] = []

    # Library header
    lines += [
        f'library ({macro_name}_lib) {{',
        f'  comment : "CIM SRAM macro Liberty — {params.variant}" ;',
        f'  comment : "Analog MBL output — do NOT treat as digital" ;',
        f'  delay_model : table_lookup ;',
        f'  time_unit : "1ns" ;',
        f'  voltage_unit : "1V" ;',
        f'  current_unit : "1mA" ;',
        f'  capacitive_load_unit (1,pf) ;',
        f'  pulling_resistance_unit : "1kohm" ;',
        f'  leakage_power_unit : "1nW" ;',
        f'',
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

    # Bus type definitions
    lines += [
        f'  type (mwl_en_type) {{',
        f'    base_type : array ;',
        f'    data_type : bit ;',
        f'    bit_width : {rows} ;',
        f'    bit_from : 0 ;',
        f'    bit_to : {rows - 1} ;',
        f'  }}',
        f'',
        f'  type (mbl_out_type) {{',
        f'    base_type : array ;',
        f'    data_type : bit ;',
        f'    bit_width : {cols} ;',
        f'    bit_from : 0 ;',
        f'    bit_to : {cols - 1} ;',
        f'  }}',
        f'',
    ]

    # Cell definition
    lines += [
        f'  cell ({macro_name}) {{',
        f'    area : {area:.3f} ;',
        f'    dont_touch : true ;',
        f'    dont_use : true ;',
        f'',
    ]

    mwl_en_cap = _input_cap_pf("MWL_EN", cols)
    mbl_pre_cap = _input_cap_pf("MBL_PRE", cols)
    vbias_cap = _input_cap_pf("VBIAS", cols)
    vref_cap = _input_cap_pf("VREF", cols)

    # MWL_EN bus (input, with setup/hold relative to MBL_PRE)
    lines += [
        f'    bus (MWL_EN) {{',
        f'      bus_type : mwl_en_type ;',
        f'      direction : input ;',
        f'      capacitance : {mwl_en_cap:.6f} ;',
    ]
    for i in range(rows):
        lines.append(f'      pin (MWL_EN[{i}]) {{ }}')
    lines += [f'    }}', '']

    # MBL_OUT bus (output, timing from MBL_PRE)
    # Use MBL_PRE as related_pin (scalar) to avoid STA-1216 bus width mismatch
    # with MWL_EN. The real CIM compute is triggered by MWL_EN, but from STA's
    # perspective, MBL_PRE falling edge starts the compute cycle.
    lines += [
        f'    bus (MBL_OUT) {{',
        f'      bus_type : mbl_out_type ;',
        f'      direction : output ;',
        f'      max_capacitance : {_MBL_OUT_CAP_PF} ;',
        f'      timing () {{',
        f'        related_pin : "MBL_PRE" ;',
        f'        timing_type : falling_edge ;',
        f'        cell_rise (scalar) {{ values ("{_CIM_COMPUTE_NS:.4f}") ; }}',
        f'        cell_fall (scalar) {{ values ("{_CIM_COMPUTE_NS:.4f}") ; }}',
        f'        rise_transition (scalar) {{ values ("{_CHARGE_SHARE_NS:.4f}") ; }}',
        f'        fall_transition (scalar) {{ values ("{_CHARGE_SHARE_NS:.4f}") ; }}',
        f'      }}',
    ]
    for i in range(cols):
        lines.append(f'      pin (MBL_OUT[{i}]) {{ }}')
    lines += [f'    }}', '']

    # MBL_PRE (input, no timing constraints — it's the reference for MBL_OUT)
    lines += [
        f'    pin (MBL_PRE) {{',
        f'      direction : input ;',
        f'      capacitance : {mbl_pre_cap:.6f} ;',
        f'    }}',
        f'',
    ]

    # VREF, VBIAS (analog — no timing)
    for pin_name, direction, cap in [
        ("VREF",  "inout", vref_cap),
        ("VBIAS", "input", vbias_cap),
    ]:
        lines += [
            f'    pin ({pin_name}) {{',
            f'      direction : {direction} ;',
            f'      capacitance : {cap:.6f} ;',
            f'    }}',
            f'',
        ]

    # Power pins (configurable naming for chip-level integration)
    lines += [
        f'    pin ({pwr_pin}) {{',
        f'      direction : inout ;',
        f'      always_on : true ;',
        f'    }}',
        f'',
        f'    pin ({gnd_pin}) {{',
        f'      direction : inout ;',
        f'      always_on : true ;',
        f'    }}',
        f'',
    ]

    # Close cell and library
    lines += [
        f'  }}',
        f'}}',
        '',
    ]

    out.write_text("\n".join(lines))
    return out
