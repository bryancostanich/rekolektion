"""Build cell_designs/reram_drv/cim_reram_bl_swap_mux.rkt.

Per-array MV PMOS polarity-swap MUX (T06 Phase B1). Single
pfet_g5v0d10v5 W=25 L=0.5 — mirror of Phase A srcmux's SETRR-side
PMOS. Routes V_SL DAC output to bl_rail during RESET.

Schematic:
  source/cim/reram/spice/cim_reram_drv_phaseB_bl.spice
  .subckt cim_reram_bl_swap_mux v_sl_dac_bus bl_rail bl_swap_mux_en_n vdd
  XM_sw bl_rail bl_swap_mux_en_n v_sl_dac_bus vdd pfet_g5v0d10v5 w=25 l=0.5

The cell has no internal nets — S / D / G / body all exit as port
labels on the primitive's own straps (S and D on li1 at y=0, G on li1
at y=+12820, body via parent n+ tap south of diff with a met1 "vdd"
strap on top).
"""
from pathlib import Path

from rekolektion.io import rkt
from rekolektion.layout import inspect_primitive, place_taps_around

# ─── Subcell ────────────────────────────────────────────────────────
PMOS25_NAME = "pfet_hv_W25p0_L0p5_nf10_core"
pmos_info = inspect_primitive(PMOS25_NAME)
print(f"pmos25 bbox: {pmos_info.bbox}")  # expect ≈ (-4275, -13000, 4275, 13000)

# Tiny "gate port" sub-cell: holds the PortName label
# "bl_swap_mux_en_n" on a small met2 patch. SRef'd inside the bl_swap
# top cell, the label becomes a SUB-CELL PORT — Magic's hierarchical
# extract names the W25 PMOS gate via this sub-cell port the same way
# Phase A srcmux does with lshift.OUT_N. Direct PARENT labels at the
# gate area trigger Magic's port-promotion bug: the auto-promoted
# w_..._# nwell port gets mis-merged with the parent gate label,
# tying gate to body. Using a sub-cell port-label avoids that path.
GATE_PORT_CELL = "bl_swap_mux_en_n_port"
# Sub-cell carries the bl_swap_mux_en_n label on a small met2 patch.
# When SRef'd at a coord north of all nwell, its labeled polygon
# sits unambiguously outside nwell. Magic's port-promotion rule that
# merges over-nwell labeled polygons with the primitive's B port
# doesn't apply, and the sub-cell port aliases correctly with the
# W=25 PMOS gate via parent met2 routing.
gate_port_subcell = rkt.Cell(
    name=GATE_PORT_CELL,
    elements=[
        rkt.Rect(layer=rkt.named("sky130", "met2"),
                 x1=-160, y1=-160, x2=160, y2=160),
        rkt.port_label(layer=rkt.named("sky130", "met2_label"),
                       text="bl_swap_mux_en_n", origin=(0, 0)),
    ],
)

# ─── Placement: single PMOS at origin ──────────────────────────────
pmos = rkt.SRef(cell=PMOS25_NAME, origin=(0, 0))

# ─── Parent nwell + hvi tub ────────────────────────────────────────
# The primitive carries its own nwell at y=±13000. We paint an
# enclosing parent nwell + hvi (margins generous enough to also
# enclose the south-side tap band that follows). Margin convention
# matches Phase A srcmux.
# Parent nwell + hvi paint. Extends the primitive's own nwell south
# to enclose the body-tap band; covers the FET interior so DRC's
# parent-nwell-encl-of-pdiff rule is satisfied.
pbb = pmos_info.bbox
NWELL_MARGIN = 400          # E/W margin past the FET
NWELL_MARGIN_N = 0          # north margin = 0 — keeps parent nwell
                            # top at primitive's nwell top. The
                            # bl_swap_mux_en_n gate label sits NORTH
                            # of the parent nwell, so Magic's port-
                            # promotion can't alias it with B (the
                            # primitive's nwell port).
