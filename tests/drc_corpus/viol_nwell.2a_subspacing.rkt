; viol_nwell.2a_subspacing — two nwells with a 1 µm gap violate
; min spacing (nwell.2a = 1.27 µm). Each nwell is 1 µm wide
; (clean per nwell.1 = 0.84 µm min). The 1 µm gap sits in the
; no-man's-land that the abut-or-tub rule names — Hard Rule #7
; in the workflow doc — so this also doubles as a regression
; canary for that pattern.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_nwell_2a_subspacing)
  (cell viol_nwell_2a_subspacing
    (rect (layer sky130:nwell)    0 0 1000 1000)
    (rect (layer sky130:nwell) 2000 0 3000 1000)))
