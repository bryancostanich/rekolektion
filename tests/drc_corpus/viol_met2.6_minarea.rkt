; viol_met2.6_minarea — a 200 × 200 nm met2 square is min-width-
; clean (200 ≥ 140) but min-area-violating (0.04 µm² < 0.0676 µm²
; min, met2.6 / KLayout m2.6).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met2_6_minarea)
  (cell viol_met2_6_minarea
    (rect (layer sky130:met2) 0 0 200 200)))
