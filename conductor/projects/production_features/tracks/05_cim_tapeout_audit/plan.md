# Track Plan: CIM SRAM Macro Tapeout Audit

Tapeout-readiness audit for the four CIM SRAM macros produced by track
03.  Track 03 delivered Magic-clean LVS / DRC / OpenROAD-routable
hard macros.  This track answers a different question: *what do we
not yet know that could make these macros fail on first silicon?*

**Output:** a risk register and a sign-off-ready audit report —
- Concrete go/no-go signal per audit step
- Documented failure modes with mitigation plans for each
- Per-variant `audit/` artefact directory with sim outputs, Calibre
  reports (or precheck output), and reviewer notes

**Depends on:** track 03 complete (LVS unique, DRC 0 real on flat +
hier, OpenROAD smoke pass — all four variants).

**Does NOT depend on:** track 02 community outreach.

---

## Confidence baseline (start of track)

Honest read of where the macros stand at end of track 03:

- **High confidence:** topological correctness (LVS match unique),
  layout legality under Magic's DRC deck, pin/abstract self-
  consistency, build reproducibility.
- **Medium confidence:** that Calibre would also flag 0 real DRC.
- **Low confidence:** functional correctness in the analog domain;
  bitcell margin under CIM activity; PVT-corner sensitivity;
  power integrity under simultaneous CIM activity.

Aggregate: ~70 % the macros work as intended on first silicon
conditional on chip-top integration being correct.  This track
moves that number to ≥90 %, or surfaces blockers that need a
respin before tape.

The 30 % residual risk concentrates in #1 (functional sim) and #4
(margin under CIM disturb).  Audit budget should reflect that.

---

## Phase 1: Functional SPICE characterisation (designer-led)

The biggest gap.  Track 03 shipped a `characterize_cim_liberty.py`
framework that runs end-to-end on SRAM-D in ~28 min and produces
clean physical numbers (V_quiescent / V_compute / t_settle), but
ran exactly **one** point with a uniform-Q=1 write — best-case
minimum-swing.  Real CIM operation has weight diversity and worst-
case patterns.

- [ ] Worst-case write-pattern sweep, per variant
    - [ ] All-Q=0 vs all-Q=1 vs alternating column pattern → bounds
          MBL swing range
    - [ ] Random-weight pattern at multiple fanout counts (e.g. 4,
          16, 64 active rows in SRAM-D) → average-case settling
- [ ] Sense margin per variant
    - [ ] Worst-case input differential at the source follower vs
          downstream comparator threshold (chip-top-defined)
    - [ ] Output noise budget — does the analog signal stay
          distinguishable across worst-case PVT?
- [ ] PVT corner sweep on the two worst patterns
    - [ ] Corners: TT, SS, FF, SF, FS at –40 °C / 25 °C / 125 °C
    - [ ] Per-corner: t_settle, swing, sense margin
- [ ] Build NLDM Liberty timing tables from the sweep
    - [ ] 4 input slews × 4 output loads per arc = 16 sims per
          variant per corner.  ~7 hr per variant × 4 variants ×
          2 critical corners ≈ 56 hr compute → overnight queue
    - [ ] Wire results back into `cim_liberty_generator.py` via a
          JSON results file consumed at generation time

**Exit criteria:** every variant has worst-case timing arcs at
SS-cold and FF-hot; documented sense margin with numerical floor;
analog signal is distinguishable across all corners.  If any
variant's worst-case sense margin is < 50 mV, **stop and discuss
respin**.

**Estimated effort:** 3–5 designer-days work + ~3 days overnight
compute.  Single biggest item in this track.

---

## Phase 2: Bitcell margin under CIM disturb

The 6T cross-coupled inverter is stable when WL=0.  CIM activity
asserts MWL on a row, which turns on T7 and pulls charge between
the latched Q node and the MIM cap.  This is a real disturbance
that conventional 6T-SRAM stability analysis doesn't cover.

- [ ] Single-cell SNM under MWL assertion, per variant
    - [ ] Bias the cell at Q=1 / Q=0; assert MWL; capture the
          cross-coupled inverter "butterfly" curve
    - [ ] Static noise margin (SNM) = side of largest square
          fitting between the two transfer curves
    - [ ] Repeat at SS-cold (worst static stability)
