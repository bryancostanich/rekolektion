# T1.1-A — Intent doc: production `pre_<tag>` (PrechargeRow)

## What this cell IS electrically

A bank of bit-line precharge circuits, one PMOS triplet per BL/BR column pair. When `p_en_bar` (active-low) is asserted, all three PMOS in each column pair conduct and pull `bl_<i>`, `br_<i>` toward `VPWR`, additionally equalizing `bl_<i>` ↔ `br_<i>`. Total devices: `3 × N` PMOS, where `N = bits × mux_ratio` (the number of *physical* bit-line pairs before column-muxing). For `sram_weight_bank_small` (32 bits × mux 4) → 384 PMOS; for `sram_activation_bank` (64 bits × mux 2) → 384 PMOS. Both shipping macros have 384.

**Per-pair structure:**
- `MP1`: BL precharge — drain=`bl_<i>`, source=`VPWR`, gate=`p_en_bar`, body=`VPB`
- `MP2`: BR precharge — drain=`br_<i>`, source=`VPWR`, gate=`p_en_bar`, body=`VPB`
- `MP3`: BL/BR equalizer — drain=`bl_<i>`, source=`br_<i>`, gate=`p_en_bar`, body=`VPB`

All three PMOS share the same gate and body, all w=0.42 µm, l=0.15 µm.

## Source

- Layout generator: `src/rekolektion/macro/precharge_row.py` (PrechargeRow)
- Per-cell PMOS sizing: `src/rekolektion/peripherals/precharge.py` (`_PRE_PFET_W=0.42`, `_PRE_PFET_L=0.15`)
- **Reference SPICE: hand-written in `src/rekolektion/macro/spice_generator.py:519` (`_write_precharge_row_subckt`).** No Magic extract; this body IS the design intent against which LVS compares the layout.
- Canonical port order: `src/rekolektion/macro/spice_generator.py:566` (`_precharge_canonical_ports`)

## Hand-written subckt body (paraphrased)

```
.subckt pre_<tag>
+ VPWR p_en_bar
+ bl_0 br_0 bl_1 br_1 ... bl_{N-1} br_{N-1}
+ VPB
* For each i in 0..N-1:
X_mp1_<i>  bl_<i>  p_en_bar  VPWR    VPB  sky130_fd_pr__pfet_01v8  w=0.42 l=0.15
X_mp2_<i>  br_<i>  p_en_bar  VPWR    VPB  sky130_fd_pr__pfet_01v8  w=0.42 l=0.15
X_mp3_<i>  bl_<i>  p_en_bar  br_<i>  VPB  sky130_fd_pr__pfet_01v8  w=0.42 l=0.15
.ends
```

Total: 3N PMOS, all identical sizing.

## Diff vs intent

| Item | Hand-written intent | Layout (post-Magic-extract) | Delta |
|------|---------------------|-----------------------------|-------|
| Port order | `VPWR p_en_bar bl_0 br_0 ... VPB` | matches | ✓ |
| Device count (N=128) | 384 PFETs | 384 PFETs | ✓ |
| Device sizes | w=0.42 l=0.15 (all 3) | w=0.42 l=0.15 (all 3) | ✓ |
| Body terminal | `VPB` (a separate net) | extracted as `w_n36_140#` (auto-named NWELL — see `audit/flood_fill_2026-05-03.md`) | mismatch reconciled by netgen `equate VPB VPWR` + waiver `nwell_bias_disclosure.md` |

**The 4-th-terminal mismatch is the documented N-well-bias-via-subsurface-conduction waiver.** Hand-written intent says VPB; layout NWELL has no metal contact to VPWR but is biased subsurface-style by neighboring strap cells. Same mechanism as the bitcell N-wells, just on the precharge row. Waivered.

## How rekolektion uses it

- One row instance at the top of the macro array (above the bitcell rows). Generator: `precharge_row.py` builds the layout cell; `spice_generator.py:_write_precharge_row_subckt` emits the matching reference body.
- Bound to `bl_<i>`/`br_<i>` per column at the array's top edge; `p_en_bar` is fanned out across the row from the control logic.
- For mux-aware periphery (Option B refactor / task #74): per-column generator inserts pass-through columns at strap positions; the top-level X-line uses the strap-aware `bl_<i>`/`br_<i>` index.

## Severity

- **PASS at intent level.** Hand-written body matches layout topology and devices; the only divergence is the documented N-well-bias waiver.

## Ambiguities / followups

- Hand-written intent assumes all three PFETs are **identical**. If a future tuning campaign sizes the equalizer (MP3) differently from the precharge legs (MP1/MP2), this doc and `_write_precharge_row_subckt` need to be updated together.
- N-well bias: the waiver covers it. Verifying physical N-tap path back to VPWR via foundry sram_sp_wlstrap chain is not done in this intent doc; the audit flood-fill (task #110) inspected the netlist-level connectivity, not the silicon-level bias path.
