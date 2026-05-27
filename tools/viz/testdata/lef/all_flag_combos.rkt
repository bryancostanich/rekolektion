; Flag-map fixture. Seven ports exercising every valid (USE, CLASS)
; mapping.
;
;   no-flags  → USE SIGNAL
;   signal    → USE SIGNAL
;   power     → USE POWER
;   ground    → USE GROUND
;   clock     → USE CLOCK
;   analog    → USE ANALOG
;   scan      → USE SIGNAL + CLASS SCAN  (scan alone implies signal)
;   signal+scan → USE SIGNAL + CLASS SCAN
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell all_flag_combos
    (props (bbox 0 0 4000 1000))
    (port (name P_NOFLAGS) (dir input)
          (layer sky130:met3)
          (shape (rect 100 100 200 200)))
    (port (name P_SIGNAL) (dir input)
          (layer sky130:met3) (flags signal)
          (shape (rect 300 100 400 200)))
    (port (name P_POWER) (dir inout)
          (layer sky130:met4) (flags power)
          (shape (rect 0 900 4000 1000)))
    (port (name P_GROUND) (dir inout)
          (layer sky130:met4) (flags ground)
          (shape (rect 0 0 4000 100)))
    (port (name P_CLOCK) (dir input)
          (layer sky130:met3) (flags clock)
          (shape (rect 500 100 600 200)))
    (port (name P_ANALOG) (dir input)
          (layer sky130:met3) (flags analog)
          (shape (rect 700 100 800 200)))
    (port (name P_SCAN_ALONE) (dir input)
          (layer sky130:met3) (flags scan)
          (shape (rect 900 100 1000 200)))
    (port (name P_SIG_SCAN) (dir input)
          (layer sky130:met3) (flags signal scan)
          (shape (rect 1100 100 1200 200)))))
