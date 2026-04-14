# Continuation Prompt: CIM SRAM Array Macros (rekolektion Track 03)

Paste this into a new Claude Code session in the rekolektion repo.

---

## Context

You are working in the **rekolektion** repo — an open-source SRAM macro generator targeting **sky130B**. Your task is **Track 03: CIM SRAM Array Macros**.

Read these files before doing anything:

1. `CLAUDE.md` — repo rules, build/DRC/render commands (uses sky130B)
2. `conductor/projects/production_features/tracks/03_cim_sram_macros/plan.md` — track plan with checkboxes
3. `conductor/projects/production_features/tracks/03_cim_sram_macros/decisions.md` — 4 design decisions (all approved)
4. `conductor/workflow.md` — development methodology, commit conventions

## Current State (as of 2026-04-14)

**Phases 1–6: COMPLETE. Phase 7 (integration test): IN PROGRESS.**

| Variant | Cap | Pitch | Macro Size | Array |
|---------|-----|-------|------------|-------|
| SRAM-A | 1.30×3.10 (~8 fF) | 2.175×5.16 | 143.5×1323 um | 256×64 |
| SRAM-B | 1.10×2.65 (~6 fF) | 1.95×4.71 | 129.3×1208 um | 256×64 |
| SRAM-C | 1.10×1.80 (~4 fF) | 1.95×3.92 | 129.3×255 um | 64×64 |
| SRAM-D | 1.00×1.45 (~3 fF) | 1.93×3.92 | 127.7×255 um | 64×64 |

**Deliverables produced:**
- `output/cim_variants/` — 4 cell GDS + SPICE
- `output/cim_macros/` — 4 assembled macro GDS + LEF + Liberty
- `output/cim_test_structures/` — ring osc + 4 unit cells
- `output/renders/cim_sram_*/` — per-layer PNGs for all variants

**Key source files:**
- `src/rekolektion/bitcell/sky130_6t_lr_cim.py` — cell generator, variants, BitcellInfo
- `src/rekolektion/macro/cim_assembler.py` — macro assembly
- `src/rekolektion/macro/cim_lef_generator.py` — CIM LEF
- `src/rekolektion/macro/cim_liberty_generator.py` — CIM Liberty
- `src/rekolektion/peripherals/cim_mwl_driver.py` — MWL driver
- `src/rekolektion/peripherals/cim_mbl_precharge.py` — MBL precharge
- `src/rekolektion/peripherals/cim_mbl_sense.py` — MBL sense buffer
- `src/rekolektion/peripherals/cim_ring_osc.py` — ring oscillator
- `src/rekolektion/peripherals/cim_unit_cell.py` — unit cell test structures

## Remaining Work

**Phase 7: Integration test** — copy CIM macros to khalkulo, verify OpenLane flow.

**Phase 8: Doc cleanup** — update continuation prompt, decisions.md final numbers,
unblock Track 04 Phase 3 (CIM macros now exist for sky130B regen).

## Constraints

- Commit directly to main, never push without asking
- MBL_OUT is analog — do NOT digitize
- Use design decision protocol for structural choices
