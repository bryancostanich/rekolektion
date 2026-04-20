# LVS flow for rekolektion SRAM macros

LVS serves two distinct roles for rekolektion macros.  Running the right
LVS at the right level tells you the right thing; running the wrong one
wastes iteration time debugging noise that isn't a bug.

## Role 1 — Macro-internal LVS (generator validation)

**Question answered:** "did the Python generator build the transistor-level
circuit we intended?"

**Flow:** `scripts/run_lvs_tiny.py`
1. `rekolektion.macro_v2.assembler.assemble()` produces the macro GDS.
2. `rekolektion.macro_v2.spice_generator.generate_reference_spice()` writes
   a hierarchical SPICE reference that mirrors the assembler's topology
   (`.include`s the foundry cells' extracted subckts, wires them by
   shared nets).
3. `rekolektion.verify.lvs.run_lvs()` drives Magic + Netgen:
   Magic extracts the GDS, Netgen compares against the reference.
4. Netgen wrapper setup equates VPB↔VPWR / VNB↔VGND and flattens
   filler / decap / tap / clk-buffer cells (though none of those
   appear in the assembler's GDS — the flatten list is carryover
   protection).

**When to run:** after ANY change to `macro_v2/assembler.py`,
`macro_v2/spice_generator.py`, or the foundry-cell sub-generators.
This is the CI gate on `rekolektion` PRs that touch the generator.

**Current state:** `sram_test_tiny` (32×8 mux=4) closes except for an
8-device parallel-merge delta that netgen collapses on the extracted
side but not the reference side (DFF well-tap pfet_hvts on shared
VPWR).  Tracked in `conductor/projects/v1a_digital_module/tracks/
02_sram_design/continuation_prompt.md`.

## Role 2 — Macro-consumer LVS (chip-level portability)

**Question answered:** "can a chip-level P&R tool actually consume this
macro's LEF / GDS / Liberty without breaking?"

**Flow:** `openlane_test_v1weight/` + `openlane_test_v1activation/`
1. Each directory is a minimal test chip that instantiates a single
   v1 production macro (`sram_weight_bank_small` or
   `sram_activation_bank`) with chip-level I/O.
2. OpenLane runs a full chip-level flow — synthesis, floorplan, macro
   placement, PDN, global + detailed routing, streamout.
3. OpenLane's own LVS compares the chip layout extraction against the
   post-PnR Verilog (Yosys synth output).  The SRAM macro appears as
   a blackbox instance on both sides.

**When to run:** after ANY change to `macro_v2/lef_generator.py`,
`macro_v2/liberty_generator.py`, or anything that affects the macro's
abstract views (pin positions, Liberty timing, LEF obstructions).  This
is the CI gate on `rekolektion` PRs that touch the artifact generators.

**What "pass" means:** OpenLane flow completes through detailed routing
and streamout, chip-level netgen LVS device+net counts match *except
for the fill/decap/tapcell pattern* (see "Known waivers" below).

**What "fail" means at each stage:**
- **Synthesis / lint fails:** blackbox Verilog stub is malformed (missing
  USE_POWER_PINS gate, wrong port widths).  Fix in
  `macro_v2/spice_generator.py` or the bb.v hand-written parts.
- **Detailed routing fails with DRT-0416 "offgrid pin shape":** LEF
  pin coordinates aren't on the 5 nm manufacturing grid.  Fixed in
  `lef_generator.py` commit 33b934a by grid-snapping all pin RECTs.
  Any regression here → grid snap broke.
- **Klayout streamout fails "LEF Cell X has no matching GDS cell":**
  a sub-cell referenced in the LEF has no GDS body.  The v1 macros
  once had an empty `sky130_fd_bd_sram__openram_write_driver` cell
  (the duplicate-cell-name import bug from early Option Y work).
  Fixed by using `write_driver_row.py`'s dedup import logic; any
  regression means that fix broke.
