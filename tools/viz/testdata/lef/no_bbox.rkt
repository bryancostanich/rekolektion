; Negative fixture — cell has no (props (bbox …)). The LEF emitter
; must reject this with MissingBboxProp rather than silently falling
; back to a polygon-bbox query.
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell no_bbox_macro
    (port (name A) (dir input)
          (layer sky130:met3)
          (flags signal)
          (shape (rect 100 100 200 200)))))
