"""Build cell_designs/khalkulo/cim_reram_drv_phaseA_srcmux.rkt.

Per-array source-rail MUX (D16, 1 instance per array).

Schematic (from cim_reram_drv_phaseA_sch.spice):
  XAND_FORM    pulse_req_1v8 pulse_kind_form_1v8  form_active_1v8 VDD VSS nand2_inv_lv
  XLSH_FORM    form_active_1v8 lsh_form_n lsh_form VDD VDDA1 VSS lshift_1v8_to_3v3
  XPMOS_FORM   SRC_RAIL lsh_form VDDA1 VDDA1 sky130_fd_pr__pfet_g5v0d10v5 w=25 l=0.5
  XAND_SETRR   pulse_req_1v8 pulse_kind_setrr_1v8 setrr_active_1v8 VDD VSS nand2_inv_lv
  XLSH_SETRR   setrr_active_1v8 lsh_setrr_n lsh_setrr VDD VDDA1 VSS lshift_1v8_to_3v3
  XPMOS_SETRR  SRC_RAIL lsh_setrr VDDA1 VDDA1 sky130_fd_pr__pfet_g5v0d10v5 w=25 l=0.5

Floorplan (user-approved): two pmos25 columns flank a central cluster
of nand+lshift cells stacked F (bottom) and S (top) mirrored.

  pmos25_FORM    [ nand_S | lshift_S ]    pmos25_SETRR
  (left col)     [ nand_F | lshift_F ]    (right col)

Routing phases:
  1. Power: VDD, VDDA1, VSS, SRC_RAIL rails on met2
  2. Local abut: none (cells are not abutted)
  3. Cross-row 2-pin: form_active_1v8 (nand_F.Y → lshift_F.IN);
                      same for SETRR; lsh_form (lshift_F.OUT_N →
                      pmos_F.G); same for SETRR
  4. Multi-fanout: pulse_req_1v8 (fans out to both nand.A pins)
"""
from pathlib import Path
from rekolektion.io import rkt
from rekolektion.layout import inspect_primitive, place_wire, place_via

# ─── Subcell info ────────────────────────────────────────────────────
NAND_NAME = "nand2_inv_lv"
LSHIFT_NAME = "lshift_1v8_to_3v3"
PMOS25_NAME = "pfet_hv_W25p0_L0p5_nf10_core"

SEARCH_DIRS = [
    Path("cell_designs/khalkulo"),
    Path("cell_designs/primitives"),
]
nand_info   = inspect_primitive(f"cell_designs/khalkulo/{NAND_NAME}.rkt", search_dirs=SEARCH_DIRS)
lshift_info = inspect_primitive(f"cell_designs/khalkulo/{LSHIFT_NAME}.rkt", search_dirs=SEARCH_DIRS)
pmos25_info = inspect_primitive(PMOS25_NAME)
print(f"nand bbox:   {nand_info.bbox}")
print(f"lshift bbox: {lshift_info.bbox}")
print(f"pmos25 bbox: {pmos25_info.bbox}")

# ─── Placement (user-approved v2 layout) ─────────────────────────────
# pmos25 columns flank a 3-row central stack:
#   top:    lshift_F & lshift_S, both rot=180 (flips IN/OUT_N to
#           land near the pmos.G column at the cell top)
#   middle: nand_F
#   bottom: nand_S
pmos_F = rkt.SRef(cell=PMOS25_NAME, origin=(18670, 0))
nand_F = rkt.SRef(cell=NAND_NAME,   origin=(24143, -4200))
lsh_F  = rkt.SRef(cell=LSHIFT_NAME, origin=(28535, 9850), rot=180.0)
nand_S = rkt.SRef(cell=NAND_NAME,   origin=(24130, -11711))
lsh_S  = rkt.SRef(cell=LSHIFT_NAME, origin=(34210, 9905), rot=180.0)
pmos_S = rkt.SRef(cell=PMOS25_NAME, origin=(39775, 0))
srefs = [pmos_F, nand_F, lsh_F, nand_S, lsh_S, pmos_S]

