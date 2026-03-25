# rekolektion

Open-source SRAM generator for the SkyWater SKY130 130nm process.

Produces optimized 6T SRAM macros targeting **50,000–150,000 bits/mm²** — a 10–30x density improvement over OpenRAM's ~6,000 bits/mm² on the same process.

![3D visualization of a 6T SRAM bitcell on SKY130](Sample_3D_Viz.jpg)

## What It Does

**Input**: Target size (words × bits), port width, column mux ratio, SKY130 DRC rules.

**Output**:
- **GDS** — Layout for fabrication
- **SPICE netlist** — For circuit simulation
- **LEF** — Abstract for place-and-route (planned)
- **Liberty .lib** — Timing model for STA (planned)
- **Verilog** — Behavioral model for simulation (planned)
- **SVG** — 2D layout visualization with layer colors
- **GLB** — 3D visualization with per-layer materials (viewable in macOS Quick Look, any glTF viewer)
- **GLB (in-situ)** — 3D cross-section showing the cell embedded in semi-transparent process strata (substrate, oxides, ILD, passivation) with layer labels
- **STL** — Per-layer 3D meshes for Blender import

## Status

**Phase 1** — 6T bitcell design and DRC iteration.

## Quick Start

```bash
pip install -e ".[dev]"

# Generate bitcell GDS + SPICE netlist
rekolektion bitcell -o output/bitcell.gds --spice

# Generate 2D SVG visualization
python -c "
import gdstk
from rekolektion.bitcell.sky130_6t import create_bitcell
cell = create_bitcell()
cell.write_svg('output/bitcell.svg', scaling=800, background='#FFFFFF')
"

# Generate 3D visualizations (STL + colored GLB + in-situ GLB)
python scripts/gds_to_stl.py output/bitcell.gds output/3d/

# View in macOS
open output/3d/bitcell_3d.glb           # colored 3D model
open output/3d/bitcell_3d_in_situ.glb   # 3D cross-section with process strata

# Run DRC (requires Magic + SKY130 PDK)
export PDK_ROOT=$HOME/.volare
bash scripts/run_drc.sh output/bitcell.gds
```

## Architecture

- `src/rekolektion/tech/` — SKY130 design rules and layer definitions
- `src/rekolektion/bitcell/` — 6T SRAM bitcell generator
- `src/rekolektion/array/` — Bitcell array tiler
- `src/rekolektion/peripherals/` — Row decoder, column mux, sense amp, write driver, precharge
- `src/rekolektion/macro/` — Full macro assembly and output generation
- `src/rekolektion/verify/` — DRC (Magic), LVS (netgen), SPICE (ngspice) automation
- `scripts/` — Helper scripts for verification tools

## Prerequisites

- Python ≥ 3.10
- [gdstk](https://github.com/heitzmann/gdstk) for GDS generation
- [Magic](http://opencircuitdesign.com/magic/) for DRC
- [netgen](http://opencircuitdesign.com/netgen/) for LVS
- [ngspice](http://ngspice.sourceforge.net/) for SPICE simulation
- [SkyWater SKY130 PDK](https://github.com/google/skywater-pdk)

## License

Apache 2.0
