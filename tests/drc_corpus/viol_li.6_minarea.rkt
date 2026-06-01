; viol_li.6_minarea — a 200 × 200 nm li1 square is min-width-
; clean (200 ≥ 170) but min-area-violating (0.04 µm² < 0.0561 µm²
; min, li.6).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_li_6_minarea)
  (cell viol_li_6_minarea
    (rect (layer sky130:li1) 0 0 200 200)))
