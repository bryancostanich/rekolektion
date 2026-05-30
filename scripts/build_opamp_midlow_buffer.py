"""Build cell_designs/dac/opamp_midlow_buffer.rkt.

Single-stage 5T OTA, PMOS LVT input pair + NMOS LVT current mirror,
for the chip-shared activation DAC taps 3-4 (240, 320 mV) — T06
Phase B0.

Schematic:
  source/cell_designs/dac/spice/opamp_midlow_buffer.spice
  .subckt opamp_midlow_buffer VIN VOUT VDD VSS VBP_LV VBN_LV
  XM_tail net_src VBP_LV VDD VDD pfet_01v8_lvt w=5  l=1 m=4
  XM1     net_d1 VIN     net_src VDD pfet_01v8_lvt w=28 l=2 m=1
  XM2     VOUT   VOUT    net_src VDD pfet_01v8_lvt w=28 l=2 m=1
  XM3     net_d1 net_d1  VSS VSS nfet_01v8_lvt w=10 l=2 m=1
  XM4     VOUT   net_d1  VSS VSS nfet_01v8_lvt w=10 l=2 m=1
  Rnull (none) — single-stage, no Miller comp.

Layout: 2-row strip. PMOS row at top (M_tail | M1 | M2, north-side
nwell tap, _botgate variant). NMOS row at bottom (M3 | M4,
south-side pwell tap, _topgate variant). VDD rail at top, VSS at
bottom. Internal nets routed through the inter-row channel.

VBN_LV is exposed as a port for interface compat with the other
buffer cells but is not internally connected (the schematic does
the same).
"""
from pathlib import Path

from rekolektion.io import rkt
from rekolektion.layout import (
    inspect_primitive, place_row, place_taps_around, place_rail,
)

# ─── Primitives ─────────────────────────────────────────────────────
P_TAIL = "pfet_01v8_lvt_W5p0_L1p0_m4_core_botgate"   # M_tail, m=4
P_PAIR = "pfet_01v8_lvt_W28p0_L2p0_core_botgate"     # M1, M2
N_MIR  = "nfet_01v8_lvt_W10p0_L2p0_core_topgate"     # M3, M4

p_tail_info = inspect_primitive(P_TAIL)
p_pair_info = inspect_primitive(P_PAIR)
n_mir_info  = inspect_primitive(N_MIR)
print(f"p_tail: {p_tail_info.bbox}")
print(f"p_pair: {p_pair_info.bbox}")
print(f"n_mir:  {n_mir_info.bbox}")

# ─── Row placement ──────────────────────────────────────────────────
# PMOS row, M_tail | M1 | M2 abutted west→east. Origins match Bryan's
# placement review (cell CENTERS at these x positions; `place_row`'s
# "origin = west edge" semantics shift centers by half-width, which
# doesn't match the hand-tuned positions). Same well type so per-cell
# nwells merge by abutment.
M_TAIL = rkt.SRef(cell=P_TAIL, origin=(980,  -7409))
M1     = rkt.SRef(cell=P_PAIR, origin=(3440, -7409))
M2     = rkt.SRef(cell=P_PAIR, origin=(6400, -7409))
pmos_row = [M_TAIL, M1, M2]

# NMOS column (rot=-90°): M3 stacked above M4, both at the same x.
# Rotating swings the FET fingers from vertical to horizontal —
# the gate strap exits east, S/D drop to the south, which lines the
# net_d1 path up with M1.D and gives a short cross-row connection.
# Bryan's positions:
NMOS_X = 4293
M3 = rkt.SRef(cell=N_MIR, origin=(NMOS_X, -24055), rot=-90.0)
M4 = rkt.SRef(cell=N_MIR, origin=(NMOS_X, -26905), rot=-90.0)
nmos_col = [M3, M4]

# ─── Rotated-bbox helper for tap placement ─────────────────────────
def rotated_bbox(prim_bbox, origin, rot_deg):
    """Return the axis-aligned parent bbox of a primitive SRef'd with
    rotation `rot_deg` (multiple of 90°) at `origin`."""
    import math
    bx1, by1, bx2, by2 = prim_bbox
    rad = math.radians(rot_deg)
    c, s = round(math.cos(rad)), round(math.sin(rad))
    corners = [(bx1, by1), (bx2, by1), (bx1, by2), (bx2, by2)]
    txed = [(c * x - s * y + origin[0], s * x + c * y + origin[1])
            for (x, y) in corners]
    xs, ys = [p[0] for p in txed], [p[1] for p in txed]
    return (min(xs), min(ys), max(xs), max(ys))

