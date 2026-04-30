# T4.1 — Intent doc: `sky130_6t_lr_cim` (custom 7T+1C CIM bitcell)

## What this cell IS electrically

Custom rekolektion 7T+1C compute-in-memory bitcell, C3SRAM-style capacitive coupling (Jiang JSSC 2020). Six MOSFETs of the LR 6T core (see `sky130_6t_lr.md`) **plus**: 1 NMOS pass transistor `T7` (gate=`MWL`, source=`Q`, drain=cap bottom plate), and 1 MIM capacitor `C_C` (cap top plate=`MBL`).

**Declared port list** (from `_write_cim_spice_netlist` in `src/rekolektion/bitcell/sky130_6t_lr_cim.py`): `BL BLB WL MWL MBL VDD VSS` — 7 ports.

## Source

- Generator: `src/rekolektion/bitcell/sky130_6t_lr_cim.py` (extends `sky130_6t_lr.py` with 1 NMOS + 1 MIM cap geometry).
- Standalone schematic emitted to: `output/sky130_6t_cim_lr.spice` (yes — file path uses `cim_lr` not `lr_cim`).
- Variants emitted via `generate_cim_variants()` to `output/cim_variants/sky130_6t_cim_lr_sram_{a,b,c,d}.gds` and corresponding `.spice` files.

## Hand-written .spice body (`output/sky130_6t_cim_lr.spice`, 25 lines)

```
.subckt sky130_sram_6t_cim_lr BL BLB WL MWL MBL VDD VSS
XPD_L Q  QB  VSS VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XPD_R QB Q   VSS VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XPU_L Q  QB  VDD VDD sky130_fd_pr__pfet_01v8 w=0.42 l=0.15
XPU_R QB Q   VDD VDD sky130_fd_pr__pfet_01v8 w=0.42 l=0.15
XPG_L BL WL  Q   VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XPG_R BLB WL QB  VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XT7 Q_cap MWL Q VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XCC MBL Q_cap sky130_fd_pr__cap_mim_m3_1 w=1.0 l=1.0
.ends
```

## Magic-extracted body found at `extracted_subckt/sky130_sram_6t_cim_lr_sram_d.subckt.sp`

```
.subckt sky130_sram_6t_cim_lr BL BLB WL MWL MBL VPWR VGND
X0 a_36_272# a_36_272# VGND VGND sky130_fd_pr__nfet_01v8 ...   (PD diode-tied? wired Q=QB? — see diff below)
X1 a_265_402# a_36_372# a_265_302# VPWR sky130_fd_pr__pfet_01v8 ...
X2 a_62_616# MWL a_36_164# VGND sky130_fd_pr__nfet_01v8 ...   (T7?)
X3 MBL a_62_616# sky130_fd_pr__cap_mim_m3_1 l=1.45 w=1
X4 VGND a_36_164# a_62_94# VGND sky130_fd_pr__nfet_01v8 ...
X5 a_36_164# WL a_265_0# VPWR sky130_fd_pr__pfet_01v8 ...    (PMOS gate-tied to WL?)
X6 a_265_302# a_36_272# VPWR VPWR sky130_fd_pr__pfet_01v8 ...
X7 a_62_94# WL BL VGND sky130_fd_pr__nfet_01v8 ...           (PG)
X8 VPWR a_36_164# a_36_164# VPWR sky130_fd_pr__pfet_01v8 ... (PMOS diode-tied?)
X9 BLB a_36_372# a_36_272# VGND sky130_fd_pr__nfet_01v8 ...  (PG?)
.ends
```

10 devices total: 6 NMOS + 3 PMOS + 1 cap. **Declared body** has 6 NMOS (PD×2 + PG×2 + T7) + 2 PMOS (PU×2) + 1 cap = **9 devices**.

## Diff vs intent

| Item | Generator/.spice declared | Magic extract has | Discrepancy / severity |
|------|---------------------------|-------------------|------------------------|
| Port list | `BL BLB WL MWL MBL VDD VSS` | `BL BLB WL MWL MBL VPWR VGND` | **Name drift VDD→VPWR, VSS→VGND.** Functional via netgen `equate VDD VPWR` / `equate VSS VGND` (`verify/lvs.py:294-308`) — label-merge LVS. P1. |
| Device count | 9 (6 NMOS + 2 PMOS + 1 cap) | 10 (6 NMOS + 3 PMOS + 1 cap) | **+1 PMOS in extract**. The extracted X1, X6, X8 are all PMOS — that's three PMOS in the extracted body but only two in the hand-written. Could indicate: (a) duplicate PMOS placed in layout (DRC bug), (b) generator omits a PMOS (intent-vs-reality mismatch), or (c) extraction recognizes a feature the generator didn't intend. **CANNOT DECIDE without Magic schematic plot.** |
| `XCC` capacitor | w=1.0, l=1.0 | w=1, l=1.45 | Cap length 0.45 µm larger in extract than in standalone .spice. The supercell variant SRAM-D config in `sky130_cim_supercell.py` shows `cap_w=1.00, cap_l=1.45` — so the extracted body is consistent with the supercell variant, but the standalone .spice (`output/sky130_6t_cim_lr.spice`) is wrong / stale (uses 1.0×1.0). **P1 — stale standalone .spice.** |
| Body bias | None (NMOS body=VSS, PMOS body=VDD via 4-port) | `VPB`/`VNB` not in port list either; PMOS body=`VPWR` directly | Same label-merge concern as for `sky130_6t_lr.md`. |

## How rekolektion uses it

- The `extracted_subckt/sky130_sram_6t_cim_lr_sram_{a,b,c,d}.subckt.sp` files are pulled into `output/v2_macros/sram_weight_bank_small.sp` as `.include` (see `weight_bank_small.sp` head: includes the four CIM variants explicitly). This means the **CIM variants are LVS-included even into the v2 SRAM macro reference**, suggesting parallel use. Verify this is intentional — currently feels like a vestigial include.
- `extract_cim_subckts.py`, `audit_drc_spatial.py`, `characterize_cim_liberty.py`, `run_lvs_cim.py`, `run_drc_cim.py`, `openroad_smoke_cim.py`, `generate_cim_production.py` all import `CIM_VARIANTS` from `sky130_6t_lr_cim` — so this **is** the production CIM cell.
- However the actual CIM macros in `output/cim_macros/cim_sram_d_64x64/cim_sram_d_64x64.sp` use **the supercell** (`sky130_cim_supercell_sram_d`) wrapping `sky130_fd_bd_sram__sram_sp_cell_opt1_qtap` (foundry 6T + Q-tap) — not this custom 7T cell. So `sky130_6t_lr_cim` is the **legacy CIM cell** used by characterization scripts and `run_cim_sweep.py`, while production CIM macros use the foundry-wrapped supercell instead. This is a **divergent codepath** — characterization runs may be measuring a cell that is not what's actually being taped out. **P1 — characterization-vs-production mismatch.** Flag for main-session.

## Severity flags

- **P0 candidate — extract device count > schematic device count.** 10 devices in extract vs 9 in hand-written body. CANNOT confirm root cause without a Magic schematic image; if the extra PMOS is a duplicate driver or a DRC-violating feature, that is silicon-breaking.
- **P1 — `output/sky130_6t_cim_lr.spice` cap dimension stale (1.0 vs 1.45).** Anyone using this for SPICE characterization gets the wrong capacitance.
- **P1 — production-vs-characterization codepath divergence.** Characterization scripts use this 7T cell; production macros use the foundry+Q-tap supercell. Liberty timing is therefore based on a cell that's not what we tape out.
