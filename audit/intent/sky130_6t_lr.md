# T4.1 — Intent doc: `sky130_6t_lr` (custom 6T bitcell)

## What this cell IS electrically

Custom rekolektion 6T SRAM bitcell, "LR" topology (NMOS-left, PMOS-right, horizontal poly gates). Six MOSFETs: two cross-coupled inverters (PD/PU pair × 2) form the storage latch holding `Q`/`QB`; two NMOS access transistors (PG_L on `BL` / PG_R on `BLB`) gated by `WL` couple the latch to bitlines. Default sizes from the generator:
`pd_w=0.42, pg_w=0.42, pu_w=0.42, l=0.15` (unitless, ngspice-hsa scales 1e-6).

**Declared port list** (from `_write_spice_netlist`, file `src/rekolektion/bitcell/sky130_6t_lr.py:~bottom`): `BL BLB WL VDD VSS` — 5 ports.

## Source

- Generator: `src/rekolektion/bitcell/sky130_6t_lr.py`
- Standalone schematic emitted to: `output/sky130_6t_lr.spice`
- The standalone schematic is what `_write_spice_netlist` writes when `generate_bitcell(generate_spice=True)` is invoked.

## Hand-written .spice body (`output/sky130_6t_lr.spice`, 28 lines)

```
.subckt sky130_sram_6t_bitcell_lr BL BLB WL VDD VSS
XPD_L Q  QB  VSS VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XPD_R QB Q   VSS VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XPU_L Q  QB  VDD VDD sky130_fd_pr__pfet_01v8 w=0.42 l=0.15
XPU_R QB Q   VDD VDD sky130_fd_pr__pfet_01v8 w=0.42 l=0.15
XPG_L BL WL  Q   VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
XPG_R BLB WL QB  VSS sky130_fd_pr__nfet_01v8 w=0.42 l=0.15
.ends
```

## Diff vs intent

| Item | Generator declared | Body actually has | Discrepancy |
|------|--------------------|--------------------|-------------|
| Port list | `BL BLB WL VDD VSS` | `BL BLB WL VDD VSS` | none |
| Transistor count | 6 (2 PD + 2 PU + 2 PG) | 6 | none |
| PMOS body | `VDD` (4-th terminal of XPU_L/R) | `VDD` | none — but **no VPB/VNB ports**: PMOS body bias and NMOS body terminal are tied directly to VDD/VSS. The cell has no separate p-well/n-well tap port, so any LVS comparison against an extracted layout must rely on a netgen `equate VPB VDD` / `equate VNB VSS` rewrite. |

## How rekolektion uses it (production scope)

- This standalone `.spice` is **not** included in `output/v2_macros/sram_*.sp` (the production reference). The v2 spice generator (`src/rekolektion/macro/spice_generator.py:30-34`) instead `.include`s the foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` extracted body, NOT this custom 6T. Imports of `load_lr_bitcell` (search results: `cli.py`, `macro_spice.py`, `tests/test_bitcell_lr.py`) treat this as a standalone DRC/SPICE smoke target, not a production bitcell.
- However, `output/run_cim_sweep.py` does include `output/sky130_6t_lr_cim.spice` directly; see `sky130_6t_lr_cim.md`.

## Severity flags

- **P1 — port-naming drift (functional risk if used as LVS reference).** Body terminals VPB/VNB are absent. If LVS ever treats this as the canonical bitcell schematic for an extracted layout that *does* expose VPB/VNB, the netgen `equate` rules in `verify/lvs.py:294-308` are the only thing keeping LVS clean — that is the exact label-merge fake the audit was designed to catch. Confirm this body is not being used as a production LVS reference.
- **P2 — name-drift between custom `VDD`/`VSS` and the v2 macros' `VPWR`/`VGND`.** Anyone who composes this cell into a top-level macro that uses VPWR/VGND must rely on netgen equate or rename-rewrite. Not directly silicon-breaking unless it's actually being composed.

## Ambiguities the auditor cannot decide

- I could not find a Magic-extracted `.subckt.sp` for this Python-generated cell at `src/rekolektion/peripherals/cells/extracted_subckt/`. The only files there are foundry/peripheral extracts. So the diff above is **generator vs hand-written body** rather than **generator vs Magic extraction**. A true T4.1 result needs a fresh Magic extract of `output/sky130_6t_lr.gds` to compare topology + body terminals to the hand-written body. Flag for main-session.
