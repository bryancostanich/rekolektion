# Hack inventory — Phase 0 of cleanup audit

**Generated:** 2026-05-03

**Scope:** every script/module in the verify/extract/generator path that rewrites, strips, relabels, equates, or filters tool output to make a downstream tool pass. Each entry distinguishes between **legitimate workaround** (Magic/netgen has a known limitation, our compensation is documented and correct) and **hack** (we're papering over a real layout/source defect).

**Method:** static read of `scripts/`, `src/rekolektion/macro/`, `src/rekolektion/verify/`, `src/rekolektion/peripherals/`, all `audit/*.md`, and the LEF/GDS for one CIM macro variant. Confirmed live behavior on `cim_sram_d_64x64`.

**Convention:** ✗ = papers over a real defect; ⚠ = compensates for a real tool limitation but is structurally suspicious / under-verified; ✓ = legitimate naming/aliasing only, no silicon implication.

---

## Category A — LVS port-list rewrites

### A1. CIM ref-port aligner ⚠ (revised 2026-05-03)

- **Location:** `scripts/run_lvs_cim.py:54-78` (`_align_ref_ports`), called at `:126`
- **What it does:** rewrites the reference SPICE's `.subckt cim_sram_*` port list to whatever Magic extracted, dropping any port that's only in the reference.
- **Initial Phase 0 hypothesis (WRONG):** "missing MWL_EN labels in the layout — fix by adding labels."
- **Verified 2026-05-03:** the labels ARE in the GDS — 64 `MWL_EN[r]` labels on layer 67/5 (li1 label) inside `cim_mwl_driver_col_64`. Standalone Magic extract on that cell finds and promotes all 64 as ports. The macro-top hierarchical extract drops them.
- **Real mechanism:** Magic ext2spice port-promotion through hierarchy is broken for this layout pattern. Same limitation hit in commit `b09c441` (F12) for production `addr[i]` pins — the F12 commit explicitly noted "regardless of .pin shape placement." F12 tried flat extraction; reverted in `a97f56f` because flat hides issue #7 per-cell drain floats.
- **Reclassified ✗→⚠:** the aligner is a Magic-tooling workaround, not a silicon-defect cover. Silicon is electrically correct (verify via task #110 flood-fill, not via end-to-end LVS port match). The risk is that any tool-limitation workaround can drift into hiding real silicon defects if scope expands silently — needs hard guards (whitelist exactly which ports may be dropped, fail on anything else) and a written waiver framing the limitation correctly.
- **Tracked as:** #64 (the long-standing pin-resolver task), now superseding #103.

### A2. Production ref-port aligner ✗

- **Location:** `scripts/run_lvs_production.py:129-159` (`_align_ref_ports`), called at `:235`
- **What it does:** same pattern as A1, applied to production macros.
- **Phase 0 verification (2026-05-03):** the GDS *does* contain top-level labels (`addr[0..8]`, `din[*]`, `dout[*]`, `clk`, `cs`, `we`, etc.) — confirmed by `strings`. So unlike CIM, production isn't suffering from missing-label syndrome. The aligner is masking a different problem: Magic's `.ext` reports 222 boundary ports while netgen's final summary reports `661 = 661 nets` with **"9 disconnected pins" in extracted vs "2 disconnected pins" in reference** and "Top level cell failed pin matching." So the aligner is hiding ~12 ports that Magic finds at the boundary as named-but-not-electrically-connected to internal logic — these are the "11 disconnects" referred to in the source comment. They could be real floating boundary pins (silicon defect: physical metal exists but no internal route reaches it) OR Magic ext quirks for sub-cell promoted addr rails.
- **Real issue:** the "manual port verification at SoC integration substitutes for failed top-level match" deferment in `signoff_gate.md:114` is a deferred check, not a verification. Need to enumerate the specific 9-vs-2 disconnected pins and confirm whether each is a real metal-route gap or a Magic extraction artifact. Most likely a mix.
- **Tracked as:** new task to be filed (Phase 1).

### A3. Foundry stdcell label strip in production GDS flatten ⚠

- **Location:** `scripts/run_lvs_production.py:60-101` (`_STRIP` dict + flatten loop)
- **What it does:** before flattening the macro for LVS, removes named labels (`A`, `X`, `Y`, `BL`, `BR`, etc.) from foundry standard cells (`sky130_fd_sc_hd__buf_*`, `inv_*`, `nand*_dec`, `write_driver`, `sense_amp`, `dff`).
- **Why it exists:** stdcell labels would N×collapse on flatten if not stripped. This is real and necessary — without it, every `A` pin from 16384 nand decoder cells would merge into one global net.
- **Why it's ⚠ not ✓:** `_LEFT_INTERNAL` warning at `:94-101` prints a warning if foundry-internal `BL`/`BR`/`WL` labels survive but **does not fail** the run. Easy to miss. Needs to be a hard-fail.
- **Real issue:** none on the strip itself; strengthen the warning into a SystemExit.

### A4. CIM extra_flatten_cells ⚠

- **Location:** `scripts/run_lvs_cim.py:140-145`, passed to `run_lvs(..., extra_flatten_cells=...)`
- **What it does:** flattens `cim_array_*`, `cim_mbl_precharge_row_*`, `cim_mbl_sense_row_*`, `cim_mwl_driver_col_*` on both circuit sides before netgen comparison.
- **Why it exists:** stated reason is netgen's instance-prefix naming; without flatten, layout-side `cim_array.../bl_0_<c>` doesn't match reference-side `bl_0_<c>` when one side flattens and the other doesn't.
- **Why it's ⚠:** flatten loses hierarchical sub-circuit isolation, which means a real disconnect *inside* `cim_array` could be hidden by global name merge after flatten. T2.1-CIM-A (#7, BL/BR drains floating) is the canonical example of how this can hide silicon defects.
- **Real issue:** verify post-flatten net counts match the un-flattened reference's nets exactly — if not, hidden disconnects exist.

---

## Category B — Reference-SPICE port substitution (stronger than alignment)

### B1. CIM cell `_PORT_LIST` substitution ✗

- **Location:** `scripts/extract_cim_subckts.py:65-80` (`_PORT_LIST` dict), `:81-119` (`_patch_subckt_ports`)
- **What it does:** for the CIM peripheral cells (precharge, sense, bitcell), throws away the port list Magic extracted and **substitutes a hardcoded canonical list**. Comment line ~70-80 admits: "Magic's extraction reliably promotes ports labeled on met layers, but poly/li1 ports (gate inputs, gate outputs labelled on li1) routinely fall off the .subckt port list even with a .pin shape. We patch the declaration post-extraction with the canonical port list we know each cell exposes."
- **What it papers over:** Magic's ext2spice promoting poly/li1 labeled ports unreliably. But the workaround declares ports that may not be labeled in the layout — same root issue as A1, applied earlier in the pipeline.
- **Real issue:** poly/li1 labels need to be on a label/text purpose layer ext2spice will promote (typically `.pin` purpose 16). If they're already there and ext2spice still drops them, that's a Magic bug to file upstream.
- **Tracked as:** new task to be filed. Closely related to A1.

### B2. CIM cell label rewrites (well/substrate/supply) ⚠

- **Location:** `scripts/extract_cim_subckts.py:108-117` (inside `_patch_subckt_ports`, only for `sky130_sram_6t_cim_lr`)
- **What it does:**
  - `re.sub(r"\bw_\d+_n?\d+#", "VPWR", ln)` — rewrites Magic's auto-generated well-fragment names (e.g. `w_85_n12#`) to `VPWR`.
  - `replace("VSUBS", "VGND")` — substrate to ground.
  - `re.sub(r"\bVDD\b", "VPWR", ln)` and `re.sub(r"\bVSS\b", "VGND", ln)` — bitcell supply rename.
- **Why it exists:** the cached LR-CIM bitcell extract uses `VDD`/`VSS` while the macro-level uses `VPWR`/`VGND`. Without rename the bitcell's body terminals don't match the macro supply names.
- **Why it's ⚠:** Note this is the **legacy LR-CIM cell only** — the production CIM macros now use `sky130_cim_supercell` (foundry-based). The cached LR-CIM extract is part of the dead/snapshot path (T1.2-B P2, deferred deletion). This rewrite is harmless if the LR-CIM extract is truly dead — but it remains executable code and would matter if anyone re-enables that path.
- **Real issue:** LR-CIM extract should be deleted (T1.2-B). Then this rewrite goes away.

### B3. Production hand-written subckts ⚠

- **Location:** `src/rekolektion/macro/spice_generator.py:89, 113-114` — comments mark `BitcellArray`, `PrechargeRow`, `ColumnMuxRow` as T1.1-A hand-written.
- **What it does:** the production reference SPICE for these three subckts is hand-built by Python code, not Magic-extracted from the layout. Was changed from self-extracting (T1.1-A P0) to hand-written in tasks #81–#84.
- **Why it exists:** original T1.1-A finding noted self-extracting refspice = self-reference comparison (LVS proves layout self-consistent, not layout-matches-intent). Hand-writing was the cleaner alternative.
- **Why it's ⚠:** the refspice now reflects what a Python author *thinks* the layout should be, not what's actually there. If the assembler diverges from the spice generator, LVS catches divergence — that's good. But the *intent* in the hand-written `.subckt` was never independently audited against the layout. T2.1 production verdict (787=787, 661=661) confirms topology consistency, but doesn't confirm hand-written intent matches design specification.
- **Real issue:** add a line-by-line cross-check between the hand-written `.subckt` ports / device list and the per-cell intent docs in `audit/intent/*.md`. Currently `audit/intent/` only covers CIM cells — production cells have no intent docs.

---

## Category C — Substrate / supply name aliasing

### C1. VSUBS→VSS textual rename in extracted SPICE ⚠

- **Location:** `src/rekolektion/verify/lvs.py:231-235`
- **What it does:** `ext_text.replace(" VSUBS ", " VSS ")` and `replace(" VSUBS\n", " VSS\n")` on the Magic-extracted SPICE before handing to netgen.
- **Why it exists:** standalone bitcell schematic uses VSS as NMOS body; Magic names the substrate VSUBS; netgen's `equate nets` is unreliable in batch mode for this specific alias.
- **Why it's ⚠:** textual rewrite of an extracted file is precisely the kind of thing to be skeptical of, even if "purely an unconnected substrate net." If a new test gets added that expects to see VSUBS in the extract, this rewrite silently breaks it.
- **Real issue:** preferable to use netgen's `equate nets` correctly, or include the alias in the wrapper setup.tcl. If batch mode genuinely doesn't honor it, that's a netgen bug to file.

### C2. netgen `_equate_pairs` aliasing ⚠

- **Location:** `src/rekolektion/verify/lvs.py:297-308` (defined), `:309-311` (emitted to wrapper setup.tcl).
- **Pairs equated:** `VPB↔VPWR`, `VNB↔VGND`, `VSUBS↔VGND`, `VDD↔VPWR`, `VSS↔VGND`, `VSUBS↔VSS`.
- **What it does:** before LVS comparison, tells netgen these net pairs should be considered the same.
- **Tier 1 audit (T1.4)** already RED-FLAGGED these:
  - `VPB↔VPWR`, `VNB↔VGND` — body-bias to supply, legitimate at chip top, but verify physical metal continuity (T2.1 must verify).
  - `VDD↔VPWR`, `VSS↔VGND` — schematic/layout supply name inconsistency, suspicious.
  - `VSUBS↔VGND` — substrate to ground, legitimate.
- **Why it's still ⚠:** the post-2026-05-03 sign-off claims T2.1 flood-fill verified these (T1.4-A "Validated by Tier 2 flood-fill" per `signoff_gate.md:110`). But the flood-fill was done WITH the equates active — circular. A clean check would be: drop the equates, confirm LVS still passes, then re-add only the ones netgen genuinely needs as global aliases.
- **Real issue:** schematic and layout should agree on supply names from the start. `VDD↔VPWR` aliasing means the schematic uses one convention and the layout another.

### C3. Foundry stdcell flatten list ⚠

- **Location:** `src/rekolektion/verify/lvs.py:256-274` (`flatten_cells`).
- **What it does:** before LVS, flattens `sky130_fd_sc_hd__fill_*`, `decap_*`, `tapvpwrvgnd_1`, `diode_2`, `clkbuf_4`, `buf_2` on both circuit sides.
- **Why it exists:** OpenLane P&R inserts purely-physical cells (fill, decap, tap, etc.) that the Python-generated reference doesn't instantiate. Without flatten, extracted side has hundreds of extra instances.
- **Why it's ⚠:** flattening `buf_2` (a real logic cell, not a fill) is suspicious. If the Python generator doesn't instantiate buf_2 anywhere but the layout does, that's intent vs. layout drift, not "purely physical." Need to confirm buf_2 only appears in the layout via OpenLane buffering or similar physical insertion.
- **Real issue:** audit each entry on the flatten list — is it truly purely-physical or is it a logic cell whose presence indicates intent drift?

---

## Category D — Layout-side label games

### D1. Foundry-cell internal label strip in `cim_supercell_array` ⚠

- **Location:** `src/rekolektion/macro/cim_supercell_array.py:5-6` (docstring), implementation throughout (~`_add_wl_strips`, `_add_mwl_strips`, etc.)
- **What it does:** strips foundry cell's internal "WL" label so per-row labels win during flatten.
- **Why it exists:** foundry SRAM bitcell labels its WL internally; without stripping, all 4096 cells' WL labels merge into one global net post-flatten — masking real disconnects (the FT8b finding).
- **Why it's ⚠:** historically this strip-and-relabel pattern is exactly what hid issue #7 (BL/BR drains floating). The pattern is "strip the global label so per-row labels can be added." Per-row labels then need to actually reach every cell's internal node (LICON1 → LI1 → MET1 → LABEL chain). The Phase 2 drain bridge fix (#49) closed this for BL/BR; verify equivalent closure exists for WL, MWL, MBL.
- **Real issue:** flood-fill verification per net (the audit anti-pattern rule lines 84-91 of signoff_gate.md). Has been done for some nets but should be re-verified now that we know `MWL_EN[r]` boundary labels are missing.

### D2. `_lib.remove(c)` post-build cell removal ✓

- **Location:** `src/rekolektion/macro/cim_assembler.py:702-704` (and similar pattern in non-CIM assembler)
- **What it does:** after building the macro library, removes all cells from the output GDS except the top cell (and selected sub-cells the LVS flow needs).
- **Why it exists:** keeps the shipped GDS minimal — only the top cell and its essential hierarchy.
- **Phase 0 verification (2026-05-03):** ran `gdstk.read_gds` over all 6 production+CIM GDSes plus the CIM flat GDS and verified every cell reference resolves to an existing cell. **No broken references.** Upgrading from ⚠ to ✓.

### D3. Missing `MWL_EN[r]` labels (the canonical case) ✗

- **Location:** `cim_mwl_driver_col_64` cell in shipped GDS — no labels matching `MWL_EN*` anywhere in the cell hierarchy.
- **What's missing:** text labels on the met2 pin polygons that the LEF generator advertises. Met2 geometry exists at the right coordinates; only the labels are absent.
- **Why this matters:** Magic ext2spice can't promote unlabeled polygons. Labels are missing → ports drop → LVS aligner papers it over (A1).
- **Real issue:** layout generator doesn't emit `LABEL` records on the met2 boundary shapes. Fix in `cim_supercell_array.py` or wherever the driver column emits its left-edge pin geometry.
- **Tracked as:** task #103.

---

## Category E — DRC filtering

### E1. Global rule-ID waiver filter ⚠

- **Location:** `src/rekolektion/verify/drc.py:_KNOWN_WAIVER_RULES` (line 28-) and `:_is_waiver` (line 206)
- **What it does:** classifies any DRC error matching a known foundry-SRAM waiver rule ID as "waiver" rather than "real," globally — without checking whether the violating tile is actually inside a foundry-cell footprint.
- **Why it exists:** foundry SRAM bitcell shipped from sky130 is dense and triggers known met1.x / li1.x / via.1a violations under COREID. Foundry has waivers for these *inside* the bitcell.
- **Why it's ⚠:** comment lines 22-26 explicitly admits this caveat: "this is a global filter, not a spatial one. A met1.2 or met1.1 violation outside the bitcell COREID region would be silently waived." A spatial filter exists in `scripts/audit_drc_waivers.py` but the default `run_drc` flow uses the global filter.
- **Real issue:** make spatial filtering the default; the global filter should only fire when no spatial bbox info is available.

### E2. DRC parser regex — historical bug, fixed ✓

- **Location:** `src/rekolektion/verify/drc.py:406` (current code) — `tile_re = re.compile(r"^\s*at:\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s*$")`
- **History:** prior session's `(\d+)` regex silently dropped negative-coord tiles. Fix #6 (commit `c3020db`) repaired it. ~640 via.1a tiles in production were hidden by the bug; the prior "DRC clean" claim for production was partially false.
- **Status:** ✓ fixed; mentioned only because Tier 1 audit didn't catch the original bug.

---

## Category F — PDK model mismatches

### F1. Foundry SRAM bitcell calls generic 1V8 instead of `__special_*` ✗

- **Location:** Magic-extracted SPICE for `sky130_fd_bd_sram__sram_sp_cell_opt1_qtap` references `sky130_fd_pr__nfet_01v8` and `sky130_fd_pr__pfet_01v8_hvt`.
- **What's wrong:** the SKY130 PDK ships dedicated narrow-W models for SRAM bitcell transistors:
  - `sky130_fd_pr__special_nfet_pass` (access transistor)
  - `sky130_fd_pr__special_nfet_latch` (pull-down)
  - `sky130_fd_pr__special_pfet_latch` (pull-up)
- The generic `__nfet_01v8` / `__pfet_01v8_hvt` models have `wmin = 5 µm` in every bin. The bitcell's actual W is 140 nm — orders of magnitude below any bin. ngspice binsearch fails.
- **Why this matters:** blocks ngspice running on any extract that contains the foundry bitcell — i.e. blocks all SPICE Liberty re-characterization (#24). Also probably means the LVS-side SPICE has wrong model references for parametric extraction (RC, area).
- **Root cause:** Magic device-class mapping during foundry-bitcell extract emits the wrong subckt name. Either Magic's extract style for SKY130 was misconfigured, or the bitcell GDS lacks the device-class hint Magic needs to pick the special model.
- **Tracked as:** task #101.

---

## Category G — Stale documentation

### G1. `audit/smoking_guns.md` shows P0s as OPEN that signoff_gate says are RESOLVED

- **Location:** `audit/smoking_guns.md` "Tracking summary" section — lists 4 P0s OPEN.
- **`audit/signoff_gate.md`:97-105** — "ALL FIVE ORIGINAL P0s ARE RESOLVED" table.
- **What this means:** the two audit files disagree. signoff_gate.md is newer and reflects 2026-05-03 state; smoking_guns.md still describes pre-resolution state.
- **Real issue:** smoking_guns.md needs updating to reflect resolution (or annotation pointing to signoff_gate.md). Confusing on read; reduces audit credibility.

### G2. `audit/tier5_history.md` verdict GREEN despite known aligner-class workarounds

- **Location:** `audit/tier5_history.md:126`
- **What it says:** "No active failure-mode-#3 patterns in the current session's history."
- **What's wrong:** "failure-mode-#3" was specifically the well-rename rewrite, which T5.2-A documented. The aligner pattern (A1, A2 in this doc) is **the same class of failure** — textual rewrite to mask Magic-side issues — but Tier 5 didn't flag it because the audit was scoped narrowly to the rewrite-text pattern, not to "any rewrite of tool output."
- **Real issue:** Tier 5 needs a broader scope. New audit tier T6 ("verify no scripts rewrite tool output between tool and audit") proposed in the cleanup plan addresses this.

---

## Summary

**Total entries:** 16 (post-revision) — F1 (✗); A1, A2, A3, A4, B1, B2, B3, C1, C2, C3, D1, D3, E1 (⚠); D2, E2 (✓); G1, G2 (doc).
- ✗ confirmed silicon-defect: **1** (F1 — foundry bitcell wrong PDK model).
- ⚠ tool-limitation workaround / structural concern needing verification: **13**
- ✓ legitimate / verified-OK: 2 (D2, E2)
- doc drift: 2 (G1, G2)

**Revision note (2026-05-03):** A1, A2, D3 were initially classified ✗ as silicon-defect coverers. Verification revealed they are all the same Magic ext2spice port-promotion-through-hierarchy limitation — the labels exist correctly in the GDS but Magic doesn't propagate child-cell ports up to the macro top. This was already established in commit `b09c441` (F12) and reverted attempt `a97f56f`. The aligners are Magic-tooling workarounds, not silicon-defect covers. They become silicon-relevant only if their scope drifts to cover real disconnects; needs hard guards. Silicon correctness must be confirmed independently via flood-fill (task #110), not via end-to-end LVS port match.

**The deeper-than-expected finding:**

The `_align_ref_ports` function exists in **both** `run_lvs_cim.py` and `run_lvs_production.py`. The CIM side papers over a real labeling gap (missing `MWL_EN[r]` labels — task #103). The production side papers over a different mechanism: 9 vs 2 disconnected boundary pins reported by netgen with "Top level cell failed pin matching" still printed even *after* alignment. The `signoff_gate.md` "manual verification at SoC integration substitutes" deferment is a punt, not a verification.

**Phase 0 closes:** every script in scope has been read end-to-end. Magic dump test confirmed CIM has missing labels (D3, ✗) and production has labels-but-disconnected-pins (A2, ✗ different mechanism). gdstk reference-resolution check verified D2 has no broken refs. No new entries from a second-pass scan.

**Resolved during Phase 0:**
- D2 verified clean.
- A2 mechanism characterized (was unverified at start; now known to be top-level pin-matching failure with 9 vs 2 disconnected pins, not missing labels).

**Phase 1 inputs:**

Each ✗ and ⚠ entry above needs a Phase 1 root-cause fix. Mapping to tasks:
- A1, D3 → task #103 (existing)
- F1 → task #101 (existing)
- A2 → new task: enumerate the 9-vs-2 disconnected pins on production weight_bank/activation_bank, classify each as silicon-defect vs Magic-extraction-artifact, fix or document accordingly.
- B1 → new task: re-investigate Magic ext2spice poly/li1 port promotion on CIM peripheral cells; fix the labels or file an upstream Magic bug; remove `_PORT_LIST` substitution.
- B3 → new task: cross-check hand-written T1.1-A subckts against per-cell intent docs; add intent docs for production cells (currently only CIM has them in `audit/intent/`).
- C1 → new task: replace `VSUBS→VSS` textual rewrite with proper netgen wrapper-setup `equate nets`.
- C2 → new task: drop `_equate_pairs` one at a time, confirm LVS still passes per-pair; only retain the ones genuinely needed as global aliases.
- C3 → new task: audit the foundry-stdcell flatten list; confirm `buf_2` flatten reflects OpenLane buffer insertion (or remove if it's an intent-drift cover).
- D1 → new task: post-fix flood-fill on WL, MWL, MBL nets to confirm strip-and-relabel didn't hide disconnects (BL/BR were closed by Phase 2 drain bridge — same pattern needs re-verification for the other per-row/col nets).
- E1 → new task: make spatial waiver filter the default in `verify/drc.py`; demote global rule-id filter to fallback only.
- A3 → small fix: change `_LEFT_INTERNAL` warning at `run_lvs_production.py:94-101` to `SystemExit` so it can't be missed.
- A4 → covered by D1 (same flood-fill verification scope).
- B2 → covered by P2 cleanup (delete LR-CIM cache, `T1.2-B`).
- G1 → small task: reconcile `smoking_guns.md` against `signoff_gate.md` 2026-05-03 update.
- G2 → broaden Tier 5 scope in the planned audit redo (Phase 5).
