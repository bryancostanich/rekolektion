# Tier 4: Design intent vs. reality

Status: T4.4 closed inline (this doc); T4.1/4.2/4.3 in subagent (pending; see `audit/intent/` and post-merge entries here).

---

## T4.4 — N-well biasing (FLOOD-FILL TEST — main-session inline)

The audit doc (line 196-198) calls out the test exactly:

> Verify every PMOS body is connected to VPWR through a tap, not just labelled VPWR. (This is exactly the kind of fake the WL bug exposed — relying on label-merge instead of physical connectivity.)

### Method

For each macro: flatten GDS, enumerate every NWELL polygon (layer 64 datatype 20), enumerate every LICON1 polygon (layer 66 datatype 44). Cluster NWELLs by transitive bbox overlap (= a single physical n-well plane in silicon). For each cluster, check if **any** LICON1 inside it points to MET1 — that's the only way to physically tie an n-well to a VPWR rail. NWELL clusters with zero LICON1 inside are floating on silicon regardless of any VPB label.

### Results — all macros tested

| Macro | NWELL polys | NWELL physical clusters | Clusters with N-tap | Clusters FLOATING | Floating-bitcell count |
|-------|-------------|-------------------------|--------------------|--------------------|------------------------|
| **CIM SRAM-D** (64×64) | 4224 | 2240 | ~30 (peripheral) | **2210** | **all 4096 bitcells** |
| **sram_weight_bank_small** (128×128 mux=4) | 16759 | 153 | 25 (peripheral) | **128** | **all 16384 bitcells** (128 row-clusters of 128) |
| **sram_activation_bank** (128×128 mux=2) | 16855 | 153 | 25 | **128** | **all 16384 bitcells** |

### Mechanism

The foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` cell has **0 LICON1, 0 MCON polygons** inside it (verified by direct polygon counting). It contains an NWELL polygon and a "VPB" label centered on the NWELL. That label is the only thing tying the cell's n-well to anything — it's a label-only declaration that relies on Magic's `equate VPB VPWR` netgen rule to LVS-clean it.

On silicon, the label has zero electrical effect. **Without an N+ diff + LICON1 inside the NWELL connecting to LI1+MCON+MET1=VPWR, the n-well is physically floating.**

X-mirror tiling makes adjacent rows' NWELLs ABUT (cell-internal NWELL spans full cell height) — that's why we see 128 row-clusters in production rather than 16384 individual cells. But the row-clusters never reach a peripheral N-tap; the gap between the rightmost bitcell and the peripheral cell column is wider than the NWELL extents.

### Verdict

**🚨 P0 universal — every silicon macro in this codebase ships with 100% of its bitcell n-wells physically floating.** PMOS body bias undefined for every bitcell PMOS device. Body-bias-dependent Vt and Idsat means timing/power data in `.lib` is meaningless. Latch-up risk if any n-well voltage drifts low enough to forward-bias the parasitic NPN.

### Why LVS reported clean

Both production LVS (`spice_generator.py` + `run_lvs_production.py`) and CIM LVS (`run_lvs_cim.py`) accept this via:

- `equate nets VPB VPWR` and `equate nets VPB VDD` in `verify/lvs.py:294-308` — netgen treats every VPB-labeled net as VPWR by **name**, regardless of physical connectivity.
- For CIM specifically, `run_lvs_cim.py:251` actively rewrites extracted SPICE auto-named well tokens to VPWR (issue #8) — collapses the label-merge into name-merge.

This is the audit's failure mode #2 (label-merge LVS) directly applied to body bias. The test the audit was designed for caught it on first run.

### Fix paths

| Option | Approach | Tradeoff |
|--------|----------|----------|
| **A — strap cells** | Insert N-tap strap cells between every N rows (where N = stable bias distance, e.g., every 4 rows). Standard SRAM-compiler convention. Each strap is an N+ diff + LICON1 + LI1 + MCON + MET1=VPWR, sized to be DRC-legal. | Largest area cost. Industry standard. Foundry probably ships such a cell — need to find it. |
| **B — N-tap inside every supercell** | Add the N-tap geometry inside `sky130_cim_supercell.py` (CIM) and inside `bitcell_array._import_bitcell_into` (production via custom override). Every cell has its own VPWR tap. | Most robust. Largest per-cell area cost. |
| **C — extend NWELL to reach peripheral N-taps** | Bridge the bitcell-to-periphery NWELL gap so the bitcell n-well touches a peripheral cluster that already has a tap. | Smallest area change. Fragile: relies on layout edge-conditions to provide bias for the entire array. Scaling concern: voltage-drop across a 16384-cell n-well to one peripheral tap may exceed body-bias spec. |

Recommendation: **A**. SoC-grade body-bias requires a tap density that B-or-C alone don't reliably hit. Foundry sky130 SRAM kits ship a `wlstrap` family that includes N-tap strap cells — confirm one exists and tile it.

### Acceptance criteria

- [ ] T4.4 flood-fill on every macro shows zero floating NWELL clusters.
- [ ] Every NWELL cluster has at least one LICON1 → LI1 → MCON → MET1 connection where the MET1 is labeled VPWR.
- [ ] LVS for all macros passes WITHOUT the `equate VPB VPWR` rule (or with the rule, but with extracted SPICE that names every cell's body terminal VPWR directly via the tap network — no auto-named tokens).
- [ ] DRC re-pass on the modified bitcell array.

---

## T4.1 — Per-cell intent docs

Each cell has a dedicated intent doc at `audit/intent/<cell>.md`:

| Cell | Intent doc | Verdict |
|------|------------|---------|
| `sky130_6t_lr` | [`audit/intent/sky130_6t_lr.md`](intent/sky130_6t_lr.md) | P1 — VDD/VSS uses non-canonical names; no VPB/VNB body terminals; standalone .spice not used in production. |
| `sky130_6t_lr_cim` | [`audit/intent/sky130_6t_lr_cim.md`](intent/sky130_6t_lr_cim.md) | **P0 candidate** — extract has 10 devices, generator declares 9; production characterization scripts use this cell while production macros use the supercell — codepath divergence. |
| `sky130_cim_supercell` | [`audit/intent/sky130_cim_supercell.md`](intent/sky130_cim_supercell.md) | P1 — VPB/VNB ports forced to VPWR/VGND at every instance line (label-merge fake); BR vs BLB name drift between supercell generator and extracted CIM variant subckts. |
| `cim_mbl_precharge` | [`audit/intent/cim_mbl_precharge.md`](intent/cim_mbl_precharge.md) | PASS at intent level — 1 PMOS, ports + body match. |
| `cim_mbl_sense` | [`audit/intent/cim_mbl_sense.md`](intent/cim_mbl_sense.md) | **P0 candidate** — uses VDD/VSS not VPWR/VGND; physical tie to macro VPWR/VGND not verified; bias-width drift 0.5→1.0 µm. |
| `sky130_fd_sc_hd__buf_2` (foundry MWL driver) | [`audit/intent/sky130_fd_sc_hd__buf_2.md`](intent/sky130_fd_sc_hd__buf_2.md) | N-A on internal topology (foundry); P1 candidate on N-well tap (T4.4 flood-fill flagged whole-array NWELL floats). |

---

## T4.2 — Per-port intent (production macros)

For each top-level port: physical structure it drives + verification verdict.

### Macro: `sram_weight_bank_small` (`output/v2_macros/sram_weight_bank_small/sram_weight_bank_small.sp`, 17,549 lines)

**Top .subckt port list (82 ports, by category):** `addr[0..8]` (9), `clk` (1), `we` (1), `cs` (1), `din[0..31]` (32), `dout[0..31]` (32), `col_sel_0..3` (4), `VPWR` (1), `VGND` (1).

| Port (representative) | Intended physical structure | Verification | Verdict |
|------------------------|------------------------------|---------------|---------|
| `addr[0..8]` | Routes 9-bit address into the row decoder; decoder outputs drive 128 word lines via WL drivers. | Did not trace the addr[i] net through `bitcell_array.py` + `row_decoder.py` to confirm every addr[i] reaches a decoder gate. **NEEDS T2 connectivity check.** | UNVERIFIED |
| `clk` | Latches addr/data via DFFs; pulse-shapes WL/PRE/SAE control | Same UNVERIFIED. | UNVERIFIED |
| `we` | Write enable — gates write_driver row | UNVERIFIED. | UNVERIFIED |
| `cs` | Chip select — gates entire macro | UNVERIFIED. | UNVERIFIED |
| `din[i]` | i-th data input → write driver column → BL/BR pair on column i | UNVERIFIED. | UNVERIFIED |
| `dout[i]` | i-th sense-amp output, latched on read | UNVERIFIED. | UNVERIFIED |
| `col_sel_0..3` | mux=4: 4-to-1 column mux selects from each group of 4 columns per output bit | UNVERIFIED. | UNVERIFIED |
| `VPWR` | Macro-wide top supply — should reach every PMOS body, every PU, and every peripheral cell. | gdstk top-cell label survey (`output/v2_macros/sram_weight_bank_small/sram_weight_bank_small.gds`): **6 VPWR labels at top, layers={(69,5):1, (68,5):5}**, distinct X positions 6, distinct Y positions 5. Inside the bitcell sub-cells the foundry cell has VPWR/VPB labels per-cell, but **T4.4 main-session flood-fill found 128 floating NWELL row-clusters covering all 16,384 bitcells** — so the VPWR label tree is **physically disconnected** from bitcell n-wells. **P0 silicon-breaking.** | **FAIL — same as T4.4** |
| `VGND` | Macro-wide top ground — should reach every NMOS body, PD source, and peripheral substrate tap. | gdstk top-cell label survey: 7 VGND labels at top, layers={(69,5):2, (68,5):5}. Substrate ground for bitcell PDs goes via foundry cell's `VNB`-labeled p-well pad. T4.4 didn't check VNB the same way it checked VPB. | UNVERIFIED on bitcell side; PASS on rail label presence at top. |

### Macro: `sram_activation_bank` (`output/v2_macros/sram_activation_bank/sram_activation_bank.sp`, 17,648 lines)

**Top port list (143 ports):** `addr[0..7]` (8), `clk`, `we`, `cs`, `din[0..63]` (64), `dout[0..63]` (64), `col_sel_0..1` (2), `VPWR`, `VGND`.

Mux=2 (vs mux=4 for the weight bank), 8-bit address → 256 wordlines.

Per-port verdict structure mirrors weight_bank — same UNVERIFIED on signals, same FAIL on VPWR (T4.4), same UNVERIFIED on VGND. Top-level VPWR label count: 6, VGND: 7, all on same layer pattern.

### Macro: `cim_sram_d_64x64` (`output/cim_macros/cim_sram_d_64x64/cim_sram_d_64x64.sp`, 4,355 lines)

**Top port list (453 ports):** `MWL_EN[0..63]` (64), `MBL_PRE`, `VREF`, `VBIAS`, `MBL_OUT[0..63]` (64), `bl_0_0..63` (64), `br_0_0..63` (64), `wl_0_0..63` (64), `mwl_0..63` (64), `mbl_0..63` (64), `VPWR`, `VGND`.

| Port (representative) | Intended physical structure | Verification | Verdict |
|------------------------|------------------------------|---------------|---------|
| `MWL_EN[r]` | Row r's multiply-WL enable — drives the buf_2 (MWL driver) input on row r | Each `MWL_EN[r]` appears in exactly the right inst lines per ref netlist sampling. Layout side: 64 buf_2 instances tiled by `cim_mwl_driver_col_64`. UNVERIFIED on physical met2 routing from each MWL_EN[r] pin through to its instance gate. | UNVERIFIED |
| `MBL_PRE` | Common precharge enable — drives all 64 cim_mbl_precharge gates | Single node fans out to 64 instances. UNVERIFIED on physical fan-out structure. | UNVERIFIED |
| `VREF` | External analog reference (typically VDD/2) — connects to all 64 precharge sources/drains via VREF input | UNVERIFIED. | UNVERIFIED |
| `VBIAS` | External bias for sense buffers — connects to all 64 sense bias-NMOS gates | UNVERIFIED. | UNVERIFIED |
| `MBL_OUT[c]` | Column c's analog output — sourced from the sense buffer's MBL_OUT pad | UNVERIFIED. | UNVERIFIED |
| `bl_0_<c>` | Column c's BL ribbon — connects to all 64 supercells in the column | Reference netlist: each `bl_0_<c>` referenced 64 times (= 64 rows × 1 supercell each). **But T2.1-CIM-A (issue #7) found that in the EXTRACTED netlist, each access transistor drain is on an isolated per-cell LI1 stub, NOT bridged to the column ribbon — `bl_0_<c>` only appears in the .subckt port list.** Same chip-killer pattern as WL_BOT/WL_TOP. **P0 silicon-breaking.** | **FAIL — see issue #7** |
| `br_0_<c>` | Column c's complementary bitline | Same as `bl_0_<c>` — same chip-killer. | **FAIL — issue #7** |
| `wl_0_<r>` | Row r's WL — connects to all 64 supercells in the row, drives both access transistors per cell | Reference netlist: each `wl_0_<r>` referenced 64 times. UNVERIFIED on layout-extract; subject to same WL_BOT/WL_TOP pattern that triggered this audit. **NEEDS T2 connectivity check.** | UNVERIFIED — high suspicion |
| `mwl_<r>` | Row r's MWL — drives all 64 supercells' T7 gates in the row | Reference: each `mwl_<r>` referenced 65 times (64 supercells + 1 buf_2 driver instance). UNVERIFIED on physical poly continuity across the row. | UNVERIFIED |
| `mbl_<c>` | Column c's MBL ribbon — top plate of MIM cap on every supercell + sense input + precharge target | Reference: each `mbl_<c>` referenced 64+1+1=66 times. UNVERIFIED on layout met4 strap continuity. | UNVERIFIED |
| `VPWR` | Macro-wide top supply | gdstk top-cell label survey (`output/cim_macros/cim_sram_d_64x64/cim_sram_d_64x64.gds`): **1 VPWR label at top** (layer (68,5)), at coord (146.06, 191.35). Single point. The strap is a single met1 vertical at `vpwr_abs_x = pre_x + (p.cols-2)*p.cell_pitch_x = ~145 µm` running full macro height (`cim_assembler.py`: `draw_vert_strap(top, "met1", vpwr_abs_x, vpwr_abs_y, macro_h)`). Whether all 4096 supercells, 64 precharge instances, 64 buf_2 drivers, and 64 sense buffers are tied to this single strap is **UNVERIFIED**. T4.4 already established that **all 4096 supercell n-wells are floating** — i.e., physical VPWR distribution to bitcell bodies fails. | **FAIL — same as T4.4** |
| `VGND` | Macro-wide top ground | Single VGND label at (146.06, 0.07), single met1 vertical strap at `vgnd_abs_x = sense_x + (p.cols-2)*p.cell_pitch_x`. UNVERIFIED on whether every bitcell PD source and every sense buffer's VSS reach this strap. Sense uses `VSS` (different name) — see new smoking gun below. | UNVERIFIED — high suspicion |

### **NEW SMOKING GUN — T4.2-CIM-A — VDD/VSS sense supplies do not bridge to VPWR/VGND in cim_assembler**

- **Severity:** P0 candidate (silicon-breaking if not bridged).
- **Description:** The CIM macro's MBL sense buffers use net names `VDD`/`VSS` while the rest of the macro uses `VPWR`/`VGND`. Inspection of `src/rekolektion/macro/cim_assembler.py` shows **0 occurrences each** of `rename`, `tieoff`, `equate`, or `rewrite` operations between these names. The reference SPICE relies on `.global VDD VSS VSUBS VPWR VGND` to merge them by name during LVS, and on netgen's setup file to equate them. **There is no physical layout operation that ties the VDD strap to the VPWR strap.**
- **Evidence:**
  - `output/cim_macros/cim_sram_d_64x64/cim_sram_d_64x64.sp` line 1: `.global VDD VSS VSUBS VPWR VGND`
  - `output/cim_macros/cim_sram_d_64x64/cim_sram_d_64x64.sp` `.subckt cim_mbl_sense VBIAS MBL VSS MBL_OUT VDD` (uses VDD/VSS).
  - `output/cim_macros/cim_sram_d_64x64/cim_sram_d_64x64.sp` `.subckt cim_sram_d_64x64 …` top port list ends with `VPWR VGND` — no VDD/VSS exposed.
  - `src/rekolektion/macro/cim_assembler.py` grep: `VPWR=10 occurrences` (for label/strap), `VGND=4 occurrences`, `tieoff=0`, `rename=0`, `equate=0`, `rewrite=0`.
  - gdstk flat label survey (`cim_sram_d_64x64.gds` top cell flattened): `VDD: 128 labels @ y=1.32 (sense row)`, `VSS: 256 labels @ y=0.15 (sense row)` — these labels are at distinct Y positions from the macro VPWR/VGND straps which run vertically at x=146.06.
- **Mechanism:** Magic flat extraction will see two physically separate metal nets, name them by their respective labels (VDD, VSS at sense row; VPWR, VGND at macro vertical straps), and ext2spice will emit them as separate nodes. Netgen `equate VDD VPWR` and `equate VSS VGND` rules will declare them equivalent for LVS purposes — but on silicon they are not connected. **Same label-merge fake LVS pattern that motivated this entire audit.**
- **Hypothesis the auditor cannot verify alone:** Magic's substrate-merge or hierarchical extraction may collapse VSS-to-VGND if both reach the substrate via in-cell taps. But VDD-to-VPWR has no substrate path; if the only thing tying them is a netgen rule, sense buffers are unpowered on silicon.
- **Required confirmation (main-session):** Trace VDD met1 polygons in the cim_mbl_sense_row layout — does the VDD rail at y=1.32 physically meet the VPWR vertical strap at x=146.06? If not, P0 silicon-broken.

---

## T4.3 — Power / ground continuity

For each macro: VPWR / VGND / VSUBS top-level label distribution + macro-pin-to-supply-tap reach + rail spanning checks.

### Method

Used `gdstk` to enumerate label and polygon presence on layers li1.label/met1.label/met2.label etc. for the top cell of each macro (no recursion = top-only labels) and again for the flattened top cell (recursive merge of all sub-references). Cross-checked with `cim_assembler.py` source for the rail-drawing code.

### `sram_weight_bank_small`

**Top-only label survey** (`output/v2_macros/sram_weight_bank_small/sram_weight_bank_small.gds`):

| Net | Label count | Layers | Distinct X | Distinct Y |
|-----|------------|--------|-----------|------------|
| VPWR | 6 | (69,5)×1, (68,5)×5 | 6 | 5 |
| VGND | 7 | (69,5)×2, (68,5)×5 | 7 | 5 |
| VSUBS, VPB, VNB, VDD, VSS | 0 (none at top) | — | — | — |

Top cell bbox: `(0.50, -15.95) — (259.46, 279.42)` µm. VPWR has 5 distinct Y-positions and 6 X — implying multiple peripheral pads, not a single corner pin. **Label distribution at top looks reasonable.**

**FAILS already documented:** T4.4 — all 16,384 bitcell NWELLs floating despite VPB labels. Same root cause: foundry bitcell has 0 LICON1, 0 MCON inside its NWELL, only a "VPB" label, and the bitcell-to-periphery NWELL gap exceeds NWELL extent. So **physical reach from VPWR pin to bitcell body bias is broken**. T4.3 verdict: **FAIL — same as T4.4. P0.**

### `sram_activation_bank`

Top-only label survey: VPWR=6, VGND=7, identical layer/X/Y signature to weight_bank_small (same generator, same template). T4.4 found same NWELL-floating pattern. T4.3 verdict: **FAIL — same as T4.4. P0.**

### `cim_sram_d_64x64`

**Top-only label survey** (`output/cim_macros/cim_sram_d_64x64/cim_sram_d_64x64.gds`):

| Net | Label count | Layers | Distinct X | Distinct Y |
|-----|------------|--------|-----------|------------|
| VPWR | 1 | (68,5)×1 | 1 (x=146.06) | 1 (y=191.35) |
| VGND | 1 | (68,5)×1 | 1 (x=146.06) | 1 (y=0.07) |
| VSUBS, VPB, VNB, VDD, VSS | 0 (at top) | — | — | — |

Top cell bbox: `(-0.19, -0.12) — (150.88, 191.78)` µm. **Only ONE VPWR and ONE VGND label at top-level**, both as point pins at single corners. Layout source confirms this in `cim_assembler.py`:

```python
draw_pin_with_label(top, text="VPWR", layer="met1",
                    rect=(vpwr_abs_x - 0.07, macro_h - 0.14,
                          vpwr_abs_x + 0.07, macro_h))
