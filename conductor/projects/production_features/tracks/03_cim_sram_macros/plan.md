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
- [~] Run at SRAM-B/C/D sizing — **DEFERRED: see decisions.md Decision 2**
    SKY130 MIM cap minimum is 2.0x2.0 um (capm.1/capm.2). All four cell
    variants use the same MIM cap and transistor sizing. The 3.0/2.5/2.07
    um² targets require 6T routing density optimization (tighter M1/M2/li1
    spacing) which is base 6T generator work, not CIM-specific. The CIM
    additions (T7 + MIM + routing) are identical across all four sizes.
    CIM performance: 19.0 mV delta (TT/1.2V/27C) for all sizes.
- [x] Create `BitcellInfo` for CIM cells with MWL/MBL pin metadata
      `load_cim_bitcell()` in sky130_6t_lr_cim.py. Returns BitcellInfo with
      7 pins (BL, BLB, WL, VGND, VPWR, MWL, MBL). Tiling pitch: 1.925 × 3.635.
- [~] Add `generate_cim_variants()` script — deferred (one variant, see Decision 2)
- [x] Render per-layer PNGs for SRAM-A (viz tool) — output/renders/cim_lr/

## Phase 2: Array Tiler CIM Extension

Extend the array tiler (`array/tiler.py`) to route MWL and MBL signals.

- [ ] Add MWL routing to `tile_array()`:
    - [ ] MWL is horizontal poly, one per row — same pattern as WL
    - [ ] MWL must be independent from WL (separate driver connection)
    - [ ] Route MWL straps at same intervals as WL straps
- [ ] Add MBL routing to `tile_array()`:
    - [ ] MBL is vertical on M4, one per column
    - [ ] No conflict with BL/BLB (M2) — different metal layer
    - [ ] Route MBL to array edge for external connection
- [ ] Update dummy cell handling:
    - [ ] Dummy cells need MWL/MBL stubs (floating OK, but must be DRC clean)
- [ ] Test: tile a small 4×4 CIM array, DRC clean
- [ ] Test: tile 256×64 (SRAM-A size), DRC clean
- [ ] Test: tile 64×64 (SRAM-C/D size), DRC clean

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
    - [ ] SRAM-A: 256×64, 3.93 um² cell
    - [ ] SRAM-B: 256×64, 3.0 um² cell
    - [ ] SRAM-C: 64×64, 2.5 um² cell
    - [ ] SRAM-D: 64×64, 2.07 um² cell
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