- **PDN-0232 "macro does not contain any shapes or vias":** macro
  LEF declares power pins at positions where the GDS has no metal.
  Keep the met2 full-width rails AND per-stub via stacks in the GDS
  matching what the LEF declares.
- **LVS with 50-150 device delta:** expected fill/decap noise; see
  waiver section below.

**Current state:** `sram_weight_bank_small` and `sram_activation_bank`
both pass through streamout + RCX + STA + LVS.  109-204 LVS errors,
all in the fill/decap/tapcell waiver class.

## Known chip-level LVS waivers

Chip-level LVS at the consumer side shows device-count deltas from:

- **fill_1 / fill_2 / fill_4 / fill_8 / fill_12** — purely physical
  area-filling cells OpenROAD inserts after placement.  Layout
  extraction sees their transistors; SPICE references don't model
  them.  Typical: 20-60 cells per chip.
- **decap_3 / decap_4 / decap_6 / decap_8 / decap_12** — decoupling
  capacitors, same story.  Typical: 50-100 cells per chip.
- **tapvpwrvgnd_1** — well-tap cells OpenROAD inserts at
  `FP_TAPCELL_DIST` pitch.  Typical: 6-30 per chip.
- **Body-bias pin drift** — sky130 std cells declare VPB and VNB as
  separate pins but they're globally connected to VPWR and VGND.
  Extraction vs post-PnR verilog sometimes disagree on which net each
  sits on; netgen's default setup doesn't equate them.

These are not circuit bugs.  For tapeout sign-off, the standard
convention is:
1. Generate chip-level LVS report.
2. Audit unique error classes.
3. File each as a documented waiver.
4. Submit the report with waivers attached to the foundry.

Attempting to auto-close all of them via `LVS_FLATTEN_CELLS` can make
things worse: some decap cells use `pfet_01v8_hvt` in their SPICE
definition but their GDS layout extracts as `pfet_01v8` (no HVT
marker).  Flattening exposes this SPICE/GDS model drift and creates
harder-to-classify mismatches.  Better: keep the cells opaque on both
sides, account for them in the waiver list.

## Not-LVS but related: chip-level DRC

`openlane_test_v1*` runs OpenLane with `RUN_MAGIC_DRC: false` because
the foundry bitcell + OpenRAM cells trigger ~1.5M Magic DRC rule
hits that require the SRAM-COREID waiver set.  `rekolektion.verify.drc`
knows about those waivers; OpenLane's vanilla Magic flow doesn't.

DRC sign-off for the macro is done separately at the macro level
(`rekolektion.verify.drc.run_drc` in `tests/test_macro_v2_assembler.py`'s
`test_sram_test_tiny_end_to_end_drc_clean`).

## Summary table

| Gate | Runs at | Role | Current state |
|------|---------|------|---------------|
| `rekolektion.verify.drc` | macro | foundry-cell waivers + generator DRC | clean (real_error_count == 0) |
| `rekolektion.verify.lvs` via `run_lvs_tiny.py` | macro | generator validation (GDS ↔ Python SPICE ref) | 8-device parallel-merge delta on `sram_test_tiny` |
| `openlane_test_v1weight/` | chip | macro abstract-view portability | passes through LVS; ~109 noise-class errors |
| `openlane_test_v1activation/` | chip | macro abstract-view portability | passes through LVS; ~204 noise-class errors |

## When in doubt

- **Macro-level LVS complaint:** the generator did something wrong.
  Debug `assembler.py` / `spice_generator.py`.
- **Chip-level LVS complaint NOT in the waiver classes above:** the
  macro's LEF / Liberty / blackbox Verilog lies about what the GDS is.
  Debug `lef_generator.py` / `liberty_generator.py`.
- **Chip-level LVS complaint IN the waiver classes:** document and
  move on.  Don't try to zero it out; the attempt usually makes the
  report harder to read, not cleaner.
