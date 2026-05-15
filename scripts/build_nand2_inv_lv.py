"""Build cell_designs/khalkulo/nand2_inv_lv.rkt — poly-bridge topology.

NAND2 + INV digital stdcell.

Schematic:
  PMOS row (top):  XNAND_PA, XNAND_PB, XINV_P  (W=2.0, L=0.15)
  NMOS row (bot):  XNAND_NA, XNAND_NB, XINV_N  (W=1.0, L=0.15)

Topology — follows the standard stdcell convention:

  Gate nets (A, B, NAND2.out→INV.in) carry their signal on the gate
  poly itself, NOT on a metal routing wire. Each PMOS+NMOS gate
  pair (e.g. PA + NA on net A) is connected by a parent-painted
  vertical poly strap bridging from the NFET's poly top to the
  PFET's poly bottom, both at the same gate-X column. A single
  polycontact + li1 + mcon + met1 pin somewhere along that strap
  is the cell's pin for the net.

  The INV gate pair (INV_P + INV_N on net nand_out) places its pin
  contact directly on the trunk Y, so its met1 patch merges with
  the horizontal met1 trunk — no via1 stack needed for the inverter
  side of nand_out.

Net topology:
  VDD       — top met1 rail
  VSS       — bottom met1 rail
  A, B      — gate poly carries the signal; one cell-pin met1 patch
              per net in the inter-row channel
  Y         — vertical met2 between INV_P.D and INV_N.D
  nand_out  — horizontal met1 trunk in inter-row channel, taps:
                PA.D, PB.D, NA.D via met2 branches (via1 at each end)
                INV gate-pair pin (merged into trunk on met1)
  nand_mid  — same-row 2-pin: NA.S → NB.D, met1 horizontal at NMOS
              S/D Y
"""
from pathlib import Path
from rekolektion.io import rkt
from rekolektion.layout import (
    place_tub, place_taps_around, place_rail, inspect_primitive,
    pin_to_rail, place_wire, place_via, poly_bridge,
)
from rekolektion.primitives.sky130 import gen_pfet_01v8, gen_nfet_01v8

# ─── Mint primitives ─────────────────────────────────────────────────
pfet_name = gen_pfet_01v8(w_um=2.0, l_um=0.15, topc=False)
nfet_name = gen_nfet_01v8(w_um=1.0, l_um=0.15, botc=False)
p_info = inspect_primitive(pfet_name)
n_info = inspect_primitive(nfet_name)
print(f"pfet bbox: {p_info.bbox}")
print(f"nfet bbox: {n_info.bbox}")

# ─── Place rows ──────────────────────────────────────────────────────
COL_PITCH = 1700
COL0_X = 600
COL1_X = COL0_X + COL_PITCH
COL2_X = COL0_X + 2 * COL_PITCH
COLS = [COL0_X, COL1_X, COL2_X]

nfet_row = [rkt.SRef(cell=nfet_name, origin=(x, 0)) for x in COLS]

# Inter-row channel stack (Y, bottom→top), with no separate A/B
# pin patches in the channel (A/B pins live on the NFET-side
# enlargers inside each FET):
#   NFET bbox top
#   140 nm met1.2 clearance (bot enlarger is inside NFET; outer edge
#                            sits AT bbox top so its outside face
#                            needs 140 to next met1)
#   trunk met1 (320 tall) — INV bridge pin merges into this on met1
#   140 nm
#   PFET bbox bot
# Minimum = 140 + 320 + 140 = 600 nm. 700 gives 50 nm slack each side.
INTER_ROW_CHANNEL = 700
pfet_y = n_info.bbox[3] + INTER_ROW_CHANNEL - p_info.bbox[1]
pfet_row = [rkt.SRef(cell=pfet_name, origin=(x, pfet_y)) for x in COLS]

# ─── PMOS tub + taps + rails ─────────────────────────────────────────
tub_inputs = [(pfet_name, (s.origin[0], s.origin[1])) for s in pfet_row]
pfet_tub = place_tub(
    tub_inputs,
    margin_um={'top': 1.2, 'bottom': 0.2, 'left': 0.4, 'right': 0.4},
)

