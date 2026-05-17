"""Build cell_designs/khalkulo/lshift_1v8_to_3v3.rkt.

Cross-coupled level shifter, VDD=1.8V → VDDA1=3.3V.

Schematic (from cim_reram_drv_phaseA_sch.spice):
  Input INV (LV):
    XINV_P D_n D VDD VDD pfet_01v8 W=2.0 L=0.15
    XINV_N D_n D VSS VSS nfet_01v8 W=1.0 L=0.15
  Cross-coupled MV PMOS (output latch):
    XPU_OUTN OUT_N OUT VDDA1 VDDA1 pfet_g5v0d10v5 W=2.0 L=0.5
    XPU_OUT  OUT OUT_N VDDA1 VDDA1 pfet_g5v0d10v5 W=2.0 L=0.5
  MV NMOS pull-downs:
    XPD_OUTN OUT_N D   VSS VSS nfet_g5v0d10v5 W=4.0 L=0.5
    XPD_OUT  OUT   D_n VSS VSS nfet_g5v0d10v5 W=4.0 L=0.5

Layout strategy:
  Two FET rows separated into LV-INV column on the left + MV-latch
  on the right. MV PMOS in nwell tub, MV NMOS in psub. LV INV needs
  its own VDD rail (1.8V) separate from VDDA1 (3.3V) — placed at top.
  This is a first-cut layout; expect DRC violations from intra-column
  collisions in the LV INV portion (same as nand2_inv_lv issue).
"""
from pathlib import Path
from rekolektion.io import rkt
from rekolektion.layout import (
    place_tub, place_taps_around, place_rail, inspect_primitive,
    pin_to_rail, place_wire, place_via, pin_patch, poly_bridge,
    gate_extension,
)
from rekolektion.primitives.sky130 import (
    gen_pfet_01v8, gen_nfet_01v8, gen_pfet_hv, gen_nfet_hv,
)

# ─── Mint primitives ─────────────────────────────────────────────────
lv_p = gen_pfet_01v8(w_um=2.0, l_um=0.15, topc=False)
lv_n = gen_nfet_01v8(w_um=1.0, l_um=0.15, botc=False)
mv_p = gen_pfet_hv(w_um=2.0, l_um=0.5, topc=False)
mv_n = gen_nfet_hv(w_um=4.0, l_um=0.5, botc=False)
lv_p_info = inspect_primitive(lv_p)
lv_n_info = inspect_primitive(lv_n)
mv_p_info = inspect_primitive(mv_p)
mv_n_info = inspect_primitive(mv_n)

# ─── Place rows ──────────────────────────────────────────────────────
# Bottom: NMOS row (LV inv N + MV PD pair).
# Top: PMOS row (LV inv P + MV PU pair).
# Layout: LV cells on the LEFT, MV cells on the RIGHT.
LV_COL_X = 600
# LV-to-MV minimum X pitch: with all PFETs sharing one nwell at VDDA1
# (the LV PFET's bulk goes to VDDA1 too — see schematic note about
# Vbs reverse bias), the binding rule is LV-vs-MV same-type diff
# spacing (diff/tap.22+.23 = 360 nm). LV PFET diff right edge at
# LV_COL_X + 365; MV PFET diff left edge at MV0_X − 540. So
# MV0_X ≥ LV_COL_X + 365 + 360 + 540 = LV_COL_X + 1265.
MV_PITCH_X = 1700
MV0_X = LV_COL_X + 1300
MV1_X = MV0_X + MV_PITCH_X

# NMOS row
ninv = rkt.SRef(cell=lv_n, origin=(LV_COL_X, 0))
npd_outn = rkt.SRef(cell=mv_n, origin=(MV0_X, 0))
npd_out  = rkt.SRef(cell=mv_n, origin=(MV1_X, 0))

