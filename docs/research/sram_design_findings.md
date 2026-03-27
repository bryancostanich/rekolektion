# Track 02 Findings: SRAM Design Research & Phase 1 Results

*Date: 2026-03-25*

## Executive Summary

Phase 1 research yielded two major discoveries that change the project approach:

1. **SkyWater's foundry SRAM cell library is publicly available** — 255 production-quality cells including a 6T single-port bitcell at 2.07 μm² and complete peripheral circuits (decoders, sense amps, write drivers, column/row ends, well straps). This is at `github.com/google/skywater-pdk-libs-sky130_fd_bd_sram`.

2. **DRC-clean bitcell design with standard device rules yields ~7 μm²** — we achieved DRC CLEAN (0 violations) but the cell is 3.5x larger than the foundry cell. The gap is due to foundry-specific SRAM device models and hand-crafted non-rectangular geometry that standard DRC rules don't permit.

**Recommendation:** Use the foundry bitcell for V1 production macros. Continue developing the custom cell as an open-source contribution. Build the generator tool (array, peripherals, macro assembly) to work with either.

---

## Discovery: SkyWater Foundry SRAM Cell Library

**Repo:** `https://github.com/google/skywater-pdk-libs-sky130_fd_bd_sram`

### Library Contents (255 cells)

| Category | Count | Description |
|---|---|---|
| SP bitcell variants | 32 | Single-port 6T cell + dummy, replica, OPC serif, metal option variants |
| DP bitcell variants | 38 | Dual-port 8T cell variants |
| Column end cells | 45 | Array edge termination (column direction) |
| Row end cells | 15 | Array edge termination (row direction) |
| WL strap cells | 10 | Well tap / word-line strapping (VDD + GND) |
| Decoder cells (NAND) | 6 | 2-input, 3-input, 4-input NAND decoders (SP + DP) |
| Write driver | 1 | Write driver circuit |
| Sense amplifier | 1 | Sense amplifier circuit |
| DFF | 1 | D flip-flop for control logic |
| Block / inverter cells | 10 | Block-level inverter variants |

Each cell includes: GDS, MAG (Magic layout), LEF, SPICE netlist, LVS netlist, SVG rendering, pin list.

### Key Bitcell: `sram_sp_cell_opt1`

| Parameter | Value |
|---|---|
| **Size** | 1.31 × 1.58 μm = **2.07 μm²** |
| **Ports** | BL, BR (bit lines), WL, VGND, VPWR, VNB, VPB |
| **Pull-down (PD)** | `special_nfet_latch` W=0.21μm, L=0.15μm |
| **Pass-gate (PG)** | `special_nfet_pass` W=0.14μm, L=0.15μm |
| **Pull-up (PU)** | `special_pfet_pass` W=0.14μm, L=0.15μm |
| **Cell ratio (PD/PG)** | W ratio: 1.5 (0.21/0.14) |
| **Non-rectangular shapes** | 21/105 polygons (20%) — triangular li1 for cross-coupling |
| **Special devices** | Uses SRAM-specific transistor models with relaxed DRC rules |
| **DRC status** | Foundry-verified; blackboxed in OpenRAM (not checked by standard DRC) |

### Key Layout Techniques in the Foundry Cell

1. **Continuous poly gates** spanning NMOS to PMOS — single poly stripe forms both PD and PU gates, with poly contact landing pad in the N-P gap where poly naturally widens
2. **Diagonal 45° li1 routing** — triangular li1 shapes for cross-coupling, connecting each drain to the opposite gate in a single diagonal segment
3. **L-shaped diffusion** — PD region is 0.21μm wide, PG is 0.14μm (dog-bone shape)
4. **Ultra-thin M1 power rails** (0.07μm) at cell edges with M2 horizontal strapping
5. **No per-cell well taps** — separate strap cells inserted between columns
6. **OPC serifs** — dedicated sub-cells for lithographic pattern matching

### Special SRAM Device Models

The foundry cell uses three non-standard transistor models:
- `sky130_fd_pr__special_nfet_pass` — SRAM-optimized NMOS pass gate
- `sky130_fd_pr__special_nfet_latch` — SRAM-optimized NMOS pull-down
- `sky130_fd_pr__special_pfet_pass` — SRAM-optimized PMOS pull-up

