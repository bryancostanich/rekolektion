; probe_foundry_waiver — single foundry-primitive SRef.  The
; primitive's internal geometry carries known foundry-waivered
; tiles (the `_KNOWN_WAIVER_RULES` set in src/rekolektion/verify/
; drc.py — sky130_fd_pr__nfet_01v8 has e.g. licon spacing rules
; that Magic's per-cell scope pre-validates).
;
; Used by `compute_primitive_footprints` to validate that the
; foundry-cell waiver pipeline applies on the F# primary path.
; NOT a viol cell — should round-trip as DRC-clean once the
; waiver flow lands under both compats.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (import "../../cell_designs/primitives/nfet_01v8_W1p5_L0p5_core_topgate.rkt")
  (top probe_foundry_waiver)
  (cell probe_foundry_waiver
    (sref (cell nfet_01v8_W1p5_L0p5_core_topgate) (origin 0 0))))
