; viol_poly.1a_subwidth — 100 nm × 1 µm poly violates min width
; (poly.1a = 0.15 µm). Length 1 µm clear of any layout context
; so secondary rules (poly.4 cross-spacing to diff, etc.) don't
; fire.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_poly_1a_subwidth)
  (cell viol_poly_1a_subwidth
    (rect (layer sky130:poly) 0 0 1000 100)))
