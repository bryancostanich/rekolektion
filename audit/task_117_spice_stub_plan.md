# Task #117 plan — SPICE-correct foundry bitcell stub

**Status:** drafted 2026-05-03. Concrete plan for the rebuild that unblocks Liberty SPICE re-characterization (#24) + functional sim (#58).

## Goal

Replace the Magic-extracted body of `sky130_fd_bd_sram__sram_sp_cell_opt1` (and its qtap variant) in both production and CIM reference SPICE with a hand-written body that:

1. **Latches correctly under SPICE simulation.** Q stores written values; cross-coupled inverter feedback works.
2. **Uses the SKY130 SRAM `__special_*` models** so all 8 transistors bin cleanly under ngspice.
3. **Stays LVS-equivalent** to the Magic-extracted body via the `equate classes` directives already in place (per the post-#101 fix).
4. **Mirrors the silicon's actual topology** — not Magic's poly-overlap-confused extract.

## Reference topology (proven working)

`docs/spice_results/foundry_cell/foundry_cell.spice` is an OpenRAM-derived SPICE for the same SkyWater foundry cell. It works in ngspice (per OpenRAM's own characterization runs). Use it as the topology template.

OpenRAM's reference uses:
- `__special_nfet_latch` (W=0.21, L=0.15) for **all four NMOS** (both pull-down AND access)
- `__special_pfet_pass` (W=0.14, L=0.15) for **both pull-up PMOS**
- 6 transistors total — no parasitic phantoms

Our hand-written qtap body (post-#101) uses a different mapping:
- `__special_nfet_pass` (W=0.14) for access
- `__special_nfet_latch` (W=0.21) for pull-down
- `__special_pfet_latch` (W=0.14, L=0.15) for pull-up
- + 2 phantom parasitic PMOS (X1, X4 with L=0.025)

The **W values are correct per Magic's own extract** (foundry cell access tx are W=0.14, pull-down are W=0.21, pull-up are W=0.14). OpenRAM's reference uses W=0.21 access — that's a different cell variant or a deliberate over-sizing.

For our stub, **keep our extracted W values** (matches the actual silicon) but apply OpenRAM's connectivity pattern (drain-shared LI1 nets named explicitly).

## Topology blueprint

```spice
.subckt sky130_fd_bd_sram__sram_sp_cell_opt1 BL BR VGND VPWR VPB WL VNB
* The 6T core: cross-coupled inverter (X2/X5 + X3/X7) with two access TX (X0, X6).
* No phantom parasitics — drop the L=0.025 entries Magic emitted.
* Drain-shared LI1 nets explicitly named Q (storage) and QB (storage_bar).

* Cross-coupled inverter, Q-side:
*   PMOS pull-up X2: drain=Q, gate=QB, source=VPWR, body=VPB
*   NMOS pull-down X5: drain=Q, gate=QB, source=VGND, body=VNB
X2 Q QB VPWR VPB sky130_fd_pr__special_pfet_latch w=0.14 l=0.15
X5 Q QB VGND VNB sky130_fd_pr__special_nfet_latch w=0.21 l=0.15

* Cross-coupled inverter, QB-side:
*   PMOS pull-up X3: drain=QB, gate=Q, source=VPWR, body=VPB
*   NMOS pull-down X7: drain=QB, gate=Q, source=VGND, body=VNB
X3 QB Q  VPWR VPB sky130_fd_pr__special_pfet_latch w=0.14 l=0.15
X7 QB Q  VGND VNB sky130_fd_pr__special_nfet_latch w=0.21 l=0.15

* Access transistors:
*   X0 (top, BR side): drain=BR, gate=WL, source=Q, body=VNB
*   X6 (bottom, BL side): drain=BL, gate=WL, source=QB, body=VNB
X0 BR WL Q  VNB sky130_fd_pr__special_nfet_pass w=0.14 l=0.15
X6 BL WL QB VNB sky130_fd_pr__special_nfet_pass w=0.14 l=0.15
.ends
```

That's 6 transistors. Magic's extract had 8 (the 2 extras are L=0.025 nm "transistors" that are really poly-overlap parasitics — we're correctly dropping them).

## qtap variant

Same body, but with the Phase 2 modifications:
- Top access TX drain wired to BR via in-qtap LICON1+LI1+MCON+M1 stack (== Q's drain in the SPICE; the silicon stack is what carries it to BR)
- Bottom access TX drain stays at QB (== silicon a_38_0# port for supercell-level drain bridge)
- Q port exposed for the supercell's T7 source connection
- li_14_0# port for the BR-bridge connection from the supercell wrapper

The SPICE for qtap is structurally similar to the bare cell, just with the additional named ports for Phase 2 modifications.

## Steps to implement

### 1. Write the bare-cell stub (~15 lines)

Replace the body emitted by `_extract_cell()` for `sky130_fd_bd_sram__sram_sp_cell_opt1` with a hand-written stub. Pattern:
- New file `src/rekolektion/macro/foundry_bitcell_spice.py` with a `write_foundry_bitcell_subckt(f, cell_name)` helper, mirroring the `_write_foundry_qtap` pattern.
- `spice_generator.py` and `cim_spice_generator.py` import this helper instead of (or in addition to) `_extract_cell()` for the foundry bitcell.

### 2. Update the qtap variant (~25 lines)

`cim_spice_generator._write_foundry_qtap` already hand-writes the qtap body (post-#101). Update it to:
- Use the topology from §1 (drop X1, X4; explicit Q / QB nets)
- Keep the qtap-specific port additions (li_14_0#, a_38_0# for Phase 2 drain bridge external connection; a_0_262# / a_0_24# for WL POLY stripe connections)
- Wire the 2 access TX drains to the right qtap-level ports

### 3. Update production refspice path

`spice_generator.py:_extract_cell()` currently extracts the foundry bitcell body via Magic. Replace with a call to the new `write_foundry_bitcell_subckt()` for the foundry bitcell specifically (other extracted cells stay as-is).

### 4. Validate LVS still passes

The hand-written body has 6 transistors; Magic's extract has 8 (2 are phantoms). Device count differs. To reconcile:

**Option A (preferred):** add 2 placeholder parasitic devices to the hand-written body that Magic's class-equivalence aliasing will accept. They'd be `__special_pfet_latch` instances with W=0.14, L=0.025 wired to VPB nets, mirroring the phantoms. They contribute nothing functional but match Magic's device count for LVS.

**Option B:** add a netgen `ignore device` directive in the wrapper-setup for the L=0.025 parasitic class. netgen drops them on both sides before comparison.

Option A keeps LVS straightforward (matches device count exactly). Option B is cleaner from a "no phantom transistors" purity perspective. Pick A for tonight; B for a future cleanup pass.

### 5. Re-run sim_supercell_functional.py

Validate Q latches on write_1, holds, and BL/BR diverge correctly on read. The pre-#117 sim showed Q stuck near 0 V on write_1 — now it should reach VDD.

### 6. Commit and update waivers

- Update `audit/hack_inventory.md` to reflect that the foundry-bitcell extract issue is now resolved (#117 closes the gap).
- Update `signoff_gate.md` to retire (or re-scope) `spice_functional_unverified.md` waiver.
- If `liberty_timing_analytical.md`'s scope changes (because SPICE re-char becomes possible), update it.

## Validation checklist

- [ ] sim_supercell_functional.py: write_1 → Q ≥ 1.5 V at end of write window
- [ ] sim_supercell_functional.py: hold_1 → Q ≥ 1.5 V at end of hold window (i.e., latch holding)
- [ ] sim_supercell_functional.py: write_0 → Q ≤ 0.3 V at end of write window
- [ ] sim_supercell_functional.py: hold_0 → Q ≤ 0.3 V at end of hold window
- [ ] sim_supercell_functional.py: read_1 → BL > BR after WL pulse with Q=1
- [ ] sim_supercell_functional.py: read_0 → BR > BL after WL pulse with Q=0
- [ ] CIM compute path: MWL_EN pulse → MBL_OUT settles to expected analog voltage based on Q + cap value
- [ ] LVS clean on all 6 macros (production weight_bank/activation_bank, CIM SRAM-A/B/C/D)
- [ ] DRC clean unchanged (no layout work, just SPICE refspice change)

## Risk notes

- **The L=0.025 phantom parasitics** are real parasitic capacitance in silicon (poly-extension overlap on diff edges). Dropping them in SPICE removes a small Cgb. For typical SRAM hold-time analysis this is negligible, but worth flagging in the waiver. If precision matters for a future PVT corner sweep, replace the phantoms with explicit poly-overlap MIM caps instead of dropping them.
- **OpenRAM's reference uses W=0.21 for access** vs our extracted W=0.14. If functional sim with W=0.14 fails to flip Q reliably (cell-ratio too weak), consider whether the foundry GDS we're using has the W=0.14 access (matches Magic) or W=0.21 access (matches OpenRAM). Direct measurement on the GDS would settle it.
- **Production AND CIM share this stub.** A bug in the hand-written body would manifest in all 6 macros simultaneously. Validate on SRAM-D first (smallest, fastest LVS turnaround) before regenerating all variants.

## Estimated scope

- **Stub writing:** ~30 lines of Python (mirror the `_write_foundry_qtap` pattern)
- **LVS validation:** 1 round trip per macro variant (~5 min for 64-row, ~30 min for 256-row)
- **Functional sim validation:** 1 ngspice run after each successful LVS run

Should fit in one focused session if no surprises. The OpenRAM reference makes the topology unambiguous; the rest is integration.
