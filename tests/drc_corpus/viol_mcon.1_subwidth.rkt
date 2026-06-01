; viol_mcon.1_subwidth — 100 × 170 nm mcon violates min width
; (KLayout ct.1_a / Magic mcon.1 = 0.17 µm). Length 170 nm sits at
; the ct.1_b max-length boundary (also 0.17 µm) so only ct.1_a
; fires.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_mcon_1_subwidth)
  (cell viol_mcon_1_subwidth
    (rect (layer sky130:mcon) 0 0 100 170)))