# PMOS row — y above NMOS bbox top.
# Inter-row channel needs four ext rows: NMOS_LO (D), NMOS_HI (D_n),
# PMOS_LO (OUT cross-couple), PMOS_HI (OUT_N cross-couple). Each
# row is a 320 nm patch with 140 nm met1.2 to neighbors, with
# 165 nm poly setback to the FET cell edge on the outer rows.
# Min channel: 165 + 320 + 140 + 320 + 140 + 320 + 140 + 320 + 165
#            = 2030 nm. 2100 leaves 70 nm slack.
INTER_ROW_CHANNEL = 2100
# Use the taller of lv_p / mv_p y_min for consistent y_origin.
# Actually two separate rows: LV INV at LV_COL_X y=lv_y, MV PMOS at MV_X y=mv_y.
# They need different y_origin to align top edges. To keep things simple,
# share a y_origin = max(nfet bbox tops) + INTER_ROW_CHANNEL - max(pfet bbox y_min).
nmos_top = max(lv_n_info.bbox[3], mv_n_info.bbox[3])
pmos_y_offset = nmos_top + INTER_ROW_CHANNEL - min(lv_p_info.bbox[1], mv_p_info.bbox[1])

pinv = rkt.SRef(cell=lv_p, origin=(LV_COL_X, pmos_y_offset))
ppu_outn = rkt.SRef(cell=mv_p, origin=(MV0_X, pmos_y_offset))
ppu_out  = rkt.SRef(cell=mv_p, origin=(MV1_X, pmos_y_offset))

nfet_row = [ninv, npd_outn, npd_out]
pfet_row = [pinv, ppu_outn, ppu_out]

# ─── PMOS tub (single nwell at VDDA1 — see schematic note) ───────────
# All three PFETs share one nwell biased to VDDA1. The LV PFET's
# bulk = VDDA1 (not the schematic's nominal VDD); we accept the
# Vbs = +1.5V reverse bias on the LV PFET's source-body junction
# to save the ~2 µm of cell width that nwell.2a would otherwise cost.
pfet_tub = place_tub(
    [
        (lv_p, (pinv.origin[0], pinv.origin[1])),
        (mv_p, (ppu_outn.origin[0], ppu_outn.origin[1])),
        (mv_p, (ppu_out.origin[0], ppu_out.origin[1])),
    ],
    margin_um={'top': 1.2, 'bottom': 0.4, 'left': 0.4, 'right': 0.4},
)

# ─── Tap bands ───────────────────────────────────────────────────────
def union_bbox(srefs, infos):
    xs1 = min(s.origin[0] + i.bbox[0] for s, i in zip(srefs, infos))
    ys1 = min(s.origin[1] + i.bbox[1] for s, i in zip(srefs, infos))
    xs2 = max(s.origin[0] + i.bbox[2] for s, i in zip(srefs, infos))
    ys2 = max(s.origin[1] + i.bbox[3] for s, i in zip(srefs, infos))
    return xs1, ys1, xs2, ys2

n_bbox = union_bbox(nfet_row, [lv_n_info, mv_n_info, mv_n_info])
p_bbox = union_bbox(pfet_row, [lv_p_info, mv_p_info, mv_p_info])

pwell_taps = place_taps_around(n_bbox, 'pwell', sides=('bottom',))
nwell_taps = place_taps_around(p_bbox, 'nwell', sides=('top',))

cell_x1 = min(n_bbox[0], p_bbox[0]) - 200
cell_x2 = max(n_bbox[2], p_bbox[2]) + 200

vss_strap = pwell_taps.li1_straps_by_side['bottom'][0]
vdda1_strap = nwell_taps.li1_straps_by_side['top'][0]
vss_y1, vss_y2 = vss_strap.y1 - 30, vss_strap.y2 + 30
vdda1_y1, vdda1_y2 = vdda1_strap.y1 - 30, vdda1_strap.y2 + 30

vss_rail = place_rail((cell_x1, vss_y1, cell_x2, vss_y2),
                      label='VSS', stitch_li1_straps=[vss_strap])
vdda1_rail = place_rail((cell_x1, vdda1_y1, cell_x2, vdda1_y2),
                        label='VDDA1', stitch_li1_straps=[vdda1_strap])

# VDD (1.8 V): a small met1 pin patch directly above the LV INV
# PMOS source. The LV INV source connects met1-north to this pin —
# no via stack. Pin sits between the PMOS row top and the VDDA1
# rail bottom (with met1.2 spacing on both sides).
inv_p_s_x_pin, _ = (LV_COL_X + 220, 0)  # LV PFET S X (pin from primitive)
vdd_y = vdda1_y1 - 140 - 160  # 140 nm spacing + 160 patch half
vdd_pin = [
    rkt.Rect(layer=rkt.named("sky130", "met1"),
             x1=inv_p_s_x_pin - 160, y1=vdd_y - 160,
             x2=inv_p_s_x_pin + 160, y2=vdd_y + 160),
    rkt.Label(layer=rkt.named("sky130", "met1_label"),
              text="VDD", origin=(inv_p_s_x_pin, vdd_y)),
]