# ─── Body / substrate tap bands ─────────────────────────────────────
# PMOS row union bbox (parent coords) — unchanged shape, rot=0
p_x1 = min(s.origin[0] + p_pair_info.bbox[0] for s in pmos_row)
p_y1 = min(s.origin[1] + p_pair_info.bbox[1] for s in pmos_row)
p_x2 = max(s.origin[0] + p_pair_info.bbox[2] for s in pmos_row)
p_y2 = max(s.origin[1] + p_pair_info.bbox[3] for s in pmos_row)

# NMOS column union bbox — uses rotated_bbox since rot=-90°
nmos_bboxes = [rotated_bbox(n_mir_info.bbox, s.origin, s.rot) for s in nmos_col]
n_x1 = min(b[0] for b in nmos_bboxes)
n_y1 = min(b[1] for b in nmos_bboxes)
n_x2 = max(b[2] for b in nmos_bboxes)
n_y2 = max(b[3] for b in nmos_bboxes)

# nwell tap north of the PMOS row. Hard Rule #20 enforced via
# inside_srefs=pmos_row.
nwell_taps = place_taps_around(
    (p_x1, p_y1, p_x2, p_y2),
    "nwell", sides=("top",),
    inside_srefs=pmos_row,
)
# pwell tap south of the NMOS column. inside_srefs check uses
# pre-rotation gate-stamp state — this column uses `_topgate`, gate
# on cell-local +y → parent +x after rot=-90°, so the south side has
# no stamp and the check passes. (See follow-up TODO: rotation-aware
# Rule #20 check.)
pwell_taps = place_taps_around(
    (n_x1, n_y1, n_x2, n_y2),
    "pwell", sides=("bottom",),
    inside_srefs=nmos_col,
)

# ─── Parent nwell + hvi over the PMOS row ───────────────────────────
# Default-clearance taps put nwell-encl margin tight; pad the parent
# nwell to enclose both the FET row and the tap band south edges.
NWELL_MARGIN = 400
pmos_nwell = rkt.Rect(
    layer=rkt.named("sky130", "nwell"),
    x1=p_x1 - NWELL_MARGIN, y1=p_y1 - NWELL_MARGIN,
    x2=p_x2 + NWELL_MARGIN, y2=p_y2 + 1100,    # cover north tap band
)
# 1.8 V FETs don't need hvi; no hvi rect.

# ─── VDD / VSS rails (placeholder — exact dims wait on placement review) ─
# Stitch tap straps to rails via place_rail.
rail_x1 = min(p_x1, n_x1) - NWELL_MARGIN
rail_x2 = max(p_x2, n_x2) + NWELL_MARGIN
VDD_RAIL_HALF = 170          # 0.34 µm rail
VDD_RAIL_Y = nwell_taps.bands["top"][0].y1 if False else None  # filled below
# Locate the north tap band's li1 strap to align the rail.
nwell_strap = nwell_taps.li1_straps_by_side.get("top", [])
pwell_strap = pwell_taps.li1_straps_by_side.get("bottom", [])
# Place VDD rail centered on the nwell tap's li1 strap so place_rail
# can stitch cleanly.
if nwell_strap:
    vdd_strap_y = (nwell_strap[0].y1 + nwell_strap[0].y2) // 2
    vdd_rail_bbox = (rail_x1, vdd_strap_y - 30 - VDD_RAIL_HALF,
                     rail_x2, vdd_strap_y + 30 + VDD_RAIL_HALF)
    vdd_rail_els = place_rail(vdd_rail_bbox, label="VDD",
                              stitch_li1_straps=nwell_strap)
else:
    vdd_rail_els = []
if pwell_strap:
    vss_strap_y = (pwell_strap[0].y1 + pwell_strap[0].y2) // 2
    vss_rail_bbox = (rail_x1, vss_strap_y - 30 - VDD_RAIL_HALF,
                     rail_x2, vss_strap_y + 30 + VDD_RAIL_HALF)
    vss_rail_els = place_rail(vss_rail_bbox, label="VSS",
                              stitch_li1_straps=pwell_strap)