nfet_xs1 = min(s.origin[0] + n_info.bbox[0] for s in nfet_row)
nfet_ys1 = min(s.origin[1] + n_info.bbox[1] for s in nfet_row)
nfet_xs2 = max(s.origin[0] + n_info.bbox[2] for s in nfet_row)
nfet_ys2 = max(s.origin[1] + n_info.bbox[3] for s in nfet_row)
pwell_taps = place_taps_around(
    (nfet_xs1, nfet_ys1, nfet_xs2, nfet_ys2),
    'pwell', sides=('bottom',),
)

pfet_xs1 = min(s.origin[0] + p_info.bbox[0] for s in pfet_row)
pfet_ys1 = min(s.origin[1] + p_info.bbox[1] for s in pfet_row)
pfet_xs2 = max(s.origin[0] + p_info.bbox[2] for s in pfet_row)
pfet_ys2 = max(s.origin[1] + p_info.bbox[3] for s in pfet_row)
nwell_taps = place_taps_around(
    (pfet_xs1, pfet_ys1, pfet_xs2, pfet_ys2),
    'nwell', sides=('top',),
)

cell_x1 = min(nfet_xs1, pfet_xs1) - 200
cell_x2 = max(nfet_xs2, pfet_xs2) + 200

vss_strap = pwell_taps.li1_straps_by_side['bottom'][0]
vdd_strap = nwell_taps.li1_straps_by_side['top'][0]
vss_y1, vss_y2 = vss_strap.y1 - 30, vss_strap.y2 + 30
vdd_y1, vdd_y2 = vdd_strap.y1 - 30, vdd_strap.y2 + 30
vss_rail = place_rail((cell_x1, vss_y1, cell_x2, vss_y2),
                      label='VSS', stitch_li1_straps=[vss_strap])
vdd_rail = place_rail((cell_x1, vdd_y1, cell_x2, vdd_y2),
                      label='VDD', stitch_li1_straps=[vdd_strap])

# ─── Phase 1 — Power ─────────────────────────────────────────────────
power_routes = []
for pfet_sref in pfet_row:
    power_routes.extend(pin_to_rail(pfet_sref, "S", vdd_strap))
power_routes.extend(pin_to_rail(nfet_row[1], "S", vss_strap))
power_routes.extend(pin_to_rail(nfet_row[2], "S", vss_strap))

# ─── Pin coords (S/D only — gates use poly_bridge below) ─────────────
def pin_xy(sref, terminal, info):
    p = info.pin(terminal)
    return (sref.origin[0] + p.origin[0], sref.origin[1] + p.origin[1])

ppa_d = pin_xy(pfet_row[0], "D", p_info)
ppb_d = pin_xy(pfet_row[1], "D", p_info)
pip_d = pin_xy(pfet_row[2], "D", p_info)
nna_d = pin_xy(nfet_row[0], "D", n_info)
nna_s = pin_xy(nfet_row[0], "S", n_info)
nnb_d = pin_xy(nfet_row[1], "D", n_info)
nin_d = pin_xy(nfet_row[2], "D", n_info)

# ─── Inter-row channel layout ─────────────────────────────────────────
inter_row_bot = n_info.bbox[3]
inter_row_top = pfet_y + p_info.bbox[1]
NAND_OUT_TRACK_Y = (inter_row_bot + inter_row_top) // 2
print(f"inter-row channel: y={inter_row_bot} to {inter_row_top}")
print(f"nand_out trunk Y: {NAND_OUT_TRACK_Y}")

sig_routes = []

# ─── Phase 2 — Gate-pair poly bridges (A, B, INV) ────────────────────
# Each PMOS+NMOS gate pair connects on gate poly. For A and B, the
# poly bridge gives no extra pin contact in the channel — the bot
# enlarger (inside NFET cell at the gate position) serves as the
# routable A/B pin patch. For INV, we DO want an in-channel pin
# directly on the trunk Y so the pin's met1 patch merges with the
# horizontal trunk and ties INV's gate net to nand_out without a
# via stack.
a_bridge = poly_bridge(pfet_row[0], nfet_row[0])  # pin_y=None → no channel contact
b_bridge = poly_bridge(pfet_row[1], nfet_row[1])
inv_bridge = poly_bridge(pfet_row[2], nfet_row[2], pin_y=NAND_OUT_TRACK_Y)
for br in (a_bridge, b_bridge, inv_bridge):
    sig_routes.extend(br.elements)

