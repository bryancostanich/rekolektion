; viol_mcon.2_subspacing — two 170 × 170 nm mcons with a 150 nm
; gap violate min spacing (KLayout ct.2 / Magic mcon.2 = 0.19 µm).
; Both contacts are at the min square size so width-class rules
; are clean.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_mcon_2_subspacing)
  (cell viol_mcon_2_subspacing
    (rect (layer sky130:mcon)   0 0 170 170)
    (rect (layer sky130:mcon) 320 0 490 170)))
