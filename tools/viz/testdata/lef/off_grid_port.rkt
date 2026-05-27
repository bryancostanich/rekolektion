; Off-grid fixture — bbox extent of 5003 DBU = 5.003 µm, not a
; multiple of the 0.005 µm sky130 manufacturing grid. The emitter
; must reject this with OffGridCoordinate.
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell off_grid_macro
    (props (bbox 0 0 5003 2000))))
