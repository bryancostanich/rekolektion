# Phase 2 plan — workaround removal

**Date:** 2026-05-03 (drafted at Phase 1 closure)

After Phase 1 (hack hunt + harden every aligner with allow-list + fail-loud), Phase 2 evaluates which workarounds can be dropped entirely now that we have:
- Independent flood-fill evidence of silicon connectivity ([`audit/flood_fill_2026-05-03.md`](flood_fill_2026-05-03.md))
- All five P0s resolved with task-traceable evidence ([`signoff_gate.md`](signoff_gate.md))
- Hardened workarounds that fail loud on unexpected drift

The Phase 0 anti-pattern rule (`signoff_gate.md:84-91`): **a workaround that's load-bearing on tooling limitations stays; a workaround that papers over silicon drift is dropped.**

## Workaround disposition table

| ID | Workaround | Phase 1 state | Phase 2 disposition |
|----|-----------|---------------|----------------------|
| **A1** | CIM `_align_ref_ports` | Hardened with allow-list (`MWL_EN[r]` drop, `MBL_<c>` add). Fails on anything else. | **KEEP.** Tool-limitation workaround for Magic ext2spice port-promotion through hierarchy (#64). Silicon is correct (per #110); the aligner just reconciles the netgen view. Write a one-line waiver-style disclosure in `nwell_bias_disclosure.md` adjacent — or a separate `lvs_pin_resolver_disclosure.md`. |
| **A2** | Production `_align_ref_ports` | Hardened with allow-list (`dec_out_<N>` add, no drops permitted). Fails on anything else. Disconnected pins identified as `addr[0..6]` (same Magic limitation as A1). | **KEEP.** Same class as A1; same disclosure scope. |
| **A3** | `_LEFT_INTERNAL` warning | Promoted to `SystemExit`. Now fails the run loud if foundry-internal labels survive flatten. | **CLOSED.** Not a workaround anymore — a hard correctness check. |
| **A4** | CIM `extra_flatten_cells` (sub-cell flatten before LVS) | Documented inline; verified by flood-fill that flatten doesn't hide disconnects. | **KEEP.** Necessary for netgen instance-prefix alignment. Already documented inline in `run_lvs_cim.py:140`. |
| **B1** | `_PORT_LIST` hardcoded substitution in `extract_cim_subckts.py` | Hardened with `_check_patch_drift` allow-list. Fails on unexpected ports. | **KEEP for now.** Same Magic ext2spice port-promotion limitation. Underlying fix would be Magic tech-file work outside our scope. |
| **B2** | LR-CIM well/supply rewrites (only fires for the legacy `sky130_sram_6t_cim_lr` cell, not production) | Inline-documented as legacy path. | **DROP** — but only after T1.2-B (P2 cleanup: delete the dead LR-CIM cache). Track as Phase 2.B1. |
| **B3** | Hand-written T1.1-A subckts (PrechargeRow / ColumnMuxRow / BitcellArray) | Per-cell intent docs added in `audit/intent/`. Hand-written body cross-checks against intent docs are clean. | **KEEP.** Hand-written WAS the resolution to T1.1-A self-reference; intent docs make it auditable. |
| **C1** | `VSUBS→VSS` textual rewrite | **REMOVED.** netgen `equate VSUBS VSS` does the same thing without textual editing of tool output. | **CLOSED.** |
| **C2** | netgen `_equate_pairs` ledger | Each pair documented inline with its purpose. | **KEEP.** All 6 entries are naming-convention bridges or supply-equate (chip-level conventions); none mask physical isolation that wasn't already covered by `nwell_bias_disclosure.md`. |
| **C3** | Foundry-stdcell flatten list | All 17 entries audited inline (16 purely-physical + buf_2 for OpenLane-buffer reconciliation). | **KEEP.** Verified-legitimate; documented for future readers. |
| **D1** | `cim_supercell_array` strip-and-relabel pattern | Verified by flood-fill (per-row WL/MWL/MBL/VPWR/VGND fan out correctly across all extracted CIM variants). | **KEEP.** Necessary for the per-row label override pattern (FT8b mechanism). Already documented in cim_supercell_array.py:5-6. |
| **D2** | `_lib.remove(c)` post-build cell stripping | Verified all GDSes resolve cell references. | **KEEP** (now ✓-classified). |
| **D3** | "MWL_EN[r] missing labels" — turned out to be Magic limitation, not missing labels | Re-classified A1's mechanism. | **CLOSED** (was a misdiagnosis; subsumed by A1). |
| **E1** | DRC global rule-id filter | Demoted to opt-in via `allow_global_waivers=True`. Spatial filter is now default. | **CLOSED** as a default-behavior workaround. Legacy callers (`verify_macro.py`) explicitly opt in. |
| **E2** | DRC parser regex (negative-coord bug) | Fixed in commit `c3020db`. | **CLOSED.** |
| **F1** | Foundry bitcell wrong PDK model | **RESOLVED** in #101 by switching to `__special_*` models. ngspice now resolves all 8 devices. | **CLOSED.** |
| **G1** | smoking_guns vs signoff_gate disagreement | Reconciled. | **CLOSED.** |
| **G2** | Tier 5 history audit narrow scope | To be addressed in Phase 5 (trust-audit redo with broader T6 — verify no scripts rewrite tool output between tool and audit). | Carried forward to Phase 5. |

## Phase 2.B1 — drop the LR-CIM dead-path rewrites

The `extract_cim_subckts.py:_patch_subckt_ports` body still contains:
```python
if cell_name == "sky130_sram_6t_cim_lr":
    ln = _re.sub(r"\bw_\d+_n?\d+#", "VPWR", ln)
    ln = ln.replace("VSUBS", "VGND")
    ln = _re.sub(r"\bVDD\b", "VPWR", ln)
    ln = _re.sub(r"\bVSS\b", "VGND", ln)
```

Only fires for the legacy LR-CIM cell, which is the T1.2-B P2 cleanup target. Sequence:
1. Delete `src/rekolektion/peripherals/cells/extracted_subckt/sky130_sram_6t_cim_lr_*.subckt.sp` (4 files).
2. Verify no live import path references those cells (already noted by audit T1.2-B as deferable).
3. Drop the LR-CIM branch in `_patch_subckt_ports`.
4. Drop the `_PORT_LIST["sky130_sram_6t_cim_lr"]` entry.

## Phase 2.A1 — write a "Magic LVS pin-resolver" disclosure

The `_align_ref_ports` aligners (CIM + production) and the `_PORT_LIST` substitution all paper over the same Magic ext2spice port-promotion-through-hierarchy limitation. They're documented inline + in `audit/hack_inventory.md`, but the user-facing disclosure is implicit (only mentioned in `signoff_gate.md:114`).

A short waiver-style document, modeled on `docs/waivers/nwell_bias_disclosure.md`, would:
- Disclose the Magic limitation to chip-integration consumers
- List the specific port name patterns that the aligners absorb (the documented allow-lists)
- Confirm silicon correctness via the flood-fill evidence
- Note that LVS at the macro top doesn't pin-match without alignment

Suggested filename: `docs/waivers/lvs_pin_resolver_disclosure.md`. Same sign-off block convention as the existing waivers.

## Phase 2 close criteria

Phase 2 closes when:
1. Phase 2.B1 (LR-CIM dead path) is done (small, mechanical).
2. Phase 2.A1 (pin-resolver disclosure) is drafted and added to `docs/waivers/`.
3. Every workaround in the inventory has either been DROPPED, CLOSED, or KEPT-with-explicit-disclosure.

## Risks

- The hardened aligners are guarded with allow-lists. If a future macro variant adds a new port pattern (e.g. a new debug strap), the aligner will fail loud — that's correct, but it'll require updating the allow-list. Document the update procedure in the aligner's docstring + audit/hack_inventory.md so future-you doesn't add a new pattern unknowingly.
- Dropping the LR-CIM dead path is mechanical but verify by `git grep` that no live code references the cell name.

## Discovered during Phase 2 — out-of-scope but worth recording

While investigating #58 (functional SPICE sim of post-Option-B supercell), discovered that the **Magic-extracted foundry SRAM bitcell SPICE has a topology defect**: cross-coupled inverter halves don't merge at the SPICE level because of phantom-parasitic transistors (X1, X4 with L=0.025nm, extracted from poly-overlap geometry) splitting the LI1-shared diff regions into separate auto-named nets. Q never latches in ngspice. Affects BOTH production and CIM (the foundry cell is the storage substrate for everything in this codebase).

Implications:
- **#101's model-name fix is correct but wasn't the only blocker.** ngspice now resolves all 8 devices, but the cross-coupled latch can't function with the disconnected drain nets.
- **NO SPICE characterization of either production OR CIM has actually been done.** The waivered "Liberty timing analytical, not SPICE-measured" was understated — at the SPICE level, the bitcell core was never simulated end-to-end.
- The `liberty_timing_analytical.md` waiver claims "read/write logical behavior is independently validated by Verilator-level RTL simulation" — that's RTL behavioral, not transistor-level. **Functional silicon correctness of the post-Phase-2 modifications has not been SPICE-verified** (only DRC + LVS + flood-fill).

Filed as follow-up tasks:
- **#117** — write a hand-written SPICE-correct foundry bitcell stub.  Drops phantom parasitics, wires X2/X3 + X5/X7 with proper Q/QB connectivity, uses `__special_*` models.  Apply to both production and CIM refspice paths.
- **#118** — draft a disclosure waiver for CIM functional behavior not being SPICE-verified.  Parallel to the analytical-Liberty waiver but covers the orthogonal "function" concern, not just "timing."

Out of Phase 2 scope (Phase 2 = remove cleanup-audit workarounds).  These two follow-ups are new SPICE-modeling work for the Liberty-re-char track (#24).
