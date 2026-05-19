"""Build cell_designs/khalkulo/cim_reram_drv_phaseA_srcmux.rkt.

Per-array source-rail MUX (D16, 1 instance per array).

STRIP-DOWN (2026-05-18): placement-only rebuild. After accreted
routing left 102 DRC errors (44 in the user's hand-edited _edited_3
copy), we're restarting from validated SRef positions and walking
DRC clean before adding any routing back. Sequence:

  1. Placement only (this script) → DRC
  2. Power routing → DRC
  3. Signal routing → DRC
  4. Port labels + DEVICE_TERMINAL labels → LVS
  5. Re-extract netlist → 9-PVT re-run

Schematic (from cim_reram_drv_phaseA_srcmux_sch.spice):
  XAND_FORM    pulse_req_1v8 pulse_kind_form_1v8  form_active_1v8 VDD VDDA1 VSS nand2_inv_lv
  XLSH_FORM    form_active_1v8 lsh_form_n lsh_form VDD VDDA1 VSS lshift_1v8_to_3v3
  XPMOS_FORM   SRC_RAIL lsh_form VDDA1 VDDA1 pfet_g5v0d10v5 w=25 l=0.5
  XAND_SETRR   pulse_req_1v8 pulse_kind_setrr_1v8 setrr_active_1v8 VDD VDDA1 VSS nand2_inv_lv
  XLSH_SETRR   setrr_active_1v8 lsh_setrr_n lsh_setrr VDD VDDA1 VSS lshift_1v8_to_3v3
  XPMOS_SETRR  SRC_RAIL lsh_setrr VDDA1 VDDA1 pfet_g5v0d10v5 w=25 l=0.5
  XINV_PR_P    pulse_req_n pulse_req_1v8 VDD VDD pfet_01v8 w=2 l=0.15
  XINV_PR_N    pulse_req_n pulse_req_1v8 VSS VSS nfet_01v8 w=1 l=0.15
  XDISCHARGE   SRC_RAIL pulse_req_n VSS VSS nfet_g5v0d10v5 w=2 l=0.5

Placement (user-validated, from cim_reram_drv_phaseA_srcmux_edited_3.rkt):
  pmos_F  (18670, 0)
  nand_F  (24143, -4200)
  lsh_F   (28535, 9850) rot 180
  nand_S  (24130, -11711)
  lsh_S   (34210, 9905) rot 180
  pmos_S  (39775, 0)
  inv_p   (32396, -4599)
  inv_n   (31338, -5025) rot 180
  disch   (30087, -4527)
"""
from pathlib import Path
from rekolektion.io import rkt
from rekolektion.layout import inspect_primitive, place_via

# ─── Subcell info ────────────────────────────────────────────────────
NAND_NAME = "nand2_inv_lv"
LSHIFT_NAME = "lshift_1v8_to_3v3"
PMOS25_NAME = "pfet_hv_W25p0_L0p5_nf10_core"
INV_PFET_NAME = "pfet_01v8_W2p0_L0p15_core_botgate"
INV_NFET_NAME = "nfet_01v8_W1p0_L0p15_core_topgate"
DISCH_NFET_NAME = "nfet_hv_W2p0_L0p5_core_topgate"

SEARCH_DIRS = [
    Path("cell_designs/khalkulo"),
    Path("cell_designs/primitives"),
]
nand_info   = inspect_primitive(f"cell_designs/khalkulo/{NAND_NAME}.rkt", search_dirs=SEARCH_DIRS)
lshift_info = inspect_primitive(f"cell_designs/khalkulo/{LSHIFT_NAME}.rkt", search_dirs=SEARCH_DIRS)
pmos25_info = inspect_primitive(PMOS25_NAME)
inv_p_info  = inspect_primitive(INV_PFET_NAME)
inv_n_info  = inspect_primitive(INV_NFET_NAME)
disch_info  = inspect_primitive(DISCH_NFET_NAME)
print(f"nand bbox:   {nand_info.bbox}")
print(f"lshift bbox: {lshift_info.bbox}")
print(f"pmos25 bbox: {pmos25_info.bbox}")
print(f"inv_p bbox:  {inv_p_info.bbox}")
print(f"inv_n bbox:  {inv_n_info.bbox}")
print(f"disch bbox:  {disch_info.bbox}")

