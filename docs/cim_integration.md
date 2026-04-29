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

Each variant produces 5 files in
`output/cim_macros/cim_sram_<variant>_<rows>x<cols>/`:

| File | Purpose |
|------|---------|
| `.gds` | Layout for fabrication (hierarchical, sky130B; flatten before fab DRC sign-off) |
| `.sp` | LVS reference SPICE; what `run_lvs_cim.py` compares Magic's flat-extract output against |
| `.lef` | Abstract for place-and-route. Pins on `met2`; OBS computed from real GDS metal usage |
| `.lib` | Liberty timing model (placeholder arcs — needs SPICE characterisation before STA sign-off) |
| `_bb.v` | Blackbox Verilog stub. Bus ports declared as Verilog arrays (`input [N-1:0] MWL_EN`, `output [63:0] MBL_OUT`) so port names round-trip with the LEF's `MWL_EN[i]` brackets and the Liberty `bus(...)` declaration. |

Regenerated as a set on every `python3 scripts/generate_cim_production.py`
invocation, so they stay in lock-step with the GDS.

## Variants

| Variant | Cap | Array | Macro Size | CIM Signal |
|---------|-----|-------|------------|------------|
| SRAM-A | 1.30×3.10 (~8 fF) | 256×64 | 143.5×1323 um | 19.0 mV (TT) |
| SRAM-B | 1.10×2.65 (~6 fF) | 256×64 | 129.3×1208 um | ~14 mV |
| SRAM-C | 1.10×1.80 (~4 fF) | 64×64 | 129.3×255 um | ~10 mV |
| SRAM-D | 1.00×1.45 (~3 fF) | 64×64 | 127.7×255 um | ~8 mV |

MIM cap dimensions are rectangular (narrow in X) to minimize tiling pitch.
All variants use the same 6T LR core transistors (W=0.42, L=0.15).

## Verification

LVS (Magic flat-extract + netgen) and DRC (Magic) signed off against the
SKY130B PDK. Bitcell density-pattern violations are waived under COREID;
the rule list lives in `src/rekolektion/verify/drc.py::_KNOWN_WAIVER_RULES`.

| Variant | LVS          | DRC (flat)             | DRC (hier)             |
|---------|--------------|-----------------------:|-----------------------:|
| SRAM-A  | match unique | 0 real / 64321 waiver  | 0 real / 63978 waiver  |
| SRAM-B  | match unique | 0 real / 13820 waiver  | 0 real / 77860 waiver  |
| SRAM-C  | match unique | 0 real /  9100 waiver  | 0 real / 23526 waiver  |
| SRAM-D  | match unique | 0 real /  8984 waiver  | 0 real / 23400 waiver  |

256-row variants (SRAM-A/B) take ~4 hours each in netgen's graph-iso
check — the LVS runner sets `netgen_timeout = 6 * 3600` for any
variant with ≥128 rows.  Run them overnight or use `-j 2` to overlap.

Common waiver categories (all foundry-cell density patterns):

- `nwell.2a`: adjacent column nwells at VDD.
- `via.2 - 2*via.4a`: SRAM-A's 5.155 µm cell pitch + mirror tiling pulls
  via1s from adjacent rows close enough to fail the directional spacing
  rule. Same composite is harmless under COREID.
- `poly.2`: poly spacing in the bitcell's mirror-pair pack.
- `licon.7`: P-tap / N-tap contact overlap in foundry SRAM tap layout.
- `var.1` / `var.2` / `var.4` / `licon.10`: Magic mis-classifies the
  `cap_mim_m3_1` MIM cap as a varactor and fires the var.x rule deck.
  False positive vs. Calibre.
- subcell overlap: shared boundary abutment (hierarchical-DRC-only).

Reproduce:

```bash
export PATH="$HOME/.local/bin:$PATH"
export PDK_ROOT="$HOME/.volare"
python3 scripts/run_lvs_cim.py SRAM-A SRAM-B SRAM-C SRAM-D -j 4
python3 scripts/run_drc_cim.py            # flat — fab sign-off mode
python3 scripts/run_drc_cim.py --hier     # hierarchical — integrator mode
python3 scripts/openroad_smoke_cim.py SRAM-D  # OpenROAD integration smoke
```

`openroad_smoke_cim.py` synthesizes a one-cell wrapper that pulls
every macro pin to a top-level port, then runs the OpenROAD flow
(read tech LEF + std-cell LEF + macro LEF + Liberty, link netlist,
floorplan, place macro, place I/O pins, global + detailed route).
Catches integration issues that the isolated parse-tests miss, e.g.
LEF OBS gaps that block routing or POWER/GROUND classification
mismatches.  All four variants pass; the 256-row variants
(SRAM-A/B) take noticeably longer at detailed-route.

Format compatibility: LEF parses cleanly under OpenROAD with the
`sky130_fd_sc_hd.tlef` tech file loaded; Liberty parses under
OpenROAD's STA; Verilog parses under Yosys. Port names agree across
all three formats.

## Test Structures

Ring oscillator and unit cell test structures in `output/cim_test_structures/`:
- **Ring oscillator**: 11-stage, W=0.42/0.42 — process monitor matching CIM transistors
- **Unit cells**: single CIM bitcell per variant with all ports accessible for direct characterization
