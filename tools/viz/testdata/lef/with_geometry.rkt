; Fixture with interior geometry on met1 + met2, used for OBS
; DerivedFromGeometry tests. The bbox is 0..5000 x 0..2000 DBU
; (5 × 2 µm); the met1 rect covers 100..400 x 200..800; the met2
; poly is a small square 1000..1200 x 100..300.
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell with_geometry
    (props (bbox 0 0 5000 2000))
    (rect (layer sky130:met1) 100 200 400 800)
    (poly (layer sky130:met2)
          (points (1000 100) (1200 100) (1200 300) (1000 300)))))
