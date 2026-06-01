; viol_met1.6_minarea — a 200 × 200 nm met1 square is min-width-
; clean (200 ≥ 140) but min-area-violating (0.04 µm² < 0.083 µm²
; min, met1.6 / KLayout m1.6).
;
; Canonical Phase 4 MinArea fixture. Tests the second rule kind
; on the same layer as Width/Spacing so the parser code paths
; cross-cover.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met1_6_minarea)
  (cell viol_met1_6_minarea
    (rect (layer sky130:met1) 0 0 200 200)))
