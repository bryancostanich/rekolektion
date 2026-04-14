# CIM SRAM Macro Integration Guide

## Overview

rekolektion generates 7T+1C CIM SRAM array macros for capacitive
compute-in-memory on SKY130 (sky130B). Each bitcell extends the 6T LR
core with a pass transistor (T7) and a MIM coupling capacitor, enabling
analog dot-product computation without digitizing inside the array.

## Pin Interface

### Per-row pins (left edge)

| Pin | Direction | Description |
|-----|-----------|-------------|
| `MWL_EN[0..rows-1]` | Input | Multiply word line enable. Assert HIGH to include a row in the CIM computation. Any subset of rows can be active simultaneously (unlike WL which is one-hot). |

### Per-column pins (bottom edge)

| Pin | Direction | Description |
|-----|-----------|-------------|
| `MBL_OUT[0..cols-1]` | Output (analog) | Multiply bitline output. Carries the analog voltage representing the weighted sum. **Do NOT treat as digital.** Route to external ADC. |

### Control pins

| Pin | Direction | Edge | Description |
|-----|-----------|------|-------------|
| `MBL_PRE` | Input | Top | Precharge control (active low). Assert before CIM compute to reset MBL to VREF. |
| `VREF` | Inout | Top | Precharge reference voltage (typically VDD_CIM/2 = 0.6V). External supply. |
| `VBIAS` | Input | Bottom | Sense buffer bias voltage. Sets quiescent current of the source follower output buffer. External supply. |

### Power pins (configurable naming)

| Pin | Default | Description |
|-----|---------|-------------|
| `VPWR` | `VPWR` | Positive supply. CIM arrays operate at 1.2V for best signal margin. |
| `VGND` | `VGND` | Ground. |

Power pin names are configurable via `pwr_pin`/`gnd_pin` parameters in
the LEF/Liberty/blackbox generators.

## CIM Compute Sequence

1. **Precharge**: Assert `MBL_PRE` (LOW) → MBL charges to VREF (~0.6V)
2. **Deassert precharge**: Release `MBL_PRE` (HIGH) → MBL floating
3. **Assert MWL_EN**: Set desired rows HIGH → T7 pass transistors conduct,
   coupling stored weights onto MBL via MIM capacitors
4. **Settle**: Wait ~2.6 ns for charge sharing to complete
5. **Read MBL_OUT**: Analog voltage at MBL_OUT represents the dot product.
   Route to external ADC for digitization.

## Operating Voltage

CIM arrays are characterized at **1.2V** (not 1.8V). At 1.2V:
- 19.0 mV per-cell delta (TT/27C) for SRAM-A (8 fF cap)
- 12 of 15 PVT corners exceed 10 mV
- Full PVT range: 2.1–21.9 mV

At 1.8V, the signal degrades severely (10.5 mV TT, only 6/15 corners > 10 mV).
The Liberty files specify `nom_voltage: 1.2V` — chip-level integration must
provide a 1.2V supply domain for CIM arrays.

## Output Artifacts

Each variant produces 4 files in `output/cim_macros/`:

| File | Purpose |
|------|---------|
| `.gds` | Layout for fabrication (hierarchical, sky130B) |
| `.lef` | Abstract for place-and-route |
| `.lib` | Liberty timing model (CIM compute latency, setup/hold) |
| `_bb.v` | Blackbox Verilog stub for synthesis |

## Variants

| Variant | Cap | Array | Macro Size | CIM Signal |
|---------|-----|-------|------------|------------|
| SRAM-A | 1.30×3.10 (~8 fF) | 256×64 | 143.5×1323 um | 19.0 mV (TT) |
| SRAM-B | 1.10×2.65 (~6 fF) | 256×64 | 129.3×1208 um | ~14 mV |
| SRAM-C | 1.10×1.80 (~4 fF) | 64×64 | 129.3×255 um | ~10 mV |
| SRAM-D | 1.00×1.45 (~3 fF) | 64×64 | 127.7×255 um | ~8 mV |

MIM cap dimensions are rectangular (narrow in X) to minimize tiling pitch.
All variants use the same 6T LR core transistors (W=0.42, L=0.15).

## DRC Waivers

CIM array tiling produces known DRC waivers (all same-potential, Magic
can't resolve nets across cell boundaries):
- `nwell.2a`: adjacent column nwells at VDD
- `via.2`: adjacent VPWR vias (separated tiling mode, SRAM-A only)
- subcell overlap: shared boundary abutment (SRAM-B/C/D)

Zero real DRC errors on any variant or macro.

## Test Structures

Ring oscillator and unit cell test structures in `output/cim_test_structures/`:
- **Ring oscillator**: 11-stage, W=0.42/0.42 — process monitor matching CIM transistors
- **Unit cells**: single CIM bitcell per variant with all ports accessible for direct characterization
