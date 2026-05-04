# Tapeout disclosure waiver — SPICE functional behavior not verified at the transistor level

**Macros affected:** `sram_activation_bank`, `sram_weight_bank_small`, `cim_sram_a/b/c/d` (all 6).
**Severity classification:** P1 (functional verification gap).
**Status:** Accepted for tapeout pending designer sign-off OR superseded by completion of task #117 (hand-written SPICE-correct foundry bitcell stub) + a passing transistor-level functional sim.

---

## Why this is separate from `liberty_timing_analytical.md`

The existing `liberty_timing_analytical.md` waiver covers **timing**: Liberty `.lib` values are analytically computed, not SPICE-measured. Its scope explicitly states it does NOT waive functional correctness, claiming:

> "the macros' read/write logical behavior is independently validated by Verilator-level RTL simulation against the SRAM behavioral model (not the .lib)."

That claim refers to **RTL behavioral simulation** — Verilator runs the macro's behavioral Verilog model, which abstracts the bitcell as a register-bank with read/write semantics. RTL simulation cannot detect transistor-level issues such as:

- A subtle modification to the foundry bitcell that breaks its ability to latch a written value
- A drain-bridge or Q-tap addition that creates a parasitic leak path
- A T7 + MIM cap arrangement whose charge-sharing-then-settle behavior diverges from the analytical CIM compute model
- A gate-coupling/Miller-effect issue that destabilizes hold

The transistor-level circuit has NOT been simulated end-to-end. This waiver discloses that gap.

## What HAS been verified (and what HASN'T)

