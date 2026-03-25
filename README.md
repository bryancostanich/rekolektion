# rekolektion

Open-source SRAM generator for the SkyWater SKY130 130nm process.

Produces optimized 6T SRAM macros targeting **50,000–150,000 bits/mm²** — a 10–30x density improvement over OpenRAM's ~6,000 bits/mm² on the same process.

## What It Does

**Input**: Target size (words × bits), port width, column mux ratio, SKY130 DRC rules.

**Output**: DRC/LVS-clean GDS, LEF, Liberty `.lib` timing model, Verilog behavioral model.

## Status

**Phase 1** — 6T bitcell design and characterization.

## Quick Start

```bash
pip install -e ".[dev]"
rekolektion bitcell -o my_cell.gds --spice
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
