# Tier 3 — Characterisation Sanity Audit

Scope: stimulus-to-net coverage of Liberty timing arcs and pin capacitances; PVT corner declarations vs SPICE-run coverage; pattern-matrix sanity for the analog CIM cell; operating-point sanity. Reports only what is observed in the repo. Does NOT make analog/timing-correctness judgments.

---

## Summary

- **Liberty arcs found**: 7 distinct value-bearing arc/pin types across 12 generated `.lib` files (5 v2 SRAM library entries × pins/arcs + 7 CIM library entries × pins/arcs).
- **Arcs classified MEASURED**: **0**.
- **Arcs classified ANALYTICAL**: **all of them** (every numeric value is computed by `compute_timing()` / `_input_cap_pf()` / `_charge_share_ns()` / `_cim_compute_ns()` from SKY130 device parameters baked into the generator, with no `.raw` / `.log` linkage).
- **Arcs that LOOK measured but have no evidence**: **0**. Both generator docstrings explicitly declare values are analytical and not SPICE-measured (acknowledged in `liberty_generator.py:1-30` and `cim_liberty_generator.py:1-20`). The lib comments themselves state `comment : "Timing from analytical model — see compute_timing()"`.
- **`output/spice_char/` directory**: **does NOT exist**. The CIM characterisation harness `scripts/characterize_cim_liberty.py` declares `_OUT_ROOT = Path("output/spice_char")` but no run has populated it.
- **`.raw` files repo-wide**: **0** (`find … -name '*.raw'` → empty).

---

## T3.1 — Stimulus-to-net coverage table

### Liberty generator source files (audited)

| Generator | Path | Timing source per docstring |
|---|---|---|
| SRAM (v2) | `src/rekolektion/macro/liberty_generator.py` | "analytically-computed timing values based on array dimensions and SKY130 device/interconnect parameters" (lines 3-7) |
| CIM | `src/rekolektion/macro/cim_liberty_generator.py` | "Timing arcs remain analytical estimates" + "A future SPICE-characterisation pass should replace these analytical numbers" (lines 11-15) |

### Liberty arcs (per arc class — every emitted .lib emits the same arc set)

#### v2 SRAM macros (`output/v2_macros/sram_activation_bank/sram_activation_bank.lib`, `sram_weight_bank_small/sram_weight_bank_small.lib`)

| Arc | Source: file:line | Value | Stimulus deck (path or "none") | .raw evidence path | Classification | Notes |
|---|---|---|---|---|---|---|
| `CLK pin capacitance` | `liberty_generator.py:140` | `0.010 pF` (`_CLK_CAP_PF`) | none | none | **ANALYTICAL** | hardcoded constant in generator, no measurement linkage |
| `WE / CS / ADDR / DIN pin capacitance` | `liberty_generator.py:139` | `0.005 pF` (`_INPUT_CAP_PF`) | none | none | **ANALYTICAL** | hardcoded constant in generator |
| `DOUT max_capacitance` | `liberty_generator.py:141` | `0.470 pF` (`_OUTPUT_CAP_PF`) | none | none | **ANALYTICAL** | derived from PFET Id_sat × W/L formula in comments (lines 119-126), not SPICE-measured |
| `WE→CLK setup_rising / hold_rising` | `liberty_generator.py:476-498` | `0.8200 / 0.2000 ns` (lib literal) | none | none | **ANALYTICAL** | `compute_timing()` formula `t_setup = t_decoder * 1.2 + _CLK_SKEW_NS` |
| `CS / ADDR / DIN setup/hold rising` | `liberty_generator.py:538-560` | `0.8200 / 0.2000 ns` | none | none | **ANALYTICAL** | same `t_setup`/`t_hold` derived in `compute_timing()` |
| `CLK → DOUT cell_rise / cell_fall` | `liberty_generator.py:571-572` | `3.2328 ns` (per lib emission) | none | none | **ANALYTICAL** | `t_clk_to_q_raw = t_decoder + t_wl + t_bl_develop + t_mux + t_sa + t_output`, all SKY130 device-param constants × array geometry; multiplied by 1.2 margin |
| `CLK → DOUT rise_transition / fall_transition` | `liberty_generator.py` | `0.1000 ns` (lib literal) | none | none | **ANALYTICAL** | hardcoded `t_rise = t_fall = 0.10` in `compute_timing()` |

#### CIM macros (`output/cim_macros/cim_sram_{a,b,c,d}_*.lib`)

