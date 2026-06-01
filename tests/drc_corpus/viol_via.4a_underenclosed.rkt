; viol_via.4a_underenclosed — a 150 × 150 nm via1 enclosed by met1
; with only 20 nm margin on each side (m1 = 0.19 × 0.19 µm). Min
; enclosure is 0.055 µm (via.4a in both Magic and KLayout deck).
;
; Different from `viol_via.1_subwidth` which has via WITHOUT met1
; (triggers via.4a_a "must be enclosed by m1" instead).  This cell
; is the corpus driver for the regular Enclosure-style via.4a
; check.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_via_4a_underenclosed)
  (cell viol_via_4a_underenclosed
    ; met1 below — 190 × 190 nm
    (rect (layer sky130:met1) -20 -20 170 170)
    ; via1 in the middle — 150 × 150 nm
    (rect (layer sky130:via)    0   0 150 150)
    ; met2 above — well enclosed (300 × 300 nm) so via.5a / via.5b
    ; don't ALSO fire and muddy the per-cell count
    (rect (layer sky130:met2) -75 -75 225 225)))