NWELL_MARGIN_S = 900        # south margin to enclose tap band
nwell_x1 = pbb[0] - NWELL_MARGIN
nwell_x2 = pbb[2] + NWELL_MARGIN
nwell_y1 = pbb[1] - NWELL_MARGIN_S
nwell_y2 = pbb[3] + NWELL_MARGIN_N

parent_paints = [
    rkt.Rect(layer=rkt.named("sky130", "nwell"),
             x1=nwell_x1, y1=nwell_y1, x2=nwell_x2, y2=nwell_y2),
    rkt.Rect(layer=rkt.named("sky130", "hvi"),
             x1=nwell_x1, y1=nwell_y1, x2=nwell_x2, y2=nwell_y2),
]

# ─── Body tap band — south side ────────────────────────────────────
# inner_bbox = primitive's diff bbox (y=±12500, x=±4095 per the .rkt).
# place_taps_around defaults clearance_um=0.3, band_width≈0.42 µm →
# tap-band center ≈ y_min - 0.3 µm - 0.21 µm = -13010 nm; south edge
# of the implant rect ≈ -13220 nm. The 900 nm parent-nwell south
# margin (down to -13900) leaves > 0.65 µm enclosure of the n-tap
# implant (nwell.5: encl of n-tap ≥ 0.18 µm).
DIFF_X1, DIFF_Y1 = -4095, -12500
DIFF_X2, DIFF_Y2 = +4095, +12500

taps = place_taps_around(
    (DIFF_X1, DIFF_Y1, DIFF_X2, DIFF_Y2),
    "nwell",
    sides=("bottom",),
)

# ─── vdd rail over the tap strap ───────────────────────────────────
# Paint a met1 rail directly over the south tap band's li1 strap so
# the body net "vdd" carries through to a met1 polygon Magic will
# promote to a port. Stitch with mcon over the overlap. The strap's
# li1 geometry comes from place_taps_around; we figure the bbox out
# from the inner geometry to keep the rail width tight.
TAP_BAND_HALF = 210         # _DEFAULT_TAP_WIDTH_UM/2 (≈ 0.42 µm wide)
TAP_BAND_Y = DIFF_Y1 - 300 - TAP_BAND_HALF   # = -13010
RAIL_HALF = 170             # 0.34 µm rail (covers mcon enclosure)
RAIL_Y = TAP_BAND_Y          # center rail on strap

vdd_rail = rkt.Rect(
    layer=rkt.named("sky130", "met1"),
    x1=DIFF_X1, y1=RAIL_Y - RAIL_HALF,
    x2=DIFF_X2, y2=RAIL_Y + RAIL_HALF,
)

# mcon array stitching met1 ↔ li1 across the rail/strap overlap.
# Periodicity ≥ 0.34 µm pitch (mcon.2). Place 5 mcons spaced ~1.8 µm.
MCON_HALF = 85               # 0.17 µm cut
MCON_XS = [-3600, -1800, 0, 1800, 3600]
mcon_stitches = [
    rkt.Rect(layer=rkt.named("sky130", "mcon"),
             x1=x - MCON_HALF, y1=RAIL_Y - MCON_HALF,
             x2=x + MCON_HALF, y2=RAIL_Y + MCON_HALF)
    for x in MCON_XS
]

# ─── Port labels ──────────────────────────────────────────────────
# Use port_label so the labels carry PortName kind — sub-block port
# labels must not alias if this cell is SRef'd multiple times in a
# parent.
def port_lbl(layer: str, text: str, origin: tuple[int, int]) -> rkt.Label:
    return rkt.port_label(layer=rkt.named("sky130", layer),
                          text=text, origin=origin)

