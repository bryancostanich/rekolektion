; viol_met3.2_subspacing — two parallel met3 rects with a 200 nm
; gap violate min spacing (met3.2 = 0.30 µm, KLayout m3.2).
; Rect widths 400 nm each (clean per met3.1 = 300 nm min).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met3_2_subspacing)
  (cell viol_met3_2_subspacing
    (rect (layer sky130:met3) 0   0 2000 400)
    (rect (layer sky130:met3) 0 600 2000 1000)))