else:
    vss_rail_els = []

# ─── Phase 1: Power routing ─────────────────────────────────────────
# M_tail.S → VDD: parent met1 vertical at the S column (parent x=1625)
# bridges all 4 finger S met1 stubs to each other AND extends north
# to abut the VDD rail. The S finger met1 stubs come from the PDK
# mos_draw output (cell-local x=530..760, four stacked y-ranges per
# finger, covering primitive y from -11415 to +11415).
S_X = 1625                          # parent x of M_tail S column
D_X = 335                           # parent x of M_tail D column
S_STUB_HALF = 115                   # primitive stub is 230 nm wide
# Bridge across all 4 fingers + extend to VDD rail
mtail_s_y_min = -7409 + (-11415)    # = -18824
vdd_to_mtail = [
    rkt.Rect(layer=rkt.named("sky130", "met1"),
        x1=S_X - S_STUB_HALF, y1=mtail_s_y_min,
        x2=S_X + S_STUB_HALF, y2=vdd_strap_y + VDD_RAIL_HALF),
]

# Phase 2: net_src (M_tail.D + M1.S + M2.S)
#
# With fet generator v5, the m=4 M_tail primitive's D/S/G fingers and
# its 4 per-finger nwells are now bridged INSIDE the primitive (see
# _add_m_stacked_ties in fet.py). The primitive exposes a single
# (D, G, S, B) port set, so M_tail.D is one net at the parent and we
# don't need an external D-finger bridge.
#
# CRITICAL: M1/M2's primitive met1 D/S strips extend the FULL FET
# length (parent y=-21229..+6771). A horizontal MET1 bus crossing
# the row at any y in this range would silently SHORT to M1.D
# (= net_d1) AND M2.D (= VOUT) via same-layer same-net merge. So
# the bus runs on MET2 — met2 over met1 doesn't merge. via1 stacks
# at each pin column take the connection back to met1 → mcon → li1.
from rekolektion.layout import place_via
NET_SRC_BUS_Y = -20800
M1_S_X, M2_S_X = 4585, 7545

# via1 enclosure pad — symmetric 0.16 µm for via1.5 narrow + wide
VIA1_PAD_HALF = 160

def via1_pad(layer, x, y):
    return rkt.Rect(
        layer=rkt.named("sky130", layer),
        x1=x - VIA1_PAD_HALF, y1=y - VIA1_PAD_HALF,
        x2=x + VIA1_PAD_HALF, y2=y + VIA1_PAD_HALF,
    )

# M_tail.D pin position — D li1 column is at cell-local x=-645
# (parent x = 980 + (-645) = 335 with M_tail origin at (980, -7409)).
M_TAIL_D_X = M_TAIL.origin[0] + (-645)
# M_tail.D li1 column extends through all 4 fingers (now bridged
# inside the primitive). The southmost finger D li1 reaches parent
# y ≈ -18489 — we land the bus south of that and drop into the li1
# via a parent met1 pad + mcon.
M_TAIL_D_LI1_SOUTH = -7409 + (-11080)   # = -18489

