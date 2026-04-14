# Track 03 Design Decisions

## Decision 1: T7 Pass Transistor Placement & Q-Node Routing

**Date:** 2026-04-14
**Status:** Implemented

### Problem

The existing `create_cim_bitcell()` places T7 above the 6T cell with its
diff overlapping the BL_top region, not the Q storage node. The current
code has 54 DRC errors AND a topology bug: T7 source connects to BLB, not Q.

### Options Considered

**A. Integrated T7 (MWL gate inserted into 6T NMOS diff)**
- Insert MWL between existing gates on the continuous NMOS diff strip.
- Problem: any gate inserted between int_bot↔gate_a or gate_b↔int_top
  would split the Q or QB node, breaking the 6T cell during normal SRAM
  operation. T7 must be a SIDE BRANCH off Q, not IN SERIES with PD/PG.
- **Rejected: breaks 6T cell operation.**

**B. External T7 with shared diff (extend 6T NMOS diff upward)**
- T7 diff merges with 6T NMOS diff above wl_top.
- Problem: the region above wl_top is the BL_top (BLB) contact area,
  separated from Q by the wl_top access gate. T7 source = BLB, not Q.
- **Rejected: wrong node.**

**C. External T7 with M1 routing to Q via NMOS int_bot**
- Route from NMOS int_bot (nmos_cx, 0.645) through M1 to T7 above cell.
- Problem: In the LR topology, NMOS int_bot is NOT on the latched Q net.
  The latched Q net is Route 1 = {PMOS int_bot, gate_A poly}. NMOS int_bot
  is driven by gate_A PD but not itself part of the latch. When Q=0,
  PD is OFF and NMOS int_bot floats — it doesn't cleanly carry Q.
- **Rejected: wrong net (NMOS int_bot ≠ latched Q).**

**D. External T7 with M1 routing to Q via Route 1 li1 (gap area)**  ← CHOSEN
- T7 placed above cell with separate diff (0.27um spacing from 6T diff).
- T7 source connected to the Route 1 li1 net (the latched Q net) via
  mcon in the N-P gap at (1.1, 0.645), running M1 vertically at X=1.1
  up to T7 source level, then horizontal to nmos_cx.
- T7 drain connected to MIM cap M3 bottom plate via full via stack
  (mcon → M1 → via1 → M2 → via2 → M3).
- Via2 placed above MIM cap top edge with 0.1um spacing (capm.8),
  and above M2 VPWR stripe with 0.14um spacing.
- **Chosen: correct net, DRC-clean routing, no modification to 6T core.**

### Cost

- Cell height increases by ~1.0um (T7 diff + gap).
- Tiling y_pitch increases from 2.04 to ~3.0um.
- Effective cell area: ~5.8 um² vs 3.93 um² for 6T-only pitch.
- This is the real physical cost of adding T7. The 3.93 um² in the SPICE
  characterization was the 6T pitch — T7 adds overhead that's unavoidable
  in the LR topology.

### DRC Fixes Applied

| Rule | Was | Fix |
|------|-----|-----|
| capm.3 (M3 enclosure of MIM) | 0.06um | 0.14um |
| capm.8 (via2 to MIM spacing) | via2 inside MIM | via2 above MIM, ≥0.1um |
| poly.7 (diff overhang past poly) | 0.135um | 0.30um |
| licon.11 (licon to gate spacing) | 0.03um | 0.065um |
| met1.4/met1.5 (M1 mcon encl.) | inadequate | proper 0.03/0.06 |
| via2.4a (M2 via2 enclosure) | inadequate | 0.085um |
| li.3 (li1 spacing) | violations | proper spacing |
| diff/tap.2 (transistor width) | violations | proper width |
| poly.1a (poly width) | possible snap error | proper snapping |

## Decision 2: MIM Cap Sizing — Rectangular Caps Per Variant (REVISED)

**Date:** 2026-04-14 (revised from earlier same-day version)
**Status:** Implemented

### Problem (original)

Originally assumed SKY130 MIM cap minimum was 2.0×2.0 um (from code
constant `MIM_MIN_WIDTH`). This collapsed all 4 variants to one cell.

### Correction

Verified against the actual Magic DRC deck (`sky130A.tech`):
`width *mimcap 1000` = **1.0 um minimum**, not 2.0. Both sky130A and
sky130B have identical 1.0um minimum. The Track 21 smaller caps
(1.4×1.4, 1.2×1.2) were physically legal all along.

### Solution

Use **rectangular** MIM caps oriented narrow-in-X to minimize X-pitch.
Each variant has a different cap area (= different capacitance = different
CIM sensitivity), with the narrow dimension keeping X-pitch close to
the 6T baseline (1.925um).

| Variant | Cap Geometry | Cap (fF) | X-pitch | Y-pitch | Cell Area |
|---------|-------------|----------|---------|---------|-----------|
| SRAM-A | 1.30 × 3.10 | ~8.1 | 2.150 | 5.155 | 11.08 um² |
| SRAM-B | 1.10 × 2.65 | ~5.8 | 1.950 | 4.705 | 9.17 um² |
| SRAM-C | 1.10 × 1.80 | ~4.0 | 1.950 | 3.915 | 7.63 um² |
| SRAM-D | 1.00 × 1.45 | ~2.9 | 1.925 | 3.915 | 7.54 um² |

SRAM-C and SRAM-D caps fit within the 6T X-pitch — no X overhead from
the cap. SRAM-D X-pitch = 6T X-pitch (6T-limited, not cap-limited).

All 4 variants DRC clean. The 4-size CIM experiment is fully restored.

## Decision 3: CIM Cell Tiling Pitch — Per-Variant (REVISED)

**Date:** 2026-04-14 (revised)
**Status:** Implemented

### Problem

Tiling pitch depends on cap dimensions (which vary per variant) and T7
overhead (constant). Need per-variant pitch calculation.

### Solution

Pitch computed per-variant in `load_cim_bitcell()`:
- **X-pitch** = max(cap_w + capm.2a spacing 0.84, 6T x_pitch 1.925)
- **Y-pitch** = max(NSDM constraint, MIM cap Y-spacing constraint)

T7 prevents 6T boundary sharing in Y for all variants. NSDM spacing
(0.38um) dominates Y for small caps; MIM cap spacing dominates for tall
caps (SRAM-A, SRAM-B).

### Impact on Array Sizes

| Array | Rows×Cols | Cell Area | Array Area |
|-------|-----------|-----------|------------|
| SRAM-A | 256×64 | 11.08 um² | 0.182 mm² |
| SRAM-B | 256×64 | 9.17 um² | 0.150 mm² |
| SRAM-C | 64×64 | 7.63 um² | 0.031 mm² |
| SRAM-D | 64×64 | 7.54 um² | 0.031 mm² |
| **Total** | | | **0.394 mm²** |

Total CIM area 0.39 mm² fits within 2.95 mm² budget with 2.56 mm²
remaining for peripherals, ADCs, and routing.
