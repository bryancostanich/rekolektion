# BUILD LOG — lshift_1v8_to_3v3

## Final state

**DRC clean. LVS MATCH against `lshift_1v8_to_3v3_sch.spice`.** Gate 3 cleared 2026-05-28.

## What unblocked it

The prior log entry (below) claimed the cell was BLOCKED at 6 met1.6 errors and would need either a stdcell-abutment helper, wider-gate-poly primitive, or a multi-net router. That diagnosis was wrong on both counts:

1. **DRC was already clean.** The build script's `poly_bridge` paints bbox-anchored met1 enlargers that automatically clear the 6 met1.6 violations on the LV inverter gate-pad strips. Re-running `verify_drc` on the existing `.rkt` reported 0 errors. The earlier "6 met1.6" reading was stale — probably from an iter before `poly_bridge` was wired in.

2. **LVS was gated by a one-line bug in `ToGds.fs`.** The build script marked the `IN_n` internal net with `internal=True` (the documented mechanism for keeping a label out of GDS so it doesn't become a spurious subckt port). The `Label` type's own docstring states *"`IsInternal = true` marks the label as a viz/debug annotation that **ToGds.fs deliberately skips when exporting GDS**."* But `ToGds.fs:187` unconditionally emitted every `LabelEl` as a GDS text record. Result: the IN_n label reached the GDS, Magic's `port makeall` promoted it to a subckt port, layout had 7 ports vs schematic's 6, LVS failed pin matching with "Netlists match uniquely with port errors."

   Fix at `tools/viz/src/Rekolektion.Viz.Core/Rkt/ToGds.fs:187`:

   ```diff
   - | LabelEl l -> [ Gds.Types.Text (labelToGds l) ]
   + | LabelEl l -> if l.IsInternal then [] else [ Gds.Types.Text (labelToGds l) ]
   ```

   Aligns implementation with the type's documented contract. With the fix: layout `.subckt` line is `VSS VDDA1 VDD IN OUT_N OUT` (6 ports), schematic matches, LVS clean.

## Regression sweep (post-fix)

- `nand2_inv_lv` — LVS also now matches (same IN_n-style internal-label leakage was the gate; DRC still has 12 met2.2 from the genuine 220 nm intra-FET pitch architectural ceiling — separate problem).
- `blc_trim_dac` — DRC clean, LVS mismatch. Pre-existing layout bug (pfet gate not wired to `SIGN` net, sits on orphan `pfet_hv_W1p0_L0p5_core_topgate_0/G`). Not regressed by the fix; labels can't disconnect a net.

## Files

- `scripts/build_lshift_1v8_to_3v3.py`
- `cell_designs/khalkulo/lshift_1v8_to_3v3.rkt`
- `cell_designs/khalkulo/lshift_1v8_to_3v3_sch.spice`

---

## Prior log entry (kept for history — diagnosis was wrong, see above)

### Iteration history (run 2)

- iter 1 (carried from prior run): placement-only build. 6 met1.6 (Metal1 minimum area) errors from primitive gate met1 strips.
- iter 2: attempted to fix met1.6 by adding `pin_patch(mcon=False)` at each gate. Bumped to 8 met1.2 (spacing) errors — the 320×320 met1 patch lands within 140 nm of adjacent S/D primitive met1 strips on the LV FETs. Same intra-FET geometry constraint as nand2.
- iter 3: reverted to placement-only baseline (6 met1.6 errors).

### Claimed final state

**BLOCKED at 6 met1.6 errors.** Re-verified 2026-05-28: actual state was 0 DRC errors. The "6 met1.6" reading was stale.