# ─── Pin-coord helper ────────────────────────────────────────────────
def pin_xy(sref, terminal, info):
    p = info.pin(terminal)
    return (sref.origin[0] + p.origin[0], sref.origin[1] + p.origin[1])

# ─── Phase 1 — Power ─────────────────────────────────────────────────
power_routes = []
# LV INV PMOS source → VDD stub (via2 stack from LV PMOS S to met2 stub).
# Quick approach: pin_to_rail to vdda1 strap for MV PMOS, manual for LV INV.
power_routes.extend(pin_to_rail(ppu_outn, "S", vdda1_strap))
power_routes.extend(pin_to_rail(ppu_out, "S", vdda1_strap))
# LV INV PMOS source → VDD pin patch. Both on met1 — paint a
# vertical met1 stub from the source pin up to the VDD pin patch.
inv_p_s_x, inv_p_s_y = pin_xy(pinv, "S", lv_p_info)
power_routes.extend(place_wire(
    (inv_p_s_x, inv_p_s_y), (inv_p_s_x, vdd_y), layer="met1",
))

# MV NMOS source → VSS.
power_routes.extend(pin_to_rail(npd_outn, "S", vss_strap))
power_routes.extend(pin_to_rail(npd_out, "S", vss_strap))
# LV INV N source → VSS.
power_routes.extend(pin_to_rail(ninv, "S", vss_strap))

gate_routes = []

# LV INV — XINV_P.G = XINV_N.G = D (input). Both gates are on the same
# net AND on the same gate-X column (LV_COL_X), so a poly_bridge
# carries the gate signal between them via the gate poly itself,
# AND the bbox-anchored met1 enlargers it paints automatically fix
# the LV gate-met1 strip min-area violations (6 met1.6 tiles in the
# prior baseline). No in-channel pin contact needed — the bot
# enlarger inside the NFET cell serves as the cell's D pin.
inv_bridge = poly_bridge(pinv, ninv)
gate_routes.extend(inv_bridge.elements)

# MV cells have cross-coupled gates (XPU_OUTN.G=OUT, XPU_OUT.G=OUT_N)
# and pull-down gates on different nets from the INV (XPD_OUTN.G=D
# but on different X column from INV, so no poly_bridge candidate).

# D distribution: net D drives both LV INV gates (already on the
# bridge) AND XPD_OUTN.G (MV0 NFET gate, different X column).
#
# Route: rise from LV INV bot enlarger on met2 up to the MV NFET
# gate's Y level (well above the MV NFET S/D met1 strips that fill
# y up to 1845), then traverse east on met2 to the MV0 gate column,
# then via1 down to the MV NFET gate met1 strip (at y=2005..2235).
# Routing on met2 in the cell interior keeps the wires off the
# met1 layer where they'd otherwise collide with MV S/D met1 strips
# (x=±[280,510] from MV gate, 230 nm wide).
d_route_y_lv = (inv_bridge.bot_in_cell_met1.y1 + inv_bridge.bot_in_cell_met1.y2) // 2
# Channel layout: NFET ext patches at the bottom of the channel
# (just above MV NFET cell top), PFET ext patches at the top
# (just below MV PFET cell bot). Both 320×320 with 165 nm poly
# setback. Cross-couple horizontals weave between.
NMOS_EXT_Y_LO = mv_n_info.bbox[3] + 165 + 160                 # 2630 (D net via XPD_OUTN)
NMOS_EXT_Y_HI = NMOS_EXT_Y_LO + 320 + 140                     # 3090 (IN_n via XPD_OUT)
PMOS_EXT_Y_HI = pmos_y_offset + mv_p_info.bbox[1] - 165 - 160 # PFET bot − 325 (OUT_N via XPU_OUT)
PMOS_EXT_Y_LO = PMOS_EXT_Y_HI - 320 - 140                     # OUT cross-couple via XPU_OUTN
# Backwards-compat alias for the existing D-distribution code.
NMOS_EXT_Y = NMOS_EXT_Y_LO
PMOS_EXT_Y = PMOS_EXT_Y_HI
# D's gate-ext on MV0 NFET (XPD_OUTN).
pd_outn_ext = gate_extension(npd_outn, contact_y=NMOS_EXT_Y)
gate_routes.extend(pd_outn_ext.elements)
pd_outn_ext_y = NMOS_EXT_Y
# via1 LV-side: LV bot enlarger covers the cut with 85 nm enclosure.
gate_routes.extend(place_via((LV_COL_X, d_route_y_lv), "met1", "met2"))
# met2 chain: vertical from LV INV up to the gate-ext Y, then
# horizontal east to the gate-ext column. Explicit corner avoids
# the implicit-L notch.
gate_routes.extend(place_wire(
    [
        (LV_COL_X, d_route_y_lv),
        (LV_COL_X, pd_outn_ext_y),
        (pd_outn_ext.center[0], pd_outn_ext_y),
    ],
    layer="met2",
))
# Met2 corner patch at the L-bend (avoids met2.1 min-width notch).
gate_routes.append(
    rkt.Rect(
        layer=rkt.named("sky130", "met2"),
        x1=LV_COL_X - 160, y1=pd_outn_ext_y - 160,
        x2=LV_COL_X + 160, y2=pd_outn_ext_y + 160,
    )
)
# MV-side via1 lands on the gate-extension's met1 patch (320×320,
# fully enclosed).
gate_routes.extend(place_via(pd_outn_ext.center, "met1", "met2"))