# ─── Phase 3 — nand_mid (same-row NA.S → NB.D) ───────────────────────
sig_routes.extend(place_wire(nna_s, nnb_d, layer="met1"))

# ─── Phase 4 — Y output (cross-row S/D INV_P.D ↔ INV_N.D) ────────────
# Y stays on met2 between the two INV drains. S/D pins (not gates).
PATCH_HALF = 160
def met1_patch(point):
    px, py = point
    return rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=px - PATCH_HALF, y1=py - PATCH_HALF,
        x2=px + PATCH_HALF, y2=py + PATCH_HALF,
    )

sig_routes.append(met1_patch(pip_d))
sig_routes.append(met1_patch(nin_d))
sig_routes.extend(place_via(pip_d, "met1", "met2"))
sig_routes.extend(place_via(nin_d, "met1", "met2"))
sig_routes.extend(place_wire(pip_d, nin_d, layer="met2"))

# ─── Phase 5 — nand_out trunk + S/D branches ─────────────────────────
# 4-pin net on this side: PA.D, PB.D, NA.D drop into the trunk via
# met2 branches; the INV gate-pair already merges its pin into the
# trunk on met1 (no via1 needed there).
sd_branches = [ppa_d, ppb_d, nna_d]
trunk_xs = [px for px, _ in sd_branches] + [inv_bridge.center[0]]
TRUNK_OVERHANG = 130
sig_routes.extend(place_wire(
    (min(trunk_xs) - TRUNK_OVERHANG, NAND_OUT_TRACK_Y),
    (max(trunk_xs) + TRUNK_OVERHANG, NAND_OUT_TRACK_Y),
    layer="met1",
    width_um=0.32,
))

for px, py in sd_branches:
    sig_routes.append(met1_patch((px, py)))
    sig_routes.extend(place_via((px, py), "met1", "met2"))
    sig_routes.extend(place_wire(
        (px, py), (px, NAND_OUT_TRACK_Y), layer="met2",
    ))
    sig_routes.extend(place_via((px, NAND_OUT_TRACK_Y), "met1", "met2"))

# ─── Net labels ──────────────────────────────────────────────────────
# A and B pin patches are on met1; Y on met2. nand_out has the trunk
# (met1) plus the 3 S/D branches (met2) plus the INV pin (already
# part of the trunk on met1). Each isolated polygon gets its own
# label. Met1 enlargers at the in-cell gate strips need labels too,
# since they're separate met1 polygons.
def enlarger_center(rect):
    return ((rect.x1 + rect.x2) // 2, (rect.y1 + rect.y2) // 2)

port_labels = [
    # CELL-LEVEL PORTS only. nand_out and nand_mid are internal
    # nets — labeling them would promote them to subckt ports
    # and fail LVS pin-matching against the schematic which
    # declares only A, B, Y, VDD, VSS.
    rkt.Label(layer=rkt.named("sky130", "met1_label"), text="A",
              origin=enlarger_center(a_bridge.bot_in_cell_met1)),
    rkt.Label(layer=rkt.named("sky130", "met1_label"), text="B",
              origin=enlarger_center(b_bridge.bot_in_cell_met1)),
    rkt.Label(layer=rkt.named("sky130", "met2_label"), text="Y", origin=pip_d),
]

doc = rkt.Document(
    imports=[
        rkt.Import(path=f"../primitives/{nfet_name}.rkt"),
        rkt.Import(path=f"../primitives/{pfet_name}.rkt"),
    ],
    cells=[
        rkt.Cell(
            name='nand2_inv_lv',
            elements=[
                *nfet_row,
                *pfet_tub.elements,
                *pwell_taps.elements,
                *nwell_taps.elements,
                *vss_rail,
                *vdd_rail,
                *power_routes,
                *sig_routes,
                *port_labels,
            ],
        ),
    ],
    top_cell='nand2_inv_lv',
)

out = Path("cell_designs/khalkulo/nand2_inv_lv.rkt")
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(rkt.write(doc))
print(f"wrote {out}")
print(f"cell extent x: {cell_x1} to {cell_x2}")
print(f"cell extent y: {vss_y1} to {vdd_y2}")