| Arc | Source: file:line | Value | Stimulus deck (path or "none") | .raw evidence path | Classification | Notes |
|---|---|---|---|---|---|---|
| `MWL_EN pin capacitance` | `cim_liberty_generator.py:81-83, 116-118` | `_BUF2_A_INPUT_FF * uplift / 1000 pF` | none | none | **ANALYTICAL** | foundry buf_2 datasheet pin cap × parasitic uplift |
| `MBL_PRE pin capacitance` | `cim_liberty_generator.py:119-121` | `cols × _gate_cap_ff(_PRECHARGE_PMOS_W) / 1000 pF` | none | none | **ANALYTICAL** | `Cox = 8.5 fF/µm²` × W·L × 1.30 uplift (formula lines 73-79) |
| `VBIAS pin capacitance` | `cim_liberty_generator.py:122-124` | `cols × _gate_cap_ff(_SENSE_BIAS_NMOS_W) / 1000 pF` | none | none | **ANALYTICAL** | Cox × W·L formula |
| `VREF pin capacitance` | `cim_liberty_generator.py:125-128` | `cols × _VREF_PER_COL_FF / 1000 pF` | none | none | **ANALYTICAL** | "Diff area ≈ W × min-S/D length × Cj_pmos" estimate (lines 91-94) |
| `MBL_OUT max_capacitance` | `cim_liberty_generator.py:100` | `0.50 pF` (`_MBL_OUT_CAP_PF`) | none | none | **ANALYTICAL** | comment line 100: "Output load assumptions (analytical placeholder until characterised)" |
| `MBL_PRE → MBL_OUT cell_rise / cell_fall (falling_edge)` | `cim_liberty_generator.py:69-71, 253-254` | `_cim_compute_ns(rows, cap_fF)` (e.g. `2.6000 ns` for SRAM-A 256x64) | none | none | **ANALYTICAL** | `_cim_compute_ns` = analytical charge-sharing + source-follower buffer model (docstring "MIM cap charge sharing with MBL parasitic", "Source follower buffer delay (~0.5 ns)", "MWL poly RC delay across row (~0.1 ns)") |
| `MBL_PRE → MBL_OUT rise_transition / fall_transition` | `cim_liberty_generator.py:255-256` | `_charge_share_ns(rows, cap_fF)` (e.g. `2.0000 ns`) | none | none | **ANALYTICAL** | analytical RC settling formula |

### Notes on what does exist in the repo

- A characterisation harness exists: `scripts/characterize_cim_liberty.py`. It declares the SimConfig, corner triples, and pattern matrix. It writes per-sim JSON to `output/spice_char/<macro>/<slug>.json`. **No JSON files were found** under `output/spice_char/` — the directory itself does not exist on disk.
- `output/cim_sweep/` contains 22 .csv waveform dumps and 22 .spice testbench decks. These exercise the **bitcell-level** `sky130_sram_6t_cim_lr` (single-cell topology) — `grep` for `cim_sram_{a,b,c,d}` in those decks returns empty. They are not stimuli for any timing arc declared in the macro `.lib` files.
- `output/feature_spice/` contains 5 testbench decks (`tb_baseline_*`, `tb_burnin_*`, `tb_clock_gating_*`, `tb_power_gating_*`, `tb_wl_switchoff_*`), all at `tt_1.80V_27C` only. These exercise an 8-row column built from `sky130_sram_6t_bitcell_lr`, not the assembled SRAM/CIM macros that the .lib files describe.
- `docs/spice_results/lr_cell/` and `docs/spice_results/foundry_cell/` contain CSVs labeled `tt/ss/ff × 1.20V/1.62V/1.80V/1.98V × 27C` for `read_snm`, `hold_snm`, `write_margin`, `transient`. These are **bitcell-level** characterisations — none are linked to a Liberty arc value in any `.lib` file.

---

## T3.2 — PVT corner coverage

### Corners declared in .lib headers

`find output -name '*.lib'` returned 12 lib paths under `output/v2_macros/` and `output/cim_macros/` (rest are sky130 std-cell .libs from openlane runs).

