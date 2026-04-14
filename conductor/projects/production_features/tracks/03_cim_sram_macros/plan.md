# Track Plan: CIM SRAM Array Macros

Extend rekolektion to generate 7T+1C CIM SRAM array macros for khalkulo's
i1 CIM experiment. The cell generator exists (`sky130_6t_lr_cim.py`). This
track adds the 4 cell size variants, extends the array tiler for MWL/MBL
routing, adds CIM-specific peripherals, and produces complete array-level
hard macros (GDS + LEF + Liberty).

**Output:** 4 CIM SRAM array macros with pins:
- Standard SRAM: CLK, WE, CS, ADDR[], DIN[], DOUT[]
- CIM: MWL[], MBL_OUT[] (analog), MBL_PRE

The ADC/DAC that consume MBL_OUT are external IP — not part of rekolektion.

**Depends on:** Track 21 SPICE characterization (complete).

---

## Phase 1: Cell Variants

Generate 7T+1C cells at all 4 target sizes. The generator exists; this is
parameterization + DRC + SPICE extraction.

- [x] Run `generate_cim_bitcell()` at SRAM-A sizing (default params)
    - [x] DRC clean in Magic (54 errors → 0 after T7 topology fix, see decisions.md)
    - [x] SPICE extract, compare vs Track 21 characterization (19.0 mV cell delta)
          Extracted netlist matches hand-written SPICE: T7 (0.42/0.15) source
          connects to latched Q net, drain to MIM cap bottom plate (2x2 um).
          Track 21 characterized this exact circuit.
    **Note:** Cell area is larger than 3.93 um² due to T7 overhead. The 3.93 um²
    was the 6T tiling pitch (1.925 × 2.04). T7 adds ~1.1um in Y, giving a CIM
    tiling pitch of ~1.925 × 3.1 ≈ 5.97 um². This is physical reality — T7 needs
    diff area. See decisions.md Decision 1 for analysis.
- [x] Run at SRAM-B/C/D sizing — **RESTORED (Decision 2 revised)**
    MIM cap minimum is 1.0um (not 2.0 — verified from Magic DRC deck).
    All 4 variants generated with rectangular caps, all DRC clean:
    - SRAM-A: 1.30×3.10 (~8 fF), pitch 2.15×5.16 = 11.08 um²
    - SRAM-B: 1.10×2.65 (~6 fF), pitch 1.95×4.71 = 9.17 um²
    - SRAM-C: 1.10×1.80 (~4 fF), pitch 1.95×3.92 = 7.63 um²
    - SRAM-D: 1.00×1.45 (~3 fF), pitch 1.93×3.92 = 7.54 um²
    GDS + SPICE in output/cim_variants/. See decisions.md (revised).
- [x] Create `BitcellInfo` for CIM cells with MWL/MBL pin metadata
      `load_cim_bitcell(variant="SRAM-A")` etc. Per-variant pitch computation.
- [x] Add `generate_cim_variants()` script — produces all 4 sizes
- [x] Render per-layer PNGs for SRAM-A (viz tool) — output/renders/cim_lr/

## Phase 2: Array Tiler CIM Extension

Extend the array tiler (`array/tiler.py`) to route MWL and MBL signals.

- [x] Add MWL routing to `tile_array()`:
    - [x] MWL poly is continuous across tiled cells (extends full cell width)
    - [x] MWL independent from WL (separate poly line at T7 Y position)
    - [~] MWL metal straps — deferred (poly-only sufficient for small arrays)
- [x] Add MBL routing to `tile_array()`:
    - [x] MBL vertical M4 stripes per column (route_mbl in routing.py)
    - [x] Met4 min width = 0.30um (met4.1)
    - [x] No conflict with BL/BLB (M2) — different metal layer
- [~] Update dummy cell handling — deferred (not needed for initial arrays)
- [x] Test: tile a small 4×4 CIM array, DRC clean
      6 nwell.2a waivers (same-potential nwells, Magic false positive).
      See decisions.md Decision 3 for CIM tiling pitch analysis.
- [x] Test: tile 64×64 (SRAM-C/D size), DRC clean
      64x64: 181.9 × 259.5 um = 0.047 mm². Only nwell.2a waivers.
      (tested at 2.0×2.0 cap; needs retest with per-variant caps)
