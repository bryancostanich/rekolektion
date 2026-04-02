# Project: Production Features

## Objective
Add production-grade features to rekolektion's macro generator, closing the gap with commercial SRAM compilers (specifically ChipFoundry's CF_SRAM_1024x32). These features are implemented as composable generator options — off by default, enabled per macro via flags.

## Competitive Context
ChipFoundry's commercial SRAM for SKY130:
- Single hard macro (1024x32, 0.118 mm²), tiled for larger configs
- ~278K bits/mm² bare macro density
- 2.44 ns CLK-to-Q at 100 MHz
- Full production features: write enables, scan chain DFT, power gating, clock gating, body bias, burn-in
- $2,500/project, closed source, no parameterization

rekolektion already wins on density (300-426K), parameterization, and openness. This project adds the production features.

## Reference
- GitHub issue: bryancostanich/rekolektion#1
- ChipFoundry specs: https://chipfoundry.io/commercial-sram-macro

## Tracks
See tracks.md for the full track registry.
