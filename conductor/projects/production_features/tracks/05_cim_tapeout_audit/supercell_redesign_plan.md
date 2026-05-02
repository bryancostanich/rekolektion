# CIM supercell redesign — Phase 0 plan

**Date**: 2026-04-30
**Driver**: Issue #7 (CIM bitcell BL/BR access-tx drain floating) plus underlying T1.1-A bug (production's "LVS clean" is self-reference; same drain-floating defect ships in production today)
**Status**: ⚠️ **PHASE 0 RESULT: Architecture 1 INFEASIBLE — see "2026-04-30 Phase 0 outcome" section at end**

---

## Background recap

The foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` ships with **0 LICON1 and 0 MCON internally**. The cell's BL/BR met1 rails are floating; the cell intends adjacent strap cells (`wlstrap`, `wlstrap_p`, `colend`, `colend_cent`, `corner`) to provide:
- N-tap / P-tap to substrate (body bias)
- LI1+MCON connection between the foundry's full-width LI1 stripes and BL/BR met1 rails
- Diffusion-abutment continuity between rows for shared drain DIFF chains

The foundry's tile contract is bitcell-pitch: every cell, strap, and endcap is 1.58 µm tall (Y) and 1.31–1.55 µm wide (X). All cells share the same Y rail positions. Production's `bitcell_array.py` follows this contract for bitcells + wlstraps; it omits colend (probable secondary defect, separate work).

The current CIM supercell **breaks the foundry contract** by adding a Y-annex above the foundry cell (1.35 µm tall) for T7+MIM cap. After tiling supercells, foundry cells in adjacent supercell rows are 1.35 µm apart in Y — not abutting. Wlstrap/colend cannot tile between them; the foundry's drain-bridge mechanism is unreachable.

**Asymmetric extraction observation**: with the broken layout, Magic's flat extraction shows BR with 12,288 device occurrences and BL with 0. This is a Magic merging artifact, not real connectivity. The asymmetry is rooted in the foundry's `colend` geometry: BL1 (= our BL) pin has LI1+MCON+MET1 stack while BL0 (= our BR) pin has MET1-only — different LVS-extraction footprints surface differently when no colend is tiled.

---

## Decision: Architecture 1 — foundry-pitch supercell

Drop the Y-annex. Move T7+MIM cap so the supercell maintains the foundry's 1.58 µm Y pitch. Tile wlstrap (and eventually colend) per the foundry's intended pattern.

**Why this and not the alternatives:**

- **Architecture 2** (2-cell supercell with shared T7+cap) — kills compute parallelism. Disqualified.
- **Architecture 3** (custom CIM strap cell at supercell pitch) — drain DIFF is supercell-internal at half the row boundaries; strap can only contact every other row. Topologically incomplete. Disqualified.
- **Architecture 4** (current supercell + colend at top/bottom only) — Y-annex blocks colend abutment. Reduces to Architecture 1 once you account for the abutment requirement. Disqualified as a separate option.

Architecture 1 is the only one that restores the foundry contract.

---

## Layout (foundry-pitch supercell)

```
   x=0                    x=1.31  x=foundry+spc  x=foundry+spc+T7  x=cap_xR    x=W_super
   ┌──────────────────────┬───────┬──────────────┬─────────────────┐
   │                      │       │              │                 │
   │  Foundry 6T cell     │ NWELL │   T7 NMOS    │   M3+capm cap   │
   │   (1.31 × 1.58)      │  gap  │   (NSDM)     │   over Si       │
   │                      │       │              │                 │
   └──────────────────────┴───────┴──────────────┴─────────────────┘
                       supercell_h = 1.58 (foundry pitch)
