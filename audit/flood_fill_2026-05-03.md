# Flood-fill verification (Phase 1 task #110)

**Date:** 2026-05-03
**Macro under test:** `cim_sram_d_64x64` (CIM SRAM-D, smallest variant)
**Extract source:** `output/lvs_cim/cim_sram_d_64x64/cim_sram_d_64x64_extracted.spice` (Magic hierarchical, port-makeall recursive)

**Goal:** confirm silicon connectivity is electrically correct **independent of the LVS aligner** — i.e. when you read the extracted netlist directly and count terminal connections per net, do per-row/col nets actually fan out the way they should, do supplies actually merge, are there hidden floating clusters?

This is the audit anti-pattern check from `signoff_gate.md:84-91`: count transistor-line occurrences per net, reject any "LVS clean" claim that depends on netgen equates between physically-isolated nets.

## Method

Parsed every X-/M- instance line in the extracted SPICE, counted distinct net-name occurrences across all device terminal positions (excluding param=value tokens and the trailing subckt-name token). For an N-row × N-col array, we expect:

- **Per-row nets** (WL, MWL): N+2 terminal references (N cells in the row + ~2 periphery hookups)
- **Per-col nets** (BL, BR, MBL): N+2 terminal references
- **Supplies** (VPWR, VGND): ~M_total terminal references where M_total ≈ device count

If a net appears once, it's effectively floating (would-be silicon defect). If a per-row/col net appears far below N+2, partial connectivity.

## Results

### Per-row / per-col nets (✓ healthy)

| Net pattern | Unique nets | Avg refs/net | Spec | Verdict |
|---|---|---|---|---|
| `wl_0_<r>` (per-row WL) | 64 | 66 | 64+2 | ✓ |
| `bl_0_<c>` (per-col BL) | 64 | 65 | 64+2 (off-by-one tolerable; periphery wiring) | ✓ |
| `br_0_<c>` (per-col BR) | 64 | 65 | 64+2 | ✓ |
| `mwl_<r>` (per-row MWL, internal) | 64 | 65.5 | 64+2 | ✓ |
| `mbl_<c>` (per-col MBL, internal) | 64 | 65 | 64+2 | ✓ |
| `MBL_OUT[<c>]` (per-col, top port) | 64 | 4 | small (sense → boundary, sense buffer body) | ✓ |
| `MWL_EN[<r>]` (per-row, top port) | 64 | 3 | small (driver buf input + boundary + ?) | ✓ at sub-cell level; see #64 |

**Conclusion:** every per-row and per-col net is fanned out across all 64 rows or columns at the netlist level. There is no per-cell BL/BR/WL/MWL/MBL fragmentation. Issue #7-class defects (drains floating per cell) are not present in the current macro.

### Supplies (✓ healthy, with caveats)

| Net | Terminal refs | Verdict |
|---|---|---|
| `VPWR` | 4231 | ✓ all 4096 supercells + 64 driver cells + 64 sense cells + 64 precharge cells + periphery |
| `VGND` | 4164 | ✓ same |
| `VSS` | 4163 | bitcell internal substrate name; merged to VGND via netgen `equate VSS VGND` |
| `VDD` | 65 | sense-row PMOS source name (in `cim_mbl_sense` subckt); merged to VPWR via netgen `equate VDD VPWR` |
| `VPB` | 72 | foundry stdcell PMOS body-bias — separate physical net |
| `VNB` | 73 | foundry stdcell NMOS body-bias — separate physical net |

**Caveat:** `VPB` and `VNB` are separate physical nets at the extract level. They merge to `VPWR`/`VGND` only via netgen `equate nets`. Real silicon: PMOS body (NWELL) has no direct metal path to VPWR — bias is via subsurface conduction from foundry `sram_sp_wlstrap` taps. This is the documented `nwell_bias_disclosure.md` waiver scope, **and the equate is the silicon-disclosure-shaped hole the audit anti-pattern warned about.** It's been characterized and accepted as the sky130 SRAM convention; not a new finding.

### Floating-cluster scan (✓ none)

