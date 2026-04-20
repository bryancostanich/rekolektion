# rekolektion

Open-source SRAM macro generator for the SkyWater SKY130 130nm process (sky130B).

Takes bitcells (foundry-provided or custom) and generates complete, characterized SRAM macros — parameterized by size, word width, and column mux ratio.

Also makes neat 3D models, so you can explore SRAM design topologies visually:

![3D visualization of a 6T SRAM bitcell on SKY130](Sample_3D_Viz.jpg)

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

**Production ready for V1 tapeout.** `256 KB` on-die SRAM across 160 macros (32 weight + 128 activation) — with an experimental `200 MHz` turbo mode targeting `204.8 GOPS`.

- **All SRAM passes `100 MHz` timing** — weight macros at `3.2 ns` CLK-to-Q (`+5.9 ns` margin), activation at `2.85 ns` (`+6.3 ns` margin)
- **`200 MHz` turbo mode** — activation banks split 2× (96 rows) for `1.33 ns` margin at `200 MHz`. Safe fallback to `100 MHz`.
- DRC-clean peripherals — foundry cells for foundry bitcell, custom generators for LR cell
- OpenLane integration validated (synthesis → floorplan → placement → CTS → routing, 0 violations)
- SPICE characterized across 9 PVT corners (both cells)

## Production Features

Six composable features close the gap with commercial SRAM compilers. Each is a CLI flag — off by default, any combination valid.

```bash
rekolektion macro --words 1024 --bits 32 --mux 8 \
  --write-enable --scan-chain --clock-gating \
  --power-gating --wl-switchoff --burn-in \
  -o output/production_sram.gds
```

| Feature | Flag | Pin(s) | What It Does |
|---------|------|--------|--------------|
| **Byte-level write enables** | `--write-enable` | `ben[N-1:0]` | AND gates on write drivers, per-byte masking. Eliminates read-modify-write. |
| **Scan chain DFT** | `--scan-chain` | `scan_in`, `scan_out`, `scan_en` | Shift register on all input flops. Validated with Fault DFT toolchain. |
| **Clock gating** | `--clock-gating` | `cen` | Latch-based ICG cell. **99.8% dynamic power reduction** when idle. |
| **Power gating** | `--power-gating` | `sleep` | PMOS header switches on VDD rail. **99.99% leakage reduction**. |
| **Wordline switchoff** | `--wl-switchoff` | `wl_off` | AND gate on each decoder output. Data retained with zero dynamic power. |
| **Burn-in test mode** | `--burn-in` | `tm` | 2:1 mux at WL driver. All wordlines active for stress testing. |