| .lib path | Corner declared in header (quoted) | SPICE runs at that corner |
|---|---|---|
| `output/v2_macros/sram_activation_bank/sram_activation_bank.lib` | `nom_process : 1.0 ; nom_voltage : 1.8 ; nom_temperature : 25.0 ;` (lines 22-24) `operating_conditions (nom) { process : 1.0 ; voltage : 1.8 ; temperature : 25.0 ; }` | **none** — no SPICE deck exercises the `sram_activation_bank` macro |
| `output/v2_macros/sram_weight_bank_small/sram_weight_bank_small.lib` | identical to above (`tt-equivalent`, 1.8V, 25C) | **none** |
| `output/cim_macros/cim_sram_a_256x64.lib` (and `cim_sram_a_256x64/cim_sram_a_256x64.lib`) | `nom_process : 1.0 ; nom_voltage : 1.2 ; nom_temperature : 25.0 ;` `operating_conditions (nom) { process : 1.0 ; voltage : 1.2 ; temperature : 25.0 ; }` | **none** at this exact corner targeting the assembled macro. `output/cim_sweep/cim_tt_1.2V_27C_*.csv` exists but operates on the bitcell, not the assembled `cim_sram_a_256x64` |
| `output/cim_macros/cim_sram_b_256x64.lib` | identical to SRAM-A header | **none** |
| `output/cim_macros/cim_sram_c_64x64.lib` | identical to SRAM-A header | **none** |
| `output/cim_macros/cim_sram_d_64x64.lib` | identical to SRAM-A header | **none** |

**Observations:**

1. **Every macro `.lib` declares only a single nominal corner** (no `tt`/`ss`/`ff` variants emitted). The CIM `.lib`s declare 1.2V; the v2 SRAM `.lib`s declare 1.8V.
2. **No multi-corner .lib emission** — `corner.slug` from `characterize_cim_liberty.py` defines `tt_25 / ss_cold / ff_hot`, but the generator code path that writes the .lib has no corner argument; it always writes a single-corner file.
3. **`docs/spice_characterization_report.md`** declares "All 9 corners pass for both cells (TT/SS/FF × 1.62V/1.80V/1.98V at 27°C)" and "Temperature sweep (-40°C/125°C) not yet performed." Those 9-corner CSVs are present in `docs/spice_results/lr_cell/` and `docs/spice_results/foundry_cell/`, but they characterise **bitcell-level** SNM / write-margin / transient, NOT macro-level Liberty arcs.

### MEMORY note cross-check

`MEMORY.md` flags `[PVT track blocked]` (`feedback/project_pvt_track_blocked.md`): "2026-04-21 — PVT SPICE track (B1 audit) escalated to 02_sram_design; 1.310 µm pair pitch is geometrically infeasible for per-pair precharge under sky130 design rules." Confirmed: no `02_sram_design` directory or `pvt` directory exists under repo root. Track is blocked upstream and PVT-corner SPICE characterisation has not run.

---

## T3.3 — Pattern coverage on analog cells (CIM-specific)

### Pattern matrix declared in `scripts/characterize_cim_liberty.py`

The harness declares 6 patterns:

```
PATTERNS = {
  "all_zero":  [[0]*c]*r
  "all_one":   [[1]*c]*r
  "alt_col":   col % 2 per column, all rows identical
  "alt_row":   row % 2 per row, all cols identical
  "checker":   (r+c) % 2
  "random":    rng.randint(0,1) per cell, seeded
}
```

A `--pattern-sweep` mode would enqueue 4 patterns at TT/25 (per phase-1 plan in the docstring).

### Cross-reference to actual SPICE runs that exercised those patterns

- `output/spice_char/` does **not exist** → no JSON output from `characterize_cim_liberty.py` was found.
- `output/cim_sweep/` testbenches are **single-bitcell** decks (`Xc_w0_0 ... sky130_sram_6t_cim_lr`) with weight=0 or weight=1. They do not exercise multi-cell patterns. Filename labels include `w0` and `w1` — these are the only two pattern values present in the testbench corpus.
- `output/tb_cim_basic.spice` and `output/tb_cim_basic_w1.spice` are likewise single-cell decks (weight=0, weight=1 cases described in header comment).
- No deck found that exercises `alt_col`, `alt_row`, `checker`, or `random` patterns through any of the assembled CIM macros.

**Pattern coverage status**: declared in source, **not exercised** by any SPICE run found in the repo. Lib `MBL_PRE→MBL_OUT` arc is a single scalar value computed by `_cim_compute_ns(rows, cap_fF)` with no pattern argument — i.e., the .lib emits one number regardless of weight pattern.

---

## T3.4 — Operating-point sanity