# ─── Placement (user-validated, _edited_3 positions) ────────────────
pmos_F = rkt.SRef(cell=PMOS25_NAME, origin=(18670, 0))
nand_F = rkt.SRef(cell=NAND_NAME,   origin=(24143, -4200))
lsh_F  = rkt.SRef(cell=LSHIFT_NAME, origin=(28535, 9850), rot=180.0)
nand_S = rkt.SRef(cell=NAND_NAME,   origin=(24130, -11711))
lsh_S  = rkt.SRef(cell=LSHIFT_NAME, origin=(34210, 9905), rot=180.0)
pmos_S = rkt.SRef(cell=PMOS25_NAME, origin=(39775, 0))

inv_p = rkt.SRef(cell=INV_PFET_NAME,   origin=(32705, -4599))  # user _edited_4
inv_n = rkt.SRef(cell=INV_NFET_NAME,   origin=(31282, -5025), rot=180.0)  # user _edited_4
disch = rkt.SRef(cell=DISCH_NFET_NAME, origin=(29747, -4527))  # fix A: -340 nm west to clear MV-LV diff spacing to inv_n

srefs = [pmos_F, nand_F, lsh_F, nand_S, lsh_S, pmos_S, inv_p, inv_n, disch]

# ─── Parent paint: nwell + hvi over pmos25 ──────────────────────────
# diff/tap.17: nwell overlap of pdiff ≥ 0.33 µm.
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

# ─── Nwell bridge strips ────────────────────────────────────────────
# Merge pmos_F + central cluster (nand_F, lsh_F, lsh_S, nand_S) + pmos_S
# into a single continuous nwell so nwell.2a (1.27 µm spacing) is
# satisfied structurally. All bulk-tied to VDDA1 → safe to merge.
# Y bands dodge every NFET row in the cluster.
NWELL_X1 = 18670 + pmos25_info.bbox[2] - 400  # 400 nm into pmos_F nwell
NWELL_X2 = 39775 + pmos25_info.bbox[0] + 400  # 400 nm into pmos_S nwell

# inv_p (1v8 PFET) sits between Strip A bottom and Strip B top, but its
# own internal nwell is biased to VDD (not VDDA1), so we can't merge
# with our strips. Cut Strip A and Strip B in the x-range around inv_p
# with 1.27 µm setback from its nwell, satisfying nwell.2a structurally.
INVP_CUT_X1 = 32705 - 555 - 1270   # inv_p left bbox - nwell setback
INVP_CUT_X2 = 32705 + 555 + 1270   # inv_p right bbox + nwell setback

# Strip A — west + east halves around inv_p
extra_paint.append(rkt.Rect(layer=rkt.named("sky130", "nwell"),
    x1=NWELL_X1, y1=-2885, x2=INVP_CUT_X1, y2=5900))  # Strip A-west
extra_paint.append(rkt.Rect(layer=rkt.named("sky130", "nwell"),
    x1=INVP_CUT_X2, y1=-2885, x2=NWELL_X2, y2=5900))  # Strip A-east

# Strip B — same cut topology
extra_paint.append(rkt.Rect(layer=rkt.named("sky130", "nwell"),
    x1=NWELL_X1, y1=-10396, x2=INVP_CUT_X1, y2=-6161))  # Strip B-west
extra_paint.append(rkt.Rect(layer=rkt.named("sky130", "nwell"),
    x1=INVP_CUT_X2, y1=-10396, x2=NWELL_X2, y2=-6161))  # Strip B-east

# ─── Power routing — Step 1: VSS ────────────────────────────────────
# Topology (user-approved from prior iteration):
#   - Met1 vertical at x=23300 (in gap between pmos_F right and central
#     cluster left). Width 140 nm (met1.1 min).
#   - L stubs to nand_F and nand_S VSS rails from their west ends.
#   - Top horizontal bridge grabs lsh_F and lsh_S VSS rails.
# New for the discharge cluster:
#   - Met2 trunk east of nand_F.VSS to bridge over disch.D (which
#     blocks straight-east met1 extension), then via1 down to disch.S
#     and inv_n.S met1 strips.
power_routes = []

VSS_VERT_X = 23300
VSS_VERT_HALF = 70  # 140 nm wide
# Main vertical met1
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=VSS_VERT_X - VSS_VERT_HALF, y1=-13211,
    x2=VSS_VERT_X + VSS_VERT_HALF, y2=12950))
