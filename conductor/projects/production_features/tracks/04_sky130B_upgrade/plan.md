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

- [ ] Audit `src/rekolektion/tech/sky130.py`:
    - [ ] Identify all PDK path references (model files, tech files, cell libraries)
    - [ ] Identify any hardcoded layer heights or parasitic values
    - [ ] Check if GDS layer numbers change (expect: no change except reram 201/20 added)
- [ ] Update PDK references from sky130A to sky130B:
    - [ ] Magic tech file: `sky130A.tech` → `sky130B.tech`
    - [ ] Magic rcfile: `sky130A.magicrc` → `sky130B.magicrc`
    - [ ] Magic TCL procs: `sky130A.tcl` → `sky130B.tcl`
    - [ ] Cell library paths: `libs.ref/*/sky130_fd_pr` (same in both, verify)
    - [ ] Standard cell paths: `libs.ref/*/sky130_fd_sc_hd` (same in both, verify)
- [ ] Verify DRC rules in sky130B.tech are a superset of sky130A.tech:
    - [ ] Diff the two tech files (expect: identical FEOL rules, added reram rules)
    - [ ] Confirm no existing DRC rules tightened or changed
    - [ ] Check MIM cap minimum dimensions (capm.1/capm.2): if sky130B allows
      smaller MIM caps than sky130A's 2.0×2.0um minimum, Track 03 Decision 2
      reopens — could enable smaller caps for SRAM-C/D variants and tighter
      tiling pitch (Decision 3)
- [ ] Run existing unit tests with sky130B — expect all pass unchanged

## Phase 2: Bitcell Regeneration

Regenerate all bitcell variants under sky130B.

- [ ] 6T foundry cell (`sram_sp_cell_opt1`):
    - [ ] Load with sky130B tech, DRC check (expect: 0 new errors)
    - [ ] Extract SPICE, compare parasitic values vs sky130A extraction
    - [ ] Flag any parasitic change >10%
- [ ] 6T custom LR cell:
    - [ ] Regenerate with sky130B tech, DRC check
    - [ ] Extract SPICE, compare vs sky130A
- [ ] 7T+1C CIM cell (Track 03):
    - [ ] Regenerate with sky130B tech, DRC check
    - [ ] MIM cap sits on M3/M4 — verify M3/M4 height shift doesn't affect
      MIM enclosure rules
    - [ ] Extract SPICE — MIM parasitic capacitance to substrate changes
      because M3 is 0.295 um higher. Quantify the shift.
    - [ ] Compare CIM signal (MBL_OUT voltage delta per cell) vs sky130A baseline

## Phase 3: Macro Regeneration

Regenerate all production macros.

- [ ] Weight macro (`sram_weight_bank_small`, 512×32):
    - [ ] Regenerate GDS with sky130B
    - [ ] DRC clean
    - [ ] Regenerate LEF (pin shapes unchanged, cell dimensions unchanged)
    - [ ] Regenerate Liberty — re-characterize timing:
        - [ ] CLK-to-Q delay (parasitic change may affect sense amp speed)
        - [ ] Setup/hold times
        - [ ] If timing changes >5%, update .lib values
    - [ ] Regenerate behavioral Verilog (unchanged)
    - [ ] Regenerate blackbox Verilog (unchanged)
    - [ ] Regenerate SPICE netlist
- [ ] Activation macro (`sram_activation_bank`, 256×64):
    - [ ] Same steps as weight macro
- [ ] CIM macros (Track 03 — 4 SRAM array variants):
    - [ ] Regenerate all 4 size variants with sky130B
    - [ ] DRC clean each
    - [ ] Re-characterize CIM-specific timing (MBL_OUT settling, precharge time)
    - [ ] Compare vs sky130A CIM SPICE results (Track 21 baseline)

## Phase 4: Verification

End-to-end check that regenerated macros work in the khalkulo flow.

- [ ] Copy all regenerated macros to `khalkulo/openlane/macros/`
- [ ] Verify OpenLane reads new LEFs without error
- [ ] Verify OpenSTA reads new Liberty files without error
- [ ] Compare area: should be identical (FEOL unchanged)
- [ ] Compare timing: document any shifts from sky130A baseline
- [ ] Run khalkulo Verilator tests with new behavioral models (expect: pass unchanged)
- [ ] Update `output/v1_macros/` with sky130B versions

## Phase 5: Documentation

- [ ] Update rekolektion README: note sky130B as target PDK
- [ ] Update any PDK setup instructions
- [ ] Document parasitic comparison (sky130A vs sky130B) for future reference
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
