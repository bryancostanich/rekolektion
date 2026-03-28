# rekolektion

Open-source SRAM macro generator for the SkyWater SKY130 130nm process.

Takes bitcells (foundry-provided or custom) and generates complete, characterized SRAM macros — parameterized by size, word width, and column mux ratio.

Also makes neat 3D models, so you can explore SRAM design topologies visually:

![3D visualization of a 6T SRAM bitcell on SKY130](Sample_3D_Viz.jpg)

This project feeds into the [Khalkulo](https://github.com/bryancostanich/khalkulo) chip design, which includes an [animated dataflow visualization](https://github.com/bryancostanich/khalkulo/blob/main/docs/viz/chip_dataflow_animated.svg) of the inference accelerator architecture.

## Why This Exists

The open-source silicon ecosystem has good bitcells (SkyWater's foundry-designed 6T cell at `2.07 μm²`) but no easy way to turn them into complete, usable SRAM macros with the specific sizes and port configurations a chip design needs. OpenRAM exists but ships pre-built macros using 8T dual-port cells at `~12 μm²`, achieving only `~6,000 bits/mm²`.

rekolektion bridges that gap:
- **Uses SkyWater's production 6T cell** (`2.07 μm²`, foundry-verified) as the default bitcell
- **`300-426K bits/mm²`** macro density — 50-70x improvement over OpenRAM
- **Generates complete macros** with array tiling, peripheral circuits, and all output artifacts (GDS, LEF, .lib, Verilog, SPICE)
- **Parameterized** — specify words × bits, column mux ratio, and get a macro
- **OpenLane validated** — synthesis through routing with 0 violations
- **SPICE characterized** — both cells verified across 9 PVT corners

## Two Bitcell Options

### Foundry cell (default, recommended)
SkyWater's `sram_sp_cell_opt1` — `1.31 × 1.58 μm = 2.07 μm²`. Uses SRAM-specific transistor models (`special_nfet_latch`, `special_pfet_pass` HVT), asymmetric sizing (cell ratio `2.0`), and SRAM core DRC rules. Matching foundry peripheral cells (precharge, column mux) from the OpenRAM library.

### Custom LR cell (educational / fully open)
Our DRC-clean 6T cell built entirely from standard `nfet_01v8`/`pfet_01v8` devices — `2.035 × 2.330 μm = 4.74 μm²` standalone, **`3.93 μm²` array-effective** with shared-boundary tiling. Uses a left/right NMOS/PMOS topology inspired by the foundry cell.
- **Matches foundry cell efficiency at standard rules** — dimensional analysis confirmed that our `3.93 μm²` array-effective area is identical to what the foundry cell would be if built with standard (non-SRAM) DRC rules. The entire `1.9×` gap between our cell and the foundry cell's `2.07 μm²` is explained by SRAM-specific design rules (relaxed `li1` spacing, diagonal cross-coupling, `L=0.025` specialty devices) — not layout inefficiency.
- **Zero DRC violations** on standard SKY130 rules (no blackbox, no waivers)
- **Fully transparent** — every polygon generated from documented design rules
- **Modifiable** — change transistor sizing, experiment with layout techniques
- **Matched peripherals** — custom DRC-clean column mux and precharge generators at `1.9 μm` pitch

## SPICE Characterization

Both cells verified across 9 PVT corners (TT/SS/FF × `1.62V`/`1.80V`/`1.98V` at `27°C`). All corners pass.

### Write Margin Comparison

| Corner | VDD | LR Custom | Foundry |
|--------|-----|-----------|---------|
| TT | 1.62V | 1.040V (64%) | 0.320V (20%) |
| TT | 1.80V | 1.110V (62%) | 1.010V (56%) |
| TT | 1.98V | 0.590V (30%) | 1.080V (55%) |
| SS | 1.62V | 1.070V (66%) | 0.230V (14%) |
| SS | 1.80V | 1.160V (64%) | 0.210V (12%) |
| SS | 1.98V | 1.230V (62%) | 1.140V (58%) |
| FF | 1.62V | 0.980V (60%) | 0.590V (36%) |
| FF | 1.80V | 0.410V (23%) | 0.940V (52%) |
| FF | 1.98V | 0.430V (22%) | 1.020V (52%) |

The LR cell is strongest where the foundry cell is weakest (SS/low-voltage), and vice versa. The LR cell's symmetric `W=0.42` transistors make it easy to write at slow corners but harder when PMOS gets strong (FF). The foundry cell's asymmetric sizing + HVT PMOS gives it stability at FF but squeezes at SS/low-voltage.

Full report and raw data: [`docs/spice_characterization_report.md`](docs/spice_characterization_report.md) | [`docs/spice_results/`](docs/spice_results/)

## What It Generates

**Input**: Bitcell choice (`foundry` or `lr`), target size (words × bits), column mux ratio (1, 2, 4, or 8).

**Output** (6 files per macro):
- **GDS** — Flattened layout for fabrication
- **LEF** — Abstract for place-and-route (OpenLane compatible)
- **Liberty .lib** — Timing model with analytical CLK-to-Q, setup, hold
- **Verilog** — Behavioral model + blackbox stub for synthesis
- **SPICE** — Subcircuit netlist

Plus visualization tools for 2D (SVG, per-layer PNG) and 3D (GLB, STL).

## Status

**Production ready for V1 tapeout.** `256 KB` on-die SRAM across 160 macros (32 weight + 128 activation), feeding 1024 MACs at `100 MHz` for `102.4 GOPS` — with an experimental `200 MHz` turbo mode targeting `204.8 GOPS`.

- **1024 INT8 MACs** (16 groups × 64) at `100 MHz` = `102.4 GOPS`
- **`256 KB` on-die SRAM** in `~5.1 mm²` — 160 macros (32 × `512×32` weight + 128 × `192×64` activation)
- **All SRAM passes `100 MHz` timing** — weight macros at `3.2 ns` CLK-to-Q (`+5.9 ns` margin), activation at `2.85 ns` (`+6.3 ns` margin)
- **`200 MHz` turbo mode** — activation banks split 2× (96 rows) for `1.33 ns` margin at `200 MHz`. Safe fallback to `100 MHz`.
- DRC-clean peripherals — foundry cells for foundry bitcell, custom generators for LR cell
- OpenLane integration validated (synthesis → floorplan → placement → CTS → routing, 0 violations)
- SPICE characterized across 9 PVT corners (both cells)

## Quick Start

```bash
pip install -e ".[dev]"

# Generate a complete SRAM macro (64 words × 8 bits, 2:1 column mux)
rekolektion macro --words 64 --bits 8 --mux 2 -o output/my_sram.gds

# Use the custom LR bitcell instead of foundry cell
rekolektion macro --words 64 --bits 8 --mux 2 --cell lr -o output/my_lr_sram.gds

# Generate a tiled array
rekolektion array --cell foundry --rows 8 --cols 32 -o output/array.gds
rekolektion array --cell lr --rows 4 --cols 4 -o output/lr_array.gds

# Generate 3D visualizations (GLB + STL)
python scripts/gds_to_stl.py output/my_sram.gds output/3d/

# Regenerate all 66 V1 production macros
python scripts/generate_v1_production.py

# Run DRC (requires Magic + SKY130 PDK)
export PDK_ROOT=$HOME/.volare
bash scripts/run_drc.sh output/my_sram.gds
```

## Documentation

- [`docs/spice_characterization_report.md`](docs/spice_characterization_report.md) — Full SPICE results and analysis
- [`docs/spice_results/`](docs/spice_results/) — Raw simulation data (write margin, SNM CSVs + SPICE netlists)
- [`docs/models/`](docs/models/) — 3D bitcell models (GLB, STL) for both cells
- [`docs/research/`](docs/research/) — Design research (foundry cell analysis, node scaling, cell comparison)

## Architecture

```
src/rekolektion/
├── bitcell/       Bitcell abstraction, foundry cell loader, custom LR generator
├── array/         Array tiler with X/Y mirroring, support cells, WL/BL routing
├── peripherals/   Column mux, precharge, sense amp, write driver, decoder
├── macro/         Macro assembler, LEF/Liberty/Verilog/SPICE generators
├── verify/        DRC (Magic), LVS (netgen), SPICE (ngspice) automation
└── tech/          SKY130 design rules and layer definitions

scripts/           Production macro generation, 3D visualization, verification
```

## Prerequisites

- Python >= 3.10
- [gdstk](https://github.com/heitzmann/gdstk) for GDS generation
- [Magic](http://opencircuitdesign.com/magic/) for DRC
- [ngspice](http://ngspice.sourceforge.net/) for SPICE simulation
- [SkyWater SKY130 PDK](https://github.com/google/skywater-pdk) (install via [volare](https://github.com/efabless/volare))
- Optional: [netgen](http://opencircuitdesign.com/netgen/) for LVS, [OpenLane](https://github.com/The-OpenROAD-Project/OpenLane) for P&R

## License

Apache 2.0
