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
# LV-to-MV minimum X pitch: the binding rule is LV-vs-MV same-type
# diff spacing (diff/tap.22+.23 = 360 nm). LV PFET diff right edge
# at LV_COL_X + 365 (LV diff half-width); MV PFET diff left edge at
# MV0_X − 540 (MV diff half-width is wider due to longer S/D taps).
# MV0_X ≥ LV_COL_X + 365 + 360 + 540 = LV_COL_X + 1265.
# 1300 leaves 35 nm slack.
MV_PITCH_X = 1700
MV0_X = LV_COL_X + 1300
MV1_X = MV0_X + MV_PITCH_X

# NMOS row
ninv = rkt.SRef(cell=lv_n, origin=(LV_COL_X, 0))
npd_outn = rkt.SRef(cell=mv_n, origin=(MV0_X, 0))
npd_out  = rkt.SRef(cell=mv_n, origin=(MV1_X, 0))

# PMOS row — y above NMOS bbox top.
INTER_ROW_CHANNEL = 700
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

# ─── PMOS tub ────────────────────────────────────────────────────────
tub_inputs = [
    (lv_p, (pinv.origin[0], pinv.origin[1])),
    (mv_p, (ppu_outn.origin[0], ppu_outn.origin[1])),
    (mv_p, (ppu_out.origin[0], ppu_out.origin[1])),
]
pfet_tub = place_tub(
    tub_inputs,
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
# VDDA1 = 3.3 V power for MV.
vdda1_rail = place_rail((cell_x1, vdda1_y1, cell_x2, vdda1_y2),
                       label='VDDA1', stitch_li1_straps=[vdda1_strap])

# VDD (1.8 V) — separate rail for LV INV. Put it as a met2 horizontal
# strap above the LV INV column only. Quick stub — paint a met2 rect.
vdd_y = pmos_y_offset + lv_p_info.bbox[3] + 400
vdd_rail_stub = [
    rkt.Rect(layer=rkt.named("sky130", "met2"),
             x1=LV_COL_X - 400, y1=vdd_y - 70, x2=LV_COL_X + 400, y2=vdd_y + 70),
    rkt.Label(layer=rkt.named("sky130", "met2_label"),
              text="VDD", origin=(LV_COL_X, vdd_y)),
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
# LV INV PMOS source → VDD stub. The primitive paints met1 over the
# S/D li1 strip; we just need to add a via1 + met2 vertical from S
# pin up to the VDD stub Y. The via1 at the stub Y overlaps the
# stub met2, so they merge into the VDD net polygon.
inv_p_s_x, inv_p_s_y = pin_xy(pinv, "S", lv_p_info)
# met1 patch at S pin (overlaps primitive met1 strip → merges).
PATCH_HALF = 160
power_routes.append(
    rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=inv_p_s_x - PATCH_HALF, y1=inv_p_s_y - PATCH_HALF,
        x2=inv_p_s_x + PATCH_HALF, y2=inv_p_s_y + PATCH_HALF,
    )
)
power_routes.extend(place_via((inv_p_s_x, inv_p_s_y), "met1", "met2"))
power_routes.extend(
    place_wire((inv_p_s_x, inv_p_s_y), (inv_p_s_x, vdd_y), layer="met2")
)
# via1 at the stub Y merges with the stub met2 (same layer, same Y).

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
# Pull the MV0 NFET gate UP into the inter-row channel via
# gate_extension, where there's clearance for a full-size via1
# met1 patch (no MV S/D met1 to worry about). The channel runs
# from MV NFET bbox top (2305) to MV PFET bbox bot (3015) — a
# 710 nm gap. Land the new contact at the channel midpoint.
pd_outn_ext_y = (mv_n_info.bbox[3] + (pmos_y_offset + mv_p_info.bbox[1])) // 2
pd_outn_ext = gate_extension(npd_outn, contact_y=pd_outn_ext_y)
gate_routes.extend(pd_outn_ext.elements)
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

port_labels = [
    rkt.Label(layer=rkt.named("sky130", "met1_label"),
              text="VDDA1", origin=(cell_x1 + 200, (vdda1_y1+vdda1_y2)//2)),
    rkt.Label(layer=rkt.named("sky130", "met1_label"),
              text="VSS", origin=(cell_x1 + 200, (vss_y1+vss_y2)//2)),
]
# Add D, OUT, OUT_N labels at a known place (just for SRef compatibility).
d_x, d_y = pin_xy(ninv, "G", lv_n_info)
out_n_x, out_n_y = pin_xy(npd_outn, "D", mv_n_info)
out_x, out_y = pin_xy(npd_out, "D", mv_n_info)
port_labels.extend([
    rkt.Label(layer=rkt.named("sky130", "li1_label"), text="D", origin=(d_x, d_y)),
    rkt.Label(layer=rkt.named("sky130", "li1_label"), text="OUT_N", origin=(out_n_x, out_n_y)),
    rkt.Label(layer=rkt.named("sky130", "li1_label"), text="OUT", origin=(out_x, out_y)),
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
                *vdd_rail_stub,
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