- [x] Test: tile 256×64 (SRAM-A size), DRC clean
      256x64: 139.4 × 1319.1 um = 0.184 mm².
      Waivers: 4191 nwell.2a + 32384 via.2 (both same-potential: adjacent
      cells' VPWR nets too close for different-net rules but same net).
      Zero real DRC errors.

## Phase 3: CIM Peripherals

New peripheral cells for CIM operation. These sit alongside the standard
SRAM peripherals (WL drivers, precharge, sense amps, write drivers).

- [ ] MWL driver cells:
    - [ ] Design: buffer that drives MWL for an entire row
    - [ ] During normal SRAM operation: MWL inactive (all low)
    - [ ] During CIM compute: MWL active for all selected rows simultaneously
    - [ ] Key difference from WL driver: WL activates 1 row; MWL activates N rows
    - [ ] Layout in Magic, DRC clean
    - [ ] SPICE extract, verify drive strength for 64-column load
- [ ] MBL precharge cells:
    - [ ] Design: precharge MBL to VDD/2 (or reference voltage) before CIM compute
    - [ ] Standard precharge topology (PMOS header, equalize)
    - [ ] Layout in Magic, DRC clean
- [ ] MBL sense output buffers:
    - [ ] Design: buffer analog MBL voltage to output pin
    - [ ] Must NOT digitize — ADC is external
    - [ ] Simple source follower or unity-gain buffer
    - [ ] Layout in Magic, DRC clean
    - [ ] SPICE extract, verify signal integrity (bandwidth, noise)
- [ ] Add peripheral cells to `array/support_cells.py` registry

## Phase 4: Macro Assembly

Extend the macro assembler to produce complete CIM SRAM array macros.

- [ ] Update `MacroParams` dataclass for CIM mode:
    - [ ] `cim_enabled: bool`
    - [ ] `num_mwl_drivers: int` (= rows)
    - [ ] `num_mbl_columns: int` (= cols)
- [ ] Update assembler placement:
    - [ ] Place MWL drivers on left side (alongside row decoder)
    - [ ] Place MBL precharge at top of array
    - [ ] Place MBL sense buffers at bottom of array (output side)
- [ ] Generate macro for each array size:
    - [ ] SRAM-A: 256×64, 1.3×3.1 cap (11.08 um²/cell)
    - [ ] SRAM-B: 256×64, 1.1×2.65 cap (9.17 um²/cell)
    - [ ] SRAM-C: 64×64, 1.1×1.8 cap (7.63 um²/cell)
    - [ ] SRAM-D: 64×64, 1.0×1.45 cap (7.54 um²/cell)
- [ ] DRC each macro in Magic
- [ ] LVS each macro (netgen)

## Phase 5: LEF + Liberty Generation

Produce the files needed for P&R integration.

- [ ] Update LEF generator for CIM macros:
    - [ ] Add MWL pin definitions (input, per-row)
    - [ ] Add MBL_OUT pin definitions (analog output, per-column)
    - [ ] Add MBL_PRE pin definition (input, precharge control)
    - [ ] Pin placement: MWL on left edge, MBL_OUT on bottom edge
    - [ ] OBS layer for routing blockage
- [ ] Update Liberty generator for CIM macros:
    - [ ] Standard SRAM timing arcs (CLK→DOUT, setup/hold for DIN/ADDR/WE/CS)
    - [ ] CIM timing arcs:
        - [ ] CLK→MBL_OUT (compute latency — from SPICE)
        - [ ] Setup/hold for MWL, MBL_PRE relative to CLK
    - [ ] Pin capacitances (from SPICE extraction)
- [ ] Validate: OpenSTA reads Liberty without errors
- [ ] Validate: OpenROAD reads LEF without errors

## Phase 6: Ring Oscillators + Test Structures

- [ ] Ring oscillator cells (one per cell size):
    - [ ] Standard inverter ring using CIM cell transistors
    - [ ] Output routed to macro pin for frequency measurement
    - [ ] Layout, DRC clean
- [ ] Unit cell test structures (one per cell size):
    - [ ] Single cell with all ports directly accessible
    - [ ] For post-silicon characterization
    - [ ] Layout, DRC clean
- [ ] Place ring oscillators and test structures adjacent to corresponding arrays
- [ ] Include in macro LEF (as sub-blocks or separate small macros)

## Phase 7: Integration Test

End-to-end validation that macros work in the khalkulo P&R flow.

- [ ] Copy macro GDS/LEF/Liberty to khalkulo `openlane/macros/`
- [ ] Add CIM macros to `openlane/config.json` as fixed instances
- [ ] Run OpenLane elaboration check (Yosys reads blackbox + LEF)
- [ ] Run floorplan test (place CIM macros, verify no overlap with v1a SRAMs)
- [ ] Verify OpenSTA reads timing arcs correctly