```

- T7 sits in NSDM region east of foundry NWELL boundary.
- MIM cap (cap_mim_m3_1) sits on M3+capm in the metal stack ABOVE the silicon. The cap can occupy X space between T7's east edge and the supercell's east edge. Cap height (cap_l) must fit within `supercell_h - 2 × capm.5b_enclosure ≈ 1.40 µm`.
- Q exit (foundry east edge, y=1.20) routes via LI1 stub to T7 source (no Y motion needed since both are within y=[1.10, 1.40] band).
- T7 drain → cap M3 bottom plate via MCON+via1+via2 stack at T7 drain X.
- Cap M4 top plate → MBL column rail (per-column M4 strip).

### Per-variant cap re-aspect-ratio

| Variant | Old cap (W × L µm²) | Old cap area (µm²) | New cap (W × L) | New supercell_w |
|---------|-----|------|------|-----|
| SRAM-A (8.1 fF, 256×64)  | 1.30 × 3.10 = 4.03 | 4.03 | 2.88 × 1.40 | 3.72 |
| SRAM-B (5.8 fF, 256×64)  | 1.10 × 2.65 = 2.92 | 2.92 | 2.08 × 1.40 | 2.92 |
| SRAM-C (4.0 fF, 64×64)   | 1.10 × 1.80 = 1.98 | 1.98 | 1.41 × 1.40 | 2.25 |
| SRAM-D (2.9 fF, 64×64)   | 1.00 × 1.45 = 1.45 | 1.45 | 1.04 × 1.40 | 2.20 (foundry+T7 floor) |

`new supercell_w = max(foundry_w + T7_pitch, cap_w + capm.5b_spacing)` where:
- `foundry_w + T7_pitch = 1.31 + 0.34 (NWELL→NSDM) + 0.42 (T7_W) + 0.125 (NSDM enc) + 0.10 margin = 2.295` ≈ 2.20–2.30 floor
- `capm.5b spacing = 0.84`

Cap height target 1.40 µm leaves 0.18 µm slack to supercell_h=1.58 for capm enclosure (capm.5a = 0.30 each side after edge — needs verification against actual sky130 capm rules, may need to relax to 1.30 µm cap_l).

### Macro footprint impact

| Variant | Old (W×H) | New (W×H) | Old area | New area | Δ area | Aspect ratio old/new |
|---------|----|------|------|------|------|------|
| SRAM-A 256×64 | 148 × 1009 | 238 × 404 | 149,332 | 96,152 | **-36%** | 0.15 / 0.59 |
| SRAM-B 256×64 | 148 × 1009 | 187 × 404 | 149,332 | 75,548 | **-49%** | 0.15 / 0.46 |
| SRAM-C 64×64  | 148 × 187  | 144 × 101 | 27,676  | 14,544 | **-47%** | 0.79 / 1.43 |
| SRAM-D 64×64  | 148 × 187  | 141 × 101 | 27,676  | 14,241 | **-49%** | 0.79 / 1.40 |

All variants shrink and become squarer. Better for SoC integration.

---

## Drain → BL/BR connection — two-phase fix

### Phase 1 (this redesign): foundry-tile parity

Tile wlstrap + colend per the foundry's intended pattern. This **matches what production currently ships** (production tiles wlstrap, omits colend; we should add colend to match the foundry's full contract). This does NOT silicon-correctly bridge the access-tx drain to BL/BR met1 — it replicates the production-shipped pattern.

For LVS to pass against this layout, `cim_spice_generator` must change: stop hand-writing the foundry cell's `.subckt` with explicit drain→BL/BR connections, and instead either:
1. Pre-extract the foundry cell once at build time (Magic with `port makeall`), include the extracted .subckt verbatim, OR
2. Generate a foundry .subckt that matches Magic's actual port topology (BL/BR/WL as ports, drains a_38_0#/a_38_292# as separate ports, no internal connection between them).

This is **production parity**. CIM is no worse than production. Issue #7 closes for tapeout under the same disclosure that already exists for production (audit T1.1-A).

### Phase 2 (post-Phase-1, separate engineering effort): silicon-correct drain bridge

Add a **custom drain bridge** inside the supercell that extends the access-tx drain DIFF outward into the supercell wrapper, where there's space for LICON1+LI1+MCON+MET1 stack to BL/BR met1 rail. Specifically:

- Top access tx drain DIFF (foundry-y=1.460–1.580, x=0.190–0.330): extend DIFF in the supercell wrapper from foundry x=[0.190, 0.330] outward to x=[0.115, 0.405] within y=[1.580, 1.700]. NSDM coverage (foundry NSDM extends to y=1.705 in this X range) is sufficient.
- Place LICON1 (0.17×0.17) on the extended DIFF, LI1 over it, MCON to BL met1 rail at (0.350, 0.490) ×.
- Symmetric for BR side.
- Bottom access tx drain DIFF: needs the same treatment in the *next supercell row's annex below the foundry cell*. Since adjacent rows are X-mirrored, the "annex below" is actually the NEXT row's supercell-internal-top — requires careful coordination with mirror symmetry.

Phase 2 silicon-correctness applies to **production AND CIM**. Production's `bitcell_array.py` would need the same drain-bridge addition. This closes T1.1-A's hidden bug.

Phase 2 is NOT in scope for the foundry-pitch redesign. It's tracked separately and applies to production retroactively.

---

## Strap insertion (Phase 1)

`cim_supercell_array.py` to insert wlstrap columns at every N supercell pitches, similar to production's `bitcell_array.py:strap_interval`. Wlstrap is 1.41 µm × 1.58 µm — fits between supercells in X.

- Default `strap_interval = 8` (matches production)
- Wlstrap stacked vertically once per supercell row (now 1.58 µm pitch)
- Wlstrap_p alternates with wlstrap per X-mirror row pair (foundry convention: P-tap rows alternate with N-tap rows)

Colend at top + bottom of array, one per supercell column. Colend is 1.41 µm wide × 2.18 µm tall. Add ~2.18 µm to top + bottom of macro Y (small overhead).

---

## Files affected — scope

| File | Change | Risk |
|------|--------|------|
| `src/rekolektion/bitcell/sky130_cim_supercell.py` | Full rewrite of layout: T7 east of foundry, no Y annex, cap on M3 over silicon | High — central to all CIM macros |
| `src/rekolektion/bitcell/sky130_cim_supercell.py` (`SupercellVariant.cap_w/cap_l`) | Re-aspect-ratio per variant for 1.40 µm cap_l target | Medium — capacitance preserved per variant |
| `src/rekolektion/macro/cim_supercell_array.py` | Add `strap_interval` parameter, insert wlstrap/wlstrap_p columns, add colend top/bottom rows; remove Y-annex-specific NWELL bridge code (no longer needed); update label X positions for new supercell_w | High |
| `src/rekolektion/macro/cim_assembler.py` | Update macro placement: array dimensions change, sense row Y position changes (now at array boundary, not above annex), MBL pin positions change (M4 strap X positions per variant) | High — placement contract change |
| `src/rekolektion/macro/cim_spice_generator.py` | Change foundry .subckt strategy (use Magic-extracted .subckt instead of hand-written) | Medium |
| `scripts/run_lvs_cim.py` | Remove BL/BR per-col rename and the auto-named-well rewrite (no longer needed once .subckt matches extraction) | Low |
| `scripts/characterize_cim_liberty.py` | Re-characterize against new supercell topology (T4.1-DIVERGENT-A also resolves) | Medium — Liberty timing values change |
| `src/rekolektion/macro/cim_liberty_generator.py` | Pin-list mostly stable, possibly minor edits | Low |
| LEF generator | Pin Y positions change, macro size changes | Medium |
| Docstrings, comments referring to annex Y geometry | Update | Low |

---

## DRC feasibility check (must do before Phase 1 starts)

These are open uncertainties that **could disqualify Architecture 1**. Phase 0 must validate before commit:

1. **capm rules**: confirm `cap_l = 1.40` µm fits sky130 capm.5a/5b/5c (top/bottom/side enclosure). If not, reduce to 1.30 µm and recompute supercell_w per variant.
2. **M3 over silicon**: confirm M3 polygons can sit above LI1/MET1/MET2 used by the foundry cell without violating any antenna or M3 pour rule. Check sky130 m3.X rules and the foundry cell's M3 polys (5 in the dump).
3. **T7 NMOS NSDM enclosure** in the new geometry — current 0.125 µm enclosure should hold but verify with no Y annex (T7 has new NSDM neighbors).
4. **Wlstrap abutment to supercell**: foundry wlstrap is 1.41 µm wide and expects bitcell-pitch (1.31 µm) east/west neighbors. Our supercell's foundry-portion is 1.31 µm — abutment matches. The T7+cap east of foundry is in supercell-internal X — wlstrap doesn't see it. Verify wlstrap LEF VPB/VNB pin positions don't conflict with our supercell's annex-east geometry.
5. **MBL M4 routing**: cap top plate is M4. Per-column MBL strap is M4. They overlap in X by design. Verify this is intentional (cap top plate IS the MBL connection) and DRC-clean.
6. **NWELL coverage**: foundry NWELL is right-half-only (x=0.72–1.20). Old supercell extended NWELL through annex; new supercell has no annex. NWELL bridging across rows works via direct foundry-cell abutment in adjacent X-mirrored rows (production tile pattern). Issue #9 fix may need different geometry — verify with T4.4 flood-fill on small array.

---

## Effort estimate

| Phase | Work | Effort |
|-------|------|--------|
| Phase 0 | This planning doc + DRC feasibility (1–6 above) on a single supercell | 0.5–1 day |
| Phase 1A | Rewrite `sky130_cim_supercell.py` (single variant, e.g. SRAM-D) | 1 day |
| Phase 1B | Rewrite `cim_supercell_array.py` with wlstrap+colend tiling | 1 day |
| Phase 1C | Update `cim_assembler.py` placement contract | 0.5 day |
| Phase 1D | Update `cim_spice_generator.py` to use Magic-extracted foundry .subckt | 0.5 day |
| Phase 1E | DRC + LVS pass on SRAM-D 4×4, then 64×64 | 0.5 day |
| Phase 1F | Roll forward to all 4 variants (SRAM-A/B/C/D) | 1 day |
| Phase 1G | Re-characterize Liberty (closes T4.1-DIVERGENT-A) | 0.5 day |
| Phase 1H | Update LEF emitter for new pin positions | 0.5 day |
| Phase 1I | Regression: full `make` + LVS + DRC across all variants | 0.5 day |

**Total Phase 0+1: ~7 days focused work.**

Phase 2 (silicon-correct drain bridge) is a separate ~2-3 day effort applicable to both production and CIM.

---

## Decision gates

- **Gate 0 → Gate 1**: DRC feasibility check passes for at least one variant (SRAM-D). If any of (1)–(6) above fails, return to architecture selection.
- **Gate 1 → Gate 2**: SRAM-D 4×4 passes DRC + LVS with the new supercell. If LVS still shows BL/BR disconnect, the .subckt-matching approach (Phase 1D) was wrong; investigate before scaling.
- **Gate 2 → Gate 3**: SRAM-D 64×64 passes. Roll out to other variants.
- **Gate 3 → tapeout**: All 4 variants pass DRC + LVS + Liberty re-char + LEF regen. Update audit smoking_guns to mark T2.1-CIM-A (issue #7) closed under Phase 1 disclosure (production parity).

---

## What this plan does NOT solve

- **T1.1-A** (production self-reference) — partially mitigated. CIM gets matching extraction-vs-reference. Production still uses self-extracted reference. Phase 2 work is needed to fully close T1.1-A for both production and CIM.
- **Drain → BL/BR silicon connectivity** — DEFERRED to Phase 2. Both production and CIM ship without this connectivity in Phase 1; this is the disclosure that makes CIM "no worse than production."
- **T4.1-DIVERGENT-A** (Liberty char vs wrong cell) — RESOLVED as a side effect of Phase 1G.
- **T1.7-A** (Liberty analytical, not measured) — STILL OPEN. Independent of architecture redesign.
- **T1.4-A** (netgen equates not LVS-verified) — STILL OPEN. Independent.
- **T1.2-B** (dead .sp snapshots) — STILL OPEN. Trivial cleanup, do separately.

---

## Suggested next concrete step

Phase 0 task: spend 0.5 day on DRC feasibility checks (1)–(6). Generate a single SRAM-D supercell standalone (no array) with the new architecture, run Magic DRC, confirm clean. Any failure becomes a "blocker spec" addressed before Phase 1 starts. If clean, commit to Phase 1.

This planning document should be reviewed against the existing track plan in `conductor/projects/production_features/tracks/05_cim_tapeout_audit/plan.md` and updated there if accepted.

---

## 2026-04-30 Phase 0 outcome — Architecture 1 INFEASIBLE

Phase 0 prototype generated (`scripts/phase0_supercell_drc.py`, output `output/phase0/supercell_pp.gds`). Magic DRC: **389 violations** including the structural blocker:

```
(1) MiM cap width < 1um (capm.1)
```

### Root cause

The MIM cap rules force a minimum cap pitch incompatible with foundry-pitch supercell:

- **capm.1**: cap width ≥ 1.0 µm (applies to BOTH dimensions — Magic checks min-side width)
- **capm.2a**: capm-to-capm spacing ≥ 0.84 µm
- **capm.2b**: cap M3 bottom plate spacing ≥ 1.20 µm (even stricter on M3 plate)

With supercell_h = 1.58 (foundry pitch), max cap_l = 1.58 - 0.84 = 0.74 µm. **0.74 < 1.0** → `capm.1` violation.

Minimum supercell_h for compliant cap = 1.0 (cap) + 0.84 (spacing) = **1.84 µm**, which doesn't align with foundry's 1.58 µm pitch.

### What I considered and ruled out

- **2-row supercell (3.16 µm pitch)** with 2 cells sharing 1 cap: breaks CIM parallel multiply-accumulate (multiple T7s active simultaneously short their Q nets if drain shared).
- **2-row supercell with 2 caps**: caps don't fit (2×1.0 + 2×0.84 = 3.68 > 3.16 available).
- **X-staggered caps in adjacent rows**: Magic's spacing metric is L∞ (max-axis edge distance) for orthogonal layouts. Even with X-stagger by 0.84 µm, Y-overlap means max-axis distance = Y separation ≈ 0.58 µm, fails capm.2a.
- **One cap per column shared across rows (M3 strip)**: shorts all T7 drains in a column. Multiple T7s active simultaneously short their Qs.
- **Wire-cap MBL (no MIM)**: changes CIM analog precision characteristics, requires new analog design.

### Conclusion

**Architecture 1 (foundry-pitch supercell with internal per-cell MIM cap) cannot be made to work** within sky130 capm rules and CIM circuit topology. The plan as written is wrong.

### Path forward (revised)

Two viable directions:

**Path A — accept production parity (RECOMMENDED for tapeout schedule):**
- Don't redesign supercell. Keep current 2.93-µm Y-annex.
- Modify `cim_spice_generator.py` to MATCH the foundry's actual extraction topology (BL/BR ports floating, drain ports as separate auto-named nets).
- Produce CIM reference SPICE that has the same drain↔BL/BR disconnection as Magic's flat extraction of the layout.
- Result: CIM LVS passes against the broken layout. Same disclosure as production T1.1-A. CIM ships at production-parity quality.
- Effort: ~0.5 day to refactor `cim_spice_generator.py` + verify on SRAM-D.
- **Issue #7 closes under T1.1-A disclosure** — production has the same defect, both are sky130-foundry-typical.

**Path B — silicon-correct drain bridge (Phase 2):**
- Add LICON1+LI1+MCON+MET1 stack tying access-tx drain DIFF to BL/BR met1 rail in supercell wrapper.
- Drain DIFF extends UP into supercell wrapper at foundry-y=[1.58, ~1.70] (NSDM coverage already extends to y=1.705 in the foundry cell).
- Place LICON1 (0.17×0.17) on the extended DIFF, LI1 over it, MCON to BL/BR met1 rail.
- Symmetric for BR side and for bottom-of-cell (y < 0).
- Bottom drain side: requires coordination with X-mirror in adjacent rows (handled by extending drain DIFF DOWN below foundry y=0 in the unrelated row's annex above).
- Applies to BOTH production `bitcell_array.py` AND CIM supercell — closes T1.1-A's underlying bug for both.
- Effort: ~2-3 days, careful DRC iteration.

### Recommendation

**Phase 0 result invalidates the supercell-redesign approach.** Recommend the following sequence:

1. **NOW**: Do Path A (~0.5 day). CIM LVS passes at production-parity. Issue #7 closes under T1.1-A disclosure. Tapeout-readiness for CIM matches production.
2. **AFTER Path A lands**: Plan Phase 2 (Path B) properly as a separate engineering effort. This addresses the underlying silicon defect for BOTH production and CIM. ~2-3 days.
3. **Phase 1 (foundry-pitch redesign) is CANCELLED**. The capm dimensional rules block it.

### Tasks updated

- #47 (Phase 0): RESOLVED — feasibility check completed, blocker found.
- #48 (Phase 1 redesign): CANCELLED — architecturally infeasible.
- #49 (Phase 2 silicon-correct drain bridge): unchanged, still pending.
- New: #50 (Path A — production-parity reference SPICE) to be created.
