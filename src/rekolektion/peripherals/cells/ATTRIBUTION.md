# Cell Attribution

The foundry peripheral cells in this directory are derived from two open-source projects:

## SkyWater SKY130 SRAM Library

**Source**: [google/skywater-pdk-libs-sky130_fd_bd_sram](https://github.com/google/skywater-pdk-libs-sky130_fd_bd_sram)
**License**: Apache 2.0
**Copyright**: Copyright 2020 SkyWater PDK Authors

Cells from this library:
- `sky130_fd_bd_sram__openram_sense_amp` — Sense amplifier
- `sky130_fd_bd_sram__openram_write_driver` — Write driver
- `sky130_fd_bd_sram__openram_sp_nand2_dec` — 2-input NAND decoder gate
- `sky130_fd_bd_sram__openram_sp_nand3_dec` — 3-input NAND decoder gate
- `sky130_fd_bd_sram__openram_sp_nand4_dec` — 4-input NAND decoder gate
- `sky130_fd_bd_sram__openram_dff` — D flip-flop

GDS and LEF files were extracted from the SkyWater SRAM build space at
`cells/<cell_name>/` in the source repository.

## OpenRAM SRAM Compiler

**Source**: [VLSIDA/OpenRAM](https://github.com/VLSIDA/OpenRAM) (`technology/sky130/gds_lib/`)
**License**: BSD 3-Clause
**Copyright**: Copyright (c) 2016-2024 Regents of the University of California and VLSIDA Group

Cells from this library:
- `precharge_0` — Bitline precharge circuit
- `single_level_column_mux` — Column multiplexer

GDS files were extracted from the OpenRAM SKY130 technology directory.
