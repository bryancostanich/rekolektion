# CIM bitcell foundry migration — supercell architecture spec

## Decision

**Replace the homegrown LR-based CIM bitcell with a "CIM supercell" that
wraps the foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` 6T core
unmodified, plus a T7 NMOS transistor and a MIM cap, both added in
the supercell layer above the foundry cell instance.**

Rationale (vs. the alternative of an annex-row architecture above the
bitcell):

1. **Foundry cell stays unmodified.** The supercell instances the
   foundry GDS without any geometry change. Foundry-validated
   characterization data still applies to the 6T core.
2. **No macro-level Q routing.** Q is connected internally inside the
   supercell. A 64×64 array would otherwise require 4096 vertical Q
   wires at the macro level to bridge bitcell rows to annex rows.
3. **Single tile pattern in the array.** Supercells substitute for
   bitcells in `tile_array`; foundry support cells (wlstrap, rowend,
   colend, corner) slot in alongside supercells the same way they
   would alongside foundry bitcells. No new tiler architecture.

## Per-variant supercell dimensions

The MIM cap is the dominant Y constraint. Cap can be aspect-ratio
adjusted per `track 03 decisions.md` (1.0 µm minimum side, area =
target capacitance; rectangular, narrow-in-X to minimise X pitch).
Cap-to-cap spacing in Y-mirrored adjacent rows is `capm.5b = 0.84 µm`.

| Variant | Cap (W×L, µm) | Cap area | Foundry h | T7 h | Supercell W | Supercell H | Supercell area | LR cell area | Improvement |
|---|---|---|---|---|---|---|---|---|---|
| SRAM-A | 1.30 × 3.10 | 4.03 µm² | 1.58 | 1.0 | 1.31 | 3.94 | **5.16 µm²** | 11.08 µm² | 53 % |
| SRAM-B | 1.10 × 2.65 | 2.92 µm² | 1.58 | 1.0 | 1.31 | 3.49 | **4.57 µm²** | 9.17 µm² | 50 % |
| SRAM-C | 1.10 × 1.80 | 1.98 µm² | 1.58 | 1.0 | 1.31 | 2.64 | **3.46 µm²** | 7.63 µm² | 55 % |
| SRAM-D | 1.00 × 1.45 | 1.45 µm² | 1.58 | 1.0 | 1.31 | 2.58 | **3.38 µm²** | 7.54 µm² | 55 % |

Supercell H is `max(foundry_h + T7_h, cap_y + 0.84)` — whichever of
"transistor real estate" or "cap-with-spacing" is larger. For SRAM-A/B,
the cap dominates; for SRAM-C/D, the foundry+T7 column dominates.

## Layer plan

| Layer | Foundry cell content | Supercell-added content |
|---|---|---|
| nwell | spans x=0.745–1.325 (PMOS region of foundry cell) | extends into annex region (y > 1.58) only over x=0.745–1.325 (matches foundry pattern for tile abutment) |
| diff/poly/li1/m1 | densely populated; M1 has BL, BR, VGND, VPWR rails consuming most of the bitcell's M1 channels | T7 NMOS transistor in annex (y > 1.58, x in NMOS region 0.0–0.745) — diff, poly gate, S/D licons, LI1 pads, M1 routing for MWL gate input + drain to via2 stack |
| li1 | Q internal as Net 42 (0.07–1.13 in X, 0.495–1.365 in Y); QB internal as Net 50 | LI1 EXTENSION column at x=1.13–1.31 (aligns with right edge of Q's wide horizontal LI1 stripe) bridges Q from inside foundry cell out to annex region. Routes Q via LI1 from y=1.225 (top of Q stripe) up to y=1.58 (foundry boundary) and into the annex |
| m2 | VGND straps at y=0.635–0.895; VPWR strap at y=1.025–1.285; UNKNOWN strap at y=0.295–0.465 (likely VGND) | M2 routing in annex region for T7 connections |
| m3 | empty | **MIM cap bottom plate** placed centred over the supercell's full X (cap_w ≤ supercell_w) and centred in Y with 0.42 µm margin top + bottom from supercell boundaries |
| capm (top plate) | empty | MIM cap top plate co-aligned with M3 bottom plate, sized per cap area |
| nsdm/psdm | foundry's existing implants at standard positions | NSDM extension covers T7's NMOS diff in annex region; PSDM unchanged |

## Q-to-T7 routing

Q's LI1 stops at x=1.13 inside the foundry cell. The supercell adds
LI1 at x=1.13–1.31, y=1.225–1.58, abutting Q's LI1 at x=1.13.

After macro flattening (which we already do as part of `run_lvs_cim.py`
for trustworthy LVS), the supercell's added LI1 + foundry cell's
internal LI1 merge into one polygon — Q is reachable from the annex
region via this extension.

T7's source connects to the LI1 extension at the foundry cell's top
boundary (y=1.58), then T7 sits in the annex above. T7 source pad → S
licon on T7 NMOS diff. T7 gate is poly with MWL signal label.

## T7-drain to cap routing

T7 drain (NMOS S/D opposite the source) → licon → LI1 → MCON → M1 →
via1 → M2 → via2 → M3 (cap bottom plate). This via stack lives in the
annex region (free of foundry cell M1/M2 conflicts) plus any extension
needed under the cap's M3 footprint.

## MBL routing

Cap top plate (capm layer) connects to MBL (per-column macro signal).
Currently MBL is on M4 in our CIM. Keep M4 for MBL: capm → via to M4
→ vertical M4 strap per column at the macro level. This is the same
pattern as the current LR-cell-based CIM — no architectural change to
MBL.

## WL routing

The supercell does NOT handle WL. WL bridging is handled by the
foundry `wlstrap` cell, inserted between supercells via
`tile_array(strap_interval=N)`. The WL signal enters the row via
`rowend`/`rowenda` cells at the array's left/right edges.

## Tile pattern in macro

```
                               wlstrap                       rowenda (right)
                                  |                                    |
