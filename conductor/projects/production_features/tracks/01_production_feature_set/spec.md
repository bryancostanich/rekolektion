# Track: Production Feature Set

## Objective
Add six production features to rekolektion as composable generator options. Each feature adds logic, pins, and corresponding updates to all generated outputs (GDS, LEF, Liberty, Verilog, SPICE). Features are enabled per macro via CLI flags (e.g., `--write-enables --scan-chain --clock-gating`).

## Features

### High Priority

**Bit-level write enables** — Gate write drivers per byte (or per bit) so the bus can write individual bytes without read-modify-write. Adds a BEN port to the macro interface, AND gates between the write drivers and column mux, and a byte-enable decoder. The column mux and write driver generators already exist — this extends them with an enable mask. Eliminates the RMW penalty that currently halves effective write bandwidth.

**Scan chain DFT** — Reusable Verilog wrapper that replaces the address, data-in, and control register flops with scan flops, chained in a fixed order within the macro. Exposes ScanIn, ScanOut, ScanEnable pins. Chip-level DFT tools stitch the macro into the SoC scan chain via those three pins without needing to know the internal structure. Required for any production test flow.

### Medium Priority

**Clock gating** — An integrated clock gating cell (ICG) on the macro clock input, controlled by an enable pin. When deasserted, no internal transitions occur — zero dynamic power on idle macros. With 160 macros in khalkulo and partial activity per cycle, this is meaningful. Single standard cell, minimal area cost.

**Power gating** — Header or footer switch cells on the macro power rails, with a sleep/enable control pin. The switch cells are SKY130 standard library cells. rekolektion generates the switches and exposes the control pin; the chip-level design handles sequencing and power grid routing. Drops leakage ~30x per macro.

**Wordline switchoff** — Additional gating in the row decoder that deasserts all wordlines when the macro is not selected. Prevents half-select disturb on bitcells during idle cycles. Improves long-term data retention reliability. Small addition to the existing decoder generator.

### Lower Priority

**Burn-in test mode** — A test mode pin (TM) that switches the wordline driver from normal decoded operation to all-wordlines-asserted simultaneously. Stresses every cell in parallel at elevated voltage for infant mortality screening. Implementation is a mux at the wordline driver output plus the TM control pin.

## What Stays Chip-Level

These features are NOT part of this track — rekolektion only exposes pins:

- **Separate power domains** — Macro exposes separate core/periphery power pins. Actual power grid design, level shifters, and sequencing are chip-level floorplan decisions.
- **Body bias** — Macro exposes well tap pins (vpb/vnb). Bias generation, routing, and tuning strategy are chip-level.

## Implementation Approach

All six features are generator options — off by default, enabled per macro. The macro assembler gains a feature flags parameter, and each feature adds its logic, pins, and corresponding updates to the generated LEF, Liberty, Verilog, and SPICE outputs. No monolithic "production macro" variant — keep the generator composable.

## Competitive Reference

| Feature | ChipFoundry | rekolektion (current) | rekolektion (after) |
|---------|:-----------:|:---------------------:|:-------------------:|
| Bit-level write enables | Yes | No | Yes |
| Scan chain DFT | Yes | No | Yes |
| Clock gating | Yes | No | Yes |
| Power gating | Yes | No | Yes |
| Wordline switchoff | Yes | No | Yes |
| Burn-in test mode | Yes | No | Yes |
| Parameterized generation | No | Yes | Yes |
| Monolithic scaling | No | Yes | Yes |
| Open source | No | Yes | Yes |
