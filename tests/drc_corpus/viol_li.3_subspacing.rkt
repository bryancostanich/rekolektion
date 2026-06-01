; viol_li.3_subspacing — two parallel li1 rects with a 100 nm gap
; violate min spacing (li.3 = 0.17 µm).  Rect widths 200 nm each
; (clean per li.1 = 170 nm min).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_li_3_subspacing)
  (cell viol_li_3_subspacing
    (rect (layer sky130:li1) 0   0 2000 200)
    (rect (layer sky130:li1) 0 300 2000 500)))