# Parent-paint nwell + hvi over each pmos25 cell (diff/tap.17:
# nwell overlap of pdiff ≥ 0.33 µm).
extra_paint = []
for sref in (pmos_F, pmos_S):
    bb = pmos25_info.bbox
    margin = 400
    extra_paint.append(rkt.Rect(layer=rkt.named("sky130", "nwell"),
        x1=sref.origin[0] + bb[0] - margin, y1=bb[1] - margin,
        x2=sref.origin[0] + bb[2] + margin, y2=bb[3] + margin))
    extra_paint.append(rkt.Rect(layer=rkt.named("sky130", "hvi"),
        x1=sref.origin[0] + bb[0] - margin, y1=bb[1] - margin,
        x2=sref.origin[0] + bb[2] + margin, y2=bb[3] + margin))

# Bridging nwell strips — merge pmos_F nwell, central-cluster
# nwells (nand_F, lsh_F, lsh_S, nand_S), and pmos_S nwell into a
# single continuous polygon so nwell.2a (1.27 µm spacing between
# distinct nwells) is satisfied structurally, not just electrically.
# All these nwells are at VDDA1 after the nand2 bulk-rebias, so
# merging is safe. Y bands picked to dodge every NFET row in the
# cluster:
#   Strip A y=-2885..5900 covers nand_F nwell (-2885..1350),
#     lsh_F nwell (1410..5845), lsh_S nwell (1465..5900).
#     Below nand_F NFETs (-4995..-3385) → above is OK.
#     Above is lsh_F NFETs (~6350+) → below is OK.
#   Strip B y=-10396..-6161 covers nand_S nwell exactly.
#     Above nand_S NFETs (top y=-10896) → below boundary is OK.
NWELL_X1 = 18670 + pmos25_info.bbox[2] - 400  # 400 nm into pmos_F nwell
NWELL_X2 = 39775 + pmos25_info.bbox[0] + 400  # 400 nm into pmos_S nwell
extra_paint.append(rkt.Rect(layer=rkt.named("sky130", "nwell"),
    x1=NWELL_X1, y1=-2885, x2=NWELL_X2, y2=5900))   # Strip A
extra_paint.append(rkt.Rect(layer=rkt.named("sky130", "nwell"),
    x1=NWELL_X1, y1=-10396, x2=NWELL_X2, y2=-6161)) # Strip B

# ─── Pin coord helper ────────────────────────────────────────────────
def pin_loc(sref, info, terminal):
    p = info.pin(terminal)
    if p is None:
        raise ValueError(f"no '{terminal}' in {sref.cell}")
    # Apply sref's rotation (currently only rot=0 or rot=180 are
    # handled; extend if other rotations are used).
    lx, ly = p.origin
    if abs(sref.rot - 180.0) < 0.001:
        lx, ly = -lx, -ly
    return (sref.origin[0] + lx, sref.origin[1] + ly)

# Cell pin coords in srcmux parent.
nf_VDD   = pin_loc(nand_F, nand_info, "VDD")
nf_VDDA1 = pin_loc(nand_F, nand_info, "VDDA1")
nf_VSS   = pin_loc(nand_F, nand_info, "VSS")
nf_A     = pin_loc(nand_F, nand_info, "A")
nf_B     = pin_loc(nand_F, nand_info, "B")
nf_Y     = pin_loc(nand_F, nand_info, "Y")
ns_VDD   = pin_loc(nand_S, nand_info, "VDD")
ns_VDDA1 = pin_loc(nand_S, nand_info, "VDDA1")
ns_VSS   = pin_loc(nand_S, nand_info, "VSS")
ns_A     = pin_loc(nand_S, nand_info, "A")
ns_B     = pin_loc(nand_S, nand_info, "B")
ns_Y     = pin_loc(nand_S, nand_info, "Y")
lf_VDD  = pin_loc(lsh_F, lshift_info, "VDD")
lf_VDDA1= pin_loc(lsh_F, lshift_info, "VDDA1")
lf_VSS  = pin_loc(lsh_F, lshift_info, "VSS")
lf_IN   = pin_loc(lsh_F, lshift_info, "IN")
lf_OUTN = pin_loc(lsh_F, lshift_info, "OUT_N")  # met1 label at (1460, 2882) — picks first
ls_VDD  = pin_loc(lsh_S, lshift_info, "VDD")
ls_VDDA1= pin_loc(lsh_S, lshift_info, "VDDA1")
ls_VSS  = pin_loc(lsh_S, lshift_info, "VSS")
ls_IN   = pin_loc(lsh_S, lshift_info, "IN")
ls_OUTN = pin_loc(lsh_S, lshift_info, "OUT_N")