# nand_S L stub: vertical → nand_S VSS rail (west end x=23975, y=-13211..-12821)
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=VSS_VERT_X - VSS_VERT_HALF, y1=-13211,
    x2=24175, y2=-12821))
# nand_F L stub: vertical → nand_F VSS rail (west end x=23988, y=-5700..-5310)
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=VSS_VERT_X - VSS_VERT_HALF, y1=-5700,
    x2=24188, y2=-5310))
# Top bridge: spans vertical → lsh_S east end (y covers lf/ls rails)
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=VSS_VERT_X - VSS_VERT_HALF, y1=12505,
    x2=34565, y2=12950))

# Discharge cluster VSS extension: met2 trunk east of nand_F.
# nand_F.VSS rail east end at x=28898, y=-5510 (centerline).
# Via1 there, met2 vertical jog up to y=-5000, horizontal east to
# inv_n.S, vias down at disch.S and inv_n.S labels.
VSS_M2_Y = -5000          # met2 trunk y (between disch.S and inv_n.S coverage)
VSS_M2_HALF = 70           # met2 trunk half-width (140 nm)
VSS_TAP_NF_X = 28870       # via1 X on nand_F.VSS rail — sized so the via1's 320 nm met2 pad (half=160 with 85 nm enclosure) clears lf VDDA1 dropper (east edge x=28560) by ≥150 nm met2 spacing
VSS_TAP_DISCH_X = 30142    # disch.S label X
VSS_TAP_INVN_X = 31062     # inv_n.S label X (after rot 180)

def m1_pad(x, y, half=160):
    """Met1 enclosure pad for a via1 cut — 320 nm square, ≥85 nm
    enclosure on the 150 nm cut to satisfy sky130 via.5a symmetric."""
    return rkt.Rect(layer=rkt.named("sky130", "met1"),
        x1=x-half, y1=y-half, x2=x+half, y2=y+half)

# Via1 from nand_F.VSS rail to met2 (rail center y=-5505)
power_routes.append(m1_pad(VSS_TAP_NF_X, -5505))
power_routes.extend(place_via((VSS_TAP_NF_X, -5505), "met1", "met2"))
# Met2 vertical jog from (-5505) up to (-5000) at VSS_TAP_NF_X
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=VSS_TAP_NF_X - VSS_M2_HALF, y1=-5505,
    x2=VSS_TAP_NF_X + VSS_M2_HALF, y2=VSS_M2_Y + VSS_M2_HALF))
# Met2 horizontal trunk east to inv_n.S column
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=VSS_TAP_NF_X - VSS_M2_HALF, y1=VSS_M2_Y - VSS_M2_HALF,
    x2=VSS_TAP_INVN_X + VSS_M2_HALF, y2=VSS_M2_Y + VSS_M2_HALF))
# Via1 down at disch.S (lands on disch.S met1 strip at y=-5000, present in y=-5682..-3682)
power_routes.append(m1_pad(VSS_TAP_DISCH_X, VSS_M2_Y))
power_routes.extend(place_via((VSS_TAP_DISCH_X, VSS_M2_Y), "met1", "met2"))
# Via1 down at inv_n.S (lands on inv_n.S strip at y=-5000, present in y=-5370..-4370)
power_routes.append(m1_pad(VSS_TAP_INVN_X, VSS_M2_Y))
power_routes.extend(place_via((VSS_TAP_INVN_X, VSS_M2_Y), "met1", "met2"))

# ─── Power routing — Step 2: VDDA1 ──────────────────────────────────
# Met2 bridge across pmos_F.S-strap (y=-12500..-12150) and pmos_S.S-strap.
# Bridge stays inside the pmos25 S-strap y range (extends 30 nm into
# strap top to keep clear of nand_S internal met2 at y=-12026..-11706).
# Then droppers up to each cluster cell's VDDA1 rail.
VDDA1_BRIDGE_X1 = pmos_F.origin[0] + 3335 - 200  # 200 nm overlap into pmos_F.S
VDDA1_BRIDGE_X2 = pmos_S.origin[0] - 3335 + 200  # 200 nm overlap into pmos_S.S
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=VDDA1_BRIDGE_X1, y1=-12500,
    x2=VDDA1_BRIDGE_X2, y2=-12170))

def vdda1_dropper(rail_y, tap_x, strap_y_top=-12170):
    """met1 pad at (tap_x, rail_y) on VDDA1 rail → via1 → met2 down to bridge."""
    rs = [m1_pad(tap_x, rail_y)]
    rs.extend(place_via((tap_x, rail_y), "met1", "met2"))
    rs.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
        x1=tap_x-70, y1=strap_y_top, x2=tap_x+70, y2=rail_y))
    return rs

