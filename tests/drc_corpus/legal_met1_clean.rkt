; legal_met1_clean — a single 1 × 1 µm met1 square. Wider than
; met1.1 min (140 nm), bigger than met1.6 min area (0.083 µm²),
; no neighbors so spacing rules don't apply, no implants /
; transistor stack to involve other layers.
;
; Both engines (Magic + KLayout) MUST report 0 violations on this
; cell. Catches false-positive bugs in either F# rule
; implementation.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top legal_met1_clean)
  (cell legal_met1_clean
    (rect (layer sky130:met1) 0 0 1000 1000)))