net_src = [
    # MET2 horizontal bus across all three pin columns
    rkt.Rect(layer=rkt.named("sky130", "met2"),
        x1=M_TAIL_D_X - VIA1_PAD_HALF, y1=NET_SRC_BUS_Y - VIA1_PAD_HALF,
        x2=M2_S_X     + VIA1_PAD_HALF, y2=NET_SRC_BUS_Y + VIA1_PAD_HALF),
    # via1 stacks at each pin column (met1 → met2)
    *place_via((M_TAIL_D_X, NET_SRC_BUS_Y), "met1", "met2"),
    *place_via((M1_S_X,     NET_SRC_BUS_Y), "met1", "met2"),
    *place_via((M2_S_X,     NET_SRC_BUS_Y), "met1", "met2"),
    # Parent met1 pad + extension north to the underlying li1 column
    # at each pin. mcon stitches met1 to li1.
    # M_tail.D
    via1_pad("met1", M_TAIL_D_X, NET_SRC_BUS_Y),
    rkt.Rect(layer=rkt.named("sky130", "met1"),
        x1=M_TAIL_D_X - 115, y1=NET_SRC_BUS_Y - VIA1_PAD_HALF,
        x2=M_TAIL_D_X + 115, y2=M_TAIL_D_LI1_SOUTH + 200),
    rkt.Rect(layer=rkt.named("sky130", "mcon"),
        x1=M_TAIL_D_X - 85, y1=M_TAIL_D_LI1_SOUTH + 30,
        x2=M_TAIL_D_X + 85, y2=M_TAIL_D_LI1_SOUTH + 200),
    # M1.S
    via1_pad("met1", M1_S_X, NET_SRC_BUS_Y),
    rkt.Rect(layer=rkt.named("sky130", "met1"),
        x1=M1_S_X - 115, y1=NET_SRC_BUS_Y - VIA1_PAD_HALF,
        x2=M1_S_X + 115, y2=-19000),
    rkt.Rect(layer=rkt.named("sky130", "mcon"),
        x1=M1_S_X - 85, y1=-19200, x2=M1_S_X + 85, y2=-19030),
    # M2.S
    via1_pad("met1", M2_S_X, NET_SRC_BUS_Y),
    rkt.Rect(layer=rkt.named("sky130", "met1"),
        x1=M2_S_X - 115, y1=NET_SRC_BUS_Y - VIA1_PAD_HALF,
        x2=M2_S_X + 115, y2=-19000),
    rkt.Rect(layer=rkt.named("sky130", "mcon"),
        x1=M2_S_X - 85, y1=-19200, x2=M2_S_X + 85, y2=-19030),
]

# M3.S, M4.S → VSS: MET2 vertical at parent x=4138 (rotated-NMOS
# S column). Rotated FETs have D/S/G strips on BOTH li1 AND met1 as
# horizontal strips spanning the full rotated cell width — running
# any vertical wire on those layers through the NMOS column shorts
# net_d1 / VOUT into VSS (M4.D met1 horizontal strip at y≈-25.76
# was the specific trap). MET2 over those strips doesn't merge.
# via1+met1+mcon stacks at each connection point (M3.S, M4.S, VSS
# rail) drop met2 → met1 → li1.
NMOS_S_X = 4138
M3_S_Y = -25200
M4_S_Y = -28050
M2_HALF = 70                        # 140 nm met2 (met2.1 min)
M1_PAD_HALF = 160                   # 320 nm met1 pad for via1 enclosure
vss_to_nmos = [
    # MET2 vertical from north of M3.S down to VSS rail y
    rkt.Rect(layer=rkt.named("sky130", "met2"),
        x1=NMOS_S_X - M2_HALF, y1=vss_strap_y - VDD_RAIL_HALF,
        x2=NMOS_S_X + M2_HALF, y2=M3_S_Y + M1_PAD_HALF),
]

# Per-pin stack: met1 pad + via1 + mcon at each (M3.S, M4.S, VSS rail)
from rekolektion.layout import place_via as _place_via_for_vss
def _vss_stack(y):
    return [
        rkt.Rect(layer=rkt.named("sky130", "met1"),
            x1=NMOS_S_X - M1_PAD_HALF, y1=y - M1_PAD_HALF,
            x2=NMOS_S_X + M1_PAD_HALF, y2=y + M1_PAD_HALF),
        *_place_via_for_vss((NMOS_S_X, y), "met1", "met2"),
        rkt.Rect(layer=rkt.named("sky130", "mcon"),
            x1=NMOS_S_X - 85, y1=y - 85,
            x2=NMOS_S_X + 85, y2=y + 85),
    ]
vss_to_nmos += _vss_stack(M3_S_Y)
vss_to_nmos += _vss_stack(M4_S_Y)
# At VSS rail: rail is already met1, so only via1 needed (rail's met1
# acts as the met1 pad).
vss_to_nmos += list(_place_via_for_vss((NMOS_S_X, vss_strap_y), "met1", "met2"))


# ─── Phase 3: net_d1 (M1.D ↔ M3.D ↔ M3.G ↔ M4.G) ─────────────────────
# Bryan's hand-route (all on li1 plus mcons at each pin):
#   - vertical li1 at x=2210..2380 from M1.D south to inter-row channel
#   - jog west to x=2034..2204, continue south
#   - horizontal li1 at y=-22995..-22825 (M3.D rotated strip y)
#     extending east to x=9498
#   - vertical li1 at x=9328..9498 from M3.G to M4.G (rotated gate
#     strap column)
#   - mcons at M1.D (-7229), M3.D (-22910), M3.G (-24055), M4.G (-26905)
def _r(layer, x1, y1, x2, y2):
    return rkt.Rect(layer=rkt.named("sky130", layer), x1=x1, y1=y1, x2=x2, y2=y2)