# nand2 VDDA1 rail cell-local y = 4860
NAND_VDDA1_LOCAL_Y = 4860
# lshift VDDA1 rail cell-local y = 7750 (rot 180 → -y in global frame)
LSH_VDDA1_LOCAL_Y = 7750

# nf, ns droppers — tap at x=24900 (clears nand col0_D right ≈ 24683)
power_routes.extend(vdda1_dropper(nand_F.origin[1] + NAND_VDDA1_LOCAL_Y, 24900))
power_routes.extend(vdda1_dropper(nand_S.origin[1] + NAND_VDDA1_LOCAL_Y, 24900))
# lf, ls droppers — tap at the (45, 7750) end-pin x → after rot 180 + origin
power_routes.extend(vdda1_dropper(lsh_F.origin[1] - LSH_VDDA1_LOCAL_Y, lsh_F.origin[0] - 45))
power_routes.extend(vdda1_dropper(lsh_S.origin[1] - LSH_VDDA1_LOCAL_Y, lsh_S.origin[0] - 45))

# ─── Power routing — Step 3: VDD ────────────────────────────────────
# Met2 strap at y=3700 (between cluster VDD rails max y≈2650 and lsh
# NFET row at y≈6350). Droppers up from each cluster cell's VDD rail.
# nand_S sits south of its sibling; its dropper is a long vertical
# from the strap down to nand_S.VDD rail at y=-7471.
VDD_STRAP_Y = 3700
VDD_STRAP_HALF = 70
VDD_STRAP_X1 = 25130
VDD_STRAP_X2 = 33500
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=VDD_STRAP_X1, y1=VDD_STRAP_Y - VDD_STRAP_HALF,
    x2=VDD_STRAP_X2, y2=VDD_STRAP_Y + VDD_STRAP_HALF))

def vdd_dropper_up(rail_y, tap_x):
    """met1 pad at (tap_x, rail_y) on VDD rail → via1 → met2 up to strap."""
    rs = [m1_pad(tap_x, rail_y)]
    rs.extend(place_via((tap_x, rail_y), "met1", "met2"))
    rs.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
        x1=tap_x-70, y1=rail_y, x2=tap_x+70, y2=VDD_STRAP_Y))
    return rs

# nand2 VDD rail cell-local y = 4240
NAND_VDD_LOCAL_Y = 4240
# nand_F VDD tap at x=25500 (clears VDDA1 dropper at x=24900 by 600 nm)
power_routes.extend(vdd_dropper_up(nand_F.origin[1] + NAND_VDD_LOCAL_Y, 25500))
# lf, ls VDD pins at fixed lshift X (label positions after rot 180 + origin)
power_routes.extend(vdd_dropper_up(2595, 27715))   # lf
power_routes.extend(vdd_dropper_up(2650, 33390))   # ls

# nand_S sits below the strap — long vertical met2 from strap down
# to nand_S.VDD rail at y=-7471, same x column as nf for consistency.
NS_VDD_RAIL_Y = nand_S.origin[1] + NAND_VDD_LOCAL_Y
NS_TAP_X = 25500
power_routes.append(m1_pad(NS_TAP_X, NS_VDD_RAIL_Y))
power_routes.extend(place_via((NS_TAP_X, NS_VDD_RAIL_Y), "met1", "met2"))
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=NS_TAP_X-70, y1=NS_VDD_RAIL_Y, x2=NS_TAP_X+70, y2=VDD_STRAP_Y))

# inv_p VDD (its S strip is the source rail at 1.8 V supply).
# inv_p.S strip global: x=32810..33040, y=-5419..-3419.
# Path: met1 east-jog from S strip into via1 column at x=33390 →
# via1 → met2 vertical UP, merging with the existing lsh_S VDD
# dropper at x=33390 (same net, same column).
INVP_VDD_Y = -4419
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=32900, y1=INVP_VDD_Y - 160, x2=33550, y2=INVP_VDD_Y + 160))
power_routes.extend(place_via((33390, INVP_VDD_Y), "met1", "met2"))
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=33390-70, y1=INVP_VDD_Y, x2=33390+70, y2=2650))

