; viol_nsdm.1_subspacing — two parallel nsdm rects with 300 nm gap
; violate min spacing (nsdm.1 = 0.38 µm in KLayout deck).
; Each rect is 500 nm wide (clean per nsdm.2 = 0.38 µm width).
;
; **Note:** KLayout deck assigns nsdm.1 to SPACING and nsdm.2 to
; WIDTH. F# Magic has these labels swapped vs the deck (pre-
; existing); F# Klayout follows the deck so the per-rule diagonal
; comparison works.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_nsdm_1_subspacing)
  (cell viol_nsdm_1_subspacing
    (rect (layer sky130:nsdm) 0   0 1000 500)
    (rect (layer sky130:nsdm) 0 800 1000 1300)))