net_d1 = [
    # mcons at the 4 pin sites
    _r("mcon", 2210, -7314, 2380, -7144),     # M1.D
    _r("mcon", 4053, -22995, 4223, -22825),   # M3.D
    _r("mcon", 9328, -24140, 9498, -23970),   # M3.G
    _r("mcon", 9328, -26990, 9498, -26820),   # M4.G
    # li1 west-spine: M1.D south through inter-row channel jogging west,
    # then east on M3.D's rotated horizontal strip
    _r("li1", 2210, -21308, 2380, -7144),     # vert at M1.D x
    _r("li1", 2034, -21308, 2380, -21138),    # jog corner
    _r("li1", 2034, -22328, 2204, -21138),    # continue south
    _r("li1", 2034, -22995, 2204, -22158),    # to M3.D y
    _r("li1", 2034, -22995, 4223, -22825),    # horizontal to M3.D
    _r("li1", 4053, -22995, 9498, -22825),    # continuation east to M3.G column
    _r("li1", 9328, -24140, 9498, -22825),    # corner at M3.G column going north
    _r("li1", 9328, -26990, 9498, -23970),    # M3.G ↔ M4.G vertical
]

# ─── Phase 4: VOUT (M2.D ↔ M2.G ↔ M4.D) ─────────────────────────────
# Bryan's hand-route — two segments:
#   (a) M2.D ↔ M2.G: met1 vertical at x=5185..5325 from M2.D south
#       to gate strap y, then horizontal to x=5325..6470 hitting M2.G
#   (b) M4.D ↔ M2.G via long east detour on met1: M4.D pad east to
#       x=9866, then north to y=-22314, then west to M2.G column.
#       Routes AROUND the FET bodies to avoid same-layer FET strip
#       crossings.
vout = [
    # mcons at M2.D, M2.G, M4.D
    _r("mcon", 5170, -7314, 5340, -7144),     # M2.D
    _r("mcon", 6315, -21634, 6485, -21464),   # M2.G
    _r("mcon", 4053, -25845, 4223, -25675),   # M4.D
    # Segment (a): M2.D ↔ M2.G
    _r("met1", 5095, -7389, 5415, -7069),     # M2.D met1 pad
    _r("met1", 6240, -21709, 6560, -21389),   # M2.G met1 pad
    _r("met1", 5185, -21619, 5325, -7159),    # vertical M2.D → M2.G y
    _r("met1", 5185, -21619, 6470, -21479),   # horizontal M2.D x → M2.G x
    # Segment (b): M4.D detour east + north + west to M2.G
    _r("met1", 3978, -25920, 4298, -25600),   # M4.D met1 pad
    _r("met1", 4068, -25830, 4208, -25644),   # short stub
    _r("met1", 4068, -25784, 9279, -25644),   # east trunk
    _r("met1", 9139, -25784, 9279, -25536),   # corner up
    _r("met1", 9139, -25676, 9866, -25536),   # east again
    _r("met1", 9726, -25676, 9866, -22314),   # long vertical north
    _r("met1", 6330, -22454, 9866, -22314),   # top horizontal west
    _r("met1", 6330, -22454, 6470, -21479),   # drop down to M2.G
]

# ─── VBN_LV interface-compat port ───────────────────────────────────
# Schematic exposes VBN_LV (used by sibling low-V / high-V buffers
# for the NMOS bias); midlow doesn't use it internally. Instead of
# painting a dangling labeled met1 island in silicon (hack), we use
# the `dangling_ports=["VBN_LV"]` kwarg at verify_lvs time — the
# schematic's port list gets stripped of VBN_LV before netgen runs.
# See rekolektion/src/rekolektion/verify/lvs.py:_apply_dangling_ports
# for the safety contract.

