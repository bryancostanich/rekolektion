# Product Definition: rekolektion

## Vision
An open-source SRAM macro generator for the SkyWater SKY130 130nm process that produces complete, characterized, production-ready SRAM macros from parameterized inputs.

## Users
- **Primary**: The khalkulo design team — generating SRAM macros for the V1 inference accelerator tapeout
- **Secondary**: Open-source silicon designers who need parameterized SRAM macros for SKY130 without commercial licensing fees

## Core Problem
The open-source silicon ecosystem has good bitcells but no easy way to turn them into complete, usable SRAM macros with arbitrary sizes, port configurations, and production features. OpenRAM ships pre-built macros at ~6,000 bits/mm². Commercial alternatives (ChipFoundry) cost $2,500/project and offer only fixed-size macros.

## Product Strategy
- **Parameterized generation**: specify words x bits x mux ratio, get a complete macro (GDS, LEF, .lib, Verilog, SPICE)
- **Two bitcell options**: SkyWater foundry cell (2.07 um², production) and custom LR cell (3.93 um², educational/open)
- **300-426K bits/mm²**: 50-70x improvement over OpenRAM
- **Production features**: close the gap with commercial SRAM compilers (write enables, DFT, power management)
- **Open source**: Apache 2.0, fully transparent, modifiable
