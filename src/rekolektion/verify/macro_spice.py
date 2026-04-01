"""Macro-level SPICE characterization for production features.

Generates ngspice testbenches that measure the impact of each production
feature using sub-circuit composition: bitcell column + precharge + sense amp
+ write driver + decoder.  Measures:

1. Baseline read/write access time (column-level transient)
2. Clock gating — dynamic current with CEN=0 vs CEN=1
3. Power gating — leakage with SLEEP=0 vs SLEEP=1
4. WL switchoff — data retention with WL_OFF asserted
5. Burn-in — stress current with all wordlines active

Each testbench is self-contained: includes SKY130 models, bitcell netlist,
and a column-level test circuit with realistic BL capacitance.

Usage::

    from rekolektion.verify.macro_spice import generate_feature_testbenches
    paths = generate_feature_testbenches("output/sky130_6t_lr.spice",
                                          output_dir="output/feature_spice")
"""

from __future__ import annotations

import os
from pathlib import Path
from string import Template

from rekolektion.tech.sky130 import SPICE_MODELS

_PDK_ROOT = os.environ.get("PDK_ROOT", os.path.expanduser("~/.volare"))


def _model_include(corner: str = "tt") -> tuple[str, str]:
    """Return (model_path, corner_name) for .lib include."""
    lib_spec = SPICE_MODELS.get(corner, SPICE_MODELS["tt"])
    path, name = lib_spec.rsplit(" ", 1)
    return path, name


# ---------------------------------------------------------------------------
# Shared column test circuit
# ---------------------------------------------------------------------------

_COLUMN_CIRCUIT = """\
* --- Column test circuit: 1 column of {nrows} bitcells ---
* Precharge, {nrows} bitcells sharing BL/BLB, sense amp, write driver

* Supply
Vvdd VDD 0 DC {vdd}
Vvss VSS 0 DC 0

* Bit line capacitance (parasitic: {nrows} cells × ~1fF/cell + routing)
Cbl  BL  0 {c_bl}f
Cblb BLB 0 {c_bl}f

* Precharge: 2 PMOS pulling BL/BLB to VDD, 1 PMOS equalizing
* Controlled by PRE_EN (active low)
XP_PRE_BL  BL  PRE_EN VDD VDD sky130_fd_pr__pfet_01v8 w=0.84 l=0.15
XP_PRE_BLB BLB PRE_EN VDD VDD sky130_fd_pr__pfet_01v8 w=0.84 l=0.15
XP_PRE_EQ  BL  PRE_EN BLB VDD sky130_fd_pr__pfet_01v8 w=0.42 l=0.15

* Write driver: complementary drive onto BL/BLB when WR_EN=1
* DIN=1 → BL=VDD, BLB=0; DIN=0 → BL=0, BLB=VDD
XN_WD_BL  BL  WR_AND_DIN  VSS VSS sky130_fd_pr__nfet_01v8 w=0.84 l=0.15
XN_WD_BLB BLB WR_AND_DINB VSS VSS sky130_fd_pr__nfet_01v8 w=0.84 l=0.15

* Sense amp: cross-coupled inverter pair + enable
* Simplified latch sense amp
XN_SA_L SA_Q  SA_QB VSS VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XN_SA_R SA_QB SA_Q  VSS VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XP_SA_L SA_Q  SA_QB VDD VDD sky130_fd_pr__pfet_01v8 w=0.42 l=0.15
XP_SA_R SA_QB SA_Q  VDD VDD sky130_fd_pr__pfet_01v8 w=0.42 l=0.15
* Input coupling (BL/BLB to sense nodes via pass gates)
XN_SA_INL SA_Q  SA_EN BL  VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XN_SA_INR SA_QB SA_EN BLB VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15

* Bitcells: {nrows} cells on the column
"""

_BITCELL_INSTANCE = "Xcell{i} BL BLB WL{i} VDD VSS {subckt}\n"

_BITCELL_IC = ".ic V(Xcell{i}.Q)={q_val} V(Xcell{i}.QB)={qb_val}\n"


def _column_circuit(
    nrows: int, subckt: str, vdd: float,
    init_cell: int = 0, init_val: int = 0,
) -> str:
    """Build the column test circuit string.

    init_val: initial value of the target cell (default 0 — empty, ready for write).
    All other cells default to '1'.
    """
    c_bl = nrows * 1.0  # fF per cell
    text = _COLUMN_CIRCUIT.format(nrows=nrows, vdd=vdd, c_bl=c_bl)
    for i in range(nrows):
        text += _BITCELL_INSTANCE.format(i=i, subckt=subckt)
    text += "\n* Initial conditions\n"
    for i in range(nrows):
        if i == init_cell:
            q = vdd if init_val else 0
            qb = 0 if init_val else vdd
        else:
            q, qb = vdd, 0  # default: store '1'
        text += _BITCELL_IC.format(i=i, q_val=q, qb_val=qb)
    return text