# pmos25 D/S/G locations: each has multiple label positions (one per
# finger). The internal met2 D-strap (y=+12325) ties all D's; S-strap
# (y=-12325) ties all S's. Use those strap Y's for parent-paint
# connections rather than per-finger label coords.
PMOS_D_STRAP_Y = 12325
PMOS_S_STRAP_Y = -12325

# For pmos25's many D/G/S labels (one per finger), pick the central
# one as the representative pin coord.
def pmos25_pin(sref, terminal):
    pins = [p for p in pmos25_info.pins if p.terminal == terminal]
    center = pins[len(pins) // 2]
    return (sref.origin[0] + center.origin[0],
            sref.origin[1] + center.origin[1])

pmos_F_D = pmos25_pin(pmos_F, "D")
pmos_F_G = pmos25_pin(pmos_F, "G")
pmos_F_S = pmos25_pin(pmos_F, "S")
pmos_S_D = pmos25_pin(pmos_S, "D")
pmos_S_G = pmos25_pin(pmos_S, "G")
pmos_S_S = pmos25_pin(pmos_S, "S")

def lbl(layer, text, origin, internal=False):
    return rkt.Label(layer=rkt.named("sky130", layer), text=text,
                     origin=origin, internal=internal)

# ─── Phase 1 — Power ─────────────────────────────────────────────────
# pmos25 internal met2 D-strap: y=12150..12500, x=-4125..4125 (local)
# pmos25 internal met2 S-strap: y=-12500..-12150, x=-3335..3335 (local)
# Bridge each strap horizontally across the central cluster gap so
# pmos_F and pmos_S share one polygon per net (SRC_RAIL = D, VDDA1 = S).
power_routes = []

# SRC_RAIL bridge: met2 at y=12150..12400 (top kept ≥140 nm below
# lf/ls VSS via1 met2 enclosure bottoms at y=12540). Still
# overlaps pmos25 D-strap (y=12150..12500) for polygon merge.
# Extend 200 nm into each pmos25 cell so junction is overlap, not abut.
SRC_BRIDGE_X1 = pmos_F.origin[0] + 4125 - 200
SRC_BRIDGE_X2 = pmos_S.origin[0] - 4125 + 200
power_routes.append(rkt.Rect(
    layer=rkt.named("sky130", "met2"),
    x1=SRC_BRIDGE_X1, y1=12150,
    x2=SRC_BRIDGE_X2, y2=12400,
))

# VDDA1 bridge across pmos25 S-straps. Top kept ≥140 nm below
# nand_S's interior met2 (local y=-315..5 → global -12026..-11706 in
# nand_S; min gap 140 → bridge top ≤ -12166). Extend into pmos25
# S-strap by 200 nm so the merge is overlap, not bare abut.
VDDA1_BRIDGE_X1 = pmos_F.origin[0] + 3335 - 200
VDDA1_BRIDGE_X2 = pmos_S.origin[0] - 3335 + 200
power_routes.append(rkt.Rect(
    layer=rkt.named("sky130", "met2"),
    x1=VDDA1_BRIDGE_X1, y1=-12500,
    x2=VDDA1_BRIDGE_X2, y2=-12170,
))

# VDDA1 droppers — connect central cluster's 4 VDDA1 pins (on met1)
# down to the VDDA1 met2 strap. Pin X is fixed by subcell, but we
# can tap the cell's met1 VDDA1 rail at an X that clears nand2's
# nand_out vertical met2 columns (col0_D right≈24683, col1_D x≈
# 26063..26383, col2_D x≈27763..28083 in nand_F coords; nand_S
# shifts by -13 nm).
def vdda1_dropper(rail_y, tap_x, strap_y_top=-12170):
    """met1 VDDA1 rail at (tap_x, rail_y) — paint via1 here, then
    vertical met2 down to the strap."""
    rs = []
    rs.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
        x1=tap_x-160, y1=rail_y-160, x2=tap_x+160, y2=rail_y+160))
    rs.extend(place_via((tap_x, rail_y), "met1", "met2"))
    # met2 vertical, width 140 (min met2.1)
    rs.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
        x1=tap_x-70, y1=strap_y_top, x2=tap_x+70, y2=rail_y))
    return rs

