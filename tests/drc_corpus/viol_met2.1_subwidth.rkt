; viol_met2.1_subwidth — 100 nm × 3 µm met2 wire violates the
; min-width rule (met2.1 = 0.14 µm, KLayout m2.1).
;
; Same pattern as viol_met1.1_subwidth, one layer up.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met2_1_subwidth)
  (cell viol_met2_1_subwidth
    (rect (layer sky130:met2) 0 0 3000 100)))
