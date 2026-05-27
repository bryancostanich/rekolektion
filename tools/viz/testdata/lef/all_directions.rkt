; Direction-map fixture. Four ports exercising every direction:
; input, output, inout, unspecified. All on met3 so layer mapping
; isn't the focus.
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell all_directions
    (props (bbox 0 0 2000 1000))
    (port (name IN_PIN)  (dir input)
          (layer sky130:met3) (flags signal)
          (shape (rect 100 100 200 200)))
    (port (name OUT_PIN) (dir output)
          (layer sky130:met3) (flags signal)
          (shape (rect 100 300 200 400)))
    (port (name BI_PIN)  (dir inout)
          (layer sky130:met3) (flags signal)
          (shape (rect 100 500 200 600)))
    (port (name UN_PIN)  (dir unspecified)
          (layer sky130:met3) (flags signal)
          (shape (rect 100 700 200 800)))))