# Gate egress: route from primitive's met1 gate pad UP via via1 to
# met2, then north past the parent nwell top (+13005). Label on met2
# in a band where NO nwell polygon (parent or primitive) sits below.
# This avoids Magic's hierarchical auto-promote of the primitive's
# w_..._# nwell port merging with the gate label.
from rekolektion.layout import place_via

GATE_VIA_X = 395             # center of primitive gate pad (165..625)
GATE_VIA_Y = 12820           # primitive gate met1 pad center

# NO parent met1 pad — drop via1 directly onto the primitive's met1
# gate pad. Adding a parent-painted m1 polygon there creates an
# autonamed m1 node that Magic chains into the nwell port, tying gate
# to body. The primitive's met1 has 40 nm enclosure on the asymmetric
# axis (below the 60 nm wide-axis rule), which would trip via1.5b in
# strict mode but lands inside the primitive footprint waiver.
gate_via1 = list(place_via((GATE_VIA_X, GATE_VIA_Y), "met1", "met2"))

# Met2 wire from gate via1 north, extending well past the parent
# nwell top (y=+13005) and terminating at a labeled SUB-CELL pad.
# Phase A srcmux works because its W=25 PMOS gate label comes from
# a SUB-CELL PORT (lsh.OUT_N) whose labeled polygon is FAR from the
# nwell. Magic's port-promotion appears to merge any parent labeled
# polygon that overlaps nwell with the primitive's B port; the only
# safe spot is inside a sub-cell whose labeled polygon sits outside
# every nwell rectangle.
GATE_MET2_HALF = 160
GATE_MET2_Y_TOP = 14000      # 1 µm past primitive nwell top
gate_met2_wire = rkt.Rect(
    layer=rkt.named("sky130", "met2"),
    x1=GATE_VIA_X - GATE_MET2_HALF, y1=GATE_VIA_Y - GATE_MET2_HALF,
    x2=GATE_VIA_X + GATE_MET2_HALF, y2=GATE_MET2_Y_TOP,
)
GATE_SUBCELL_Y = 13800       # well above parent nwell top (13005)

# Pick label positions on the FET's existing li1 straps.
# Per the primitive: S at x ∈ {-3160, -1580, 0, +1580, +3160}, y=0.
#                    D at x ∈ {-3950, -2370, -790, +790, +2370}, y=0.
#                    G at x ∈ {-3555, -2765, ..., +3555}, y=+12820.
# Use the center finger for each net.
port_labels = [
    port_lbl("li1_label",  "v_sl_dac_bus",    (0,    0)),       # S center
    port_lbl("li1_label",  "bl_rail",         (790,  0)),       # D adjacent
    port_lbl("met1_label", "vdd",             (0,    RAIL_Y)),  # body rail
    # bl_swap_mux_en_n is supplied by the SRef'd gate_port_subcell
    # below, whose labeled met2 patch sits outside every nwell rect.
]

# SRef gate-port sub-cell at a coord north of all nwell, so its
# labeled met2 patch is unambiguously outside nwell. The gate met2
# wire extends north to overlap the sub-cell's patch and merge.
gate_port_sref = rkt.SRef(
    cell=GATE_PORT_CELL,
    origin=(GATE_VIA_X, GATE_SUBCELL_Y),
)

# ─── Assemble doc ──────────────────────────────────────────────────
doc = rkt.Document(
    imports=[
        rkt.Import(path=f"../primitives/{PMOS25_NAME}.rkt"),
    ],
    cells=[
        gate_port_subcell,
        rkt.Cell(
            name="cim_reram_bl_swap_mux",
            elements=[
                pmos,
                *parent_paints,
                *taps.elements,
                vdd_rail,
                *mcon_stitches,
                *gate_via1,
                gate_met2_wire,
                gate_port_sref,
                *port_labels,
            ],
        ),
    ],
    top_cell="cim_reram_bl_swap_mux",
)

out = Path("cell_designs/reram_drv/cim_reram_bl_swap_mux.rkt")
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(rkt.write(doc))
print(f"wrote {out}")
