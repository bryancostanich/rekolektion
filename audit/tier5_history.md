# Tier 5: Git / history forensics

Status: in progress (2026-04-30)
Method: `git log --grep` + `git log -- <file>` + read suspect commit messages and diffs

---

## T5.1 — Reference schematic edits

Every commit touching `*.spice` or `*.subckt.sp` reviewed. Classification by failure-mode-#3 risk:

| Commit | Date | Subject | Effect | Verdict |
|--------|------|---------|--------|---------|
| 154e970 | 2026-04-27 23:45 | rename bitcell VDD/VSS → VPWR/VGND | Edited cached `sky130_sram_6t_cim_lr` bitcell .subckt body to use VPWR/VGND. Dual-edited the layout label rename via `_flatten_gds`. | **MIXED** — both ref and layout aligned. Affected cell now dead (replaced by foundry-supercell). Historical pattern for current track was used here. |
| 0431f5f | 2026-04-27 23:18 | substitute auto-named well → VDD/VSS | Substituted `w_xx_yy#` tokens in cached LR-CIM bitcell .subckt with VDD/VSS to globally tie wells to supplies. | **🚨 FAILURE MODE #3** — reference rewritten to mask 1024 disconnected NWELL groups in the layout. Affected cell now dead but the technique was applied to a known layout flaw, not the layout fixed. |
| 011146b | 2026-04-27 22:27 | regenerate cached extracted subckts | Re-extracted peripheral cells from layout. | OK — cache refresh, layout is source. |
| 8376c64 | 2026-04-27 21:30 | replace custom mwl_driver with foundry buf_2 | Layout change + cache regen. | OK — both sides of pair changed coherently. |
| 61a38d3 | 2026-04-27 20:46 | feat(cim): add reference SPICE generator | Initial creation. | OK |
| 25af22d | 2026-04-22 22:06 | align cached foundry cell port orders to Magic native | Renamed port orders in cached foundry cells to match Magic's emit. Topology unchanged. Commit declares "no LVS impact — purely cosmetic". | OK — port-order rename, not topology edit. |
| d1f5d01 | 2026-04-22 20:52 | match DFF port order to Magic's native | Removed Q_N port from cached DFF (which the foundry body never connected). | OK — Q_N was extraneous; topology preserved. |
| 039a1f0 | 2026-04-22 20:20 | use Magic's native bitcell port order | Re-extracted bitcell raw, updated reference's instance call. **Commit message acknowledges remaining "shared rails leak through cell boundaries" mismatch** — exactly the WL_BOT fingerprint, not traced to silicon at the time. | OK port-order; **HISTORICAL DATA POINT** — the WL_BOT-class issue was visible in LVS output 5+ days before it was diagnosed. |
| 025687d | 2026-04-20 10:44 | commit cached foundry-cell extracted subckts | Initial cache. | OK |

### T5.1 finding — historical failure-mode-#3 patterns

Two commits (154e970, 0431f5f) edited reference subckts to mask layout flaws (well-merge gaps, supply-naming inconsistency). Both target the now-dead `sky130_sram_6t_cim_lr` bitcell — replaced by the foundry-cell supercell migration. **Not currently masking a bug** but documents the pattern was in active use.

---

## T5.2 — Verification setup edits

Active `_flatten_gds` mechanism in `scripts/run_lvs_cim.py:241-251`:

