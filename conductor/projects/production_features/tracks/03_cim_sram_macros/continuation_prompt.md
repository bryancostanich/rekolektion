# Continuation Prompt: CIM SRAM Array Macros (rekolektion Track 03)

Paste this into a new Claude Code session in the rekolektion repo.

---

## Context

You are working in the **rekolektion** repo — an open-source SRAM macro generator for SKY130 130nm. Your task is **Track 03: CIM SRAM Array Macros**.

Read these files before doing anything:

1. `CLAUDE.md` — repo rules, build/DRC/render commands (after every bitcell change: generate GDS, render PNGs, run DRC — exact commands are in CLAUDE.md, follow them precisely)
2. `conductor/projects/production_features/tracks/03_cim_sram_macros/plan.md` — the full track plan with phases and checkboxes
3. `conductor/workflow.md` — development methodology, commit conventions
4. `src/rekolektion/bitcell/sky130_6t_lr_cim.py` — the existing 7T+1C CIM cell generator
5. `src/rekolektion/bitcell/sky130_6t_lr.py` — the base 6T LR cell it extends
6. `src/rekolektion/bitcell/base.py` — the `BitcellInfo` abstraction
7. `src/rekolektion/array/tiler.py` — the array tiler (needs CIM extension)
8. `src/rekolektion/macro/assembler.py` — the macro assembler (needs CIM extension)
9. `src/rekolektion/macro/lef_generator.py` — LEF output (needs CIM pin additions)
10. `src/rekolektion/macro/liberty_generator.py` — Liberty output (needs CIM timing arcs)

Also read the SPICE characterization data from the khalkulo repo:
- `~/Git_Repos/bryan_costanich/khalkulo/conductor/projects/v1b_cim_addition/tracks/21_sram_cim_cells/spice_results_3.93um.md`
- `~/Git_Repos/bryan_costanich/khalkulo/conductor/projects/v1b_cim_addition/tracks/21_sram_cim_cells/spice_results_3.0um.md`
- `~/Git_Repos/bryan_costanich/khalkulo/conductor/projects/v1b_cim_addition/tracks/21_sram_cim_cells/spice_results_2.5um.md`
- `~/Git_Repos/bryan_costanich/khalkulo/conductor/projects/v1b_cim_addition/tracks/21_sram_cim_cells/spice_results_2.07um.md`

## What you're building

Extend rekolektion to produce 7T+1C CIM SRAM array hard macros (GDS + LEF + Liberty) for the khalkulo i1 CIM experiment. The cell generator already exists. You need to:

1. **Phase 1:** Generate cells at 4 sizes (3.93, 3.0, 2.5, 2.07 um²), DRC each in Magic, SPICE extract and verify against Track 21 characterization data in `khalkulo/conductor/projects/v1b_cim_addition/tracks/21_sram_cim_cells/` (SPICE results files).

2. **Phase 2:** Extend the array tiler to route MWL (multiply wordline — horizontal poly, like WL) and MBL (multiply bitline — vertical M4, like BL but on a higher metal layer). The existing tiler handles WL and BL; MWL/MBL follow the same patterns on different layers.

3. **Phase 3:** Design and lay out CIM-specific peripherals in Magic: MWL drivers (like WL drivers but all-active during CIM compute), MBL precharge, and MBL analog output buffers. The ADC is NOT part of this — it's external.

4. **Phase 4-5:** Assemble complete macros and generate LEF + Liberty.

## Key constraints

- The MIM capacitor sits on M3/M4 above the 6T core — zero XY area overhead
- The SRAM-D cell (2.07 um²) may be too small for minimum MIM cap (2.0 um width) — flag if it doesn't fit
- MBL_OUT pins carry analog voltages — do NOT digitize them. The ADC is a separate block.
- After every bitcell change: generate GDS, render PNGs, run DRC (see CLAUDE.md for exact commands)
- Commit directly to main (single dev, no branches/PRs)
- Never git push without asking first

## Starting point

Start with Phase 1. The generator is at `src/rekolektion/bitcell/sky130_6t_lr_cim.py`. Run it at default params first to verify the existing cell, then work through the 4 sizes.

## Target design

These macros are for **khalkulo** — a ~700K gate INT8 inference accelerator on SKY130 chipIgnite OpenFrame (15.1 mm² die). The CIM experiment uses ~2.95 mm² of free die area. The 4 SRAM CIM arrays need:

| Array | Rows×Cols | Cell Size | Total Area |
|-------|-----------|-----------|------------|
| SRAM-A | 256×64 | 3.93 um² | ~0.27 mm² |
| SRAM-B | 256×64 | 3.0 um² | ~0.20 mm² |
| SRAM-C | 64×64 | 2.5 um² | ~0.13 mm² |
| SRAM-D | 64×64 | 2.07 um² | ~0.13 mm² |
