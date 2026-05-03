# Tapeout disclosure waiver — Liberty timing data is analytical, not SPICE-measured

**Macros affected:** `sram_activation_bank`, `sram_weight_bank_small`, `cim_sram_a/b/c/d` (all 6).
**Severity classification:** P1.
**Status:** Accepted for tapeout pending designer sign-off, OR superseded by Liberty re-characterization (track via task #24).

---

## What the `.lib` files actually contain

Every `.lib` shipped from this codebase has timing arcs computed by analytical formulas in the generator code path, **not** by extracting numbers from SPICE simulation. Both generator docstrings declare this explicitly:

- `src/rekolektion/macro/liberty_generator.py:1-25`: "analytically-computed timing values based on array dimensions and SKY130 device/interconnect parameters". Read-path delay = WL RC + BL develop + sense-amp settle, derived from analytical models of foundry I_read (~5 µA at 1.8 V), sense-amp trip voltage (~100 mV differential), NAND decoder per-stage delay (~0.15 ns).
- `src/rekolektion/macro/cim_liberty_generator.py:1-15`: "Timing arcs remain analytical estimates: MIM cap charge sharing with MBL parasitic (~256 fF/column), source-follower buffer delay (~0.5 ns), MWL poly RC delay (~0.1 ns)". Explicitly names a "future SPICE-characterisation pass" as the proper fix.

The Liberty comments themselves declare this in the file: `comment : "Timing from analytical model — see compute_timing()"`.

`output/spice_char/` directory does not exist. There are 0 `.raw` files in the repository.

## What this means for SoC integration

Liberty timing is the contract a downstream SoC consumer uses to close timing. Analytical Liberty values:

| Concern | Implication |
|---------|-------------|
| **Process corner not measured.** Generator uses TT-typical device params, no SS/FF-specific equations. | Worst-case access time may be longer than the .lib reports. SS-corner read may miss a cycle the .lib says fits. |
| **Voltage corner not measured.** Generator uses VDD=1.8 V values; no 1.62/1.98 V coverage. | Low-voltage read margin (1.62 V) may be tighter than the analytical model predicts. |
| **Temperature corner not measured.** No 27/85/125°C variation. | Hot-temperature mobility drop affects WL/BL RC; .lib doesn't capture it. |
| **Setup/hold against CLK** computed from generator-internal CLK-to-Q assumptions. | Real CLK/Q observed delay may diverge — RTL timing analysis with Liberty as ground truth would miss it. |
| **Liberty for CIM uses legacy LR-CIM cell topology** (per T4.1-DIVERGENT-A / #10). | CIM `.lib` was characterized against an obsolete bitcell. Compute-cell timing claims do not represent the current supercell topology. |
| **Capacitance values** for non-trivial fanout pins (MBL_PRE, VBIAS) come from analytical Cox * W * L + 30% routing-parasitic margin. | Margin assumption may be off; sims would expose it. |

## Mitigations / how to sign with this waiver

The chip-integration team accepting this waiver should:

1. **Treat Liberty timing values as upper bounds, not measurements.** Add 15–25% margin in worst-case path analysis touching SRAM read/write.
2. **Constrain CLK-to-SRAM access timing in RTL** to leave headroom: e.g., insert one pipeline stage between SRAM output and any combinational logic that gates the next clock edge.
3. **For CIM compute timing specifically:** budget 2× the .lib analytical value until SPICE-measured numbers replace them. CIM compute is the chip critical path; over-budgeting here is the cheapest insurance.
4. **For tapeout-day timing closure:** rely on full-chip STA reports against this Liberty as a relative comparator. Absolute timing must come from post-silicon characterization or pre-tapeout SPICE re-char (track via task #24).

## Plan to retire this waiver

Task #24 (F10 — Liberty re-characterization for supercell-based variants) is the proper fix. Scope:

- Per-corner SPICE testbench: TT/SS/FF × 1.62/1.80/1.98 V × {27, 85, 125} °C — 27 corners minimum.
- Per macro variant: a representative read (worst-case row × worst-case column), a representative write, and a hold-time sweep.
- Replace `compute_timing()` and `_cim_compute_ns()` numbers in the Liberty generators with table-lookups indexed by the SPICE-extracted values.

Estimated effort: 1–2 sessions (Magic-extract netlists already exist; ngspice testbench infrastructure is the missing piece).

If the next shuttle taping out from this codebase is timing-critical at the SRAM access path, **#24 must complete first**. If not (e.g., the SoC has substantial slack on the SRAM path), this waiver covers tapeout.

## Scope of this waiver

- Applies to the **CI2605 shuttle** (or any tapeout from this codebase before task #24 lands).
- Does NOT waive functional correctness — the macros' read/write logical behavior is independently validated by Verilator-level RTL simulation against the SRAM behavioral model (not the .lib).
- Does NOT waive any DRC/LVS finding. Silicon-correctness is GREEN per the trust audit.

## Sign-off

| Role | Name | Date |
|------|------|------|
| Designer (SRAM track) | _________________ | _________________ |
| SoC timing closure owner | _________________ | _________________ |

By signing, the designer acknowledges:
1. The shipped `.lib` timing values are analytical, not SPICE-measured.
2. The list of unmeasured corners and the listed implications.
3. The mitigation rules (margin assumptions, RTL pipelining, CIM compute budgeting) are in effect for tapeout.
4. Task #24 is tracked as the future replacement for this waiver.
