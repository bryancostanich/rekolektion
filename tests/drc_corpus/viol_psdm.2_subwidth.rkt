; viol_psdm.2_subwidth — 200 nm × 2 µm psdm rect violates min width
; (psdm.2 = 0.38 µm in KLayout deck).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_psdm_2_subwidth)
  (cell viol_psdm_2_subwidth
    (rect (layer sky130:psdm) 0 0 2000 200)))