**Verified:**
- ✓ DRC clean across all 6 macros at the geometry level
- ✓ LVS topology equivalence (extracted layout matches reference SPICE per netgen graph-iso, modulo the documented Magic port-promotion artifact in `lvs_pin_resolver_disclosure.md`)
- ✓ Per-row/col flood-fill on the extracted netlist (`audit/flood_fill_2026-05-03.md`): WL/MWL/BL/BR/MBL fan out across the full array; supplies merge cleanly; zero floating well clusters in CIM, one cluster in production covered by `nwell_bias_disclosure.md`
- ✓ Foundry SRAM bitcell GDS layout is the SkyWater foundry-validated `sky130_fd_bd_sram__sram_sp_cell_opt1` cell — known-functional silicon shipping on every SRAM tapeout that uses it
- ✓ Phase 2 modifications (drain bridge, Q-node tap) are within foundry-design-rule territory (DRC clean, LVS clean against a hand-written reference that includes the modifications)
- ✓ T7 + MIM cap supercell additions are DRC clean (capm.8 fixed in task #56; body-bias addressed via Path 3 tap supercell)

**NOT verified (the gap this waiver discloses):**
- ✗ Transistor-level SPICE write/read functional verification of the post-Phase-2 modified bitcell
- ✗ Transistor-level SPICE verification of the CIM compute path (MWL_EN → T7 gate → MIM cap charge sharing → MBL_OUT settle)
- ✗ Any PVT corner sweep at the transistor level
- ✗ Any leakage or hold-stability analysis under at-temperature corners

## Why this gap exists

**Root cause:** the Magic-extracted SPICE for the foundry SRAM bitcell `sky130_fd_bd_sram__sram_sp_cell_opt1` has a topology defect at the SPICE level — Magic identifies poly-overlap geometry as phantom transistors (X1, X4 with L=0.025 nm = below process minimum), which split the LI1 region that bridges the cross-coupled inverter pull-up and pull-down into separate auto-named nets (`a_174_212#` and `a_38_212#`/`Q`). In silicon those LI1 segments are connected; in the SPICE extract they're not. Result: any ngspice testbench against the macro reference SPICE shows Q never latching.

This was discovered during cleanup-audit task #58 (functional SPICE sim of post-Option-B supercell). Initial `sim_supercell_functional.py` run on 2026-05-03 produced:
- write-1: Q stuck near 0 V (expected ≥ 1.26 V)
- hold-1: Q drifts to -0.12 V (no latch holding)
- write-0: Q stuck near 1.17 V (no successful flip)
- hold-0: Q drifts to 0.54 V

These results reflect the SPICE-extract topology defect, NOT the silicon. The defect propagates to BOTH production and CIM macros (the foundry bitcell is the storage substrate for all 6).

## Plan to retire this waiver

**Task #117** is the proper fix: write a hand-written SPICE-correct foundry bitcell stub that:
1. Drops the phantom parasitics (X1, X4 with L=0.025 nm — they're not real transistors)
2. Wires X2/X3 (PMOS pull-ups) and X5/X7 (NMOS pull-downs) with proper Q/QB connectivity using single named nets, mirroring real-silicon LI1 connectivity
3. Uses the SKY130 SRAM-special models per task #101 (`__special_nfet_pass`, `__special_nfet_latch`, `__special_pfet_latch`)

Reference: `docs/spice_results/foundry_cell/foundry_cell.spice` is an OpenRAM-derived working foundry SRAM bitcell SPICE — same cell, characterized SPICE-correct by the OpenRAM project. Use that as the topology template.

After #117:
- Apply the stub to the production bitcell wrapper (`sky130_fd_bd_sram__sram_sp_cell_opt1` body inside `sram_array_<tag>`)
- Apply the stub inside the CIM qtap subckt body (replacing `_write_foundry_qtap` in `cim_spice_generator.py`)
- Re-run `sim_supercell_functional.py` (task #58); confirm Q latches on write_1, holds, BL/BR diverge correctly on read
- Re-run with the CIM compute path active (MWL_EN pulse → MBL_OUT settle); confirm settling matches analytical Liberty's `_cim_compute_ns()` within tolerance
- Then this waiver is retired (or its scope tightens to the specific corners that haven't been swept)

Estimated effort: 1-2 sessions for #117 + functional sim validation. The OpenRAM reference makes the topology unambiguous.

## Mitigations / why this is acceptable for the CI2605 tapeout

1. **Foundry cell is foundry-validated.** The SkyWater `sram_sp_cell_opt1` cell is shipping silicon. Without our Phase 2 modifications, the cell is known-functional.
2. **Phase 2 modifications are silicon-conservative.** Drain bridge and Q-tap additions are documented in `audit/intent/sky130_cim_supercell.md` and reviewed against foundry DRC + LVS rules. They add metal connections; they don't change transistor sizing or topology of the underlying 6T core.
3. **Same modifications used by production.** The drain-bridge wrapper has been in production weight_bank/activation_bank for the same time window as CIM. Production has been LVS-clean at 16,384/16,384 cells across both macros. If the modification broke the cell functionally, both production AND CIM would manifest the same issue.
4. **CIM compute path is conservatively over-designed.** Analytical Liberty assumes 2× margin on `_cim_compute_ns` (per `liberty_timing_analytical.md` mitigation #3). MIM cap value, T7 gate delay, sense-amp threshold all carry analytical safety factors.
5. **First-shuttle silicon characterization.** The CI2605 shuttle includes test structures intended to catch any post-fab functional surprise. A functional regression caught post-fab is a learning iteration, not a chip kill.

## Scope of this waiver

- Applies to all 6 macros listed above on the **CI2605 shuttle** (or any shuttle taping out from this codebase before task #117 lands a SPICE-correct foundry bitcell stub).
- Does NOT waive any DRC/LVS/connectivity finding.  All P0 silicon defects identified in the trust audit (T1.1-A, T2.1-CIM-A, T4.2-CIM-A, T5.2-A, original T4.4-A) are independently resolved.
- Does NOT waive the analytical-Liberty timing concern — see `liberty_timing_analytical.md`.
- Does NOT waive the N-well biasing concern — see `nwell_bias_disclosure.md`.
- Does NOT waive the LVS pin-resolver Magic-tooling artifact — see `lvs_pin_resolver_disclosure.md`.

## Sign-off

| Role | Name | Date |
|------|------|------|
| Designer (SRAM track) | _________________ | _________________ |
| Designer (chip integration) | _________________ | _________________ |

By signing, the designer acknowledges:
1. SPICE-level transistor functional verification of the post-Phase-2 modified bitcell + CIM compute path has not been performed.
2. The verification gap is rooted in a Magic-extract topology defect (foundry bitcell SPICE), not a silicon-correctness defect — but functional silicon correctness has not been independently confirmed by simulation.
3. The mitigations listed above are accepted as the basis for tapeout absent SPICE verification.
4. Task #117 is tracked as the proper fix; if it completes before the next shuttle, this waiver retires.