# nand2 VDDA1 rail Y in cell-local: 4665..5055 → tap at midline y=4860
NAND_VDDA1_LOCAL_Y = 4860
LSH_VDDA1_LOCAL_Y = 7750   # lshift internal VDDA1 rail (in lshift local)

# nf, ns — tap at x=24900 (clears nand col0_D right 24683 by 147 nm
# and nand_S col0_D right 24670 by 160 nm).
power_routes.extend(vdda1_dropper(
    nand_F.origin[1] + NAND_VDDA1_LOCAL_Y, 24900))
power_routes.extend(vdda1_dropper(
    nand_S.origin[1] + NAND_VDDA1_LOCAL_Y, 24900))

# lf, ls — lshift is rot=180; the cell's VDDA1 rail (local y=7750)
# lands at global y = lsh.origin[1] - 7750. Tap at the existing
# (x=45) end-pin global X, which is the outer edge of the cell.
power_routes.extend(vdda1_dropper(
    lsh_F.origin[1] - LSH_VDDA1_LOCAL_Y, lsh_F.origin[0] - 45))
power_routes.extend(vdda1_dropper(
    lsh_S.origin[1] - LSH_VDDA1_LOCAL_Y, lsh_S.origin[0] - 45))

# VDD strap (central cluster). On met2 at y=3700, spanning the 3
# upper VDD pins (nf, lf, ls) with one extra vertical down to ns_VDD.
# y=3700 sits above all 4 cell VDD pins (max y=2650) and below
# lshift's NFET row (lf NFETs start ~y=6350).
VDD_STRAP_Y = 3700
VDD_STRAP_HALF = 70
VDD_STRAP_X1 = 25130
VDD_STRAP_X2 = 33500
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=VDD_STRAP_X1, y1=VDD_STRAP_Y - VDD_STRAP_HALF,
    x2=VDD_STRAP_X2, y2=VDD_STRAP_Y + VDD_STRAP_HALF))

def vdd_dropper_up(rail_y, tap_x):
    """tap met1 VDD rail at (tap_x, rail_y) → via1 → vertical met2
    up to VDD strap at VDD_STRAP_Y."""
    rs = []
    rs.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
        x1=tap_x-160, y1=rail_y-160, x2=tap_x+160, y2=rail_y+160))
    rs.extend(place_via((tap_x, rail_y), "met1", "met2"))
    rs.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
        x1=tap_x-70, y1=rail_y, x2=tap_x+70, y2=VDD_STRAP_Y))
    return rs

# nand2 VDD strap Y in cell-local: 4170..4310 → tap at midline 4240
NAND_VDD_LOCAL_Y = 4240
# nand_F: tap at x=25200 (between col0_D and col1_D nand_out met2,
# 230 nm clear of VDDA1 dropper at x=24900).
power_routes.extend(vdd_dropper_up(nand_F.origin[1] + NAND_VDD_LOCAL_Y, 25500))
# lf, ls VDD pins are at fixed lshift X.
power_routes.extend(vdd_dropper_up(2595, 27715))   # lf
power_routes.extend(vdd_dropper_up(2650, 33390))   # ls

# ns_VDD outlier: long vertical met2 from VDD strap (y=3700) down
# to nand_S VDD rail (y=-7471). x=25200 (same column as nf for
# consistency).
NS_VDD_RAIL_Y = nand_S.origin[1] + NAND_VDD_LOCAL_Y
NS_TAP_X = 25500
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=NS_TAP_X-160, y1=NS_VDD_RAIL_Y-160,
    x2=NS_TAP_X+160, y2=NS_VDD_RAIL_Y+160))
power_routes.extend(place_via((NS_TAP_X, NS_VDD_RAIL_Y), "met1", "met2"))
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=NS_TAP_X-70, y1=NS_VDD_RAIL_Y, x2=NS_TAP_X+70, y2=VDD_STRAP_Y))

