# Cell Comparison Notes: Foundry vs Custom

*2026-03-25 — Visual analysis from automated per-layer rendering.*

## Layer-by-Layer Comparison

### Diffusion (layer 65,20)
- **Foundry**: 8 shapes forming L-shaped "dog-bone" diffusion. Wider where pull-down transistors need more W, narrower for pass gates. Horizontal bars where diff widens for shared source/drain tap regions. Every square micron used.
- **Ours (LR)**: 2 plain uniform-width rectangles. Same width top to bottom. Large areas of unused space on the sides and between strips.
- **Gap driver**: Standard device min width (0.42 μm) forces uniform wide strips. Foundry uses 0.14-0.21 μm special devices allowing shaped diff.

### Polysilicon (layer 66,20)
- **Foundry**: 4 clean horizontal stripes spanning full cell width. Top/bottom = word lines, middle two = cross-coupled gates. Each crosses both NMOS and PMOS diff in one continuous run. No pads, no widening, no fragmentation.
- **Ours (LR)**: 7 shapes — separate gate segments plus widened poly contact landing pads. Gates don't span the full width continuously.
- **Gap driver**: We need landing pads for licon contacts on poly. Their poly contacts sit in the N-P gap where poly naturally widens between diff regions. Our wider diff (0.42 vs 0.14) means our poly gate section is wider, leaving less room for the landing pad.

### Local Interconnect / li1 (layer 67,20)
- **Foundry**: 53 shapes forming a dense Z-shaped routing pattern. Two large diagonal paths cross the cell for cross-coupling (upper-left to lower-right, lower-left to upper-right). Rectangular pads at contact points surrounded by diagonal routing fills. Li1 practically fills the cell — barely any empty space. Li1 is used as a dense routing fabric.
- **Ours (LR)**: 16 isolated rectangular pads floating in mostly empty black space. Each pad sits over a contact but does minimal routing work. Cross-coupling is done through a few L-shaped jogs.
- **Gap driver**: Their diagonal li1 routes are shorter than our Manhattan L-shapes. The foundry cell is DRC-blackboxed so non-Manhattan li1 isn't checked against standard rules. We use li1 only as contact pads, not as a routing layer.
- **Key insight**: This is the single biggest density difference. They use li1 as routing fabric; we use it as contact pads.

### Metal 1 (layer 68,20)
- **Foundry**: 17 shapes — 4 main vertical strips (VGND, BL, BR, VPWR) plus horizontal stubs and via landing pads. Met1 carries both power AND signal (bit lines). Complex shapes with stubs for via connections.
- **Ours (LR)**: 8 shapes — thin vertical power rails plus horizontal stubs to contacts. Met1 is mostly just power delivery.
- **Gap driver**: We route bit lines through li1/contacts. They route bit lines on met1, freeing li1 for cross-coupling. More efficient use of the metal stack.

### Metal 2 (layer 69,20)
- **Foundry**: 5 horizontal stripes for power delivery. Two wide (VPWR, VGND) plus three smaller extensions.
- **Ours (LR)**: 2 horizontal stripes (VPWR, VGND). Only power, no signal.
- **Gap driver**: We could add more met2 for redundant power strapping or signal routing.

## Summary: Design Philosophy Differences

| Aspect | Foundry | Ours |
|---|---|---|
| **Geometry** | 20% non-rectangular (diagonal, L-shaped) | 100% rectangular (Manhattan) |
| **Li1 usage** | Dense routing fabric (53 shapes) | Contact pads only (16 shapes) |
| **Diff shaping** | L-shaped dog-bone per transistor size | Uniform rectangles |
| **Poly** | 4 continuous full-width stripes | 7 fragmented segments + pads |
| **Met1** | Power + signal routing (BL/BR) | Power only |
| **Met2** | 5 stripes (power + extensions) | 2 stripes (power) |
| **Overall** | Every layer doing maximum work | Conservative, single-purpose layers |

## Optimization Priority (for custom cell)

Ranked by expected area impact and implementation feasibility:

1. **Diagonal li1 cross-coupling** — biggest single gap, but highest DRC risk
2. **L-shaped diffusion** — well-understood, standard DRC compliant
3. **Met1 for bit line routing** — frees li1 for cross-coupling, standard technique
4. **Continuous poly** (already attempted, blocked by standard device width rules)
5. **More met2 strapping** — diminishing returns, already partially done

## Cell Sizes

| Cell | Area | vs Foundry |
|---|---|---|
| Custom top/bottom (original) | 7.22 μm² | 3.5x |
| Custom top/bottom (optimized) | 6.89 μm² | 3.3x |
| Custom left/right | 6.23 μm² | 3.0x |
| Custom LR + M2 strapping | 5.53 μm² | 2.7x |
| **Foundry SP opt1** | **2.07 μm²** | **1.0x** |

The remaining 2.7x gap is primarily:
- Standard device min width (0.42 vs 0.14 μm) — ~40% of the gap
- Standard diff extension (0.38 vs 0.045 μm) — ~30% of the gap
- Manhattan-only routing (no diagonal li1) — ~20% of the gap
- Conservative layer utilization — ~10% of the gap