# ─── Phase 3 — D_n distribution ──────────────────────────────────────
# D_n connects: XINV_P.D + XINV_N.D (both at LV col x=380, opposite
# Y rows) AND XPD_OUT.G (MV1 NFET, gate-ext at NMOS_EXT_Y).
#
# D and D_n both have wires at the LV column area. To avoid met2.2
# spacing collisions (the two columns are only 220 nm apart but
# need 140+140+140=420 nm centerline for two met2 wires), D_n
# routes vertically on MET1 (D is on met2). Different layers, no
# conflict. The MV1 NFET ext sits at NMOS_EXT_Y just like D's MV0
# ext (different X column, no overlap).
inv_p_d_x, inv_p_d_y = pin_xy(pinv, "D", lv_p_info)
inv_n_d_x, inv_n_d_y = pin_xy(ninv, "D", lv_n_info)
pd_out_ext = gate_extension(npd_out, contact_y=NMOS_EXT_Y_HI)
gate_routes.extend(pd_out_ext.elements)
# Vertical met1 from LV NFET drain up to LV PFET drain. Shift X
# WEST of the drain pins (to x=300) so the 140 nm wide wire
# (x=230..370) doesn't overlap the LV INV poly_bridge's bot
# enlarger (x=440..760, on net IN, not IN_n) which would merge
# the two nets on met1 — that overlap is geometrically a single
# polygon (DRC sees no violation) but electrically a short. The
# wire still overlaps the drain met1 strips at x=265..495 (same
# IN_n net via primitive li1_label="D" → fine).
INV_DRAIN_WIRE_X = 230
gate_routes.extend(place_wire(
    (INV_DRAIN_WIRE_X, inv_n_d_y), (INV_DRAIN_WIRE_X, inv_p_d_y), layer="met1",
))
# At NMOS_EXT_Y_HI (D_n's NFET ext row, ABOVE D's row), paint a
# met1 patch for via1 enclosure on the LV side, then met2
# horizontal east to MV1 NFET ext. Different Y from D's
# horizontal at NMOS_EXT_Y_LO → no met2 short between D and D_n.
gate_routes.append(
    rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=INV_DRAIN_WIRE_X - 160, y1=NMOS_EXT_Y_HI - 160,
        x2=INV_DRAIN_WIRE_X + 160, y2=NMOS_EXT_Y_HI + 160,
    )
)
gate_routes.extend(place_via((INV_DRAIN_WIRE_X, NMOS_EXT_Y_HI), "met1", "met2"))
gate_routes.extend(place_wire(
    (INV_DRAIN_WIRE_X, NMOS_EXT_Y_HI), (pd_out_ext.center[0], NMOS_EXT_Y_HI),
    layer="met2",
))
gate_routes.extend(place_via(pd_out_ext.center, "met1", "met2"))