# ─── Power routing — Step 4: SRC_RAIL ───────────────────────────────
# Met2 top bridge spans pmos_F.D-strap (y=12150..12500) → pmos_S.D-strap.
# Bridge kept at y=12150..12400 (top stays ≥140 nm below the cell's
# lsh_F/lsh_S VSS via1 met2 enclosures at y=12540).
SRC_BRIDGE_X1 = pmos_F.origin[0] + 4125 - 200  # 200 nm overlap into pmos_F.D
SRC_BRIDGE_X2 = pmos_S.origin[0] - 4125 + 200  # 200 nm overlap into pmos_S.D
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=SRC_BRIDGE_X1, y1=12150,
    x2=SRC_BRIDGE_X2, y2=12400))

# disch.D → SRC bridge: met1 vertical extending the disch.D strip
# north, then via1 up to met2 inside the SRC bridge near the top.
# Met1 is used for the long vertical so we don't blow through the
# VDD met2 strap at y=3700 (same-layer met2 short).
# Column is offset slightly west of the strip center so it stays
# clear of the gate pad east of disch.D.
# - disch.D strip:   x=29237..29467, y=-5682..-3682
# - VSS top bridge:  x=23230..34565, y=12505..12950 (met1 — must
#                    keep ≥140 nm clearance to our met1 pad)
# - SRC bridge:      x=22595..35850, y=12150..12400 (met2 — target)
SRC_VERT_X1 = 29230
SRC_VERT_X2 = 29370
power_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=SRC_VERT_X1, y1=-5682, x2=SRC_VERT_X2, y2=12100))
# Transition to met2 inside SRC bridge y range. Met1 pad top stays
# 145 nm clear of VSS top bridge bottom.
SRC_VIA_X = (SRC_VERT_X1 + SRC_VERT_X2) // 2   # 29330
SRC_VIA_Y = 12200
power_routes.append(m1_pad(SRC_VIA_X, SRC_VIA_Y))
power_routes.extend(place_via((SRC_VIA_X, SRC_VIA_Y), "met1", "met2"))

# ─── Signal routing — Step 1: internal nets ─────────────────────────
signal_routes = []

# pulse_req_1v8: nf.A → ns.A on li1. Both nand2's NFET-col0 gates
# already have a poly→licon→li1→mcon→met1 stack inside the nand2
# primitive. Extending the existing gate li1 patch leftward and a
# vertical li1 spine bridges the two cells.
PR_VERT_X = 23700
PR_VERT_HALF = 85   # 170 nm (li1.1 ≥ 170)
# nf NFET-col0 gate li1 at global (24578..24908, -3665..-3495)
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "li1"),
    x1=PR_VERT_X-PR_VERT_HALF, y1=-3665,
    x2=24908, y2=-3495))
# ns NFET-col0 gate li1 at global (24565..24895, -11176..-11006)
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "li1"),
    x1=PR_VERT_X-PR_VERT_HALF, y1=-11176,
    x2=24895, y2=-11006))
# Vertical li1 spine
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "li1"),
    x1=PR_VERT_X-PR_VERT_HALF, y1=-11176,
    x2=PR_VERT_X+PR_VERT_HALF, y2=-3495))

# form_active_1v8: nf.Y (met2) → lf.IN (met1). Vertical met3 with
# via2 stacks at both ends.
FA_X_NF = 27923   # nf.Y pin X (after nand_F origin)
FA_X_LF = 27935   # lf.IN pin X
# nf.Y end stack
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=FA_X_NF-185, y1=-1355, x2=FA_X_NF+185, y2=-985))
signal_routes.extend(place_via((FA_X_NF, -1170), "met2", "met3"))
# lf.IN end stack
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=FA_X_LF-185, y1=9010, x2=FA_X_LF+185, y2=9380))
signal_routes.extend(place_via((FA_X_LF, 9195), "met2", "met3"))
# Met3 vertical bridge between via2 cuts
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=min(FA_X_NF, FA_X_LF)-195, y1=-1365,
    x2=max(FA_X_NF, FA_X_LF)+195, y2=9390))