# ---------------------------------------------------------------------------
# 1. Baseline transient: write then read one cell
# ---------------------------------------------------------------------------

_BASELINE_TEMPLATE = Template("""\
* Baseline Read/Write Access Time — Column-Level Transient
* ${nrows}-row column, ${corner_name} corner, VDD=${vdd}V, T=${temp}C
* Sequence: precharge → write '1' to cell 0 → precharge → read cell 0

.param vdd_val=${vdd}
.temp ${temp}

.lib "${pdk_root}/sky130A/${model_path}" ${corner_name}
.include "${bitcell_spice}"

${column_circuit}

* --- Stimulus ---
* Clock: 20ns period
Vclk CLK 0 PULSE(0 {vdd_val} 1n 0.1n 0.1n 9.9n 20n)

* Precharge: active during first half of cycle (CLK low)
Vpre PRE_EN 0 PULSE({vdd_val} 0 0n 0.1n 0.1n 9.9n 20n)

* Word line 0: active during 2nd and 4th cycle (write, then read)
Vwl0 WL0 0 PWL(
+  0n 0  20n 0  20.1n {vdd_val}  40n {vdd_val}  40.1n 0
+  60n 0  60.1n {vdd_val}  80n {vdd_val}  80.1n 0)

* All other wordlines OFF
${other_wl_sources}

* Write driver enable: active during 2nd cycle only
Vwr_en WR_EN 0 PWL(0n 0  20n 0  20.1n {vdd_val}  40n {vdd_val}  40.1n 0)

* Data in = 1 for write
Vdin DIN 0 DC {vdd_val}
* Write driver AND gates (simplified)
Eand  WR_AND_DIN  0 VOL='V(WR_EN) > {vdd_val}/2 && V(DIN) > {vdd_val}/2 ? {vdd_val} : 0'
Eandb WR_AND_DINB 0 VOL='V(WR_EN) > {vdd_val}/2 && V(DIN) < {vdd_val}/2 ? {vdd_val} : 0'

* Sense amp enable: active during 4th cycle (read), delayed after WL
Vsa_en SA_EN 0 PWL(0n 0  65n 0  65.1n {vdd_val}  80n {vdd_val}  80.1n 0)

.tran 0.05n 100n

.control
run
let t_access = 0

* Measure BL development time during read (WL rise to BL/BLB divergence)
meas tran t_bl_dev TRIG V(WL0) VAL=${vdd_half} RISE=2 TARG V(BL) VAL=${vdd_half} FALL=1

* Measure write time: WL rise to Q settling
meas tran t_write TRIG V(WL0) VAL=${vdd_half} RISE=1 TARG V(Xcell0.Q) VAL=${vdd_half} CROSS=1

echo ""
echo "=== Baseline Access Time ==="
echo "Corner: ${corner_name}  VDD: ${vdd}V  Temp: ${temp}C"
print t_write
print t_bl_dev

wrdata ${output_prefix}_baseline.csv V(CLK) V(WL0) V(BL) V(BLB) V(Xcell0.Q) V(Xcell0.QB) V(SA_Q) V(SA_QB)
.endc

.end
""")


# ---------------------------------------------------------------------------
# 2. Clock gating: measure dynamic current with gated vs ungated clock
# ---------------------------------------------------------------------------

