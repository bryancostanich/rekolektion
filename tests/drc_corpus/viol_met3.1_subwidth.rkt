; viol_met3.1_subwidth — 200 nm × 3 µm met3 wire violates min
; width (met3.1 = 0.30 µm, KLayout m3.1). Higher min than met1/met2
; so the wire dimensions differ.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met3_1_subwidth)
  (cell viol_met3_1_subwidth
    (rect (layer sky130:met3) 0 0 3000 200)))
