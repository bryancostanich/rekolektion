# Trust Audit: Find every false-positive verification result

## Why this exists

Track 03 shipped "LVS clean" CIM macros. Track 05 is the tapeout-readiness
audit. Mid-audit, we found that the macro LVS pipeline has been hiding a
connectivity bug (wl_bot[r] and wl_top[r] of every cell physically isolated
across the array — every cell except possibly one per row would be
floating-gate on silicon, but LVS reports clean by virtue of label-name
merging).

The original LVS pass was a **false positive**. The audit hadn't surfaced
it. We only found it by hand-tracing the comp.out and pushing on the
analysis. **That means there could be more false positives we haven't
surfaced**, and the existing audit (Phases 1–3 of track 05) doesn't have
machinery to find them.

This trust audit is the machinery.

**Output:** for every verification artefact we currently rely on (LVS
results, DRC results, characterisation results, schematic references), an
explicit PASS / FAIL / NOT-APPLICABLE classification with quoted evidence.
A "smoking guns" report listing every FAIL in priority order. Sign-off
gate before tape: **zero unresolved FAILs** plus a written rationale for
every NOT-APPLICABLE.

**Depends on:** nothing. This blocks tapeout-readiness sign-off (any later
"clean" claim is meaningless until this audit closes).

---

## Failure modes we are hunting

Three patterns proven to bypass verification in this stack:

1. **Self-reference comparison.** Extracted-vs-extracted LVS where both
   sides come from the same layout. Catches no bugs because both sides
   share the same layout errors. (Hit this in track 04 — fixed by the
   "trustworthy LVS" pipeline.)
2. **Label-merge LVS.** Layout has physically isolated nets that share a
   label name. Magic merges by name during extraction. LVS reports clean.
   Silicon broken. (Hit this in the WL_BOT/WL_TOP case found mid-track-05.)
3. **Reference edited to match layout.** Someone modified the reference
   schematic to make LVS pass instead of fixing the layout. (Suspected
   pattern — needs git-history audit to confirm or refute.)

Plus support patterns: DRC waivers, missing tests, characterisation
stimuli that didn't actually exercise the broken path, "clean" claims
that weren't re-verified after a downstream change.

---

## Scope

In scope: every artefact in `output/lvs_*/`, `output/drc_*/`, every
`run_lvs_*.py` and `run_drc_*.py` script, every reference `.spice` file
under `output/`, every `*.subckt.sp` under
`src/rekolektion/peripherals/cells/extracted_subckt/`, every Liberty /
LEF generator script, every netgen `.setup` file, every Magic `.magicrc`
override.

In scope per-cell: LR bitcell, CIM bitcell (4 variants), MWL driver, MBL
precharge, MBL sense, the 4 CIM macros (SRAM-A/B/C/D), production SRAM
macros (activation_bank, weight_bank_small).

Out of scope (separate efforts): RTL trust, sim infra trust. This audit
is purely about layout verification.

### ReRAM CIM track — pre-emptive guidance

The ReRAM CIM (track 03 of the v1b_cim_module project) currently has
no GDS — Phase 7 (GDS + LEF generation) is unstarted. So there's no
layout to audit yet. But the bug pattern that bit us on SRAM CIM
(label-merge false-positive LVS hiding fragmented internal nets) is
generic to any cell that has internal multi-stripe routing requiring
external strap cells. When ReRAM Phase 7 starts:

- Verify the 4T1R_MUX foundry cell's internal-net architecture before
  tiling — does any per-row or per-col signal require a strap-style
  bridging cell? If yes, integrate it into `tile_array` from day one.
- Use the trustworthy LVS pipeline (extracted ↔ hand-written reference
  schematic, no extracted-vs-extracted self-reference) from the first
  GDS, not as an after-the-fact retrofit.
- Run the Tier 2 flood-fill connectivity check on every ReRAM macro
  port before reporting LVS clean.

---

## Tier 1: Verification-pipeline integrity (audit the auditor)

For every LVS / DRC / characterisation artefact:

- [ ] **T1.1 — Reference source provenance.** For each `run_lvs_*.py`,
      identify the schematic argument's path. Classify each:
      hand-written `.spice` (good) | generated from extracted SPICE (bad,
      = self-reference) | generated from a hand-written file (good if the
      hand-written file passes T1.3). Output: table of (LVS run, ref
      path, classification, evidence).
- [ ] **T1.2 — Reference recency.** For each reference file in T1.1, run
      `git log -1` on the file, then `git log -1` on the corresponding
      layout generator. Flag any case where the layout was modified AFTER
      the reference was last updated (= reference may not model current
      layout). Output: table.
- [ ] **T1.3 — Reference vs intent.** For each hand-written reference
      schematic, write down (separate doc) what the cell is supposed to
      do at transistor level (port list, device count, connectivity).
      Compare to the file. Mismatches are reference bugs that fake LVS.
      Output: per-cell intent-vs-reference comparison.
