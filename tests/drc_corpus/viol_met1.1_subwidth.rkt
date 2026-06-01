; viol_met1.1_subwidth — 100 nm × 3 µm met1 wire violates the
; min-width rule (met1.1 = 0.14 µm, KLayout m1.1).
;
; Expected violations: 1 tile on both Magic and KLayout external
; runs.
;
; This is the smallest possible Width violation. Used as the
; canonical Phase 4 Width fixture. See tests/drc_corpus/README.md.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met1_1_subwidth)
  (cell viol_met1_1_subwidth
    (rect (layer sky130:met1) 0 0 3000 100)))
