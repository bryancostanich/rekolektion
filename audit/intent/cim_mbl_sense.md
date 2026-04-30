# T4.1 — Intent doc: `cim_mbl_sense`

## What this cell IS electrically

NMOS source-follower output buffer with current bias. Two NMOS transistors stacked on a shared diff strip: M1 (driver, gate=`MBL`, drain=`VDD`, source=`MBL_OUT`) and M2 (bias, gate=`VBIAS`, drain=`MBL_OUT`, source=`VSS`). Buffers the analog MBL voltage to the `MBL_OUT` pin without digitizing — ADC is external.

**Declared port list** (from generator docstring `src/rekolektion/peripherals/cim_mbl_sense.py`): `MBL MBL_OUT VBIAS VDD VSS`.

**SUPPLY NAMING:** `VDD` and `VSS` (not `VPWR`/`VGND`).

## Source

- Generator: `src/rekolektion/peripherals/cim_mbl_sense.py`
- Cached extracted body: `src/rekolektion/peripherals/cells/extracted_subckt/cim_mbl_sense.subckt.sp` (7 lines)

## Cached Magic extract (full file)

```
* NGSPICE file created from cim_mbl_sense.ext - technology: sky130B

.subckt cim_mbl_sense VBIAS MBL VSS MBL_OUT VDD
X0 VDD MBL MBL_OUT VSS sky130_fd_pr__nfet_01v8 ad=0.33 pd=2.66 as=0.26 ps=1.52 w=1 l=0.15
X1 MBL_OUT VBIAS VSS VSS sky130_fd_pr__nfet_01v8 ad=0.26 pd=1.52 as=0.33 ps=2.66 w=1 l=0.15
.ends
```

**2 NMOS devices.** M1 (X0): drain=VDD, gate=MBL, source=MBL_OUT, body=VSS. M2 (X1): drain=MBL_OUT, gate=VBIAS, source=VSS, body=VSS. Driver width 1.0 µm, bias width 1.0 µm.

## Diff vs intent

| Item | Generator declared | Magic extract | Discrepancy |
|------|--------------------|----------------|-------------|
| Port list | `MBL MBL_OUT VBIAS VDD VSS` (5 ports, generator-level) | `VBIAS MBL VSS MBL_OUT VDD` (5 ports, extract order) | **Same set, different order.** Functional via positional convention — extract port order `VBIAS MBL VSS MBL_OUT VDD` is what callers must use. P2 — name-stable, but order drift complicates direct LVS. |
| Device count | 2 NMOS | 2 NMOS | none |
| Device sizes | "Driver=1.00 µm" | w=1, l=0.15 (both devices) | OK. Note **bias width is 1.0**, but the generator docstring earlier shows `_BIAS_W = 0.50` — possible **size drift** between docstring intent and final layout extract (1.0 µm vs 0.5 µm). **P1.** |
| Body bias | None declared (NMOS body assumed VSS) | Both devices have body=VSS — implicit p-substrate | OK. |

## ⚠ Critical naming concern: VDD/VSS vs VPWR/VGND

This cell uses `VDD`/`VSS`, while the rest of the macro hierarchy uses `VPWR`/`VGND`. The two name conventions **must be physically connected** for the sense buffer to function. The macro top-level reference SPICE (`cim_sram_d_64x64.sp`) declares `.global VDD VSS VSUBS VPWR VGND` — meaning the LVS comparator is told these are all the same global net **by name**. But:

- `cim_assembler.py` shows zero `rename`, `tieoff`, `equate` tokens for VDD↔VPWR or VSS↔VGND.
- `cim_assembler.py` only emits one `VPWR` label and one `VGND` label at the macro top.
- The flattened-top GDS label survey for `cim_sram_d_64x64` shows **128 VDD labels and 256 VSS labels at the sense-row level (Y=0.15 and Y=1.32)**, distinct positions, distinct nets, that don't get re-stamped to VPWR/VGND.

**This is a label-merge fake LVS pattern**: VDD↔VPWR are merged by `.global` declaration in the reference SPICE, and merged by label-name equivalence in netgen's setup if `equate VDD VPWR` is configured. Whether they are physically tied on silicon is undetermined. **P0 candidate** — same chip-killer pattern as WL_BOT/WL_TOP and BL/BR; needs T2 connectivity check at this exact metal layer.

## How rekolektion uses it

- 64 instances tiled by `cim_mbl_sense_row_64` (one per column) at the bottom of the macro array.
- `cim_assembler.py` places this row at the bottom and emits `MBL_OUT[col]` external pins on met2 down to y=0.

## Severity flags

- **P0 candidate — VDD/VSS to VPWR/VGND tie unverified.** Sense supply lives on a different net name from the array supply. There is **no `rename`/`equate`/`tieoff` operation in `cim_assembler.py` source** that tells the layout to physically merge VDD onto VPWR. If silicon has VDD on a separate met1 strap that doesn't touch the VPWR strap, the sense buffer is unpowered on every column. Recommend T2 layer-trace of VDD net continuity to a VPWR-labeled rail. Files this as a **new smoking gun** subject to main-session confirmation.
- **P1 — bias-width drift (0.5 vs 1.0).** Generator docstring says `_BIAS_W = 0.50`, extract says w=1. If liberty timing was characterized with the 0.5-µm assumption, gain/bandwidth predictions are off by 2x.
- **P2 — port-order drift.** Generator declared `MBL MBL_OUT VBIAS VDD VSS`, extract has `VBIAS MBL VSS MBL_OUT VDD`. Functional only if every callsite uses the extract order.

## Ambiguities

- I did not run a Magic flat-extract on the full CIM macro to count physical met1 polygons connecting VDD pads to VPWR pads. The .sp file says `.global VDD VSS VSUBS VPWR VGND` and the macro top has no `VDD`/`VSS` ports — implying LVS-side either equates them or relies on flattening. Either way, **layout-side physical merge cannot be confirmed from cached files alone**. Flag for main-session.
