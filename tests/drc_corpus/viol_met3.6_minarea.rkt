; viol_met3.6_minarea — a 400 × 400 nm met3 square is min-width-
; clean (400 ≥ 300) but min-area-violating (0.16 µm² < 0.240 µm²
; min, met3.6 / KLayout m3.6).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met3_6_minarea)
  (cell viol_met3_6_minarea
    (rect (layer sky130:met3) 0 0 400 400)))