These enable: sub-minimum transistor widths (0.14μm vs 0.42μm standard minimum), reduced diff extension past poly (0.045μm vs 0.25μm), tighter contact-to-gate spacing, and reduced N-well proximity rules. The SPICE models are in the standard PDK; the DRC rules are not publicly documented.

---

## Phase 1 Custom Cell Results

### DRC-Clean Cell Achieved

Our custom bitcell generator (`rekolektion`) achieved **DRC CLEAN** (0 violations) on SKY130 using standard device rules.

| Parameter | Our Cell | Foundry Cell | Ratio |
|---|---|---|---|
| **Area** | 7.22 μm² | 2.07 μm² | 3.5x |
| **Width** | 2.32 μm | 1.31 μm | 1.8x |
| **Height** | 3.11 μm | 1.58 μm | 2.0x |
| **PD width** | 0.42 μm | 0.21 μm | 2.0x |
| **PG width** | 0.42 μm | 0.14 μm | 3.0x |
| **PU width** | 0.42 μm | 0.14 μm | 3.0x |
| **Diff extension** | 0.38 μm | ~0.045 μm | 8.4x |
| **Poly gates** | Separate + pads | Continuous | — |
| **Cross-coupling** | Rectangular li1 jog | Diagonal li1 | — |
| **Geometry** | 100% rectangular | 20% non-rectangular | — |

### DRC Iteration History

| Pass | Violation Types | Instances | Key Fixes |
|---|---|---|---|
| Initial | 41 | 800+ | Baseline |
| Topology rewrite | 17 | ~170 | Separate gates, proper diff ext, N-P gap |
| Spacing fixes | 10 | 93 | Pad spacing, tap sizing |
| Grid alignment | 10 | 84 | 5nm grid snap |
| Enclosure fixes | 7 | 58 | licon.8a, PMOS contact spacing |
| Tap removal | 3 | 20 | Taps delegated to array level |
| Rail + pad fixes | 1 | 11 | met1 enclosure, poly spacing |
| Route jog | 0 | 0 | **DRC CLEAN** |

### Why Our Cell Is Larger

The dominant area costs (standard device rules vs foundry special):

| Constraint | Our Cell | Foundry | Impact |
|---|---|---|---|
| Min transistor width | 0.42 μm | 0.14 μm | 3x wider diff strips |
| Diff extension for contacts | 0.38 μm | ~0.045 μm | 8x taller per gate edge |
| Separate poly gates + pads | ~0.50 μm margin | Continuous poly | Extra width for pads |
| Rectangular routing | Li1 jog needed | Diagonal li1 | Extra height for spacing |
| Standard N-well rules | 0.52 μm N-P gap | ~0.19 μm | 2.7x taller transition |

---

## Density Impact on V1 Architecture

### With Foundry Cell (2.07 μm²)

| Peripheral Overhead | Macro Density | 88 KB needs | 10 mm² yields |
|---|---|---|---|
| 30% | 338K bits/mm² | 2.1 mm² | 413 KB |
| 40% | 290K bits/mm² | 2.5 mm² | 354 KB |
| 50% | 242K bits/mm² | 3.0 mm² | 295 KB |

**Key insight:** The 88 KB SRAM target needs only **2.5 mm²** (at 40% overhead), freeing **7.5 mm²** of the original 10 mm² SRAM budget.

### MAC-to-SRAM Scaling Rule

From the architecture spec, the fundamental constraint is **1 activation byte per MAC per cycle**:
- Each activation bank has a 64-bit port → feeds 8 MACs (8 bytes/cycle)
- **Activation banks = MAC count / 8**
- Activation scratch per bank: ~3 KB
- Weight banks: 2 × 32 KB (double-buffered, fixed regardless of MAC count)
- **Total SRAM = 64 KB (weights) + (MAC count / 8) × 3 KB (activations)**

### V1 Design Points with Foundry Cell

| MACs | Act Banks | Weight KB | Act KB | Total KB | SRAM mm² | MAC mm² | Total mm² | Die Used |
|---|---|---|---|---|---|---|---|---|
| 64 | 8 | 64 | 24 | **88** | 2.5 | 0.5 | **5.0** | 33% |
| 128 | 16 | 64 | 48 | **112** | 3.2 | 1.0 | **6.2** | 41% |
| 256 | 32 | 64 | 96 | **160** | 4.5 | 2.1 | **8.6** | 57% |
| 600 | 75 | 64 | 225 | **289** | 8.2 | 4.9 | **15.1** | 100% |

