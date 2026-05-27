; Conflict fixture — power+ground on one port. Emitter must reject
; with ConflictingFlags.
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell conflicting_flags
    (props (bbox 0 0 1000 1000))
    (port (name BAD) (dir inout)
          (layer sky130:met4) (flags power ground)
          (shape (rect 0 0 1000 1000)))))
