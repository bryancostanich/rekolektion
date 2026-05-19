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

# ─── Assemble doc (placement-only — no routing, no labels) ──────────
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
            elements=[*srefs, *extra_paint, *power_routes],
        ),
    ],
    top_cell='cim_reram_drv_phaseA_srcmux',
)

out = Path("cell_designs/khalkulo/cim_reram_drv_phaseA_srcmux.rkt")
out.write_text(rkt.write(doc))
print(f"wrote {out}")