- [ ] **T1.4 — Equate / ignore lists.** Grep all netgen `.setup` files
      for `equate`, `ignore`, `permute`. For each entry, document why
      it exists. Reject any entry that "equates" two things that are
      not actually electrically connected — that's a sanctioned false
      positive. Output: equate ledger with rationale per entry.
- [ ] **T1.5 — Port-alignment scope.** For every `_align_ref_ports`-style
      helper (currently in `scripts/run_lvs_cim.py`), confirm it ONLY
      rewrites the macro top-level subckt port list, never bitcell or
      sub-cell ports. Confirm the sub-cell `.subckt` lines are unchanged
      between input ref and output aligned ref. Output: diff per LVS run.
- [ ] **T1.6 — Magic / netgen tech file overrides.** Diff our magicrc
      and netgen setup against the PDK's stock files. Each delta needs
      rationale. Overrides that loosen a check are red flags.
- [ ] **T1.7 — Liberty generator source.** For each `*_liberty_generator.py`,
      confirm the timing data input is FLAT extracted SPICE (post-PEX if
      available), not a behavioural model and not the reference schematic.

## Tier 2: Connectivity sanity (audit the layout)

For each macro and each top-level port:

- [ ] **T2.1 — Per-row / per-column flood-fill connectivity.** For every
      per-row signal (WL, MWL) and per-column signal (BL, BLB, MBL):
      pick three cells in the row/col (col 0, col cols/2, col cols-1
      for per-row; analogous for per-col). In Magic, `select net` from
      cell 0's gate/drain and check whether the flood reaches the other
      two cells via metal/poly. If flood doesn't reach all three, the
      signal is fragmented in silicon regardless of LVS naming.
      **This is the test that would have caught the WL bug.** Output:
      pass/fail table per port per macro.
- [ ] **T2.2 — Floating-gate scan.** For each cell type, list every
      transistor gate. Verify each gate's net is in a connected component
      that reaches at least one macro pin. Floating gates are bugs even
      if LVS doesn't catch them. Output: floating-gate report per cell.
- [ ] **T2.3 — Label-only nets.** Use Magic to enumerate nets named
      purely by label coincidence (i.e. multiple disjoint geometry pieces
      with the same label name, no metal connecting them). Each such net
      is a label-merge candidate. For each, decide: real (intentional
      label tying that the chip-top will wire up) or fake (bug, layout
      missing a strap). Output: label-merge ledger with classification.
- [ ] **T2.4 — Schematic net count vs layout physical-net count.** For
      each macro, compare the schematic's net count for each port against
      the count of physically-distinct geometry pieces driving that port
      in the layout. Mismatches indicate the layout has more isolated
      pieces than the schematic models — i.e. the schematic is hiding
      physical fragmentation.
- [ ] **T2.5 — Boundary continuity check.** For per-row/per-col signals
      that should be continuous across cell boundaries, verify the
      relevant geometry actually meets across the boundary (not just
      "near" — meets within 0 µm spacing or via a shared shape).

## Tier 3: Characterisation sanity (audit the timing)

- [ ] **T3.1 — Stimulus-to-net coverage.** For each Liberty arc that
      claims a t_setup/t_hold/t_pd value, find the corresponding SPICE
      run and grep the .raw file for the actual node toggling. If the
      named pin's voltage didn't actually transition, the data is fake.
- [ ] **T3.2 — PVT corner coverage.** For each Liberty arc, confirm the
      arc was characterised at the corners declared in the .lib header.
      Missing corner sims = extrapolated data, not measured.
- [ ] **T3.3 — Pattern coverage on analog cells.** The CIM macro's
      MBL_OUT depends on the input weight pattern. Confirm
      characterisation ran the pattern matrix declared in
      `characterize_cim_liberty.py` (not just the default uniform-Q=1
      best case).
- [ ] **T3.4 — Operating-point sanity.** For each analog node in each
      SPICE run, verify the operating-point voltage is in a reasonable
      range (e.g., MBL sits between VGND+0.05 and VPWR-0.05). Out-of-range
      OPs indicate the SPICE deck has a wiring bug that gives nonsense
      timing data.

## Tier 4: Design intent vs. reality

- [ ] **T4.1 — Per-cell intent docs.** Write one paragraph per
      hand-written cell schematic (LR bitcell, CIM bitcell, MWL driver,
      MBL precharge, MBL sense): what is this cell electrically? Then
      diff against the .subckt body. Discrepancies are bugs the schematic
      encodes (= silently wrong reference).
