# Track Plan: Production Feature Set

## Verilog Test Infrastructure

All Verilog behavioral verification uses **verifrog** — an F#/Expecto test framework that drives RTL through Verilator and Icarus Verilog.

- **Repo**: `/Users/bryancostanich/Git_Repos/bryan_costanich/verifrog/`
- **GitHub**: https://github.com/bryancostanich/verifrog
- **Backends**: Verilator (primary, compiled C++), Icarus Verilog (optional)
- **Test runner**: `verifrog test` or `dotnet test` with `--category` filtering
- **Config**: `verifrog.toml` in test project root (specifies top module, sources, memory regions, register maps)
- **Test categories**: Smoke, Unit, Integration, Parametric, Stress, Golden, Regression
- **Key capabilities**: signal read/write, cycle stepping, checkpoint/restore, signal forcing, fork (what-if), sweep, trace, RunUntil/RunUntilSignal
- **Declarative tests**: `.verifrog` files for simple set/step/expect patterns
- **F# tests**: Expecto framework for complex scenarios (multi-step protocols, state machines)

Each phase that modifies Verilog behavioral models must include verifrog tests covering the new functionality. Tests should be organized by feature (e.g., `tests/write_enables/`, `tests/scan_chain/`).

---

## Phase 1: Bit-Level Write Enables

- [x] Design byte-enable decoder logic (BEN[N-1:0] → per-driver gate signals)
- [x] Add AND gates between write drivers and column mux in peripheral generator
- [x] Add BEN port to macro interface (pin count = data_width / 8 for byte granularity)
- [x] Update Verilog behavioral model with byte-enable support
- [x] Update Liberty .lib with BEN setup/hold timing
- [x] Update LEF with BEN pin placement
- [x] Update SPICE subcircuit with BEN port
- [x] DRC on macro with write enables enabled
- [x] Verifrog tests: byte-level writes (write byte 0 only, read back full word; write all bytes independently; overlapping writes)

## Phase 2: Scan Chain DFT

- [x] Design scan flop chain order (address → control → data-in)
- [x] Create Verilog scan wrapper module (replaces standard flops with scan flops)
- [x] Add ScanIn, ScanOut, ScanEnable ports to macro interface
- [x] Update Liberty .lib with scan timing constraints (TM/SM setup/hold)
- [x] Update LEF with scan pin placement
- [x] Update SPICE subcircuit with scan ports
- [x] Verifrog tests: shift-in known test pattern via ScanIn, clock through chain, verify ScanOut matches expected sequence
- [x] Verifrog tests: scan chain length matches expected flop count
- [x] Integration test: Fault (open-source DFT) synthesized wrapper + stitched SRAM blackbox into 55-element scan chain (28 internal + 27 boundary). ATPG blocked by pyverilog parsing bug — filed upstream.

## Phase 3: Clock Gating

- [x] Add ICG cell instantiation at macro clock input
- [x] Add clock enable pin (CEN) to macro interface
- [x] Update Verilog model with clock gating
- [x] Update Liberty .lib with CEN timing
- [x] Update LEF with CEN pin
- [x] Verify zero dynamic power when CEN deasserted (SPICE) — 99.8% reduction
- [x] Verifrog tests: CEN=0 blocks writes/reads, data preserved on reassert, composability with other features

## Phase 4: Power Gating

- [x] Identify SKY130 standard library power switch cells — sky130_fd_pr__pfet_01v8 (header), lpflow_isobufsrc (isolation), lpflow_bleeder (bleeder)
- [x] Add header/footer switch generator to macro assembler — power_switch.py, auto-scaled to macro width
- [x] Add sleep/enable control pin to macro interface
- [x] Update LEF with switch cell placement and control pin
- [x] Update Liberty .lib with power-down state
- [x] Measure leakage reduction (SPICE: gated vs ungated) — 99.99% reduction
- [x] Document chip-level integration requirements — docs/power_gating_integration.md
- [x] Verifrog tests: sleep=1 blocks writes/reads, functional after wake-up

## Phase 5: Wordline Switchoff

- [x] Add gating logic to row decoder — wl_gate.py AND cell placed adjacent to each decoder NAND
- [x] Tie to existing chip-select (CS) signal or add dedicated WL_OFF pin
- [x] Update Verilog model
- [x] DRC on modified decoder — no new violation categories vs baseline (delta from approximate placement)
- [x] Verify no half-select disturb in SPICE transient simulation — V(Q)=1.800V after 250ns
- [x] Verifrog tests: wl_off=1 blocks writes/reads, data retained after deassert

## Phase 6: Burn-In Test Mode

- [x] Add mux at wordline driver output — wl_mux.py, 2:1 TM-controlled mux per row, placed after decoder/WL gate
- [x] Add test mode pin (TM) to macro interface
- [x] Update Verilog model with test mode behavior
- [x] Update LEF with TM pin
- [x] Verify all-wordline stress operation in SPICE — 870,000x stress current ratio
- [x] Verifrog tests: TM toggling does not corrupt data, normal operation unaffected

## Phase 7a: RTL Audit Against Reference Standards

Audit generated Verilog behavioral models against khalkulo/docs/reference/ design guidelines.

- [x] **SRAM 3-block pattern**: Migrated to OpenRAM 3-block pattern (posedge capture / negedge write / negedge read). All verifrog tests updated for 1-cycle read latency. Lint clean with BLKSEQ suppression for intentional blocking in posedge capture. (`sram_behavioral_model_pattern.md`)
- [x] **Port polarity convention**: Using active-high (cs/we/ben) for behavioral models. Real macro wrapper inverts at boundary (csb=~cs, web=~we). Documented. (`synchronous_sram_interface.md`)
- [x] **Lint compliance**: All generated Verilog passes `verilator --lint-only -Wall` clean — both minimal and all-features configs. Fixed COMBDLY (ICG latch), BLKANDNBLK (power gating dout), UNUSEDSIGNAL (tm). (`lint_rules.md`)
- [x] **NBA/blocking discipline**: Sequential blocks use only NBA; combinational/latch uses blocking. No mixing. (`verilog_coding_standards.md`)
- [x] **ICG pattern**: Latch-based ICG (always_latch + AND) matches glitch-free standard. (`clock_mux_clock_gating.md`)
- [x] **Scan chain DFT**: Fixed chain order (addr → we → cs → din → ben), documented in Verilog header comment. (`dft_scan_chain.md`)

## Phase 7b: Integration & CLI

- [x] Add feature flags to macro assembler (`--write-enables`, `--scan-chain`, `--clock-gating`, `--power-gating`, `--wl-switchoff`, `--burn-in`)
- [x] Update `rekolektion macro` CLI with feature flag options
- [x] Generate test macro with all features enabled — verify all 6 output files
- [x] OpenLane integration test — 1024x32 mux8 production macro: all 6 outputs (GDS/LEF/Liberty/Verilog/SPICE), 90 pins, Yosys reads blackbox clean
- [x] Verifrog regression suite — 15/15 tests pass with all 6 features enabled simultaneously, including stress test
- [x] Update documentation and README — production features section, CLI examples, SPICE numbers, architecture update