```python
# Rename all auto-named n-well nodes (`w_<n>_<n>#`) to VPWR.  The
# bitcell layout has 1024 disconnected n-well groups (NWELL gaps
# between mirrored row-pair boundaries prevent full merge), but
# the reference SPICE substitutes the same auto-named tokens to
# VDD which is already mapped to macro VPWR via cell port order.
# Renaming to VPWR ties them all to the same macro net for LVS.
...
new_text = re.sub(r"\bw_n?\d+_n?\d+#", "VPWR", text)
```

### T5.2 finding — P0 active failure-mode-#3

**This rename is currently active in CIM LVS and is exactly failure mode #2 + #3 from `trust_audit.md`:**

- Mode #2 (label-merge): the layout has 1024 physically-disconnected n-well fragments. They share a name (VPB → VPWR via netgen equate) but not metal.
- Mode #3 (reference edited to match layout): the LVS pipeline post-processes the extracted SPICE so all 1024 fragments collapse to one global VPWR net. LVS then passes the n-well biasing check.

The bug it conceals: **1024 cell n-wells may not be physically connected to VPWR via metal**. PMOS body bias on silicon would be undefined for any cell whose n-well doesn't reach a tap. Latch-up risk; potentially non-functional devices.

**Severity: P0 silicon-killer for CIM macros.** Same class as the BL/BR floating-drain bug (issue #7) but on body-bias instead of signal connectivity, and with broader impact (every cell, not just access-tx drains).

**Verification needed:** Tier 4.4 flood-fill on each n-well fragment to confirm metal path to a VPWR tap. If any fragment is floating, this is silicon-blocking.

---

## T5.3 — "LVS clean" / "DRC clean" claim audit

Commits with "LVS clean" / "match" / "PASS" in subject:

| Commit | Date | Claim | What was actually verified |
|--------|------|-------|----------------------------|
| 41c718e | 2026-04-29 10:54 | rebuild LR bitcell layout to match schematic — strict LVS clean | Bitcell-only LVS via `run_lvs_bitcell.py lr`. Hand-written reference. **Re-verifies trust** (this is the right direction — fix layout to schematic, not reverse). |
| a36e7f3 | 2026-04-29 11:14 | rebuild CIM bitcell wiring + LR M2 jumper for strict LVS | Bitcell-only LVS. Same. |
| 48d8bde | 2026-04-28 20:49 | docs(cim): all 4 variants now LVS match-unique (SRAM-A/B finished) | CIM macro LVS via `run_lvs_cim.py`. **Stale claim** — uses the well-rename rewrite that today's audit just flagged P0. The "match-unique" was real but predicated on the failure-mode-#3 rewrite. |
| 48286fe | 2026-04-28 15:12 | SRAM-C/D LVS clean | Same — predicated on the rewrite. |
| 4dc68e3 | 2026-04-27 18:30 | close out remaining LVS net deltas | A real layout fix (s_en cross-merge) + reference VSUBS→VGND alignment. Layout fix was substantive. |
| ade963b | 2026-04-23 09:35 | wl_driver: bridge NAND3 VDD port to external VPWR rail | Real layout bridge. OK. |
| b09c441 | 2026-04-29 23:16 | F11+F13+F12 connectivity fixes | This session's WL/BL/BR strip + parent-strip work. Trust audit re-verified F11 at T2.1 ✅; F13 verified for production but found broken for CIM (issue #7). |

### T5.3 finding — stale "clean" claims

The 2026-04-28 "LVS match-unique" claims for CIM SRAM-A/B/C/D (commits 48d8bde, 48286fe) **rely on the active well-rename rewrite** (T5.2 P0 finding). These claims do not represent silicon-correct connectivity for n-well bias; they represent textual netlist match after rewrite.

---

## T5.4 — Cross-reference issue audit

Open `bug` issues:
- **#7 (filed today)** P0 CIM SRAM-D BL/BR floating. From this audit's T2.1.

Other issues (#4, #5, #6) are functional/architectural improvements, not LVS/DRC clean claims. No closed-without-evidence issues found that would mask another false positive.

---

## Tier 5 status: closed with one new P0 finding

**P0 surfaced**: T5.2-A — CIM LVS actively rewrites extracted SPICE to mask 1024 floating n-well fragments.

**Pattern findings**:
- Failure-mode-#3 was historically applied to dead bitcell subckts (LR-CIM → supercell migration retired them); not currently masking a bug there.
- Failure-mode-#3 IS currently masking a P0 well-merge issue in the live CIM LVS path.
- "LVS clean" claims for CIM macros (SRAM-A/B/C/D) on 2026-04-28 are **not silicon-correctness claims** — they're net-name match claims after rewrite. Need re-verification post-fix.

Tier 5 closes once T5.2-A has a written disposition (issue or waiver).
