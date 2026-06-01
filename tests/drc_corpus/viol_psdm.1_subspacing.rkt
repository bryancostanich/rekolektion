; viol_psdm.1_subspacing — two parallel psdm rects with 300 nm gap
; violate min spacing (psdm.1 = 0.38 µm in KLayout deck).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_psdm_1_subspacing)
  (cell viol_psdm_1_subspacing
    (rect (layer sky130:psdm) 0   0 1000 500)
    (rect (layer sky130:psdm) 0 800 1000 1300)))