# ─── VSS routing — met1 vertical up the LEFT of the cluster (x=23300,
# in the gap between pmos_F right x=22950 and central cluster left
# x≈23775). Stubs reach each cell's met1 VSS rail from the rail's
# left end. Top bridge spans across to grab both lf and ls rails.
# All one polygon on met1, single net.
VSS_VERT_X = 23300
VSS_VERT_HALF = 70   # 140 nm wide (min met1.1)
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=VSS_VERT_X-VSS_VERT_HALF, y1=-13211,
    x2=VSS_VERT_X+VSS_VERT_HALF, y2=12950))
# ns L: rail left edge x=23975, extend left to vertical at x=23370.
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=VSS_VERT_X-VSS_VERT_HALF, y1=-13211,
    x2=24175, y2=-12821))
# nf stub: rail left x=23988, extend left to vertical.
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=VSS_VERT_X-VSS_VERT_HALF, y1=-5700,
    x2=24188, y2=-5310))
# Top bridge spans from vertical across to ls rail right end —
# grabs lf VSS rail (24010..28690) on the way through.
# Y covers both lf (12505..12895) and ls (12560..12950) rail ranges.
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=VSS_VERT_X-VSS_VERT_HALF, y1=12505,
    x2=34565, y2=12950))

signal_routes = []
# pulse_req_1v8: nf.A → ns.A on li1.
# Each nand2's NFET-col0 gate already has a poly→licon→li1→mcon→
# met1 stack (from the NFET primitive). Extending the existing
# gate li1 patch leftward into the gap connects to a li1 vertical.
# No new mcon needed — the existing NFET gate mcon already bridges
# A pin met1 (with the parent's "pulse_req_1v8" label) to gate li1.
PR_VERT_X = 23600
PR_VERT_HALF = 85   # 170 nm wide (li1 min width)
# nf NFET-col0 gate li1 globally at (24578, -3665)-(24908, -3495).
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "li1"),
    x1=PR_VERT_X-PR_VERT_HALF, y1=-3665,
    x2=24908, y2=-3495))
# ns NFET-col0 gate li1 globally at (24565, -11176)-(24895, -11006).
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "li1"),
    x1=PR_VERT_X-PR_VERT_HALF, y1=-11176,
    x2=24895, y2=-11006))
# Vertical li1 bridging both extensions.
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "li1"),
    x1=PR_VERT_X-PR_VERT_HALF, y1=-11176,
    x2=PR_VERT_X+PR_VERT_HALF, y2=-3495))

# form_active_1v8: nf.Y (met2) → lf.IN (met1). Same X column
# (~27930). lf has internal met2 at this X (IN pin routing), so
# the vertical must be on met3. nf.Y already has met2 vertical;
# extend it slightly for via2 enclosure. lf.IN already has via1
# (met1↔met2) inside the lshift primitive; add via2 on top.
FA_X_NF = 27923   # nf.Y pin X
FA_X_LF = 27935   # lf.IN pin X
# Met2 landing pad at nf.Y (extends existing col2 met2 verticals)
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=FA_X_NF-185, y1=-1355, x2=FA_X_NF+185, y2=-985))
signal_routes.extend(place_via((FA_X_NF, -1170), "met2", "met3"))
# Met2 landing pad at lf.IN (covers existing IN met1/met2 + provides
# via2 enclosure)
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=FA_X_LF-185, y1=9010, x2=FA_X_LF+185, y2=9380))
signal_routes.extend(place_via((FA_X_LF, 9195), "met2", "met3"))
# Met3 wire connecting the two via2 cuts (full enclosure on both)
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=min(FA_X_NF, FA_X_LF)-195, y1=-1365,
    x2=max(FA_X_NF, FA_X_LF)+195, y2=9390))

