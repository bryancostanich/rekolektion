# Foundry Bitcell Attribution

## SkyWater SKY130 SRAM Library

**Source**: [google/skywater-pdk-libs-sky130_fd_bd_sram](https://github.com/google/skywater-pdk-libs-sky130_fd_bd_sram)
**License**: Apache 2.0
**Copyright**: Copyright 2020 SkyWater PDK Authors

Cells from this library:

- `sky130_fd_bd_sram__sram_sp_cell_opt1` — 6T single-port SRAM
  bitcell (1.31 × 1.58 µm = 2.07 µm²). Foundry-shipped, uses
  SRAM-specific transistor models (`special_nfet_latch`,
  `special_pfet_pass` HVT) and asymmetric sizing (cell ratio 2.0).
  Carries the foundry-approved COREID DRC waivers documented in
  `src/rekolektion/verify/drc.py::_KNOWN_WAIVER_RULES`.

Files in this directory:

- `sky130_fd_bd_sram__sram_sp_cell_opt1.gds` — full-layout GDS, the
  source of truth for the foundry-cell macro path.
- `sky130_fd_bd_sram__sram_sp_cell_opt1.magic.lef` — abstract LEF,
  used by `bitcell/foundry_sp.py` to parse pin positions.

Both files copied verbatim from
`cells/sram_sp_cell_opt1/sky130_fd_bd_sram__sram_sp_cell_opt1.{gds,magic.lef}`
in the source repository. License terms in the source repo apply.