# setrr_active_1v8: ns.Y → ls.IN. L on met3.
SR_X_NS = 27910
SR_X_LS = 33610
SR_Y_NS = -8681
SR_Y_LS = 9250
# ns.Y end stack
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=SR_X_NS-185, y1=SR_Y_NS-185, x2=SR_X_NS+185, y2=SR_Y_NS+185))
signal_routes.extend(place_via((SR_X_NS, SR_Y_NS), "met2", "met3"))
# ls.IN end stack
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=SR_X_LS-185, y1=SR_Y_LS-185, x2=SR_X_LS+185, y2=SR_Y_LS+185))
signal_routes.extend(place_via((SR_X_LS, SR_Y_LS), "met2", "met3"))
# Met3 L: horizontal at SR_Y_NS + vertical at SR_X_LS
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=SR_X_NS-195, y1=SR_Y_NS-195,
    x2=SR_X_LS+195, y2=SR_Y_NS+195))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=SR_X_LS-195, y1=SR_Y_NS-195,
    x2=SR_X_LS+195, y2=SR_Y_LS+195))

# lsh_form: lf.OUT_N → pmos_F.G. L on met3.
LF_X, LF_Y = 27200, 8000   # lf.OUT_N stack location
PG_X = 19065               # pmos_F.G column
PG_VIA1_Y = 12820
PG_VIA2_Y = 12950
# lf.OUT_N stack: met1 + via1 + met2 + via2
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=LF_X-160, y1=LF_Y-185, x2=LF_X+160, y2=LF_Y+185))
signal_routes.extend(place_via((LF_X, LF_Y), "met1", "met2"))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=LF_X-160, y1=LF_Y-185, x2=LF_X+160, y2=LF_Y+185))
signal_routes.extend(place_via((LF_X, LF_Y), "met2", "met3"))
# pmos_F.G stack
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=PG_X-185, y1=12660, x2=PG_X+185, y2=13135))
signal_routes.extend(place_via((PG_X, PG_VIA1_Y), "met1", "met2"))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=PG_X-185, y1=12660, x2=PG_X+185, y2=13135))
signal_routes.extend(place_via((PG_X, PG_VIA2_Y), "met2", "met3"))
# Met3 L: vertical at LF_X + horizontal at PG_VIA2_Y
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=LF_X-195, y1=LF_Y-195, x2=LF_X+195, y2=PG_VIA2_Y+195))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=PG_X-195, y1=PG_VIA2_Y-195, x2=LF_X+195, y2=PG_VIA2_Y+195))

# lsh_setrr: ls.OUT_N → pmos_S.G. Mirror of lsh_form.
LS_X, LS_Y = 32875, 8000
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
# pmos_S.G stack
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=PG_S_X-185, y1=12660, x2=PG_S_X+185, y2=13135))
signal_routes.extend(place_via((PG_S_X, PG_S_VIA1_Y), "met1", "met2"))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met2"),
    x1=PG_S_X-185, y1=12660, x2=PG_S_X+185, y2=13135))
signal_routes.extend(place_via((PG_S_X, PG_S_VIA2_Y), "met2", "met3"))
# Met3 L: vertical at LS_X + horizontal at PG_S_VIA2_Y
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=LS_X-195, y1=LS_Y-195, x2=LS_X+195, y2=PG_S_VIA2_Y+195))
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=LS_X-195, y1=PG_S_VIA2_Y-195, x2=PG_S_X+195, y2=PG_S_VIA2_Y+195))

# ─── Signal routing — Step 2: pulse_req_n ───────────────────────────
# Connects the inverter output (inv_p.D + inv_n.D) to disch.G. All on
# met1. Topology:
#   A. inv_p.D ↔ inv_n.D bridge (horizontal at y≈-4450, inside both
#      D-strip y ranges; runs through the gap between inv_n east and
#      inv_p west).
#   B. inv_n.D north extension (vertical above inv_n.D's strip top
#      from y=-4370 north to y=-3290, in the open area above inv_n).
#   C. Horizontal at y=-3430..-3290 from disch.G pad east to overlap
#      the vertical at the top.
# Pin coords used:
#   inv_p.D strip: x=32370..32600, y=-5419..-3419
#   inv_n.D strip: x=31387..31617, y=-5370..-4370 (rot 180 swaps L/R)
#   disch.G pad:   x=29517..29977, y=-3522..-3292

# A. inv_p.D ↔ inv_n.D bridge (140 nm tall, inside both strips' y).
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=31500, y1=-4520, x2=32500, y2=-4380))

# B. inv_n.D north extension (overlaps strip top by 30 nm).
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=31387, y1=-4400, x2=31617, y2=-3290))

# C. Horizontal to disch.G (overlaps disch.G pad in y=-3430..-3292 = 138 nm).
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=29517, y1=-3430, x2=31617, y2=-3290))