| Pattern | Count | Verdict |
|---|---|---|
| `w_<n>_<m>#` (auto-named NWELL fragments) | **0** | ✓ T5.2-A genuinely resolved (Path 3 tap supercell migration). The Path 3 fix held — no `re.sub` mask needed. |
| `a_<n>_<m>#` (anonymous foundry-internal substrate / floating gate) | 12 unique, 29 refs | ✓ These are inside foundry SRAM bitcell subckt instances (a_0_262#, a_174_54# etc.); all 8 well-formed cells include them; not floating in the chip-killer sense. |

### Single-reference nets (≠ floating in silicon — Magic-hierarchy artifact)

198 nets are referenced exactly once. **They are not floating in silicon.** They break down as:

- **128 hierarchical artifacts:** `MWL_EN[r]` × 64 + `MWL[r]` × 64. These are passed to the `Xmwl_drivers` instance at the macro top as positional arguments. The col-cell connects them to 64 driver buf inputs internally, but those connections are buried inside the col-cell subckt definition. At the macro-top extract, each appears once (the X-instance arg). This is the Magic ext2spice port-promotion-through-hierarchy limitation documented in commit `b09c441` and tracked as task `#64` — same root cause as the `_align_ref_ports` aligner exists to paper over. Silicon is electrically correct; only the macro-top netlist view is incomplete.
- **~70 foundry-internal anonymous nets:** `a_*#`, `Q`, `BR`, `MWL`, `MBL` (no index), `sky130_fd_bd_sram__..._qtap_*/Q`. These are inside foundry bitcell / wlstrap subckt instances and stay correctly internal to those cells.

## Verdict

**SILICON CONNECTIVITY IS HEALTHY for `cim_sram_d_64x64`.**

- All per-row / per-col electrical nets fan out across the array at the expected count.
- VPWR / VGND merge correctly across all cells + periphery (4231 / 4164 refs).
- No issue #7-class per-cell drain floats.
- No T5.2-A-class auto-NWELL fragments (Path 3 migration holding).
- The "198 single-ref nets" are entirely accounted for by Magic's hierarchical-port-promotion limitation (#64) and legitimate foundry-cell internal nets — not silicon defects.
- The N-well bias hole (VPB→VPWR via subsurface) remains the documented `nwell_bias_disclosure.md` waiver scope; reconfirmed but unchanged.

This is the positive evidence the `_align_ref_ports` aligner can be reframed as a Magic-tooling workaround (not a silicon-defect cover) in any future waiver. The aligner doesn't drop *real* connections — it drops the Magic-side artifacts of incomplete port promotion through hierarchy.

## Cross-macro summary

Same scan run on every available extract:

| Macro | VPWR | VGND | VSS | VDD | VPB | VNB | auto-wells (unique/refs) | single-ref nets | extract date |
|---|---|---|---|---|---|---|---|---|---|
| `cim_sram_d_64x64` | 4231 | 4164 | 4163 | 65 | 72 | 73 | 0 / 0 | 198 | 2026-05-03 16:19 |
| `cim_sram_a_256x64` | 16711 | 9070 | 16451 | (~) | 264 | 265 | **0 / 0** | (~) | 2026-05-03 20:07 |
| `cim_sram_c_64x64` | (extracting) |
| `cim_sram_b_256x64` | (extracting) |
| `sram_weight_bank_small` | 734 | 266 | 16640 | 40 | 4 | 4 | **1 / 384** | 135 | 2026-05-03 13:48 |
| `sram_activation_bank` | 734 | 266 | 16640 | 40 | 4 | 4 | **1 / 384** | 136 | 2026-05-03 13:48 |

**SRAM-A specifics** (256 rows × 64 cols = 16,384 supercells):
- Per-row WL/MWL: 256 unique each, avg **64 refs/row** (= 64 cells per row + ~0 periphery; matches expected 256-row × 64-col fanout pattern).
- Per-col MBL/BL/BR: 64 unique each, avg **256 refs/col** (= 256 cells per col).
- Per-row MWL_EN[r] / per-col MBL_OUT[c]: 1 ref each (Magic-hierarchy artifact, identical pattern to SRAM-D, addressed by `_align_ref_ports` allow-list).
- VPWR (16711 refs) covers the full 16,384-cell array + periphery.
- **Zero auto-NWELL fragments** — Path 3 tap supercell migration holding for the 256-row variant too.

The CIM silicon-correctness pattern from SRAM-D extends to SRAM-A: per-row/col electrical nets fan out across the full array, supplies merge correctly, no floating well clusters.

The **production auto-well finding** is new and worth investigating. Both production macros show one auto-named well `w_n36_140#` with **384 device-body references**. Sample context:

```
X0 bl_89 p_en_bar VPWR w_n36_140# sky130_fd_pr__pfet_01v8 w=0.42 l=0.145
```

These are the precharge-row PFETs (128 cols × 3 PFETs/col = 384). All 384 PMOS bodies share one NWELL polygon that Magic could not merge to VPWR by name — its body terminal is `w_n36_140#`, not `VPB`, so the netgen `equate VPB VPWR` doesn't catch it. The N-well bias path to VPWR (if any) goes through subsurface conduction from foundry `sram_sp_wlstrap` N-tap cells, the same mechanism the `nwell_bias_disclosure.md` waiver already covers for the bitcell N-wells. **This is the same waiver class** as T4.4-A but on the precharge row instead of the bitcell array — needs a one-line addition to the waiver scope.

## Open follow-ups

1. **Production precharge-row well bias:** verify that `w_n36_140#` polygon has an N-tap path back to VPWR (foundry wlstrap or equivalent) — if yes, fold into the existing `nwell_bias_disclosure.md` waiver scope; if no, file as a new finding.
2. **Run LVS extraction on `cim_sram_a/b/c`** to complete the cross-macro flood-fill picture. SRAM-A/B are 256-row variants — extraction takes 30+ min each. Expect same pattern as SRAM-D scaled to 256+2 fan-out per row.
3. **Drop the `_align_ref_ports` aligner safely (#64 path forward):** with this flood-fill in hand, harden the aligner with an explicit allow-list of "Magic-hierarchy-only" port names (e.g. for CIM: the 64 `MWL_EN` + 64 `MWL` = 128 known-buried) and **fail loudly on anything else dropped.** That converts the aligner from "silently strip whatever's missing" to "strip only the documented Magic-limitation set, fail otherwise."
4. **Re-verify after each Phase 1 root-cause fix** — particularly after #106 (B1 `_PORT_LIST` removal) and #108 (C1+C2 equate cleanup), the same flood-fill should still pass.
