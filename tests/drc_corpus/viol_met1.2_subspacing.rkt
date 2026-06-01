; viol_met1.2_subspacing — two parallel met1 rects with a 100 nm
; gap violate the min-spacing rule (met1.2 = 0.14 µm, KLayout m1.2).
;
; Rect A: (0,0)..(2000,200) — DRC-clean width (200 nm > 140 nm min)
; Rect B: (0,300)..(2000,500) — same; gap A.top→B.bot = 100 nm
;
; Expected violations: 1 tile (Magic, edge-pair on KLayout) per
; external run. Canonical Phase 4 Spacing fixture.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (top viol_met1_2_subspacing)
  (cell viol_met1_2_subspacing
    (rect (layer sky130:met1) 0   0 2000 200)
    (rect (layer sky130:met1) 0 300 2000 500)))