# setrr_active_1v8: ns.Y → ls.IN. L-shape on met3 (horizontal at
# y=ns.Y from ns.Y to ls.IN column, then vertical up to ls.IN).
SR_X_NS = 27910
SR_X_LS = 33610
SR_Y_NS = -8681
SR_Y_LS = 9250
# Met2 landings at each endpoint
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=SR_X_NS-185, y1=SR_Y_NS-185, x2=SR_X_NS+185, y2=SR_Y_NS+185))
signal_routes.extend(place_via((SR_X_NS, SR_Y_NS), "met2", "met3"))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=SR_X_LS-185, y1=SR_Y_LS-185, x2=SR_X_LS+185, y2=SR_Y_LS+185))
signal_routes.extend(place_via((SR_X_LS, SR_Y_LS), "met2", "met3"))
# Met3 L: horizontal at y=ns.Y + vertical at x=ls.IN
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=SR_X_NS-195, y1=SR_Y_NS-195,
    x2=SR_X_LS+195, y2=SR_Y_NS+195))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=SR_X_LS-195, y1=SR_Y_NS-195,
    x2=SR_X_LS+195, y2=SR_Y_LS+195))

# lsh_form: lf.OUT_N (met1) → pmos_F.G (li1). L on met3.
# lf.OUT_N end: shift via2 cut UP to y=7160 (from OUT_N label y=6968)
# so met2 patch bot (6975) clears lshift's internal IN_n met2 (global
# y=6690..6830 inside lf) by 145 nm.
# pmos_F.G end: via1 lands on existing pmos25 G met1 (12705..12935).
# Via2 cut shifted UP to y=12950 so met2 patch bot (12765) clears
# pmos25 D-strap top (12500) by 265 nm.
LF_X, LF_Y = 27200, 8000   # extra X-shift right for 245 nm gap to lf met1 at x=26795
PG_X = 19065
PG_VIA1_Y = 12820
PG_VIA2_Y = 12950
# lf.OUT_N stack — symmetric 320×370 met1/met2 patches
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=LF_X-160, y1=LF_Y-185, x2=LF_X+160, y2=LF_Y+185))
signal_routes.extend(place_via((LF_X, LF_Y), "met1", "met2"))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=LF_X-160, y1=LF_Y-185, x2=LF_X+160, y2=LF_Y+185))
signal_routes.extend(place_via((LF_X, LF_Y), "met2", "met3"))
# pmos_F.G stack — met1 patch covers via1, met2 patch covers via2
# both vias stack at the same X column
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=PG_X-185, y1=12660, x2=PG_X+185, y2=13135))
signal_routes.extend(place_via((PG_X, PG_VIA1_Y), "met1", "met2"))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=PG_X-185, y1=12660, x2=PG_X+185, y2=13135))
signal_routes.extend(place_via((PG_X, PG_VIA2_Y), "met2", "met3"))
# Met3 L: vertical at lf.OUT_N X + horizontal at pmos_F.G's via2 Y
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=LF_X-195, y1=LF_Y-195, x2=LF_X+195, y2=PG_VIA2_Y+195))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=PG_X-195, y1=PG_VIA2_Y-195, x2=LF_X+195, y2=PG_VIA2_Y+195))

# lsh_setrr: ls.OUT_N → pmos_S.G. Mirror of lsh_form.
LS_X, LS_Y = 32875, 8000   # mirror of LF_X (ls OUT_N vertical center + 125)
PG_S_X = 40170
PG_S_VIA1_Y = 12820
PG_S_VIA2_Y = 12950
# ls.OUT_N stack
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=LS_X-160, y1=LS_Y-185, x2=LS_X+160, y2=LS_Y+185))
signal_routes.extend(place_via((LS_X, LS_Y), "met1", "met2"))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=LS_X-160, y1=LS_Y-185, x2=LS_X+160, y2=LS_Y+185))
signal_routes.extend(place_via((LS_X, LS_Y), "met2", "met3"))
# pmos_S.G stack — met1/met2 patches covering both vias
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=PG_S_X-185, y1=12660, x2=PG_S_X+185, y2=13135))
signal_routes.extend(place_via((PG_S_X, PG_S_VIA1_Y), "met1", "met2"))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=PG_S_X-185, y1=12660, x2=PG_S_X+185, y2=13135))
signal_routes.extend(place_via((PG_S_X, PG_S_VIA2_Y), "met2", "met3"))
# Met3 L: vertical at ls.OUT_N + horizontal at pmos_S.G's via2 Y
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=LS_X-195, y1=LS_Y-195, x2=LS_X+195, y2=PG_S_VIA2_Y+195))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=LS_X-195, y1=PG_S_VIA2_Y-195, x2=PG_S_X+195, y2=PG_S_VIA2_Y+195))