# ─── Phase 4 — OUT_N net ─────────────────────────────────────────────
# OUT_N = MV0 PFET drain (XPU_OUTN.D) + MV0 NFET drain (XPD_OUTN.D)
# + MV1 PFET gate (XPU_OUT.G, cross-couple).
# Vertical met2 at MV0 drain X (905) connects the two drains; the
# cross-couple to MV1 PFET gate uses gate_extension to pull the
# gate down to PMOS_EXT_Y_HI in the channel, then a met2 horizontal
# east at PMOS_EXT_Y_HI.
ppu_outn_d = pin_xy(ppu_outn, "D", mv_p_info)
npd_outn_d = pin_xy(npd_outn, "D", mv_n_info)
pu_out_ext = gate_extension(ppu_out, contact_y=PMOS_EXT_Y_HI)
gate_routes.extend(pu_out_ext.elements)
# Met1 patches at MV drain via locations (primitive S/D met1 strip
# is only 230 nm wide → 40 nm X enclosure of via1, fails via.4a).
for px, py in [ppu_outn_d, npd_outn_d]:
    gate_routes.append(
        rkt.Rect(
            layer=rkt.named("sky130", "met1"),
            x1=px - 160, y1=py - 160, x2=px + 160, y2=py + 160,
        )
    )
# Vertical met1 connecting MV0 PFET drain (top) to MV0 NFET drain
# (bot). On MET1 (not met2) so we don't short to D's / D_n's met2
# horizontals where this vertical crosses them at the channel Y rows.
OUT_N_VERT_X = ppu_outn_d[0] - 45  # 860 — 140 nm gap from MV0 PFET gate met1 strip (parent x=1070-1530)
gate_routes.extend(place_wire(
    (OUT_N_VERT_X, npd_outn_d[1]), (OUT_N_VERT_X, ppu_outn_d[1]), layer="met1",
))
# Met1 patch + via1 at PMOS_EXT_Y_HI to tap onto met2. Center on
# OUT_N_VERT_X (the wire's X) and use an asymmetric width (280 nm
# in X, 320 nm in Y) so the patch right edge keeps 140 nm met1.2
# from pu_outn_ext's bridge at x=[1740, 2060]. Asym enclosure of
# the via1 cut: X (65 nm) ≥ 55 and Y (85 nm) ≥ 85 — meets via.4a.
gate_routes.append(
    rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=OUT_N_VERT_X - 140, y1=PMOS_EXT_Y_HI - 160,
        x2=OUT_N_VERT_X + 140, y2=PMOS_EXT_Y_HI + 160,
    )
)
gate_routes.extend(place_via((OUT_N_VERT_X, PMOS_EXT_Y_HI), "met1", "met2"))
# Cross-couple: met2 east at PMOS_EXT_Y_HI from the OUT_N wire
# column to MV1 PFET ext patch.
gate_routes.extend(place_wire(
    (OUT_N_VERT_X, PMOS_EXT_Y_HI), pu_out_ext.center, layer="met2",
))
gate_routes.extend(place_via(pu_out_ext.center, "met1", "met2"))

# ─── Phase 5 — OUT net ───────────────────────────────────────────────
# OUT = MV1 PFET drain (XPU_OUT.D) + MV1 NFET drain (XPD_OUT.D)
# + MV0 PFET gate (XPU_OUTN.G, cross-couple).
# Symmetric to Phase 4 but at MV1 drain X (2605) and using
# PMOS_EXT_Y_LO so OUT's met2 horizontal is at a different Y from
# OUT_N's (PMOS_EXT_Y_HI) — no same-layer merge.
ppu_out_d = pin_xy(ppu_out, "D", mv_p_info)
npd_out_d = pin_xy(npd_out, "D", mv_n_info)
pu_outn_ext = gate_extension(ppu_outn, contact_y=PMOS_EXT_Y_LO)
gate_routes.extend(pu_outn_ext.elements)
for px, py in [ppu_out_d, npd_out_d]:
    gate_routes.append(
        rkt.Rect(
            layer=rkt.named("sky130", "met1"),
            x1=px - 160, y1=py - 160, x2=px + 160, y2=py + 160,
        )
    )
