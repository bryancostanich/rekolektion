; LEF emitter P0 fixture — a 3-port macro with declared bbox.
; Used by RktToLefTests for SIZE/ORIGIN derivation and (in later
; phases) PIN entries on different layers + flag combinations.
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell simple_3port
    (props (bbox 0 0 5000 2000)
           (description "minimal 3-port LEF fixture"))
    ; Power/ground rails as ports on met4.
    (port (name VDD) (dir inout)
          (layer sky130:met4)
          (flags power)
          (shape (rect 0 1900 5000 2000)))
    (port (name VSS) (dir inout)
          (layer sky130:met4)
          (flags ground)
          (shape (rect 0 0 5000 100)))
    ; Single signal pin on met3.
    (port (name A) (dir input)
          (layer sky130:met3)
          (flags signal)
          (shape (rect 100 500 200 600)))))