_CLOCK_GATING_TEMPLATE = Template("""\
* Clock Gating Power Measurement
* Compare supply current with CEN=1 (active) vs CEN=0 (gated)
* ${corner_name} corner, VDD=${vdd}V, T=${temp}C

.param vdd_val=${vdd}
.temp ${temp}

.lib "${pdk_root}/sky130A/${model_path}" ${corner_name}
.include "${bitcell_spice}"

${column_circuit}

* --- ICG (Integrated Clock Gating) cell ---
* Latch-based: CEN sampled on CLK low, AND with CLK
* GCLK = CLK & CEN_latched
Vclk_raw CLK_RAW 0 PULSE(0 {vdd_val} 1n 0.1n 0.1n 9.9n 20n)

* CEN: HIGH for first 100ns (active), then LOW for next 100ns (gated)
Vcen CEN 0 PWL(0n {vdd_val}  100n {vdd_val}  100.1n 0  200n 0)

* ICG behavioral model
BICG GCLK 0 V='V(CLK_RAW) > {vdd_val}/2 && V(CEN) > {vdd_val}/2 ? {vdd_val} : 0'

* Use GCLK to drive precharge and wordlines
Vpre PRE_EN 0 PULSE({vdd_val} 0 0n 0.1n 0.1n 9.9n 20n)
Vwl0 WL0 0 PWL(0n 0  20n 0  20.1n {vdd_val}  40n {vdd_val}  40.1n 0)

${other_wl_sources}

* No write/read activity — just measuring clock power
Vwr_en WR_EN 0 DC 0
Vdin DIN 0 DC 0
EAND  WR_AND_DIN  0 VOL='0'
EANDB WR_AND_DINB 0 VOL='0'
Vsa_en SA_EN 0 DC 0

* Measure current from VDD supply
.tran 0.1n 200n

.control
run

* Average current during active phase (0-100ns)
meas tran i_active AVG I(Vvdd) FROM=10n TO=100n
* Average current during gated phase (100-200ns)
meas tran i_gated AVG I(Vvdd) FROM=110n TO=200n

let ratio = abs(i_gated) / abs(i_active) * 100

echo ""
echo "=== Clock Gating Power ==="
echo "Corner: ${corner_name}  VDD: ${vdd}V  Temp: ${temp}C"
print i_active
print i_gated
print ratio
echo "(ratio = gated/active × 100, lower is better)"

wrdata ${output_prefix}_clock_gating.csv I(Vvdd) V(CEN) V(GCLK)
.endc

.end
""")


# ---------------------------------------------------------------------------
# 3. Power gating: measure leakage with sleep ON vs OFF
# ---------------------------------------------------------------------------

_POWER_GATING_TEMPLATE = Template("""\
* Power Gating Leakage Measurement
* Compare static leakage with power rail connected vs gated
* ${corner_name} corner, VDD=${vdd}V, T=${temp}C

.param vdd_val=${vdd}
.temp ${temp}

.lib "${pdk_root}/sky130A/${model_path}" ${corner_name}
.include "${bitcell_spice}"

* --- Power switch (header switch) ---
* PMOS header between real VDD and virtual VDD (VVDD)
* SLEEP drives PMOS gate directly:
*   SLEEP=0 → Vgs=-VDD → PMOS ON → normal operation
*   SLEEP=1 (VDD) → Vgs≈0 → PMOS OFF → power gated
Vvdd_real VDD_REAL 0 DC {vdd_val}
Vvss VSS 0 DC 0

* Header switch: 2 parallel PMOS (D=VDD, G=SLEEP, S=VDD_REAL, B=VDD_REAL)
XP_SW1 VDD SLEEP VDD_REAL VDD_REAL sky130_fd_pr__pfet_01v8 w=5.0 l=0.15
XP_SW2 VDD SLEEP VDD_REAL VDD_REAL sky130_fd_pr__pfet_01v8 w=5.0 l=0.15

* SLEEP: LOW for first 500ns (normal), HIGH for next 500ns (power gated)
Vsleep SLEEP 0 PWL(0n 0  500n 0  500.1n {vdd_val}  1000n {vdd_val})

* Bit line capacitance
Cbl  BL  0 ${c_bl}f
Cblb BLB 0 ${c_bl}f

* Precharge OFF (static measurement)
XP_PRE_BL  BL  PRE_EN VDD VDD sky130_fd_pr__pfet_01v8 w=0.84 l=0.15
XP_PRE_BLB BLB PRE_EN VDD VDD sky130_fd_pr__pfet_01v8 w=0.84 l=0.15
XP_PRE_EQ  BL  PRE_EN BLB VDD sky130_fd_pr__pfet_01v8 w=0.42 l=0.15
Vpre PRE_EN 0 DC {vdd_val}

* ${nrows} bitcells, all wordlines OFF (hold mode)
${bitcell_instances}

${bitcell_ics}

${other_wl_sources}

.tran 1n 1000n

.control
run

* Leakage with power ON (0-500ns)
meas tran i_leak_on AVG I(Vvdd_real) FROM=100n TO=500n
* Leakage with power gated (500-1000ns)
meas tran i_leak_off AVG I(Vvdd_real) FROM=600n TO=1000n

let reduction = (1 - abs(i_leak_off)/abs(i_leak_on)) * 100

echo ""
echo "=== Power Gating Leakage ==="
echo "Corner: ${corner_name}  VDD: ${vdd}V  Temp: ${temp}C"
print i_leak_on
print i_leak_off
print reduction
echo "(reduction = % leakage saved)"

wrdata ${output_prefix}_power_gating.csv I(Vvdd_real) V(SLEEP) V(VDD)
.endc

.end
""")


