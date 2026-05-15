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
MV_PITCH_X = 1700
MV0_X = LV_COL_X + 2200
MV1_X = MV0_X + MV_PITCH_X

# NMOS row
ninv = rkt.SRef(cell=lv_n, origin=(LV_COL_X, 0))
npd_outn = rkt.SRef(cell=mv_n, origin=(MV0_X, 0))
npd_out  = rkt.SRef(cell=mv_n, origin=(MV1_X, 0))

# PMOS row — y above NMOS bbox top.
INTER_ROW_CHANNEL = 1500
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

# ─── Phase 1 — Power ─────────────────────────────────────────────────
power_routes = []
# LV INV PMOS source → VDD stub (via2 stack from LV PMOS S to met2 stub).
# Quick approach: pin_to_rail to vdda1 strap for MV PMOS, manual for LV INV.
power_routes.extend(pin_to_rail(ppu_outn, "S", vdda1_strap))
power_routes.extend(pin_to_rail(ppu_out, "S", vdda1_strap))
# LV INV P source → not VDDA1, it's VDD. Route to met2 vdd stub via via1.
# For now skip LV INV power — known LVS gap.

# MV NMOS source → VSS.
power_routes.extend(pin_to_rail(npd_outn, "S", vss_strap))
power_routes.extend(pin_to_rail(npd_out, "S", vss_strap))
# LV INV N source → VSS.
power_routes.extend(pin_to_rail(ninv, "S", vss_strap))

# ─── Pin coords ──────────────────────────────────────────────────────
def pin_xy(sref, terminal, info):
    p = info.pin(terminal)
    return (sref.origin[0] + p.origin[0], sref.origin[1] + p.origin[1])

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
# Cross-coupling routing left for a later pass — current baseline is
# DRC-clean with just the INV poly bridge.

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