rowend (left) — supercell — supercell — wlstrap — supercell — supercell
                |              |                       |              |
                                                                     ↑ supercells continue across cols
```

Plus column ends at top + bottom of every column (`colend`,
`colend_cent` over wlstrap columns), and `corner` cells at the four
corners. All these support cells already exist in the repo; we just
have to use them.

## Annex Y partitioning

Annex Y region (above foundry cell) is shared between T7 and any cap
overhang. Concrete partitioning per variant:

- **SRAM-A** (supercell h=3.94): cap centred y=0.42–3.52; T7 sits at
  x=0.0–0.745 in the lowest part of the annex (y=1.58–2.58); cap
  overhangs over T7 on M3 (different layer, no conflict).
- **SRAM-B** (supercell h=3.49): cap centred y=0.42–3.07; T7 same
  position. Same overlap pattern.
- **SRAM-C** (supercell h=2.64): cap centred y=0.42–2.22; T7 at
  y=1.58–2.58 (extends slightly past cap y range).
- **SRAM-D** (supercell h=2.58): cap centred y=0.565–2.015; T7 at
  y=1.58–2.58.

In all cases the cap spans at least half the foundry cell on M3 (no
M3 routing in the foundry cell, so OK).

## DRC / LVS verification plan

1. **Standalone supercell DRC** per variant (4 GDS files). Catches any
   spacing/enclosure issues from the new T7+cap geometry interacting
   with the foundry cell's existing features.
2. **Standalone supercell LVS** per variant against a hand-written
   reference schematic with the bitcell's internal devices declared
   identically to the foundry SkyWater netlist + the new T7 + the
   new MIM cap. Trustworthy LVS (no extracted-vs-extracted self-ref).
3. **Macro DRC** (Magic) on each of the 4 CIM macros after assembly.
4. **Macro LVS** with the trustworthy pipeline + Tier 2 flood-fill
   connectivity check on every per-row/per-col macro pin.
5. **Recharacterise Liberty arcs** per variant — the supercell has a
   different parasitic profile than the LR cell, so the existing
   timing data is invalidated. Re-run `characterize_cim_liberty.py`
   per variant once the macros are clean.

## Risk register

- **R1: LI1 abutment merge fragility.** The supercell relies on its
  LI1 extension touching the foundry cell's internal LI1 at x=1.13.
  If Magic's hierarchical extraction doesn't merge them, the macro
  will fail LVS. **Mitigation:** flatten before extraction (already
  the trustworthy LVS pattern); add explicit Tier 2 flood-fill check
  on Q net continuity.
- **R2: Cap aspect re-design impacts capacitance.** Cap area is
  preserved per variant, so capacitance value is preserved (within
  ~5 % tolerance for fringe effects). **Mitigation:** SPICE
  characterise the cap value of one variant pre-tape to confirm
  assumption.
- **R3: T7 placement near foundry cell boundary may have implant
  spacing issues.** NSDM around T7 must clear foundry cell's NWELL
  by `nwell.5` (0.34 µm). **Mitigation:** verify in standalone
  supercell DRC; T7 X position adjustable.
- **R4: Macro routing congestion.** Adding T7+cap per cell adds MWL
  per row, MBL per col on top of existing BL, BR, WL routing. With
  the 50–55 % area improvement, the routing density per channel may
  be tighter. **Mitigation:** evaluate after first macro builds; can
  thin power straps if needed.

## Implementation tasks (replaces F1–F4 in the task tracker)

1. **F1 — Q LI1 location** (DONE — Net 42, x=0.07–1.13, y=0.495–1.365)
2. **F2 — foundry support cell inventory** (DONE)
3. **F3 — supercell architecture spec** (THIS DOC)
4. **F5 — supercell layout generator** — Python module that emits the
   supercell GDS (foundry cell instance + T7 + cap + LI1 extension +
   nwell/implant boundary) per variant
5. **F6 — supercell DRC + standalone LVS** — clean the supercell
   per-variant before integrating into a macro
6. **F7 — array tiler integration** — update `cim_bitcell_array.py`
   to use supercell + tile with wlstrap/rowend/colend/corner via
   `tile_array(with_dummy=True, strap_interval=N)`
7. **F8 — macro assembler refactor** — update `cim_assembler.py` to
   replace per-cell T7/cap routing (no longer needed at macro level)
   with macro-level MWL/MBL/BL/BR/WL routing only
8. **F9 — macro DRC + trustworthy LVS + Tier 2 flood-fill** per variant
9. **F10 — Liberty re-characterisation** per variant
10. **F11 — production SRAM wlstrap fix** — apply the same wlstrap
    integration to `macro/bitcell_array.py` for activation_bank +
    weight_bank_small
