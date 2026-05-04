# Tapeout disclosure waiver — bitcell + periphery N-well biasing

**Macros affected:** `sram_activation_bank`, `sram_weight_bank_small`, `cim_sram_a/b/c/d` (all 6).
**Severity classification:** P1 (downgraded from P0 on 2026-04-30 after architectural reassessment).
**Status:** Accepted for tapeout pending designer sign-off on this disclosure.

---

## What the layout actually has

The SkyWater foundry bitcell `sky130_fd_bd_sram__sram_sp_cell_opt1` contains an NWELL polygon under its PMOS access/pull-up transistors and a `VPB` (PMOS body) label centered on that NWELL — but **zero LICON1 contacts and zero MCON contacts inside the cell**. There is no internal metal path from the cell's NWELL to the macro VPWR rail.

X-mirror tiling makes adjacent rows' NWELLs abut, so the array's 16,384 (production) / 4,096 (CIM) per-cell NWELLs collapse into ~128 / ~64 row-clusters. None of those row-clusters extend across the bitcell-to-periphery gap to reach a peripheral cell's N-tap.

**Result:** the bitcell n-well is not directly connected to VPWR via metal. Body bias propagates to each cell only through silicon-substrate conduction from peripheral N-tap structures (sense-row taps, precharge taps, wlstrap N-tap structures inserted every N rows).

LVS sees all bitcell NWELLs as `VPB`, then equates `VPB ↔ VPWR` (`scripts/run_lvs_*.py` netgen setup), which is why the macros show net-name match. **The equate does not represent a metal path.**

### Periphery NWELLs (production precharge row) — same mechanism, separate cluster

Audit re-verification (`audit/flood_fill_2026-05-03.md`, task #110) found that the **production precharge row's PMOS bodies form a separate auto-named NWELL cluster** in the extracted netlist. Magic identifies it as `w_n36_140#` with 384 PFET-body terminal references — that's all 384 precharge PFETs (128 columns × 3 PFETs/column for BL precharge + BR precharge + equalize) sharing one NWELL polygon that has no `VPWR` label and no metal contact to VPWR. Same biasing mechanism as the bitcell NWELLs (subsurface conduction from neighboring strap/tap structures), separate physical cluster.

The CIM macros do not exhibit this on the precharge row (`cim_mbl_precharge` has only 64 PFETs and reaches the per-row M2 VPWR/VGND rails introduced for the Path 3 tap-supercell migration; flood-fill shows zero auto-NWELL fragments on CIM SRAM-D). Production precharge is older topology that predates the per-row VPWR rail mechanism.

## Why this is the SKY130 SRAM convention

The foundry's own dummy bitcell (`sky130_fd_bd_sram__openram_sp_cell_opt1_dummy`) also has 0 LICON1 by design. Investigation of every architectural alternative confirms this is structural to the SKY130 SRAM bitcell:

- **Inserting per-cell N-taps:** breaks foundry density rules (NWELL gap to N+ DIFF, N+ DIFF to PMOS DIFF). Tested and rejected (Fix #3 → reverted).
- **Bridging the NWELL across mirrored-pair boundaries with NWELL fill:** would overlap NMOS DIFF (`nwell.5` DRC violation). 0.22 µm gap from X-mirror geometry cannot be closed without violating DIFF rules.
- **Per-row N-tap (wlstrap-style):** what the foundry does. We use the same pattern (wlstrap inserted every 8 rows; for CIM, an explicit `cim_tap_supercell` every 8 columns). This gives strap-anchored NWELL clusters that bias neighboring bitcells via subsurface conduction.
- **Strap-aware periphery:** further reduces but does not eliminate floating clusters. ≥9 floating clusters remain at any reasonable strap interval — that's the residual structural floating.

The shipped layouts use the foundry's intended pattern: strap-cell-anchored NWELLs biasing the bulk array via subsurface conduction. This is what every SKY130 SRAM macro on every shuttle does.

## Risks accepted by this waiver

| Risk | Magnitude (estimated) | Mitigation in design / SoC |
|------|----------------------|----------------------------|
| Power-up bias settle time (NWELL charges via subsurface conduction, not a metal path) | Empirical — typically ≤ a few µs, depending on substrate doping and well-tap density. The strap pattern bounds it. | RTL power-up sequence holds SRAM cs/we deasserted for the SoC's standard reset window (≫ µs). No SRAM access during reset. |
| Reduced latch-up margin near power supply transients | NWELL well-tap resistance is non-zero; supply transients see series-R before the body sees them. | Decoupling capacitance on VPWR/VGND macro pins; SoC-level latch-up qualification per SKY130 process spec. |
| Liberty timing not characterized at-settle | Liberty is currently analytical (separate waiver, see `liberty_timing_analytical.md`). Even after SPICE re-characterization, timing should be measured with body bias FULLY settled. | First read/write after power-up uses the SoC's standard SRAM-init delay; design-margin in clock period absorbs any per-cell body-bias drift. |

## Scope of this waiver

- Applies to all 6 macros listed above on the **CI2605 shuttle** (or any shuttle taping out from this codebase before strap-cell N-tap is replaced with per-cell N-tap, which is not currently planned).
- Covers both the bitcell N-well clusters (described above) and the production precharge-row NWELL cluster (extracted as `w_n36_140#` in production weight_bank and activation_bank). Both are biased via subsurface conduction from neighboring strap/tap structures; neither has a direct metal path to VPWR.
- Does NOT waive any other category of LVS, DRC, or simulation finding. The 5 P0 silicon defects identified in the trust audit (T1.1-A, T2.1-CIM-A, T4.2-CIM-A, T5.2-A, original T4.4-A) have all been independently resolved and are not covered by this waiver.

## Sign-off

| Role | Name | Date |
|------|------|------|
| Designer (SRAM track) | _________________ | _________________ |
| Designer (chip integration) | _________________ | _________________ |

By signing, the designer acknowledges:
1. The bitcell N-well biasing relies on subsurface conduction from strap-cell-anchored NWELLs.
2. The production precharge-row N-well (extracted as `w_n36_140#`) is biased by the same mechanism.
3. This is the SKY130 SRAM convention (matches foundry dummy bitcell behavior).
4. The risks listed above are accepted for this tapeout.
5. Future iterations may switch to a per-cell N-tap architecture (Phase 3 / per-cell tap research) if a future shuttle requires it.
