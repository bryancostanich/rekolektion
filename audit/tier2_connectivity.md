# Tier 2: Connectivity sanity (audit the layout)

Status: in progress (2026-04-30)
Method: count transistor-line occurrences of each per-row/per-col labeled net in the flat extracted SPICE. A properly bridged net N must appear on >= (cells × terminals-driving-it) device lines. A fragmented net appears only on the .subckt port list.

---

## T2.1 — Per-row / per-column flood-fill connectivity

### CIM SRAM-D (64×64, foundry-cell-based supercell array)

**Source**: `output/lvs_cim/cim_sram_d_64x64/cim_sram_d_64x64_extracted.spice` (flat extraction, post-F39 supercell strip).

| Signal | Sample row/col | Device-line count | Expected | Verdict |
|--------|----------------|-------------------|----------|---------|
| `wl_0_0` | row 0 | 256 | 256 (= 64 cells × 4 transistors gating WL: X0+X4+X1+X6 — both WL_TOP and WL_BOT polys, F11-bridged) | ✅ PASS |
| `wl_0_32` | row 32 | 256 | 256 | ✅ PASS |
| `wl_0_63` | row 63 | 256 | 256 | ✅ PASS |
| `mwl_0` | row 0 | 68 | ~65 (= 64 T7 gates + buf_2 driver internals) | ✅ PASS |
| `mwl_32` | row 32 | 68 | ~65 | ✅ PASS |
| `mwl_63` | row 63 | 64 | ~65 | ✅ PASS |
| `mbl_0` | col 0 | 67 | ~65 (= 64 cap top plates + sense/precharge) | ✅ PASS |
| `mbl_32` | col 32 | 67 | ~65 | ✅ PASS |
| `mbl_63` | col 63 | 67 | ~65 | ✅ PASS |
| **`bl_0_0`** | col 0 | **1** | 65 (= 64 access drains + 1 port) | **🚨 FAIL — fragmented** |
| **`bl_0_32`** | col 32 | **1** | 65 | **🚨 FAIL** |
| **`bl_0_63`** | col 63 | **1** | 65 | **🚨 FAIL** |
| **`br_0_0`** | col 0 | **1** | 65 | **🚨 FAIL** |
| **`br_0_32`** | col 32 | **1** | 65 | **🚨 FAIL** |
| **`br_0_63`** | col 63 | **1** | 65 | **🚨 FAIL** |

### CIM finding — P0 chip-killer

**The CIM SRAM-D macro has a BL/BR fragmentation bug analogous to WL_BOT/WL_TOP.** The bl_0_<c> and br_0_<c> labels at the array's south boundary are dangling — they appear in the macro `.subckt` port list but on **zero device lines**. Every bitcell's BL/BR access-transistor drain is on an isolated per-cell LI1 stub (auto-named), electrically disconnected from the column ribbon.

**Mechanism (matches the WL bug pattern):**
1. Foundry `sram_sp_cell_opt1` has no LICON1/MCON inside its bitcell — it relies on Magic's `areaid_sram` label-promotion to virtual-tie LI1 stubs (access-tx drains) to MET1 rails (BL/BR rail) within the cell.
2. The foundry cell's `BL` and `BR` labels are the handles that label-promotion uses.
3. `cim_supercell_array.build()` strips those `BL`/`BR` labels from the qtap copy (F39) so the per-col `bl_0_<c>` parent label can win.
4. `run_lvs_cim._flatten_gds` flattens the macro — sub-cell port boundaries vanish.
5. Without the foundry `BL`/`BR` labels AND without sub-cell port mapping, **the LI1 stubs at each access tx drain have no path to the parent's per-col MET1 strip** (LI1 and MET1 are different layers; only an MCON or a label-promotion handle can bridge them).

**Why production weight_bank survives this**: production uses **hierarchical** extraction (no flatten). Magic preserves the foundry sub-cell's port boundary; the parent passes `bl_0_<c>` as the BL port arg to each foundry instance; Magic's areaid label-promotion *inside the sub-cell extraction* still ties the LI1 stub to the MET1 BL rail via the cell's internal extraction context. Production gets connectivity by accident of hierarchical extraction; CIM loses it because flatten was needed to bypass a different Magic limitation (port-name promotion at the macro top).

**Severity: P0 silicon-breaking.** The CIM macro on silicon would have all BL/BR access transistors floating at their drain — no read path through the bitcell from the column line.

