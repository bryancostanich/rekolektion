# Track: Community Outreach & Publicizing

## Objective

Publicize rekolektion and the hidden SkyWater foundry SRAM cell library to the open-source silicon community.

## Why This Track Exists

The SkyWater foundry SRAM cell library (`google/skywater-pdk-libs-sky130_fd_bd_sram`) is effectively undiscoverable:
- Not linked from the main SKY130 PDK documentation
- Not included in the standard PDK install via volare
- Not mentioned in OpenRAM docs or tutorials
- No blog posts, talks, or articles about it

Most SKY130 users don't know these cells exist. They're either using OpenRAM's output (~6,000 bits/mm², roughly 1% of what the process supports) or building register files from flip-flops (10-50x less dense). Meanwhile, this library contains 255 production-quality cells including a 2.07 um^2 bitcell and complete peripheral circuits that can deliver 400K+ bits/mm^2.

rekolektion makes these cells usable — it generates complete, characterized SRAM macros (GDS, LEF, Liberty, Verilog, SPICE) from parameterized inputs. This is genuinely useful information that could help anyone taping out on SKY130.

## Scope

1. **Blog post / technical writeup** covering the foundry cell discovery, density analysis, and rekolektion as an open-source SRAM generator
2. **Social media / community distribution** via Reddit, Hacker News, and open-source silicon channels
3. **Conference outreach** if timing aligns (ORConf, FOSSi Dial-Up, etc.)
