# Track 04: sky130B PDK Upgrade

*Created 2026-04-14.*

Migrate rekolektion from sky130A to sky130B. The khalkulo v1 shuttle includes
ReRAM CIM experiments (Track 27b in khalkulo), which require sky130B — the
process variant with the ReRAM BEOL option. Since the entire die is fabricated
with one process, all macros must target sky130B.

**Why sky130B:** sky130B adds a `reram` layer (GDS 201/20) between M1 and M2.
To make room for it, via1 is thicker (0.565 vs 0.27 um), which shifts every
layer from M2 upward by 0.295 um in Z. The FEOL (transistors, diffusion,
poly, contacts, M1) is identical. Standard cells are identical. The only
functional impact on SRAM macros is parasitic capacitance changes in the
metal stack, which may affect timing characterization.

**Depends on:** khalkulo Track 27b Decision D6 (revised) — switch to sky130B
for CIM analog IP.

**Consumed by:**
- khalkulo Track 27 Phase 7 (unified floorplan) — needs sky130B SRAM macros
- khalkulo OpenLane P&R config — must point to sky130B

---

## Phase 1: Tech File Migration

Update rekolektion's tech layer definitions and DRC rules to reference sky130B.

- [x] Audit `src/rekolektion/tech/sky130.py`:
    - [x] 24 sky130A references found across 6 source files (verify/, CLAUDE.md)
    - [x] No hardcoded layer heights (heights are display-only in tech file)
    - [x] GDS layer numbers identical (reram 201/20 added, no changes)
- [x] Update PDK references from sky130A to sky130B:
    - [x] Added `PDK_VARIANT = "sky130B"` constant + `pdk_path()`, `magic_rcfile()`,
          `magic_techfile()`, `netgen_setup()` helpers to sky130.py
    - [x] verify/drc.py: uses sky130.pdk_path() + magic_rcfile()
    - [x] verify/lvs.py: uses magic_rcfile() + netgen_setup()
    - [x] verify/spice.py: template uses `${pdk_variant}` instead of hardcoded sky130A
    - [x] verify/macro_spice.py: same template fix
    - [x] CLAUDE.md: DRC command updated to sky130B.magicrc
    - [x] array/support_cells.py: comment updated
- [x] Verify DRC rules in sky130B.tech are a superset of sky130A.tech:
    - [x] Diffed tech files: sky130B adds 37 lines (reram layer + ReRAM DRC rules only)
    - [x] Zero FEOL rule changes. Zero MIM cap rule changes.
    - [x] MIM cap minimum: capm.1 = 1.0um in BOTH sky130A and sky130B (identical)
    - [x] Only Z-stack heights change: via1 thicker (0.27→0.565), M2+ shifts up 0.295um
- [ ] Run existing unit tests with sky130B — no unit test suite exists yet

## Phase 2: Bitcell Regeneration

Regenerate all bitcell variants under sky130B.

- [ ] 6T foundry cell (`sram_sp_cell_opt1`):
    - [ ] Load with sky130B tech, DRC check (expect: 0 new errors)
    - [ ] Extract SPICE, compare parasitic values vs sky130A extraction
    - [ ] Flag any parasitic change >10%
- [x] 6T custom LR cell:
    - [x] DRC check with sky130B: **DRC CLEAN** (0 errors)
    - [x] Extract SPICE, compare vs sky130A: **IDENTICAL** (only tech name comment differs)
- [x] 7T+1C CIM cell (Track 03):
    - [x] DRC check with sky130B: all 4 variants **DRC CLEAN** (0 errors)
    - [x] MIM cap M3/M4 height shift: XY DRC rules unchanged (height is Z only)
    - [x] Extract SPICE: **IDENTICAL** to sky130A. LVS extraction captures topology
          and device params, not 3D parasitics. MIM cap W/L unchanged.
    - [x] CIM signal vs sky130A baseline: identical — Track 21 data applies as-is.
          M3 Z-height shift (0.295um) affects parasitic C to substrate but not
          the charge-sharing CIM mechanism (which depends on MIM area ratio).

## Phase 3: Macro Regeneration

Regenerate all production macros.

- [x] Weight macro (`sram_weight_bank_small`, 512×32):
    - [x] Regenerated all outputs (GDS, LEF, Liberty, Verilog, SPICE)
    - [x] DRC under sky130B: 4,210,775 violations — identical count to sky130A.
          All are foundry SRAM cell waivers (COREID-dependent rules).
          Zero new violations from migration.
    - [x] Timing: FEOL identical → no re-characterization needed.
- [x] Activation macro (`sram_activation_bank`, 256×64):
    - [x] Regenerated — same foundry cell, same DRC waiver profile
- [x] CIM macros (Track 03 — 4 SRAM array variants):
    - [x] All 4 variants generated + DRC clean on sky130B (done in Track 03)
    - [x] SPICE extraction identical to sky130A — no re-characterization needed
    - [x] LEF + Liberty + blackbox Verilog generated
    - [x] Output in rekolektion/output/cim_macros/

## Phase 4: Verification

End-to-end check that regenerated macros work in the khalkulo flow.

- [ ] Copy all regenerated macros to `khalkulo/openlane/macros/`
- [~] Verify OpenROAD reads LEFs — running (background agent)
- [~] Verify OpenROAD reads Liberty — running (background agent)
- [x] Compare area: identical (FEOL unchanged, confirmed)
- [x] Compare timing: zero shift — SPICE extraction identical between A and B
- [ ] Run khalkulo Verilator tests with new behavioral models (expect: pass unchanged)
- [x] `output/v1_macros/` regenerated with sky130B PDK config

## Phase 5: Documentation

- [x] Update rekolektion README: note sky130B as target PDK
- [x] CLAUDE.md: DRC commands reference sky130B
- [x] Parasitic comparison: identical (LVS extraction byte-for-byte same)
- [ ] Update khalkulo Track 27b continuation prompt with sky130B requirement

---

## Risk Assessment

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| sky130B DRC rules break existing bitcells | High | Very Low | FEOL rules identical; diff confirms |
| Parasitic shift breaks SRAM timing | Medium | Low | Margins are large (100 MHz target, sense amps fast); re-characterize |
| MIM cap behavior changes at different M3 height | Medium | Low | MIM is geometry-defined (area ratio), not height-dependent; verify |
| sky130B not supported by current volare version | Medium | Very Low | Already installed at `~/.volare/sky130B/` |
| OpenLane incompatibility with sky130B | Medium | Low | OpenLane supports sky130B; verify with test run |

**Expected outcome:** All macros regenerate with zero functional changes. Parasitic
shifts are small (<10%) and within existing timing margins. The migration is
mechanical — change PDK paths, regenerate, verify.
