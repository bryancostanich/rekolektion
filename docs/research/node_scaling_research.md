# SRAM 6T Bitcell Scalability Across Process Nodes

*Date: 2026-03-25*

## Key Finding

The 6T SRAM bitcell topology is universal — the same circuit (2 cross-coupled inverters + 2 access transistors) is used at every process node from 250nm to 3nm. It is arguably the most-scaled single circuit in semiconductor history. The layout techniques developed for rekolektion at SKY130 130nm are conceptually portable to any node, though process-specific constraints change character significantly.

---

## What Stays the Same Across Nodes

- 6T circuit topology: cross-coupled CMOS inverters + NMOS pass gates
- The fundamental layout problem: connect drain-to-gate for cross-coupling without shorting nets
- Power/ground rails, bit lines, word lines as the four signal classes
- The cell ratio (PD/PG) and pull-up ratio (PU/PG) as key design knobs
- Mirror-symmetric tiling for array construction

## What Changes by Node

| Aspect | 130nm (SKY130) | 28nm | 14nm / 7nm | 3nm |
|--------|---------------|------|------------|-----|
| Transistor type | Planar MOSFET | Planar (some FinFET) | FinFET | GAA / Nanosheet |
| Gate patterning | Single patterning | Single/double | Multi-patterning (SADP) | EUV |
| Transistor width | Continuous (arbitrary W) | Continuous | Quantized (fin count) | Quantized (sheet count) |
| Typical cell area | ~2 μm² | ~0.12 μm² | ~0.03 μm² | ~0.02 μm² |
| Routing layers used | li1 + met1-2 | Local interconnect + M1-3 | M0/M1 buried routes | Buried power rail + M0-2 |
| Cross-coupling method | li1 routing (diagonal or Manhattan) | Local interconnect or M1 | Built into fin/gate patterning | Gate-level cross-coupling |
| Dominant design challenge | Layout geometry & routing | Process variation | Variation + leakage | Variation + power + patterning |
| Min width control | DRC rules, generous | Tighter DRC | Lithography-limited | EUV-limited |

## Node-Specific Details

### 130nm (SKY130 — our node)

- Planar bulk CMOS. Well-behaved transistors with predictable characteristics.
- Primary challenge is layout geometry: fitting 6 devices + cross-coupling routing in minimum area.
- Transistor width is a continuous design knob — we choose 0.42 μm, 0.36 μm, etc.
- Local interconnect (li1) is a major advantage for SRAM — provides an extra routing layer below met1. SKY130 allows 45° diagonal li1 inside SRAM core cells (`areaid:ce`), with relaxed 140nm width/spacing rules.
- The foundry SRAM cell (2.07 μm²) uses sub-minimum SRAM-specific device models (W=0.14 μm) that standard cells can't access. Our standard-device cell floors around 3.5-4.0 μm².

### 28nm — The Sweet Spot

- Last major planar node. Very mature tooling and manufacturing.
- **Massive production volume** — IoT, automotive, RF, mobile baseband. Still a high-volume node in 2026.
- Random dopant fluctuation becomes significant: two identically-drawn minimum-size NMOS can show 30-50mV of Vt mismatch. SRAM cells need extra margin.
- Read-assist and write-assist circuits become common (lowering WL voltage, boosting/collapsing supply) — these are unnecessary at 130nm.
- Some 28nm variants (28nm FDSOI from STMicro/GlobalFoundries) offer body-biasing as a knob for SRAM tuning.
- **If we wanted to port rekolektion to another node, 28nm is the natural target** — especially if open 28nm PDKs emerge.

### 14nm / 7nm — FinFET Era

- Transistor width becomes quantized: 1 fin, 2 fins, 3 fins. Can't fine-tune cell ratio like at 130nm.
- SRAM cells typically use 1 fin for pull-up, 1 fin for access, 1-2 fins for pull-down.
- The "W" knob is replaced by "number of fins" — coarser but simpler.
- Self-aligned double patterning (SADP) imposes strict grid constraints on metal routing. Layout is more like "coloring a grid" than "placing rectangles."
- Cross-coupling is increasingly done at the gate/contact level rather than with metal routing.
- Density scaling slows: 7nm → 5nm → 3nm gives ~1.2-1.5x per step, not 2x.

### 3nm and Beyond — GAA/Nanosheet

- Gate-All-Around (GAA) or nanosheet transistors replace FinFETs.
- Width is quantized by sheet count and sheet width.
- Buried power rails (BPR) move VGND/VDD below the transistors, freeing routing tracks.
- Backside power delivery (BSPDN) is emerging.
- The 6T topology persists, but the layout is almost entirely determined by process constraints and coloring rules rather than designer choice.

## Implications for Rekolektion

1. **Generator architecture is portable.** The parameterized approach (bitcell → array → peripherals → macro) works at any node. Only the bitcell layout, design rules, and device models change.

2. **Cross-coupling routing is the universal hard problem.** What we do with li1 in the N-P gap at 130nm is conceptually identical to what Intel does at 7nm with M0 — the pitches are 20nm instead of 170nm, but the topological problem is the same.

3. **Natural port targets:**
   - IHP 130nm SiGe BiCMOS (open PDK, similar rules)
   - Future open 28nm PDKs (if/when they emerge)
   - Any node with a gdstk-compatible PDK and documented DRC rules

4. **Skills transfer directly.** Understanding DRC rules, contact placement, layer utilization, mirror tiling, and power routing at 130nm builds intuition that applies at every node. The physics changes; the layout methodology doesn't.

---

## References

- SemiEngineering: "Node Within A Node" — process margin reduction as a scaling technique
- SemiWiki: "Declining density scaling trend for TSMC nodes" — 7nm→5nm→3nm density data
- Wikipedia: 7nm process — industry overview of FinFET SRAM density
- SKY130 Magic tech file (lines 4456-4458) — SRAM core cell diagonal li1 rules