```

```python
draw_pin_with_label(top, text="VGND", layer="met1",
                    rect=(vgnd_abs_x - 0.07, 0.0,
                          vgnd_abs_x + 0.07, 0.14))
```

Each is a single 0.14 × 0.14-µm pin pad. Adjacent to each is a single `met1` vertical strap that runs `macro_h` (~191 µm). Whether this strap actually reaches **every** supercell row, every MWL driver, every precharge, and every sense buffer is **UNVERIFIED** at T4.3 level — the auditor would need to trace met1 polygon adjacency from the strap into each peripheral row + the bitcell array supply tap structure.

**Flattened-cell label survey** of `cim_sram_d_64x64.gds`:

| Net | Total labels (flat) | Layers (flat) | Distinct Y values |
|-----|---------------------|----------------|-------------------|
| VPWR | 4354 | (68,5)×4225, (68,20)×64, (69,5)×1, (67,5)×64 | 131 |
| VGND | 4226 | (68,5)×4161, (69,5)×1, (67,5)×64 | 130 |
| VPB | 4160 | (64,5)×4160 | 128 |
| VNB | 4160 | (64,59)×4160 | 128 |
| VDD | 128 | (67,5)×64, (67,20)×64 | 1 (Y=1.32, sense row) |
| VSS | 256 | (67,5)×64, (68,5)×64, (67,20)×64, (68,20)×64 | 1 (Y=0.15, sense row) |

VPWR/VGND labels propagate via foundry cell instances into ~4200 places per net. **However:** these are *labels*, and per T4.4 the physical NWELL clusters under those labels are not connected to any tap.

VDD/VSS labels appear ONLY at the sense-row Y-coordinates (y=1.32 and y=0.15), as 64-128 labels per net. There is no VDD/VSS label co-located with any VPWR/VGND vertical strap, supporting the **new smoking gun T4.2-CIM-A** above.

VSUBS: **0 labels anywhere.** The reference SPICE declares `.global VSUBS` but there is no VSUBS-labeled metal in the layout. If LVS is comparing against a reference that uses VSUBS as a substrate node, the absence of VSUBS labels in the extract means Magic auto-merges substrate via the `defaultsubstrate` directive — typically OK for SKY130 if `extract style ngspice(orderedports)` is set, but worth flagging as a verification dependency.

### Summary verdict per macro

| Macro | VPWR rail spanning | VGND rail spanning | VSUBS handling | T4.3 verdict |
|-------|--------------------|--------------------|----------------|---------------|
| sram_weight_bank_small | Top labels OK; bitcell n-wells FAIL T4.4 | Top labels OK; bitcell substrate UNVERIFIED | Implicit | **FAIL P0 — T4.4 escalates here** |
| sram_activation_bank | Top labels OK; bitcell n-wells FAIL T4.4 | Top labels OK; bitcell substrate UNVERIFIED | Implicit | **FAIL P0 — T4.4 escalates here** |
| cim_sram_d_64x64 | Single point pin + single strap; bitcell n-wells FAIL T4.4; VDD/VPWR bridge UNVERIFIED | Single point pin + single strap; VSS/VGND bridge UNVERIFIED | No VSUBS labels | **FAIL P0 — T4.4 + new T4.2-CIM-A** |

### Findings to add to `audit/smoking_guns.md` (do not edit directly per audit rules)

1. **T4.2-CIM-A (NEW, P0 candidate):** CIM macro's sense-row VDD/VSS supplies are on net names disjoint from the macro's VPWR/VGND straps; `cim_assembler.py` contains zero `rename`/`equate`/`tieoff`/`rewrite` operations bridging them; LVS-clean depends entirely on netgen `.global` declaration in reference SPICE plus equate rules. Layout-side VDD↔VPWR physical connectivity is **unverified**; if the strap at x=146.06 does not physically merge with the sense-row VDD met1 at y=1.32, sense buffers are unpowered on silicon.
2. **T4.1-6T_LR_CIM-A (NEW, P0 candidate):** Extracted CIM variant subckt (`extracted_subckt/sky130_sram_6t_cim_lr_sram_d.subckt.sp`) has 10 devices, while the hand-written `_write_cim_spice_netlist` in `sky130_6t_lr_cim.py` declares 9 devices (6 NMOS + 2 PMOS + 1 cap). Extract has **3 PMOS** instead of 2. CANNOT decide root cause from the cached file alone — needs Magic schematic plot. Could be: (a) generator omits a PMOS (intent vs reality), (b) layout has a duplicate PMOS device (DRC bug), (c) extraction sees a real device the spec does not describe.
3. **T4.1-6T_LR_CIM-B (NEW, P1):** `output/sky130_6t_cim_lr.spice` ships `XCC w=1.0 l=1.0` for the MIM cap, but every supercell variant (`CIM_SUPERCELL_VARIANTS`) uses cap_l ≥ 1.45 (SRAM-D=1.45, SRAM-C=1.80, SRAM-B=2.65, SRAM-A=3.10). The standalone .spice has stale dimensions; any SPICE characterization using `output/sky130_6t_cim_lr.spice` directly (via `output/run_cim_sweep.py`, which does this — `CIM_CELL = "output/sky130_6t_lr_cim.spice"`) is measuring a smaller capacitance than what's taped out.
4. **T4.1-MBL_SENSE-A (NEW, P1):** Bias NMOS width drift between docstring (`_BIAS_W = 0.50`) and Magic-extracted body (`w=1`). Liberty timing characterized with the wrong bias-current may misestimate sense gain/bandwidth by 2x.
5. **T4.1-CIM_DIVERGENT_PATH-A (NEW, P1):** Production CIM macros use the `sky130_cim_supercell` (foundry 6T + Q-tap + T7 + cap) while characterization scripts (`characterize_cim_liberty.py`, `audit_drc_spatial.py`, `run_cim_sweep.py`, `run_lvs_cim.py`, `run_drc_cim.py`, `openroad_smoke_cim.py`, `generate_cim_production.py`) all import `CIM_VARIANTS` from `sky130_6t_lr_cim` — the legacy custom 7T cell. Liberty timing data in `cim_macros/*.lib` may be characterized against the wrong cell.
6. **T4.1-SUPERCELL-A (NEW, P1):** `sky130_cim_supercell` reference netlist instance line forces VPB=VPWR and VNB=VGND at every callsite, collapsing the body-bias terminals onto the supply rails. Same label-merge fake pattern as T4.4 — LVS-clean conditional on netgen `equate VPB VPWR` / `equate VNB VGND`.
7. **T4.1-SUPERCELL-B (NEW, P2):** `BR` vs `BLB` net-name drift between supercell generator and extracted CIM variant subckts. Functionally equivalent but verification fragility.
