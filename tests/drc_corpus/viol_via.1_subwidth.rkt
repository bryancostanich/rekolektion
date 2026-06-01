; viol_via.1_subwidth — 100 × 150 nm via1 violates min width
; (KLayout via.1a_a / Magic via.1 = 0.15 µm). Length 150 nm sits at
; the via.1a_b max-length boundary (0.15 µm) so only the width rule
; fires.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_via_1_subwidth)
  (cell viol_via_1_subwidth
    (rect (layer sky130:via) 0 0 100 150)))