# ---------------------------------------------------------------------------
# 4. WL switchoff: verify data retention with all WLs off
# ---------------------------------------------------------------------------

_WL_SWITCHOFF_TEMPLATE = Template("""\
* WL Switchoff Data Retention Test
* Write a value, deassert all WLs, wait, read back
* ${corner_name} corner, VDD=${vdd}V, T=${temp}C

.param vdd_val=${vdd}
.temp ${temp}

.lib "${pdk_root}/sky130A/${model_path}" ${corner_name}
.include "${bitcell_spice}"

${column_circuit_stored}

* --- Stimulus ---
* Cell 0 initialized with Q=VDD. All WLs off for 220ns, then read back.

* All wordlines OFF for entire retention window (0-250ns)
* Then briefly activate WL0 for read (250-270ns)
Vwl0 WL0 0 PWL(0n 0  250n 0  250.1n {vdd_val}  270n {vdd_val}  270.1n 0)

${other_wl_sources}

* Precharge: keep BLs precharged (active low — 0 = precharge ON)
Vpre PRE_EN 0 DC 0

* Write driver OFF
Vwr_en WR_EN 0 DC 0
Vdin DIN 0 DC 0
Eand  WR_AND_DIN  0 VOL='0'
Eandb WR_AND_DINB 0 VOL='0'

* SA enable during read
Vsa_en SA_EN 0 PWL(0n 0  255n 0  255.1n {vdd_val}  270n {vdd_val}  270.1n 0)

.tran 0.1n 280n

.control
run

* Check cell Q voltage after long idle (just before read WL activation)
meas tran v_q_hold FIND V(Xcell0.Q) AT=249n
meas tran v_qb_hold FIND V(Xcell0.QB) AT=249n

* Retention pass: Q should still be > 80% VDD
let pass = (v_q_hold > ${vdd_80pct})

echo ""
echo "=== WL Switchoff Data Retention ==="
echo "Corner: ${corner_name}  VDD: ${vdd}V  Temp: ${temp}C"
print v_q_hold
print v_qb_hold
print pass
echo "(pass=1 means data survived 250ns idle with all WLs off)"

wrdata ${output_prefix}_wl_switchoff.csv V(WL0) V(Xcell0.Q) V(Xcell0.QB) V(BL) V(BLB) V(SA_Q)
.endc

.end
""")


# ---------------------------------------------------------------------------
# 5. Burn-in: all wordlines active simultaneously, measure stress current
# ---------------------------------------------------------------------------

