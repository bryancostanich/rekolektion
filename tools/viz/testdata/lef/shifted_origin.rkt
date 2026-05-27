; Fixture exercising negative bbox origin. SIZE should be (x1-x0) BY
; (y1-y0); ORIGIN should be the LEF-local frame offset, which is the
; negation of the bbox origin.
;
; bbox = (-1140, -720, 6430, 720) — on the sky130 5 nm grid (the
; docs/io/rkt.md illustrative example is off-grid; using a real
; on-grid extent here so the fixture exercises a passing case
; rather than tripping the OffGridCoordinate guard).
;
;   width  = 6430 - (-1140) = 7570 DBU = 7.570 µm
;   height =  720 - (-720)  = 1440 DBU = 1.440 µm
;   origin = (1140, 720) µm-shifted to (1.140, 0.720) so the LEF
;            frame starts at (0,0).
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell cim_reram_array_256x64
    (props (bbox -1140 -720 6430 720)
           (description "256x64 ReRAM CIM array - Track 09"))))