*Assumes 40% peripheral overhead on SRAM, 0.0081 mm² per MAC, 2.0 mm² fixed overhead (pads, I/O, control). Die total: 15.1 mm².*

**The die can support up to 600 MACs** (9.4x the original 64) with proportionally scaled activation banks. The sweet spot for balanced compute/memory is likely **128-256 MACs** (2-4x original), using 41-57% of the die.

### With Custom Cell (7.22 μm²)

| Peripheral Overhead | Macro Density | 88 KB needs | 10 mm² yields |
|---|---|---|---|
| 40% | 83K bits/mm² | 8.6 mm² | 102 KB |

Custom cell density is marginal for V1 at current size. Optimization to ~3.5 μm² (see below) would improve this significantly.

---

## Optimization Roadmap for Custom Cell

Research indicates these techniques could reduce our cell from 7.22 μm² to ~3.0-3.5 μm² while staying DRC-clean on standard devices:

| Technique | Estimated Savings | DRC Risk | Priority |
|---|---|---|---|
| Continuous poly (NMOS→PMOS) | 15-25% of width | Medium | **1 — highest impact** |
| L-shaped diffusion (PD wider, PG narrower) | 5-8% of area | Low | **2** |
| Thin M1 rails + M2 power strapping | 5-10% of height | Low | **3** |
| Notched N-well boundary | 5-10% of height | Low | **4** |
| Diagonal li1 cross-coupling | 5-15% of area | High | **5** |
| Chamfered poly contact pads | 2-5% | Low | **6** |

### Academic References

- TSMC 130nm production SRAM cell: ~2.08 μm² (published literature)
- IBM 130nm SOI SRAM cell: ~1.5 μm² (SOI-specific advantages)
- OpenRAM academic paper (Guthaus et al., UCSC, DAC 2016): reports 3-5x larger than production cells
- Intel US Patent 7,446,352: non-Manhattan routing in memory cells

---

## Revised Strategy

### Two-Track Approach

**Track A: V1 Production (foundry cell)**
- Use `sram_sp_cell_opt1` (2.07 μm²) as the bitcell
- Build array tiler, peripherals, and macro assembly around it
- Leverage the 255-cell foundry library for peripheral circuits
- Target: generate the 10 V1 macros (2× weight + 8× activation)

**Track B: Open-Source Contribution (custom cell)**
- Continue optimizing the rekolektion custom cell
- Apply non-rectangular geometry techniques
- Target 3.0-3.5 μm² with standard device rules, DRC-clean
- Novel contribution: first open-source DRC-clean SRAM generator for SKY130

### Updated Open Questions

- [x] ~~Exact cell sizing~~ → Using foundry cell (2.07 μm²) for V1; custom cell targeting 3.0-3.5 μm²
- [x] ~~Licensing~~ → Apache 2.0 (rekolektion tool); foundry cells under SkyWater's license
- [ ] Column mux ratio for activation macros — still open
- [ ] Sense amp design — foundry library has `openram_sense_amp`, evaluate if suitable
- [ ] Can we use the foundry peripheral cells (decoder, write driver, sense amp) directly?
- [ ] SPICE characterization of foundry cell across PVT corners
- [ ] Are the foundry cell SPICE models available in our PDK install for simulation?
- [ ] Array-level DRC with foundry cells + our generator (well taps, edge cells, etc.)

---

## Tool Status: rekolektion

**Repo:** `github.com/bryancostanich/rekolektion` (Apache 2.0)

### Completed
- [x] SKY130 design rules module
- [x] 6T bitcell generator (DRC CLEAN on standard devices)
- [x] SPICE netlist generator
- [x] DRC automation (Magic batch mode)
- [x] LVS automation (netgen, scripted)
- [x] SPICE testbench generator (SNM, write/hold margin, transient, PVT corners)
- [x] 2D SVG visualization
- [x] 3D GLB visualization (colored layers + in-situ process cross-section)
- [x] STL export for Blender
- [x] Full output pipeline (`scripts/generate_all.sh`)

### Next
- [ ] Integrate foundry SP cell as default bitcell option
- [ ] Array tiler (Phase 2)
- [ ] Peripheral circuit integration (Phase 3)
- [ ] Full macro assembly (Phase 4)
- [ ] Custom cell optimization with non-rectangular geometry