- [ ] Multi-row simultaneous-CIM disturb on SRAM-A (256 rows)
    - [ ] All rows asserting MWL_EN simultaneously is the
          intended worst case for compute throughput; what's the
          per-cell SNM degradation?
    - [ ] Add VPWR droop into the analysis (couples with phase 5)
- [ ] Read-disturb under CIM
    - [ ] Standard 6T read disturb is via WL.  CIM also pulls Q
          via T7+MIM cap.  Quantify the additional disturb.

**Exit criteria:** SNM ≥ 100 mV at SS-cold under simultaneous
multi-row CIM disturb (industry rule of thumb for SRAM
robustness; tighter for analog/CIM).  If below, the bitcell sizing
or the T7 size needs revisit.

**Estimated effort:** 1–2 designer-days.

---

## Phase 3: Foundry-tool DRC sign-off

Magic ≠ Calibre.  We waived Magic-only false-positives on
`var.x` (Magic mis-classifies cap_mim_m3_1 as a varactor), `licon.7`
(foundry tap density), and a few others.  These are *almost
certainly* Magic-only artefacts, but the only way to confirm is
running the actual sign-off tool.

- [ ] Run the four macros through Calibre nmDRC
    - [ ] Path A: Efabless precheck server (free, async, takes
          a calendar day or two to come back)
    - [ ] Path B: SkyWater partner with Calibre access — short-
          turnaround if available
- [ ] Diff Magic real-error count vs. Calibre real-error count
    - [ ] If equal: confirms our waiver list is accurate
    - [ ] If Calibre flags violations Magic missed: real bugs;
          fix → re-run track 03 LVS+DRC → re-run this audit step
- [ ] Run Calibre nmLVS as a cross-check on at least SRAM-D
    - [ ] netgen LVS is "match unique" — Calibre LVS should agree

**Exit criteria:** Calibre 0 real DRC across all four variants;
Calibre LVS matches on at least the smallest variant.

**Estimated effort:** 0.5 day (handoff) + 1–7 day async wait for
precheck results.

---

## Phase 4: Antenna, density, latch-up

These are separate decks the foundry runs alongside DRC.  Magic
doesn't run them at the macro level; OpenROAD has equivalents.

- [ ] Antenna check on each variant
    - [ ] Run OpenROAD `check_antennas` after the smoke test's
          detailed_route on each variant (the smoke flow already
          exists; add the one extra command)
    - [ ] Any flags → either fix the offending net, add jumpers,
          or document for the consumer's chip-top to handle
- [ ] Density rules
    - [ ] Per-layer fill density inside macro + at macro edges
    - [ ] If any layer is < 30% (typical SkyWater fill density
          target), flag for chip-top to handle via fill cells
- [ ] Latch-up
    - [ ] Confirm well taps are present at adequate spacing
          inside the bitcell array and peripheral rows
    - [ ] Confirm n-well + p-substrate isolation under CIM
          activity (couples with phase 2)

**Exit criteria:** antenna 0 violations; density inside foundry
acceptable range or a documented fill plan; latch-up tap spacing
≤ 25 µm everywhere.

**Estimated effort:** 0.5–1 day.

---

## Phase 5: Power integrity

LVS is structural; it doesn't catch IR drop, EM, or supply-rail
sufficiency.  CIM activity has very different current profiles
than standard SRAM read/write.

- [ ] IR-drop sim under simultaneous-row CIM activity
    - [ ] SRAM-A worst case: 256 buf_2 drivers all switching →
          current spike on VPWR, voltage droop into the array
    - [ ] Tool: OpenROAD `analyze_power_grid` or equivalent
- [ ] Electromigration on the macro PDN straps
    - [ ] Met4 vertical PDN straps carry the full activity current
    - [ ] Current density vs. SkyWater EM rules per layer
- [ ] VBIAS / VREF analog supply isolation
    - [ ] These bias the sense path; digital VPWR noise coupling
          would corrupt the analog signal
    - [ ] Quantify the coupling capacitance and confirm it's
          within the bias network's filtering envelope (chip-top
          decision, but flag from the macro side)

**Exit criteria:** IR drop < 5% of nominal under worst-case CIM
activity.  EM rules met or documented mitigation.  VBIAS/VREF
coupling characterised so chip-top integrators have a number to
design against.

**Estimated effort:** 1–2 designer-days.

---

## Phase 6: Independent design review

A human who didn't write the code looking at the layouts in
klayout / Magic with the SkyWater design rules document open.
Sometimes catches what the rule deck and the originator's tests
both miss.

