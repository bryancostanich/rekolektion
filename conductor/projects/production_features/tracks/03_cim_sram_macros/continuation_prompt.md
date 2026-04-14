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

Key source files:
- `src/rekolektion/bitcell/sky130_6t_lr_cim.py` — CIM cell generator, `CIM_VARIANTS`, `load_cim_bitcell()`, `generate_cim_variants()`
- `src/rekolektion/bitcell/sky130_6t_lr.py` — base 6T LR cell
- `src/rekolektion/tech/sky130.py` — PDK config (`PDK_VARIANT = "sky130B"`), design rules, centralized PDK path helpers
- `src/rekolektion/array/tiler.py` — array tiler
- `src/rekolektion/array/routing.py` — WL/BL/power/MBL routing
- `src/rekolektion/array/support_cells.py` — cell registry (includes `get_cim_peripheral()`)
- `src/rekolektion/peripherals/cim_mwl_driver.py` — MWL driver (2-inverter buffer)
- `src/rekolektion/peripherals/cim_mbl_precharge.py` — MBL precharge (PMOS switch)
- `src/rekolektion/peripherals/cim_mbl_sense.py` — MBL sense buffer (NMOS source follower)
- `src/rekolektion/macro/cim_assembler.py` — CIM macro assembler
- `src/rekolektion/macro/lef_generator.py` — LEF output (needs CIM pin additions)
- `src/rekolektion/macro/liberty_generator.py` — Liberty output (needs CIM timing arcs)

## Current State (as of 2026-04-14)

**Phases 1–4: COMPLETE.**

| Variant | Cap | Pitch | Macro Size | Array |
|---------|-----|-------|------------|-------|
| SRAM-A | 1.30×3.10 (~8 fF) | 2.175×5.16 | 143.5×1323 um | 256×64 |
| SRAM-B | 1.10×2.65 (~6 fF) | 1.95×4.71 | 129.3×1208 um | 256×64 |
| SRAM-C | 1.10×1.80 (~4 fF) | 1.95×3.92 | 129.3×255 um | 64×64 |
| SRAM-D | 1.00×1.45 (~3 fF) | 1.93×3.92 | 127.7×255 um | 64×64 |

All cells, arrays, peripherals, and macros DRC clean on sky130B.
GDS in `output/cim_variants/` (cells) and `output/cim_macros/` (assembled).
Macros use hierarchical GDS (no flatten — gdstk flatten distorts coordinates).

**sky130B Migration: COMPLETE (Track 04 Phases 1–2)**

`PDK_VARIANT = "sky130B"` in sky130.py. All cells identical between A and B.
SPICE extraction confirms zero functional difference.

## Key Design Decisions (all approved)

1. **T7 connects to Route 1 li1 (latched Q net)** via M1 through N-P gap.
2. **Rectangular MIM caps per variant.** MIM minimum is 1.0um. Caps oriented narrow-in-X.
3. **Per-variant tiling pitch.** SRAM-C/D fit within 6T X-pitch. SRAM-A uses separated mode (2.175).
4. **Peripheral cells**: MWL driver (2-inv buffer), MBL precharge (PMOS switch to external VREF), MBL sense (NMOS source follower, VBIAS external).

## Known DRC Waivers

All are same-potential inter-cell issues (Magic can't resolve nets across cell boundaries):
- **nwell.2a**: adjacent column nwells at VDD (separated mode, SRAM-A)
- **via.2**: adjacent VPWR vias (separated mode, SRAM-A)
- **subcell overlap**: shared boundary abutment (SRAM-B/C/D)

## Constraints

- After every bitcell change: generate GDS, render PNGs, run DRC (see CLAUDE.md)
- Commit directly to main (single dev, no branches/PRs)
- Never git push without asking first
- MBL_OUT pins carry analog voltages — do NOT digitize. ADC is external.
- Use the design decision protocol for structural choices

## Next Steps — Phase 5+

1. **Phase 5: LEF + Liberty generation**
   - CIM LEF: MWL_EN[] pins (input, left edge), MBL_OUT[] pins (analog output, bottom),
     MBL_PRE (input), VREF (inout), VBIAS (input), VDD/VSS. OBS layers from GDS.
   - CIM Liberty: timing arcs for MWL_EN→MBL_OUT (CIM compute latency),
     setup/hold for MBL_PRE, pin capacitances
   - Validate with OpenSTA and OpenROAD

2. **Phase 6: Ring oscillators + test structures** (optional for shuttle)

3. **Phase 7: Integration test** — copy to khalkulo, verify OpenLane reads LEF/Liberty,
   floorplan test with existing v1a SRAMs
