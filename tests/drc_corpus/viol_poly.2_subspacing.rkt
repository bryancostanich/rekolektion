; viol_poly.2_subspacing — two parallel poly rects with a 150 nm
; gap violate min spacing (poly.2 = 0.21 µm). Widths 200 nm each
; (clean per poly.1a = 150 nm min).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_poly_2_subspacing)
  (cell viol_poly_2_subspacing
    (rect (layer sky130:poly) 0   0 1000 200)
    (rect (layer sky130:poly) 0 350 1000 550)))