port_labels = [
    # ─── External cell ports ──────────────────────────────────────
    # VDD (1.8V, LV supply) — at nand.VDD and lshift.VDD pins
    lbl("met1_label", "VDD", nf_VDD),
    lbl("met1_label", "VDD", ns_VDD),
    lbl("met1_label", "VDD", lf_VDD),
    lbl("met1_label", "VDD", ls_VDD),
    # VDDA1 (3.3V, MV supply) — lshift VDDA1 + nand2 VDDA1 (nwell
    # bulk after rebias) + pmos25 S pins
    lbl("met1_label", "VDDA1", lf_VDDA1),
    lbl("met1_label", "VDDA1", ls_VDDA1),
    lbl("met1_label", "VDDA1", nf_VDDA1),
    lbl("met1_label", "VDDA1", ns_VDDA1),
    lbl("li1_label",  "VDDA1", pmos_F_S),
    lbl("li1_label",  "VDDA1", pmos_S_S),
    # VSS — all cell VSS pins
    lbl("met1_label", "VSS", nf_VSS),
    lbl("met1_label", "VSS", ns_VSS),
    lbl("met1_label", "VSS", lf_VSS),
    lbl("met1_label", "VSS", ls_VSS),
    # SRC_RAIL — pmos25 D (output)
    lbl("li1_label", "SRC_RAIL", pmos_F_D),
    lbl("li1_label", "SRC_RAIL", pmos_S_D),
    # Inputs
    lbl("met1_label", "pulse_req_1v8",        nf_A),
    lbl("met1_label", "pulse_req_1v8",        ns_A),
    lbl("met1_label", "pulse_kind_form_1v8",  nf_B),
    lbl("met1_label", "pulse_kind_setrr_1v8", ns_B),
    # ─── Internal nets (internal=True so they don't become ports) ─
    # form_active_1v8 = nand_F.Y → lshift_F.IN
    lbl("met2_label", "form_active_1v8",  nf_Y,   internal=True),
    lbl("met1_label", "form_active_1v8",  lf_IN,  internal=True),
    # setrr_active_1v8 = nand_S.Y → lshift_S.IN
    lbl("met2_label", "setrr_active_1v8", ns_Y,   internal=True),
    lbl("met1_label", "setrr_active_1v8", ls_IN,  internal=True),
    # lsh_form = lshift_F.OUT_N → pmos_F.G
    lbl("met1_label", "lsh_form",  lf_OUTN,  internal=True),
    lbl("li1_label",  "lsh_form",  pmos_F_G, internal=True),
    # lsh_setrr = lshift_S.OUT_N → pmos_S.G
    lbl("met1_label", "lsh_setrr", ls_OUTN,  internal=True),
    lbl("li1_label",  "lsh_setrr", pmos_S_G, internal=True),
]

# ─── Assemble doc ────────────────────────────────────────────────────
doc = rkt.Document(
    imports=[
        rkt.Import(path=f"./{NAND_NAME}.rkt"),
        rkt.Import(path=f"./{LSHIFT_NAME}.rkt"),
        rkt.Import(path=f"../primitives/{PMOS25_NAME}.rkt"),
    ],
    cells=[
        rkt.Cell(
            name='cim_reram_drv_phaseA_srcmux',
            elements=[
                *srefs,
                *extra_paint,
                *power_routes,
                *signal_routes,
                *port_labels,
            ],
        ),
    ],
    top_cell='cim_reram_drv_phaseA_srcmux',
)

out = Path("cell_designs/khalkulo/cim_reram_drv_phaseA_srcmux.rkt")
out.write_text(rkt.write(doc))
print(f"wrote {out}")
x_min = min(s.origin[0] + (pmos25_info.bbox[0] if s.cell == PMOS25_NAME else (nand_info.bbox[0] if s.cell == NAND_NAME else lshift_info.bbox[0])) for s in srefs)
x_max = max(s.origin[0] + (pmos25_info.bbox[2] if s.cell == PMOS25_NAME else (nand_info.bbox[2] if s.cell == NAND_NAME else lshift_info.bbox[2])) for s in srefs)
print(f"cell extent: x={x_min}..{x_max}")