- [ ] **T4.2 — Per-port intent docs.** For each macro top-level port,
      write one sentence on what physical structure it drives ("WL[r]
      drives both access transistors of all 64 cells in row r"). Verify
      in the layout. The intent doc is the spec the audit checks against.
- [ ] **T4.3 — Power / ground continuity.** For VPWR / VGND / VSUBS,
      verify the physical net spans every cell (not just some) and that
      the macro pin reaches every supply tap. Open supply rails are
      catastrophic.
- [ ] **T4.4 — N-well biasing.** Verify every PMOS body is connected to
      VPWR through a tap, not just labelled VPWR. (This is exactly the
      kind of fake the WL bug exposed — relying on label-merge instead
      of physical connectivity.)

## Tier 5: Git / history forensics

- [ ] **T5.1 — Recent reference schematic edits.** `git log -p` on every
      `.spice` file under `output/` and `src/rekolektion/peripherals/`.
      For every commit: was the schematic edited to match a layout
      change (OK) or to make LVS pass (red flag)? Output: per-commit
      classification with evidence quote from the commit message + diff.
- [ ] **T5.2 — Recent verification setup edits.** Same on `.setup`,
      `.magicrc`, and on the `_align_ref_ports` / wildcard-strip /
      label-rename machinery in `run_lvs_cim.py` and `verify/lvs.py`.
- [ ] **T5.3 — "LVS clean" / "DRC clean" claim audit.** Find every
      commit message and conductor-plan check-box that claims clean.
      For each, identify what was actually verified vs assumed.
      Re-run the verification today against current layout and compare.
      Stale "clean" claims are unverified state.
- [ ] **T5.4 — Cross-reference issues.** For every existing rekolektion
      issue tagged `lvs` or `drc` or `tapeout`: confirm the issue was
      either resolved with evidence or is still open. Closed-without-
      evidence issues need re-verification.

---

## Process

This audit is not run by a single agent. The point is to NOT trust any
single report. Procedure:

1. **Tiers 1, 2, 5 inline in the main session.** These are grep-heavy
   and short; need full repo context for findings; written up as the
   audit progresses.
2. **Tiers 3 and 4 in a subagent**, with strict instructions: "report
   what you found, do not make changes, do not declare anything clean
   without quoting evidence, do not generate new reference files."
3. **Diff stage.** After both passes, diff findings against every prior
   "clean" claim recorded in conductor plans, commit messages, or PR
   bodies. Anything that was previously claimed clean but the audit
   now flags is the bug we missed and must go on the smoking-gun list.
4. **Smoking-gun review.** User-led, item-by-item disposition: bug
   confirmed → file an issue + add to a fix backlog; reference bug →
   regenerate reference + re-run LVS; intent ambiguity → write the
   intent doc, then decide.

Each tier completes when every item is PASS / FAIL / N-A with cited
evidence (file path + line range or a magic console transcript). FAILs
populate the smoking-gun list. N-A entries need a one-line justification.

## Output artefacts

- `audit/tier1_pipeline.md` — table per item with evidence
- `audit/tier2_connectivity.md` — table per macro per port
- `audit/tier3_characterisation.md` — table per Liberty arc
- `audit/tier4_intent.md` — per-cell + per-port intent docs and diffs
- `audit/tier5_history.md` — per-commit classification
- `audit/smoking_guns.md` — every FAIL across tiers, one per row, with
  severity (P0 silicon-breaking, P1 functional, P2 cosmetic) and
  recommended action
- `audit/equate_ledger.md` — every netgen equate/ignore with rationale
- `audit/intent/` — one `.md` per cell with intent doc

## Sign-off gate (blocks tapeout)

- [ ] Every Tier 1–4 item has a recorded result (no "skipped — looked
      fine")
- [ ] `smoking_guns.md` has zero P0 entries unresolved
- [ ] `smoking_guns.md` has zero P1 entries unresolved OR has a written
      designer waiver per remaining P1 explaining why it's safe to ship
- [ ] Equate ledger reviewed, every entry has a justification beyond
      "it makes LVS pass"
- [ ] Per-cell intent docs reviewed, all diffs against schematic
      reconciled

## Anti-patterns to refuse during the audit

- **Skipping with "looks correct."** Every item needs evidence.
- **Re-running a tool that previously passed and quoting that pass.**
  The previous pass is what put us here. Re-derive from first principles.
- **Modifying the reference file when the layout fails.** If the layout
  is wrong, fix the layout. Reference edits are only OK when the
  intent doc proves the schematic was wrong.
- **Treating LVS-pass as connectivity-correct.** Use Tier 2 flood-fill,
  not LVS, to verify physical connectivity.
- **Subagent self-clearance.** If a subagent runs Tier 3 or 4, the main
  session reviews the evidence before accepting any item as PASS.

## What we expect to find

Honest predictions before starting (will be checked against actual
findings to calibrate audit completeness):

- **High likelihood:** more label-merge fakes in the CIM macros (the
  WL_BOT/WL_TOP case may not be unique — MBL columns, MWL rows, BL/BLB
  columns all rely on the same per-row/per-col clustering pattern, and
  the same physical-isolation problem could exist for any of them).
- **Medium likelihood:** Liberty timing data characterised on a SPICE
  deck whose connectivity didn't match the layout (because reference
  drift between layout and the cim_spice_generator).
- **Medium likelihood:** at least one waiver in the netgen equate list
  that is sanctioning a real bug rather than handling a known-safe
  difference.
- **Low likelihood but high cost:** the LR bitcell or one of the
  peripheral cells has a similar physical-isolation issue we haven't
  noticed because LR strict LVS is also using label-merge under the
  hood.

If actual findings dramatically diverge from these predictions (much
fewer fakes, or much more), the audit's coverage is wrong and Tiers
need to be expanded.