SPICE-characterized on SKY130 (TT, 1.8V, 27C). RTL follows OpenRAM 3-block pattern (posedge capture / negedge read). Passes `verilator --lint-only -Wall`. DFT integration verified with [Fault](https://github.com/AUCOHL/Fault).

See [`docs/power_gating_integration.md`](docs/power_gating_integration.md) for chip-level integration guide.

## CIM (Compute-in-Memory) Macros

rekolektion also generates **7T+1C CIM SRAM arrays** for in-memory dot-product computation using capacitive coupling (C3SRAM-style). Each cell adds a pass transistor (T7) and a MIM coupling capacitor to the 6T LR core. The stored weight couples charge onto a shared multiply bitline (MBL) — no ADC needed inside the array.

Four variants with different MIM cap sizes for a silicon sensitivity experiment:

| Variant | MIM Cap | ~fF | Macro (256×64 or 64×64) | CIM Delta (TT/1.2V) |
|---------|---------|-----|------------------------|---------------------|
| SRAM-A | 1.30 × 3.10 um | 8 | 143 × 1323 um | 19.0 mV |
| SRAM-B | 1.10 × 2.65 um | 6 | 129 × 1208 um | ~14 mV |
| SRAM-C | 1.10 × 1.80 um | 4 | 129 × 255 um | ~10 mV |
| SRAM-D | 1.00 × 1.45 um | 3 | 128 × 255 um | ~8 mV |

```bash
# Generate all 4 CIM cell variants
python -c "from rekolektion.bitcell.sky130_6t_lr_cim import generate_cim_variants; generate_cim_variants()"

# Assemble all 4 CIM macros (GDS + LEF + Liberty + blackbox Verilog)
python -c "from rekolektion.macro.cim_assembler import generate_all_cim_macros; generate_all_cim_macros()"
```

CIM macros include MWL drivers, MBL precharge, and analog sense buffers. MBL_OUT carries an analog voltage — the ADC is external. Operates at 1.2V for best CIM signal margin.

See [`docs/cim_integration.md`](docs/cim_integration.md) for pin descriptions, timing, and integration guide.

## Quick Start

```bash
pip install -e ".[dev]"

# Generate a complete SRAM macro (64 words × 8 bits, 2:1 column mux)
rekolektion macro --words 64 --bits 8 --mux 2 -o output/my_sram.gds

# With production features
rekolektion macro --words 256 --bits 32 --mux 4 --write-enable --clock-gating -o output/sram_prod.gds

# Extract transistor-level SPICE netlist from GDS via Magic
rekolektion macro --words 64 --bits 8 --mux 2 --extracted-spice -o output/my_sram.gds

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

## DRC — important caveats

Two things to remember before trusting a DRC pass on this repo:

**1. Use the fixed wrapper, not `drc count`.** Magic's `drc count` only reports errors in the currently loaded cell's *own* geometry. Any top cell composed purely of references (bitcell arrays, integration stacks, macro tops) has zero direct tiles and looks spuriously clean — while child cells can hold thousands of errors. `src/rekolektion/verify/drc.py` counts via `drc listall why`, which walks the full hierarchy. Don't regress this.

**2. Foundry SRAM cells carry known COREID waivers.** `sky130_fd_bd_sram__sram_sp_cell_opt1` (our default bitcell) reports ~283 DRC errors standalone under stock sky130 rules — all from the COREID-waivered SRAM ruleset (tighter `li`, narrow transistor, relaxed nwell spacing). These are foundry-approved in silicon. A 512×32 weight macro logs ~4.2M such tiles; every variant of this cell trips the same rules. `DRCResult` must separate `real_error_count` from `waiver_error_count` — `clean` means zero *real* errors, not zero total. The authoritative list is maintained in `src/rekolektion/verify/drc.py::_KNOWN_WAIVER_RULES` and covers: local interconnect (`li.1`, `li.3`, `li.c1`, `li.6`), diffusion/transistor (`diff/tap.1/2/3/8/9`), wells (`nwell.1/2a/7`, `dnwell.2/3`), poly (`poly.2/4/5/7/8`), non-Manhattan li (`x.2`), contacts (`psd.5a/5b`, `nsd.10b`, `licon.5b/8a/9/14`, `hvtp.4`), metal widths/spacings (`met1.1/2/6`, `met2.1/2/6`, `mcon.2`), plus the rule-less `"Can't overlap those layers"` message. **Known limitation:** this is a global filter, not spatial. A `met1.2` from a bug in our own routing is currently waived too. Fixing this requires tagging bitcell footprints and only waiving inside them. Tracked as future work.

## Documentation

- [`docs/power_gating_integration.md`](docs/power_gating_integration.md) — Chip-level power gating integration guide (sequencing, isolation, SKY130 cells)
- [`docs/spice_characterization_report.md`](docs/spice_characterization_report.md) — Full SPICE results and analysis
- [`docs/spice_results/`](docs/spice_results/) — Raw simulation data (write margin, SNM CSVs + SPICE netlists)
- [`docs/models/`](docs/models/) — 3D bitcell models (GLB, STL) for both cells
- [`docs/research/`](docs/research/) — Design research (foundry cell analysis, node scaling, cell comparison)

## Architecture

```
src/rekolektion/
├── bitcell/       Bitcell abstraction, foundry cell loader, custom LR generator
├── array/         Array tiler with X/Y mirroring, support cells, WL/BL routing
├── peripherals/   Column mux, precharge, sense amp, write driver, decoder,
│                  write enable gates, power switch, WL gate, WL mux
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

## Attribution

Foundry peripheral cells (sense amp, write driver, decoder gates, DFF) are from the [SkyWater SKY130 SRAM library](https://github.com/google/skywater-pdk-libs-sky130_fd_bd_sram) (Apache 2.0, Copyright 2020 SkyWater PDK Authors). Precharge and column mux cells are from [OpenRAM](https://github.com/VLSIDA/OpenRAM) (BSD 3-Clause, Copyright 2016-2024 Regents of the University of California). See [`src/rekolektion/peripherals/cells/ATTRIBUTION.md`](src/rekolektion/peripherals/cells/ATTRIBUTION.md) for details.

## License

Apache 2.0