# ─── Signal routing — Step 3: pulse_req_1v8 fan-out ─────────────────
# The existing li1 vertical handles nf.A and ns.A gates. Extend to
# the discharge cluster gates (inv_p.G + inv_n.G) via a met3 trunk.
# Transition stack drops in the empty band between nand_F (south
# edge y=-5840) and nand_S (north edge y=-6161). All four metal
# layers stacked at the same point: li1 → mcon → met1 → via1 →
# met2 → via2 → met3.
def m2_pad(x, y, half=185):
    """Met2 pad for via2 lower enclosure (≥85 nm on a 200 nm cut)."""
    return rkt.Rect(layer=rkt.named("sky130", "met2"),
        x1=x-half, y1=y-half, x2=x+half, y2=y+half)

def m3_pad(x, y, half=195):
    """Met3 pad for via2 upper enclosure (≥95 nm on a 200 nm cut)."""
    return rkt.Rect(layer=rkt.named("sky130", "met3"),
        x1=x-half, y1=y-half, x2=x+half, y2=y+half)

# Transition stack on li1 vertical at (23700, -6000).
PR_TRANS_X = PR_VERT_X
PR_TRANS_Y = -6000
signal_routes.append(m1_pad(PR_TRANS_X, PR_TRANS_Y))
signal_routes.extend(place_via((PR_TRANS_X, PR_TRANS_Y), "li1", "met1"))
signal_routes.append(m2_pad(PR_TRANS_X, PR_TRANS_Y))
signal_routes.extend(place_via((PR_TRANS_X, PR_TRANS_Y), "met1", "met2"))
signal_routes.append(m3_pad(PR_TRANS_X, PR_TRANS_Y))
signal_routes.extend(place_via((PR_TRANS_X, PR_TRANS_Y), "met2", "met3"))

# Met3 trunk east at y=-5700 ±195 (390 nm tall — wide enough to
# fully enclose via2 cuts at both gate sites and to overlap the
# transition stack's m3_pad).
PR_FAN_Y = -5700
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met3"),
    x1=PR_TRANS_X-195, y1=PR_FAN_Y-195,
    x2=33000, y2=PR_FAN_Y+195))

# inv_n.G drop at (31282, -5645). Asymmetric m1_pad — top edge kept
# 170 nm clear of inv_n.S strip (south edge y=-5370) by trimming the
# pad to y=-5540 (still gives 30 nm top via1 enclosure, 85 nm bottom).
signal_routes.append(rkt.Rect(layer=rkt.named("sky130", "met1"),
    x1=31122, y1=-5805, x2=31442, y2=-5510))
signal_routes.append(m2_pad(31282, -5645))
signal_routes.extend(place_via((31282, -5645), "met1", "met2"))
signal_routes.extend(place_via((31282, -5645), "met2", "met3"))

# inv_p.G drop at (32705, -5739). Symmetric m1_pad.
signal_routes.append(m1_pad(32705, -5739))
signal_routes.append(m2_pad(32705, -5739))
signal_routes.extend(place_via((32705, -5739), "met1", "met2"))
signal_routes.extend(place_via((32705, -5739), "met2", "met3"))

# ─── Pin coord helpers ──────────────────────────────────────────────
def pin_loc(sref, info, terminal):
    p = info.pin(terminal)
    if p is None:
        raise ValueError(f"no '{terminal}' in {sref.cell}")
    lx, ly = p.origin
    if abs(sref.rot - 180.0) < 0.001:
        lx, ly = -lx, -ly
    return (sref.origin[0] + lx, sref.origin[1] + ly)

