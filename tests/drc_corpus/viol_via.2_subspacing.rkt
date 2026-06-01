; viol_via.2_subspacing — two 150 × 150 nm via1s with a 150 nm gap
; violate min spacing (via.2 = 0.17 µm). Squares at the min via1
; size so width-class rules are clean.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_via_2_subspacing)
  (cell viol_via_2_subspacing
    (rect (layer sky130:via)   0 0 150 150)
    (rect (layer sky130:via) 300 0 450 150)))
