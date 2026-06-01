; viol_met2.2_subspacing — two parallel met2 rects with a 100 nm
; gap violate min spacing (met2.2 = 0.14 µm, KLayout m2.2).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met2_2_subspacing)
  (cell viol_met2_2_subspacing
    (rect (layer sky130:met2) 0   0 2000 200)
    (rect (layer sky130:met2) 0 300 2000 500)))