# ─── Net-intent labels (placement-only — for viz ratlines) ──────────
# NetName labels at each FET pin per the schematic. Internal nets
# (net_src, net_d1) carry internal=True so they appear in viz but
# are absent from the GDS Magic reads (no spurious LVS ports).
def pin_parent_coord(sref, info, terminal):
    """Translate a primitive's pin (cell-local) to parent coord, with
    rotation applied per the SRef. Picks the first finger if there are
    multiples (D0/D1/...). Rotation supports multiples of 90° via the
    reflect+rot matrix from the workflow doc (rot only, no reflect)."""
    import math
    p = info.pin(terminal)
    if p is None:
        for suffix in ("0", "1", "2", "3"):
            p = info.pin(f"{terminal}{suffix}")
            if p is not None:
                break
    if p is None:
        raise ValueError(f"no '{terminal}' pin in {sref.cell}")
    lx, ly = p.origin
    rot = getattr(sref, "rot", 0.0) or 0.0
    if rot:
        rad = math.radians(rot)
        c, s = round(math.cos(rad)), round(math.sin(rad))
        ox, oy = lx, ly
        lx, ly = c * ox - s * oy, s * ox + c * oy
    return (sref.origin[0] + lx, sref.origin[1] + ly)

def _net_label(text: str, origin: tuple[int, int], *, internal: bool = False) -> rkt.Label:
    return rkt.Label(
        layer=rkt.named("sky130", "li1_label"),
        text=text, origin=origin, internal=internal,
    )

# Schematic-driven net assignment, FET-by-FET pin.
intent_labels = [
    # M_tail (PMOS, m=4): D=net_src, S=VDD, G=VBP_LV
    _net_label("net_src", pin_parent_coord(M_TAIL, p_tail_info, "D"), internal=True),
    _net_label("VDD",     pin_parent_coord(M_TAIL, p_tail_info, "S")),
    _net_label("VBP_LV",  pin_parent_coord(M_TAIL, p_tail_info, "G")),
    # M1: D=net_d1, S=net_src, G=VIN
    _net_label("net_d1",  pin_parent_coord(M1, p_pair_info, "D"), internal=True),
    _net_label("net_src", pin_parent_coord(M1, p_pair_info, "S"), internal=True),
    _net_label("VIN",     pin_parent_coord(M1, p_pair_info, "G")),
    # M2: D=VOUT, S=net_src, G=VOUT (diode-tied — same net as drain)
    _net_label("VOUT",    pin_parent_coord(M2, p_pair_info, "D")),
    _net_label("net_src", pin_parent_coord(M2, p_pair_info, "S"), internal=True),
    _net_label("VOUT",    pin_parent_coord(M2, p_pair_info, "G")),
    # M3 (NMOS): D=net_d1, S=VSS, G=net_d1
    _net_label("net_d1",  pin_parent_coord(M3, n_mir_info, "D"), internal=True),
    _net_label("VSS",     pin_parent_coord(M3, n_mir_info, "S")),
    _net_label("net_d1",  pin_parent_coord(M3, n_mir_info, "G"), internal=True),
    # M4: D=VOUT, S=VSS, G=net_d1
    _net_label("VOUT",    pin_parent_coord(M4, n_mir_info, "D")),
    _net_label("VSS",     pin_parent_coord(M4, n_mir_info, "S")),
    _net_label("net_d1",  pin_parent_coord(M4, n_mir_info, "G"), internal=True),
]

# ─── Assemble — placement only (no signal routing yet) ───────────────
doc = rkt.Document(
    imports=[
        rkt.Import(path=f"../primitives/{P_TAIL}.rkt"),
        rkt.Import(path=f"../primitives/{P_PAIR}.rkt"),
        rkt.Import(path=f"../primitives/{N_MIR}.rkt"),
    ],
    cells=[
        rkt.Cell(
            name="opamp_midlow_buffer",
            elements=[
                pmos_nwell,
                *pmos_row, *nmos_col,
                *nwell_taps.elements, *pwell_taps.elements,
                *vdd_rail_els, *vss_rail_els,
                *vdd_to_mtail, *vss_to_nmos, *net_src,
                *net_d1, *vout,
                *intent_labels,
            ],
        ),
    ],
    top_cell="opamp_midlow_buffer",
)

out = Path("cell_designs/dac/opamp_midlow_buffer.rkt")
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(rkt.write(doc))
print(f"wrote {out}")
