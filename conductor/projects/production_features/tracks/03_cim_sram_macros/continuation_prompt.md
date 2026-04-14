# Continuation Prompt: CIM SRAM Array Macros (rekolektion Track 03)

Paste this into a new Claude Code session in the rekolektion repo.

---

## Context

You are working in the **rekolektion** repo — an open-source SRAM macro generator for SKY130. Your task is **Track 03: CIM SRAM Array Macros**.

Read these files before doing anything:

1. `CLAUDE.md` — repo rules, build/DRC/render commands
2. `conductor/projects/production_features/tracks/03_cim_sram_macros/plan.md` — track plan with checkboxes
3. `conductor/projects/production_features/tracks/03_cim_sram_macros/decisions.md` — 3 design decisions (reviewed and approved)
4. `conductor/workflow.md` — development methodology, commit conventions

Key source files:
- `src/rekolektion/bitcell/sky130_6t_lr_cim.py` — CIM cell generator + `load_cim_bitcell()` + `CIM_VARIANTS` + `generate_cim_variants()`
- `src/rekolektion/bitcell/sky130_6t_lr.py` — base 6T LR cell
- `src/rekolektion/bitcell/base.py` — `BitcellInfo` abstraction
- `src/rekolektion/array/tiler.py` — array tiler
- `src/rekolektion/array/routing.py` — WL/BL/power/MBL routing
- `src/rekolektion/macro/assembler.py` — macro assembler (needs CIM extension)
- `src/rekolektion/macro/lef_generator.py` — LEF output (needs CIM pins)
- `src/rekolektion/macro/liberty_generator.py` — Liberty output (needs CIM timing)

SPICE characterization data (khalkulo repo):
- `~/Git_Repos/bryan_costanich/khalkulo/conductor/projects/v1b_cim_addition/tracks/21_sram_cim_cells/spice_results_*.md`

## Current State (as of 2026-04-14)

**Phase 1 (Cell Variants): COMPLETE.** All 4 CIM cell variants DRC clean:

| Variant | Cap Geometry | ~fF | Pitch | Cell Area |
|---------|-------------|-----|-------|-----------|
| SRAM-A | 1.30 × 3.10 | 8.1 | 2.15 × 5.16 | 11.08 um² |
| SRAM-B | 1.10 × 2.65 | 5.8 | 1.95 × 4.71 | 9.17 um² |
| SRAM-C | 1.10 × 1.80 | 4.0 | 1.95 × 3.92 | 7.63 um² |
| SRAM-D | 1.00 × 1.45 | 2.9 | 1.93 × 3.92 | 7.54 um² |

GDS + SPICE in `output/cim_variants/`. Rectangular caps oriented narrow-in-X.
MIM cap minimum is 1.0um (verified from Magic DRC deck, NOT the 2.0um in old code).

**Phase 2 (Array Tiler): MOSTLY COMPLETE.**
- MWL: continuous poly across cells (no extra routing needed)
- MBL: vertical M4 stripes via `route_mbl()` in routing.py
- 4×4 and 64×64 arrays DRC clean (nwell.2a waivers only — same-potential)
- Array routing module (WL/BL/power) causes M1/M2 spacing errors with CIM cells — needs adaptation for non-shared-boundary tiling
- 256×64 array not yet tested

**Phase 3–7: NOT STARTED.**

## Key Design Decisions (approved)

1. **T7 connects to Route 1 li1 (latched Q net)** via M1 through N-P gap. NOT to NMOS int_bot (wrong net) or shared diff (wrong node). See Decision 1.

2. **Rectangular MIM caps per variant.** MIM minimum is 1.0um (not 2.0). Caps oriented narrow-in-X to minimize X-pitch. SRAM-C/D fit within 6T X-pitch. See Decision 2 (revised).

3. **Per-variant tiling pitch.** X-pitch = max(cap_w + 0.84, 1.925). Y-pitch = max(NSDM, cap Y-spacing). Total array area 0.39 mm². See Decision 3 (revised).

## Important: sky130B Migration Pending

Track 04 (`conductor/projects/production_features/tracks/04_sky130B_upgrade/`) plans migration from sky130A to sky130B. FEOL is identical. MIM cap rules are identical (verified). Track 03 work is not affected by the migration — it's a PDK path change + re-DRC, not a redesign.

## Constraints

- After every bitcell change: generate GDS, render PNGs, run DRC (see CLAUDE.md)
- Commit directly to main (single dev, no branches/PRs)
- Never git push without asking first
- MBL_OUT pins carry analog voltages — do NOT digitize. ADC is external.
- Use the design decision protocol for any structural choices

## Next Steps

Continue from Phase 2 remaining items (array routing adaptation, 256×64 test), then Phase 3 (CIM peripherals).