- [ ] Reviewer ≠ author for each of the four variants
- [ ] Open the GDS in klayout alongside SkyWater's design-rule
      manual and the cap_mim_m3_1 foundry datasheet
- [ ] Walk the bitcell tile, the MWL driver column, the precharge
      row, the sense row.  Look for:
    - [ ] Anything that looks like a routing channel violation
          the rule deck didn't catch
    - [ ] Pin shapes too small / placed wrong for chip-top access
    - [ ] Layer-stack order issues (e.g. met2 over MIM cap when
          it should be over CAPM)
- [ ] Reviewer signs off in writing per variant

**Exit criteria:** four signed-off variants.  Any flagged item
either fixed or documented as accepted risk.

**Estimated effort:** 0.5 day per variant × 4 = 2 days reviewer
time.

---

## Phase 7: SkyWater design-rule documentation review

Foundry tech docs sometimes cover constraints the rule deck
doesn't.  Read them with the macro in front of you.

- [ ] Read SkyWater's CIM / mixed-signal design notes (if any)
- [ ] Read the cap_mim_m3_1 datasheet for usage constraints —
      e.g. density limits, signal-routing-over-cap restrictions,
      thermal derating
- [ ] Read the latch-up application note for tap spacing in
      analog macros
- [ ] Document any constraint our layout violates → fix or
      formal waiver

**Exit criteria:** every documented constraint either met or
formally waivered with rationale.

**Estimated effort:** 1 day.

---

## Phase 8: Audit report + risk register

Roll-up document for the integrator and (if applicable) the
foundry submission packet.

- [ ] Per-variant section
    - [ ] LVS / DRC sign-off (already from track 03)
    - [ ] Functional SPICE summary (from phase 1)
    - [ ] SNM / margin numbers (from phase 2)
    - [ ] Calibre delta vs. Magic (from phase 3)
    - [ ] Antenna / density / latch-up status (from phase 4)
    - [ ] IR drop / EM headroom (from phase 5)
    - [ ] Reviewer sign-off (from phase 6)
    - [ ] Foundry-doc compliance (from phase 7)
- [ ] Cross-cutting risk register
    - [ ] Each known risk, mitigation status, residual likelihood
- [ ] Final go/no-go recommendation per variant
- [ ] Rev-history pointer to the GDS / LEF / Liberty / Verilog
      drop in `output/cim_macros/` (git SHA pinned)

**Exit criteria:** report PR'd, reviewed, merged.  Either
"all four variants tapeout-ready" with the risk register
acknowledged, or specific blockers identified.

**Estimated effort:** 1 day write-up.

---

## Total budget

| Phase | Effort (designer-days) | Compute (hours) | Notes |
|-------|-----------------------:|-----------------:|-------|
| 1 — SPICE characterisation | 3–5 | ~56 (overnight) | biggest gap |
| 2 — Bitcell margin under CIM | 1–2 | minimal | |
| 3 — Calibre sign-off | 0.5 + async wait | foundry tool | external dependency |
| 4 — Antenna / density / latch-up | 0.5–1 | minimal | |
| 5 — Power integrity | 1–2 | minimal | |
| 6 — Independent review | 2 (reviewer time) | n/a | |
| 7 — Foundry doc review | 1 | n/a | |
| 8 — Audit report | 1 | n/a | |
| **Total** | **10–14 designer-days** | **~3 nights compute** | + Calibre async wait |

That's roughly two designer-weeks plus a foundry-tool round-trip.
Most of it can run in parallel except phases 1 and 2 which
inform phase 5 (IR drop sims need realistic activity profiles).

---

## Stop conditions

If any of these surface, the audit pauses and the team has a
respin discussion before continuing:

- Phase 1: any variant's worst-case sense margin < 50 mV
- Phase 2: SNM < 100 mV at SS-cold under simultaneous-row CIM
- Phase 3: Calibre flags real (non-foundry-cell) DRC violations
- Phase 5: IR drop > 10% nominal under worst-case CIM activity
- Phase 7: a SkyWater-documented constraint our layout violates
  with no available waiver path

---

## What this track does NOT cover

- Chip-top integration (lives in the consumer's repo).
- Analog post-route DRC at chip-top (consumer's job, but the
  macro-level results from this track are an input).
- Tapeout submission packet itself (foundry-specific format,
  separate flow).
- Anything DAC/ADC related — that's external IP per track 03's
  scope.