# Vertical met1 (not met2) for the same reason as OUT_N: avoid
# shorting to D / D_n / OUT_N met2 horizontals at the channel rows.
OUT_VERT_X = ppu_out_d[0] - 45  # 2560 — same met1.2 reasoning as OUT_N
gate_routes.extend(place_wire(
    (OUT_VERT_X, npd_out_d[1]), (OUT_VERT_X, ppu_out_d[1]), layer="met1",
))
# Asymmetric met1 patch + via1 centered on OUT_VERT_X (same reason
# as OUT_N side: keep 140 nm gap from pu_out_ext bridge to the east).
gate_routes.append(
    rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=OUT_VERT_X - 140, y1=PMOS_EXT_Y_LO - 160,
        x2=OUT_VERT_X + 140, y2=PMOS_EXT_Y_LO + 160,
    )
)
gate_routes.extend(place_via((OUT_VERT_X, PMOS_EXT_Y_LO), "met1", "met2"))
gate_routes.extend(place_wire(
    (OUT_VERT_X, PMOS_EXT_Y_LO), pu_outn_ext.center, layer="met2",
))
gate_routes.extend(place_via(pu_outn_ext.center, "met1", "met2"))

port_labels = [
    rkt.Label(layer=rkt.named("sky130", "met1_label"),
              text="VDDA1", origin=(cell_x1 + 200, (vdda1_y1+vdda1_y2)//2)),
    rkt.Label(layer=rkt.named("sky130", "met1_label"),
              text="VSS", origin=(cell_x1 + 200, (vss_y1+vss_y2)//2)),
]
# Cell-port labels. The input net is named 'IN' (not 'D') to
# avoid collision with the FET primitives' built-in li1_label="D"
# terminal markers. Place IN on the poly_bridge's bot enlarger
# (which is on the gate-D net, fully merged with the bridge).
in_label_pt = (
    (inv_bridge.bot_in_cell_met1.x1 + inv_bridge.bot_in_cell_met1.x2) // 2,
    (inv_bridge.bot_in_cell_met1.y1 + inv_bridge.bot_in_cell_met1.y2) // 2,
)
out_n_x, out_n_y = pin_xy(npd_outn, "D", mv_n_info)
out_x, out_y = pin_xy(npd_out, "D", mv_n_info)
port_labels.extend([
    rkt.Label(layer=rkt.named("sky130", "met1_label"), text="IN", origin=in_label_pt),
    # IN_n is an INTERNAL net (LV INV output drives MV1 NFET gate).
    # Labeled with internal=True so it's visible in viz / LabelFlood
    # but NOT exported to GDS — Magic's port_makeall never sees it
    # → not promoted to a subckt port → LVS pin-match unaffected.
    rkt.Label(layer=rkt.named("sky130", "met2_label"), text="IN_n",
              origin=((INV_DRAIN_WIRE_X + pd_out_ext.center[0]) // 2, NMOS_EXT_Y_HI),
              internal=True),
    rkt.Label(layer=rkt.named("sky130", "li1_label"), text="OUT_N", origin=(out_n_x, out_n_y)),
    rkt.Label(layer=rkt.named("sky130", "li1_label"), text="OUT", origin=(out_x, out_y)),
    # Belt & suspenders: also label the parent-paint met1 verticals
    # so the OUT/OUT_N net polygons carry the name even when label
    # propagation through subckt boundaries is finicky.
    rkt.Label(layer=rkt.named("sky130", "met1_label"), text="OUT_N",
              origin=(OUT_N_VERT_X, (npd_outn_d[1] + ppu_outn_d[1]) // 2)),
    rkt.Label(layer=rkt.named("sky130", "met1_label"), text="OUT",
              origin=(OUT_VERT_X, (npd_out_d[1] + ppu_out_d[1]) // 2)),
])

doc = rkt.Document(
    imports=[
        rkt.Import(path=f"../primitives/{lv_n}.rkt"),
        rkt.Import(path=f"../primitives/{lv_p}.rkt"),
        rkt.Import(path=f"../primitives/{mv_n}.rkt"),
        rkt.Import(path=f"../primitives/{mv_p}.rkt"),
    ],
    cells=[
        rkt.Cell(
            name='lshift_1v8_to_3v3',
            elements=[
                *nfet_row,
                *pfet_tub.elements,
                *pwell_taps.elements,
                *nwell_taps.elements,
                *vss_rail,
                *vdda1_rail,
                *vdd_pin,
                *power_routes,
                *gate_routes,
                *port_labels,
            ],
        ),
    ],
    top_cell='lshift_1v8_to_3v3',
)

out = Path("cell_designs/khalkulo/lshift_1v8_to_3v3.rkt")
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(rkt.write(doc))
print(f"wrote {out}")
print(f"cell extent: x={cell_x1}..{cell_x2}, y={vss_y1}..{vdda1_y2}")
