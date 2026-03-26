# rekolektion Reference

## Overview

`rekolektion` is an open-source SRAM macro generator targeting the SkyWater SKY130 130nm process. It bridges the gap between foundry-provided bitcells and complete, usable SRAM macros by enabling parameterized generation of SRAM arrays with specified dimensions, port widths, and column mux ratios. The tool supports both SkyWater's production 6T bitcell (2.07 μm²) and a custom DRC-clean 6T cell built from standard devices (7.22 μm²), offering flexibility for production use or educational exploration.

The tool generates multiple output formats including GDS layout, SPICE netlists, LEF abstracts, Liberty timing models, Verilog behavioral models, SVG visualizations, and 3D meshes (STL, GLB) for both 2D and 3D visualization. It is designed to achieve high macro density (~290,000 bits/mm²) compared to existing open-source solutions.

## Architecture

The tool is organized into several key modules:

- `src/rekolektion/bitcell/` — Handles bitcell abstraction, foundry cell loading, and custom cell generation
- `src/rekolektion/array/` — Implements array tiling with mirroring, support cell integration, and wiring
- `src/rekolektion/tech/` — Contains SKY130 design rules and layer definitions
- `src/rekolektion/peripherals/` — Planned module for row decoder, column mux, sense amp, and write driver
- `src/rekolektion/macro/` — Planned module for full macro assembly and output generation
- `src/rekolektion/verify/` — Handles DRC (Magic), LVS (netgen), and SPICE (ngspice) verification

The CLI entry point `rekolektion` routes commands to appropriate submodules, with core functionality built using `gdstk` for GDS generation and leveraging the SkyWater SKY130 PDK.

## Key Data Structures

### Bitcell Configuration
```python
class BitcellConfig:
    name: str
    width: float
    height: float
    area: float
    transistors: list[Transistor]
    layers: dict[str, Layer]
```

### Array Parameters
```python
class ArrayParams:
    rows: int
    cols: int
    port_width: int
    column_mux_ratio: int
    cell_config: BitcellConfig
```

### GDS Cell Structure
```python
class GDSCell:
    name: str
    polygons: list[Polygon]
    instances: list[Instance]
    references: list[Reference]
```

### Layer Definition
```python
class Layer:
    name: str
    datatype: int
    color: str
    drc_rules: dict[str, float]
```

### Output Format Enum
```python
class OutputFormat(Enum):
    GDS = "gds"
    SPICE = "spice"
    LEF = "lef"
    LIB = "lib"
    VERILOG = "verilog"
    SVG = "svg"
    STL = "stl"
    GLB = "glb"
    GLB_IN_SITU = "glb_in_situ"
```

### Cell Instance
```python
class Instance:
    cell_name: str
    origin: tuple[float, float]
    transformation: tuple[float, float, float, float, float, float]
    properties: dict[str, str]
```

## APIs & Protocols

### CLI Interface
```
rekolektion array --cell foundry --rows 8 --cols 32 -o output/array.gds
rekolektion bitcell -o output/bitcell.gds --spice
```

### GDS Output Format
GDS files use the standard GDSII format with:
- Magic number: `0x0000`
- Header: `0x0000` (version), `0x0000` (reserved)
- Cell name: 32-character string
- Polygon data: Each polygon defined by (x, y) coordinates
- Layer/datatype: 16-bit layer number, 16-bit datatype
- Cell references: 32-character cell name, transformation matrix

### SPICE Netlist Format
Generated SPICE netlists follow standard conventions:
- `.SUBCKT` for bitcell instances
- `.MODEL` definitions for transistors
- `.PARAM` for design parameters
- `.END` for end of subcircuit

### Layer Mapping
The tool maps design layers to standard SKY130 PDK layers:
- `metal1` → Layer 44/0
- `metal2` → Layer 45/0
- `metal3` → Layer 46/0
- `poly` → Layer 53/0
- `diffusion` → Layer 54/0
- `li1` → Layer 55/0
- `metal4` → Layer 47/0

## Conventions

### Naming Patterns
- Bitcell names: `sram_sp_cell_opt1` (foundry), `custom_6t` (custom)
- Array names: `sram_array_{rows}x{cols}_{port_width}w_{mux_ratio}m`
- Output files: `{name}.{format}` (e.g., `array.gds`, `bitcell.spice`)
- Layer names: `metal1`, `metal2`, etc., following SKY130 PDK conventions

### Constants
- Bitcell area (foundry): 2.07 μm²
- Bitcell area (custom): 7.22 μm²
- Cell dimensions (foundry): 1.31 × 1.58 μm
- Cell dimensions (custom): 2.32 × 3.11 μm
- Default column mux ratio: 1
- Default port width: 1
- Magic DRC rule file: `sky130A.drc`

### Gotchas
- All GDS generation uses `gdstk` with 1nm unit scaling
- Foundry cell must be placed in `src/rekolektion/bitcell/foundry_cell.gds`
- Custom cell generation requires proper device sizing parameters
- DRC verification requires Magic and SKY130 PDK installed via volare
- Output file paths are relative to working directory unless absolute paths provided

## File Layout

```
src/rekolektion/
├── __init__.py
├── cli.py
├── bitcell/
│   ├── __init__.py
│   ├── bitcell.py
│   ├── foundry_loader.py
│   └── custom_generator.py
├── array/
│   ├── __init__.py
│   ├── tiler.py
│   ├── wiring.py
│   └── support_cells.py
├── tech/
│   ├── __init__.py
│   ├── layers.py
│   └── rules.py
├── peripherals/
│   ├── __init__.py
│   ├── decoder.py
│   ├── mux.py
│   └── sense_amp.py
├── macro/
│   ├── __init__.py
│   ├── assembler.py
│   └── output.py
└── verify/
    ├── __init__.py
    ├── drc.py
    ├── lvs.py
    └── spice.py

scripts/
├── generate_all.sh
├── run_drc.sh
├── gds_to_stl.py
└── gds_to_svg.py

tests/
├── test_bitcell.py
├── test_array.py
└── test_tech.py
```