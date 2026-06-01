; viol_nsdm.2_subwidth — 200 nm × 2 µm nsdm rect violates min width
; (nsdm.2 = 0.38 µm in KLayout deck).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_nsdm_2_subwidth)
  (cell viol_nsdm_2_subwidth
    (rect (layer sky130:nsdm) 0 0 2000 200)))