def pmos25_pin(sref, terminal):
    pins = [p for p in pmos25_info.pins if p.terminal == terminal]
    center = pins[len(pins) // 2]
    return (sref.origin[0] + center.origin[0],
            sref.origin[1] + center.origin[1])

# Cluster cell pin coords
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
lf_VDD   = pin_loc(lsh_F, lshift_info, "VDD")
lf_VDDA1 = pin_loc(lsh_F, lshift_info, "VDDA1")
lf_VSS   = pin_loc(lsh_F, lshift_info, "VSS")
lf_IN    = pin_loc(lsh_F, lshift_info, "IN")
lf_OUTN  = pin_loc(lsh_F, lshift_info, "OUT_N")
ls_VDD   = pin_loc(lsh_S, lshift_info, "VDD")
ls_VDDA1 = pin_loc(lsh_S, lshift_info, "VDDA1")
ls_VSS   = pin_loc(lsh_S, lshift_info, "VSS")
ls_IN    = pin_loc(lsh_S, lshift_info, "IN")
ls_OUTN  = pin_loc(lsh_S, lshift_info, "OUT_N")
pmos_F_D = pmos25_pin(pmos_F, "D")
pmos_F_G = pmos25_pin(pmos_F, "G")
pmos_F_S = pmos25_pin(pmos_F, "S")
pmos_S_D = pmos25_pin(pmos_S, "D")
pmos_S_G = pmos25_pin(pmos_S, "G")
pmos_S_S = pmos25_pin(pmos_S, "S")

_KINDS = {"port-name": rkt.LabelKind.PORT_NAME,
          "net-name":  rkt.LabelKind.NET_NAME}
def lbl(layer, text, origin, kind="port-name"):
    return rkt.Label(layer=rkt.named("sky130", layer), text=text,
                     origin=origin, kind=_KINDS[kind])

# ─── Port labels + internal-net labels for LVS ──────────────────────
port_labels = [
    # ─── External ports ─────────────────────────────────────────────
    lbl("met1_label", "VDD", nf_VDD),
    lbl("met1_label", "VDD", ns_VDD),
    lbl("met1_label", "VDD", lf_VDD),
    lbl("met1_label", "VDD", ls_VDD),
    lbl("met1_label", "VDDA1", lf_VDDA1),
    lbl("met1_label", "VDDA1", ls_VDDA1),
    lbl("met1_label", "VDDA1", nf_VDDA1),
    lbl("met1_label", "VDDA1", ns_VDDA1),
    lbl("li1_label",  "VDDA1", pmos_F_S),
    lbl("li1_label",  "VDDA1", pmos_S_S),
    lbl("met1_label", "VSS", nf_VSS),
    lbl("met1_label", "VSS", ns_VSS),
    lbl("met1_label", "VSS", lf_VSS),
    lbl("met1_label", "VSS", ls_VSS),
    lbl("li1_label",  "SRC_RAIL", pmos_F_D),
    lbl("li1_label",  "SRC_RAIL", pmos_S_D),
    lbl("met1_label", "pulse_req_1v8",        nf_A),
    lbl("met1_label", "pulse_req_1v8",        ns_A),
    lbl("met1_label", "pulse_kind_form_1v8",  nf_B),
    lbl("met1_label", "pulse_kind_setrr_1v8", ns_B),

    # ─── Discharge cell terminal port labels ────────────────────────
    # All port-name kind so the bulk and discharge connections land on
    # the right external nets at LVS. Magic auto-traces connectivity
    # between these labels via the routed met1 — no extra "internal"
    # labels needed.
    lbl("met1_label", "VDD",           (32925, -4419)),  # inv_p.S
    lbl("met1_label", "VSS",           (31062, -4870)),  # inv_n.S
    lbl("met1_label", "VSS",           (30142, -4682)),  # disch.S
    lbl("met1_label", "SRC_RAIL",      (29352, -4682)),  # disch.D
    lbl("met1_label", "pulse_req_1v8", (32705, -5739)),  # inv_p.G
    lbl("met1_label", "pulse_req_1v8", (31282, -5645)),  # inv_n.G
]

# ─── Assemble doc ───────────────────────────────────────────────────
doc = rkt.Document(
    imports=[
        rkt.Import(path=f"./{NAND_NAME}.rkt"),
        rkt.Import(path=f"./{LSHIFT_NAME}.rkt"),
        rkt.Import(path=f"../primitives/{PMOS25_NAME}.rkt"),
        rkt.Import(path=f"../primitives/{INV_PFET_NAME}.rkt"),
        rkt.Import(path=f"../primitives/{INV_NFET_NAME}.rkt"),
        rkt.Import(path=f"../primitives/{DISCH_NFET_NAME}.rkt"),
    ],
    cells=[
        rkt.Cell(
            name='cim_reram_drv_phaseA_srcmux',
            elements=[*srefs, *extra_paint, *power_routes, *signal_routes, *port_labels],
        ),
    ],
    top_cell='cim_reram_drv_phaseA_srcmux',
)

out = Path("cell_designs/khalkulo/cim_reram_drv_phaseA_srcmux.rkt")
out.write_text(rkt.write(doc))
print(f"wrote {out}")
