# Power Gating Integration Guide

## Overview

rekolektion SRAM macros support power gating via the `--power-gating` flag. When enabled, the macro includes:

- **PMOS header switches** on the VDD rail (generated, sized for low Rdson)
- **SLEEP control pin** — assert to shut down the macro, deassert to wake
- Behavioral model gates all reads/writes when SLEEP=1

The macro exposes the SLEEP pin and the physical header switches. **Chip-level design is responsible for:**
- Power-on/off sequencing
- Output isolation
- Retention strategy
- Power grid routing

## Pin Interface

| Pin | Direction | Description |
|-----|-----------|-------------|
| `sleep` | Input | Assert HIGH to power-gate the macro. Deassert LOW for normal operation. |
| `VPWR` | Inout | Virtual VDD — gated by header switches. Connects to macro internal logic. |
| `VGND` | Inout | Ground — always connected (no footer switches). |

## SKY130 Cells Used

| Cell | Purpose | Notes |
|------|---------|-------|
| `sky130_fd_pr__pfet_01v8` (W=5µm) | Header switch | Custom-generated, ~4 per macro. Sized for < 100mV IR drop at typical operating current. |
| `sky130_fd_sc_hd__lpflow_isobufsrc` | Output isolation | **Chip-level.** Place on macro DOUT pins. Clamps outputs to 0 when SLEEP=1. |
| `sky130_fd_sc_hd__lpflow_bleeder` | Virtual VDD bleeder | **Chip-level.** Maintains weak VDD on virtual rail to prevent floating during shutdown. |
| `sky130_fd_sc_hd__lpflow_clkbufkapwr` | Always-on clock buffer | **Chip-level.** If macro CLK must remain active during sleep (e.g., for scan chain), use KAPWR-powered buffer. |

## Power-On Sequencing

```
         SLEEP=1 (power gated)           SLEEP=0 (normal)
         ─────────────────────────────── ──────────────────
VDD_REAL ████████████████████████████████ ████████████████████
VDD      ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ ████████████████████
                                        ↑
                                    Wake-up point

Timing:
  1. Assert SLEEP = HIGH         → Header switches OFF, VDD collapses
  2. (Macro is now power-gated)  → Leakage reduced ~99.99%
  3. Deassert SLEEP = LOW        → Header switches ON, VDD ramps
  4. Wait for VDD stable         → ~10-50 ns (depends on decap + load)
  5. Assert RST_N (if needed)    → Re-initialize macro state
  6. Resume normal operation     → CS, WE, ADDR, DIN functional
```

**Important:** Memory contents are NOT retained during power gating. The SRAM cells lose state when VDD collapses. If data retention is needed, use **WL switchoff** (`--wl-switchoff`) instead — it deasserts wordlines while keeping VDD active, preserving stored data with zero dynamic power.

## Chip-Level Checklist

- [ ] Place `lpflow_isobufsrc` on all macro output pins (dout, scan_out if applicable)
- [ ] Place `lpflow_bleeder` on the virtual VDD rail (prevents floating during shutdown)
- [ ] Route SLEEP signal from power management unit (PMU) to macro SLEEP pin
- [ ] Ensure SLEEP is synchronized to macro clock domain (avoid glitches during transition)
- [ ] Add decoupling capacitance on virtual VDD rail (reduces wake-up time)
- [ ] If using scan chain during sleep: route CLK through `lpflow_clkbufkapwr` from KAPWR
- [ ] Verify IR drop: header switch Rdson × operating current < 100mV
- [ ] Sequence: deassert SLEEP → wait VDD stable → deassert RST_N → resume

## SPICE Characterization Results

| Metric | Value | Corner |
|--------|-------|--------|
| Normal leakage (SLEEP=0, idle) | 35.4 µA | TT, 1.8V, 27°C |
| Gated leakage (SLEEP=1) | 0.16 pA | TT, 1.8V, 27°C |
| Leakage reduction | 99.99% | — |
| Header switch Rdson (per switch) | ~50 Ω | TT, W=5µm |
| Estimated IR drop (4 switches, 1mA) | ~12.5 mV | — |

## Feature Interaction

| Combined with | Behavior |
|---------------|----------|
| Clock gating (CEN) | Both can be active. CEN saves dynamic power; SLEEP saves leakage. Use CEN for idle cycles, SLEEP for long shutdown. |
| WL switchoff | WL switchoff preserves data; power gating does not. Use WL switchoff for light sleep, power gating for deep sleep. |
| Scan chain | Scan chain requires CLK. If scanning during sleep, use KAPWR-powered clock buffer. |
| Write enables | No interaction — BEN is gated by SLEEP like all other control signals. |
