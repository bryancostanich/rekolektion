; First-consumer fixture for Rkt.ToLef — the shape khalkulo's
; ReRAM_IRL Track 09 (`tracks/09_array_assembly/plan.md`) array
; assembler will emit when it grows .rkt output.
;
; The cell extents come from Track 03's bit-cell streamed GDS
; (4.430 × 1.440 µm per cell, 2026-05-10) scaled to a 64-column ×
; 256-row tiling at the 4.600 × 1.440 µm identity step.
;
; Approximate bbox: ~295 × 369 µm. Snapped to 5 nm sky130 grid here
; for the test (real coordinates land at layout time).
;
; Ports declared:
;   - VDDA1 / VSS — power rails on met4 strap pattern.
;   - WL[i]       — wordline access pins per row (illustrative subset)
;   - BL[i] / SL[i] — column nets on met2 (per row 03 close-out, met2
;                     pin nubs are narrower than via1; Track 09 brings
;                     its own enclosure on its own strap — the LEF PIN
;                     declares the pin handle position, geometry width
;                     here is the placement handle, not the via target).
;   - MWL_EN[r]   — compute-mode multi-wordline enable (Track 09 owns).
;
; NOTE: This fixture is illustrative — a small subset of the real
; macro's 256 + 64 × 3 + 256 ports. Sufficient to exercise the
; LEF emitter against the production-shape declarations.
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell cim_reram_array_256x64
    (props (bbox 0 0 295000 369000)
           (description "256x64 ReRAM CIM array - Track 09")
           (owner "track-09"))
    ; Power on met4 — full-width strap at top and bottom.
    (port (name VDDA1) (dir inout)
          (layer sky130:met4) (flags power)
          (shape (rect 0 368900 295000 369000)))
    (port (name VSS) (dir inout)
          (layer sky130:met4) (flags ground)
          (shape (rect 0 0 295000 100)))
    ; A subset of wordlines (row 0 + row 255). Bracket-bus names are
    ; quoted because the .rkt symbol grammar reserves [ and ] for
    ; potential future use; quoted strings round-trip via the Reader.
    (port (name "WL[0]") (dir input)
          (layer sky130:met3) (flags signal)
          (shape (rect 100 500 200 600)))
    (port (name "WL[255]") (dir input)
          (layer sky130:met3) (flags signal)
          (shape (rect 100 367000 200 367100)))
    ; A subset of columns (col 0 BL/SL pair).
    (port (name "BL[0]") (dir inout)
          (layer sky130:met2) (flags signal)
          (shape (rect 100 1000 200 1100)))
    (port (name "SL[0]") (dir inout)
          (layer sky130:met2) (flags signal)
          (shape (rect 100 1200 200 1300)))
    ; Multi-wordline enable, compute-mode (per-row).
    (port (name "MWL_EN[0]") (dir input)
          (layer sky130:met3) (flags signal)
          (shape (rect 100 700 200 800)))
    (port (name "MWL_EN[255]") (dir input)
          (layer sky130:met3) (flags signal)
          (shape (rect 100 367200 200 367300)))))