**Fix paths (for evaluation):**
- (a) Add explicit MCONs in the supercell at every cell's BL/BR rail position to physically tie LI1↔MET1 within each cell. Then `bl_0_<c>` propagation across the column works geometrically without label-promotion.
- (b) Don't strip the foundry `BL`/`BR` labels in `cim_supercell_array.build()`; instead use the run_lvs_cim per-col rename mechanism to disambiguate `BL` → `BL_<c>` per column. This relies on label-promotion still working for label-rename.
- (c) Switch CIM LVS back to hierarchical extraction (no `top.flatten()`) — same as production. Loses the original reason flatten was added (top-level port promotion); needs revisit.

### Production weight_bank (128×128, mux=4)

**Source**: `output/lvs_production/sram_weight_bank_small/sram_weight_bank_small_extracted.spice` (hierarchical extraction with `port makeall` recursive, 2026-04-30 09:08).

| Signal | Sample | Token count | Expected | Verdict |
|--------|--------|-------------|----------|---------|
| `wl_0_0` | row 0 | 257 | 257 (= 256 PG/PD/dummy gating WL × 2 polys F11-bridged + 1 port) | ✅ PASS |
| `wl_0_64` | row 64 | 257 | 257 | ✅ PASS |
| `wl_0_127` | row 127 | 257 | 257 | ✅ PASS |
| `bl_0_0` | col 0 | 129 | 129 (= 128 foundry-instance calls in column + 1 port) | ✅ PASS |
| `bl_0_64` | col 64 | 129 | 129 | ✅ PASS |
| `bl_0_127` | col 127 | 129 | 129 | ✅ PASS |
| `br_0_0` | col 0 | 129 | 129 | ✅ PASS |
| `br_0_64` | col 64 | 129 | 129 | ✅ PASS |
| `br_0_127` | col 127 | 129 | 129 | ✅ PASS |

**Verdict: WL/BL/BR all properly bridged in production weight_bank.** Mechanism: hierarchical extraction preserves the foundry sub-cell port boundary; each instance call passes `bl_0_<c>` / `br_0_<c>` / `wl_0_<r>` to the foundry's BL/BR/WL ports; Magic's `areaid_sram` label-promotion inside the per-cell extraction context still ties the access tx drain LI1 stub to the MET1 BL/BR rail. F11+F13 confirmed silicon-correct.

### Production activation_bank (128×128, mux=2)

**Source**: same extraction, 2026-04-30 09:08.

| Signal | Sample | Token count | Expected | Verdict |
|--------|--------|-------------|----------|---------|
| `wl_0_{0,64,127}` | 3 rows | 257 / 257 / 257 | 257 | ✅ PASS |
| `bl_0_{0,64,127}` | 3 cols | 129 / 129 / 129 | 129 | ✅ PASS |
| `br_0_{0,64,127}` | 3 cols | 129 / 129 / 129 | 129 | ✅ PASS |

**Verdict: WL/BL/BR all properly bridged in production activation_bank.** Same mechanism as weight_bank.

---

## T2.2 — Floating-gate scan

Status: **deferred** until production extracts complete. Will scan for any transistor whose gate net doesn't reach a top-level macro pin.

---

## T2.3 — Label-only nets

Status: **deferred** to Tier 5 (git history can identify when label-only nets were introduced).

---

## T2.4 — Schematic net count vs. layout physical-net count

| Macro | Reference net count | Layout net count | Delta | Status |
|-------|---------------------|------------------|-------|--------|
| sram_array_m4_512x32 (in weight_bank) | 33793 | 33793 | 0 | ✅ But see Tier 1 T1.1-A — reference is self-extracted, so this match is weaker than it appears |
| cim_sram_d_64x64 (top) | 49541 | 43494 | **-6047** | **🚨 reference has 6047 more nets** — consistent with BL/BR fragmentation (64 cols × ~95 net loss/col? See T2.1). Will fix when T2.1-CIM-A resolved. |

---

## T2.5 — Boundary continuity

Status: **deferred** — needs Magic visualization or per-cell geometry inspection. Will run if T2.1 results show ambiguity (so far T2.1 directly confirms or denies bridging by counting transistor terminals).

---

## Open items before Tier 2 closure

- T2.1 production weight_bank/activation_bank flood-fill (extract running)
- T2.2 floating-gate scan (after production extract)
- T2.3 label-only nets enumeration (Tier 5)
- T2.5 boundary continuity (only if needed)