_BURNIN_TEMPLATE = Template("""\
* Burn-In Stress Current Measurement
* All wordlines asserted simultaneously (test mode)
* ${corner_name} corner, VDD=${vdd}V, T=${temp}C

.param vdd_val=${vdd}
.temp ${temp}

.lib "${pdk_root}/sky130A/${model_path}" ${corner_name}
.include "${bitcell_spice}"

* Supply (measure current)
Vvdd VDD 0 DC {vdd_val}
Vvss VSS 0 DC 0

* BL capacitance
Cbl  BL  0 ${c_bl}f
Cblb BLB 0 ${c_bl}f

* Precharge ON (hold BL/BLB at VDD)
XP_PRE_BL  BL  PRE_EN VDD VDD sky130_fd_pr__pfet_01v8 w=0.84 l=0.15
XP_PRE_BLB BLB PRE_EN VDD VDD sky130_fd_pr__pfet_01v8 w=0.84 l=0.15
Vpre PRE_EN 0 DC 0

* ${nrows} bitcells
${bitcell_instances}

${bitcell_ics}

* TM (test mode): OFF for first 50ns (normal), ON for next 100ns (burn-in)
* When TM=1, ALL wordlines go high simultaneously
Vtm TM 0 PWL(0n 0  50n 0  50.1n {vdd_val}  150n {vdd_val}  150.1n 0)

* All wordlines driven by TM
${burnin_wl_sources}

.tran 0.1n 200n

.control
run

* Current during normal (all WL off, 10-50ns)
meas tran i_normal AVG I(Vvdd) FROM=10n TO=50n
* Current during burn-in stress (all WL on, 60-150ns)
meas tran i_stress AVG I(Vvdd) FROM=60n TO=150n

let stress_ratio = abs(i_stress) / abs(i_normal)

echo ""
echo "=== Burn-In Stress Current ==="
echo "Corner: ${corner_name}  VDD: ${vdd}V  Temp: ${temp}C"
print i_normal
print i_stress
print stress_ratio
echo "(stress_ratio = burn-in current / idle current)"

wrdata ${output_prefix}_burnin.csv I(Vvdd) V(TM) V(WL0)
.endc

.end
""")


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def generate_feature_testbenches(
    bitcell_spice: str | Path,
    output_dir: str | Path = ".",
    nrows: int = 8,
    corners: list[str] | None = None,
    voltages: list[float] | None = None,
    temperatures: list[float] | None = None,
) -> list[Path]:
    """Generate SPICE testbenches for all production features.

    Args:
        bitcell_spice: Path to the bitcell SPICE netlist (.subckt).
        output_dir: Directory for generated testbench files.
        nrows: Number of rows in the test column (default 8).
        corners: Process corners. Default: ["tt"].
        voltages: Supply voltages. Default: [1.8].
        temperatures: Temperatures in C. Default: [27.0].

    Returns:
        List of paths to generated testbench files.
    """
    if corners is None:
        corners = ["tt"]
    if voltages is None:
        voltages = [1.8]
    if temperatures is None:
        temperatures = [27.0]

    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    bitcell_spice = Path(bitcell_spice).resolve()

    # Extract subcircuit name from netlist
    subckt_name = "sky130_sram_6t_bitcell_lr"
    for line in bitcell_spice.read_text().splitlines():
        if line.strip().lower().startswith(".subckt"):
            subckt_name = line.split()[1]
            break

    generated: list[Path] = []

    for corner in corners:
        model_path, corner_name = _model_include(corner)

        for vdd in voltages:
            for temp in temperatures:
                c_bl = nrows * 1.0  # fF
                prefix = f"{corner}_{vdd:.2f}V_{temp:.0f}C"

                # Column circuit: cell 0 empty (for baseline write test)
                col_empty = _column_circuit(nrows, subckt_name, vdd, init_cell=0, init_val=0)
                # Column circuit: cell 0 has data (for retention/read tests)
                col_stored = _column_circuit(nrows, subckt_name, vdd, init_cell=0, init_val=1)

                # Other WL sources (all OFF by default)
                other_wl = ""
                for i in range(1, nrows):
                    other_wl += f"Vwl{i} WL{i} 0 DC 0\n"

                # Bitcell instances + ICs (for templates that don't use _column_circuit)
                bc_inst = ""
                bc_ic = ""
                for i in range(nrows):
                    bc_inst += f"Xcell{i} BL BLB WL{i} VDD VSS {subckt_name}\n"
                    bc_ic += f".ic V(Xcell{i}.Q)={vdd} V(Xcell{i}.QB)=0\n"

                # Burn-in WL sources (driven by TM)
                burnin_wl = ""
                for i in range(nrows):
                    burnin_wl += f"Bwl{i} WL{i} 0 V='V(TM) > {{vdd_val}}/2 ? {{vdd_val}} : 0'\n"

                common = {
                    "vdd": f"{vdd:.2f}",
                    "vdd_half": f"{vdd / 2:.3f}",
                    "vdd_80pct": f"{vdd * 0.8:.3f}",
                    "temp": f"{temp:.1f}",
                    "corner_name": corner_name,
                    "model_path": model_path,
                    "bitcell_spice": str(bitcell_spice),
                    "pdk_root": _PDK_ROOT,
                    "output_prefix": prefix,
                    "nrows": str(nrows),
                    "c_bl": f"{c_bl:.1f}",
                    "column_circuit": col_empty,
                    "column_circuit_stored": col_stored,
                    "other_wl_sources": other_wl,
                    "bitcell_instances": bc_inst,
                    "bitcell_ics": bc_ic,
                    "burnin_wl_sources": burnin_wl,
                }

                templates = {
                    "baseline": _BASELINE_TEMPLATE,
                    "clock_gating": _CLOCK_GATING_TEMPLATE,
                    "power_gating": _POWER_GATING_TEMPLATE,
                    "wl_switchoff": _WL_SWITCHOFF_TEMPLATE,
                    "burnin": _BURNIN_TEMPLATE,
                }

                for name, tmpl in templates.items():
                    filename = f"tb_{name}_{prefix}.spice"
                    path = output_dir / filename
                    path.write_text(tmpl.substitute(common))
                    generated.append(path)

    return generated