### Searchable evidence

`find -name '*.raw'` returned **0 files**. There are no ngspice-generated raw waveform files in the repo.

The `.csv` waveform dumps in `output/cim_sweep/cim_tt_1.8V_27C_w0.csv` etc. are `wrdata` output (sample at `cim_sweep_tb_sample` line: `wrdata cim_tt_1.8V_27C_w0.csv V(MBL)`). They are time-series of `V(MBL)` only (single-node trace, 2-column format `time   voltage`). They do **NOT** contain DC-operating-point listings of internal nodes (Q, QB, MBL_OUT bus members). Sample of `cim_tt_1.8V_27C_w0.csv`:

```
 0.00000000e+00  9.00000055e-01
 1.00000000e-12  9.00000055e-01
 ...
```

(initial value 0.9 V on V(MBL); single trace; no per-node OP table)

### Per-macro OP listing for `cim_sram_a/b/c/d` and `sram_activation_bank` / `sram_weight_bank_small`

**No data.** No SPICE run was found that simulates an assembled macro and records a `.op` listing. The macro-level transient harness `characterize_cim_liberty.py` would have written `output/spice_char/<macro>/<slug>.json` with `v_quiescent` / `v_compute` / sample trace, but the directory is empty / does not exist.

### Out-of-range flagging

- Per the audit charter ("flag any out-of-range values e.g. MBL between VGND+0.05 and VPWR-0.05 IF the data exists"): **the data does not exist for any assembled macro**.
- The cim_sweep CSV dumps are the only V(MBL) traces; they show a starting value of `9.00e-1 V` for the 1.8V w0 testbench. Since this audit must not make analog judgments, no PASS/FAIL is asserted.
- `RESPIN_LOW_SWING_col<c>` flagging is implemented in `characterize_cim_liberty.py:_parse_measurements` (threshold `RESPIN_V_SWING_FLOOR`), but cannot fire because no run output exists.

---

## Items that need main-session review

1. **T3-NEW-A** — Liberty `nom_voltage` mismatch: v2 SRAM .libs declare `1.8V`, CIM .libs declare `1.2V`. The CIM cell-level SPICE testbenches that exist (`cim_tt_1.8V_27C_*.csv`, `cim_tt_1.2V_27C_*.csv`) exercise both 1.2V and 1.8V on a single bitcell, but no Liberty .lib is emitted at 1.8V for the CIM macro. Whether this is intentional ("CIM operates at 1.2V" per `cim_liberty_generator.py:101` comment "CIM operates at 1.2V (not 1.8V) per Track 21") or an oversight in lib coverage is a judgment call for main session.
2. **T3-NEW-B** — Lib emission has no corner argument. `_emit_liberty()` in `liberty_generator.py` and `generate_cim_liberty()` in `cim_liberty_generator.py` accept no `corner` parameter. Even if `compute_timing()` were upgraded to pull from SS/FF tables, the current emission path can only ship one corner per .lib. Multi-corner sign-off would require generator changes.
3. **T3-NEW-C** — `output/spice_char/` directory has never been populated. The `characterize_cim_liberty.py` harness is implemented (full pattern matrix, NLDM grid mode, bias sweep mode, per-sim JSON output, `RESPIN_LOW_SWING` flagging) but **no run has been executed and committed**. This is consistent with the MEMORY note that the PVT track is blocked on the upstream pitch issue in `02_sram_design`.
4. **T3-NEW-D** — Bitcell-level CSVs in `docs/spice_results/lr_cell/` and `docs/spice_results/foundry_cell/` cover 9 corners (TT/SS/FF × 1.62V/1.80V/1.98V at 27°C) for read_snm / hold_snm / write_margin / transient. These exist but are **not linked to** any value in any macro .lib. They are bitcell trust evidence, not Liberty-arc evidence. If the project plans to upgrade lib values from these CSVs, that's a generator work item, not a "they're already linked" claim.
5. **T3-NEW-E** — `cim_sweep/` and `feature_spice/` testbenches exist at multiple corners but operate on the **bitcell** topology (`sky130_sram_6t_cim_lr` / `sky130_sram_6t_bitcell_lr`), not the assembled macros named in the .lib files. None of these provide Liberty-arc evidence either.
6. **Existing tier1 finding T1.7-A** is corroborated and should remain P1 in `smoking_guns.md` — every macro-level Liberty arc is ANALYTICAL.

