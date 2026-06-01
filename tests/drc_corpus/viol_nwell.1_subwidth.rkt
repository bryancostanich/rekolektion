; viol_nwell.1_subwidth — 500 nm × 3 µm nwell violates min width
; (nwell.1 = 0.84 µm). The much larger min reflects the well-
; spacing constraint at this process node.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_nwell_1_subwidth)
  (cell viol_nwell_1_subwidth
    (rect (layer sky130:nwell) 0 0 3000 500)))
