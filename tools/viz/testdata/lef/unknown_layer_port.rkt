; Unknown-layer fixture — port on an `unknown:N/D` layer that the
; sky130 layer table doesn't map. LEF has no concept of bare
; (number, datatype) pairs, so the emitter must reject with
; UnknownLayer.
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell unknown_layer_macro
    (props (bbox 0 0 1000 1000))
    (port (name MYSTERY) (dir input)
          (layer unknown:999/77) (flags signal)
          (shape (rect 100 100 200 200)))))
