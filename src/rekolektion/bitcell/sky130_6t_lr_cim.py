"""6T+1T+1C CIM bitcell layout generator — C3SRAM-style capacitive coupling.

Extends the LR 6T SRAM cell with:
- 1 NMOS pass transistor (T7 gated by MWL)
- 1 MIM capacitor (C_C coupling Q to MBL)

The MIM cap sits directly above the 6T core on M3/M4. The 6T core
uses M1/M2 only, so there's no routing conflict.

Port map:
    BL, BLB  — standard read/write bitlines (M2, vertical)
    WL       — standard word line (poly, horizontal)
    MWL      — multiply word line (poly, horizontal)
    MBL      — multiply bitline (M4, vertical — MIM cap top plate)
    VDD, VSS — power rails (M1, vertical)

All dimensions in micrometers. Grid snapped to 5nm for SKY130.
"""

from pathlib import Path

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES, NMOS_MODEL, PMOS_MODEL
from rekolektion.bitcell.sky130_6t_lr import (
    create_bitcell as create_6t_cell,
    _compute_cell_geometry,
    _snap, _rect, _label, _contact, _li_pad,
    PD_WIDTH, PG_WIDTH, PU_WIDTH, GATE_LENGTH,
)


# ---------------------------------------------------------------------------
# MIM cap GDS layers (from SKY130 tech file)
# ---------------------------------------------------------------------------

LAYER_MIMCAP = (89, 44)     # MIM cap plate 1 (between M3 and M4)
LAYER_MET3 = (70, 20)       # Metal 3
LAYER_VIA2 = (69, 44)       # Via 2 (M2→M3)
LAYER_MET4 = (71, 20)       # Metal 4
LAYER_VIA3 = (70, 44)       # Via 3 (M3→M4)

# MIM cap design rules
MIM_MIN_WIDTH = 2.0          # Minimum MIM cap width (um)
MIM_MIN_LENGTH = 2.0         # Minimum MIM cap length (um)
MIM_M3_ENCLOSURE = 0.06     # M3 enclosure of MIM cap edge
MIM_M4_OVERLAP = 0.14       # M4 minimum overlap past MIM edge

# Pass transistor sizing (same as access gates for symmetry)
T7_WIDTH = 0.42


def create_cim_bitcell(
    pd_w: float = PD_WIDTH,
    pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH,
    mim_w: float = MIM_MIN_WIDTH,
    mim_l: float = MIM_MIN_LENGTH,
) -> gdstk.Cell:
    """Create a 6T+1T+1C CIM bitcell.

    Returns a gdstk.Cell with the complete CIM cell layout.
    """
    # Start with the 6T core
    cell_6t = create_6t_cell(pd_w=pd_w, pg_w=pg_w, pu_w=pu_w)
    g = _compute_cell_geometry(pd_w, pg_w, pu_w)

    cell = gdstk.Cell("sky130_sram_6t_cim_lr")

    # Copy all polygons and labels from 6T cell
    for poly in cell_6t.polygons:
        cell.add(poly.copy())
    for lbl in cell_6t.labels:
        cell.add(lbl.copy())

    cw = g["cell_w"]
    ch = g["cell_h"]

    # --- Pass transistor T7 (Q → C_C, gated by MWL) ---
    # Sits just above the 6T core, on the NMOS side (aligned with Q node)

    cim_y_start = ch + 0.2  # gap above 6T cell
    t7_gate_y = _snap(cim_y_start)
    t7_diff_bot = _snap(t7_gate_y - T7_WIDTH / 2)
    t7_diff_top = _snap(t7_gate_y + T7_WIDTH / 2)

    nmos_cx = g["nmos_cx"]

    # T7 diffusion
    _rect(cell, LAYERS.DIFF.as_tuple,
          g["nmos_diff_x0"], t7_diff_bot,
          g["nmos_diff_x1"], t7_diff_top)
    _rect(cell, LAYERS.NSDM.as_tuple,
          g["nmos_diff_x0"] - 0.125, t7_diff_bot - 0.125,
          g["nmos_diff_x1"] + 0.125, t7_diff_top + 0.125)

    # T7 gate (poly, horizontal) — this is the MWL line
    mwl_poly_y0 = _snap(t7_gate_y - GATE_LENGTH / 2)
    mwl_poly_y1 = _snap(t7_gate_y + GATE_LENGTH / 2)
    poly_ext = RULES.POLY_MIN_EXTENSION_PAST_DIFF
    _rect(cell, LAYERS.POLY.as_tuple,
          g["nmos_diff_x0"] - poly_ext, mwl_poly_y0,
          g["nmos_diff_x1"] + poly_ext, mwl_poly_y1)

    # --- MIM Capacitor (single, directly over the 6T core) ---
    # M3/M4 layers sit above M1/M2 in the stack — no XY conflict.
    # Center the cap on the 6T cell footprint.

    cell_cx = cw / 2.0
    cell_cy = ch / 2.0
    cap_x0 = _snap(cell_cx - mim_w / 2.0)
    cap_x1 = _snap(cap_x0 + mim_w)
    cap_y0 = _snap(cell_cy - mim_l / 2.0)
    cap_y1 = _snap(cap_y0 + mim_l)

    # M3 bottom plate (with enclosure)
    _rect(cell, LAYER_MET3,
          cap_x0 - MIM_M3_ENCLOSURE, cap_y0 - MIM_M3_ENCLOSURE,
          cap_x1 + MIM_M3_ENCLOSURE, cap_y1 + MIM_M3_ENCLOSURE)

    # MIM cap layer
    _rect(cell, LAYER_MIMCAP, cap_x0, cap_y0, cap_x1, cap_y1)

    # M4 top plate (MBL connection, with overlap)
    _rect(cell, LAYER_MET4,
          cap_x0 - MIM_M4_OVERLAP, cap_y0 - MIM_M4_OVERLAP,
          cap_x1 + MIM_M4_OVERLAP, cap_y1 + MIM_M4_OVERLAP)

    # --- Via stack: T7 drain → li1 → M1 → mcon → M2 → via2 → M3 (cap bottom plate) ---
    # T7 is above the 6T core; the cap overlaps the core on M3/M4.
    # The via stack runs DOWN from T7 into the cap's M3 footprint.

    via2_sz = 0.2
    m1_w = RULES.MET1_MIN_WIDTH + 2 * 0.03
    via_cx = nmos_cx

    # Li1 pad + licon on T7 drain
    t7_drain_y = _snap(t7_gate_y + T7_WIDTH / 4)
    _li_pad(cell, via_cx, t7_drain_y,
            RULES.LI1_MIN_WIDTH, RULES.LI1_MIN_WIDTH + 2 * RULES.LI1_ENCLOSURE_OF_LICON)
    _contact(cell, via_cx, t7_drain_y,
             LAYERS.LICON1.as_tuple, RULES.LICON_SIZE)

    # M1 pad + mcon
    _rect(cell, LAYERS.MET1.as_tuple,
          via_cx - m1_w / 2, _snap(cap_y1 - 0.3), via_cx + m1_w / 2, _snap(t7_diff_top))
    _contact(cell, via_cx, t7_drain_y,
             LAYERS.MCON.as_tuple, RULES.MCON_SIZE)

    # M2 runs from T7 drain down into the cap area
    via2_y = _snap(cap_y1 - 0.3)  # place via2 near top of cap
    _rect(cell, LAYERS.MET2.as_tuple,
          via_cx - 0.14, via2_y, via_cx + 0.14, _snap(t7_diff_top))

    # Via2 (M2→M3) inside the cap's M3 footprint
    _rect(cell, LAYER_VIA2,
          via_cx - via2_sz / 2, via2_y,
          via_cx + via2_sz / 2, _snap(via2_y + via2_sz))

    # --- Labels ---
    _label(cell, "MWL", LAYERS.POLY.as_tuple,
           g["nmos_diff_x0"] - poly_ext, t7_gate_y)
    _label(cell, "MBL", LAYER_MET4,
           cell_cx, (cap_y0 + cap_y1) / 2)

    return cell


# ---------------------------------------------------------------------------
# SPICE netlist
# ---------------------------------------------------------------------------

def _write_cim_spice_netlist(
    path: Path,
    pd_w: float = PD_WIDTH, pg_w: float = PG_WIDTH, pu_w: float = PU_WIDTH,
    mim_w: float = MIM_MIN_WIDTH, mim_l: float = MIM_MIN_LENGTH,
) -> None:
    netlist = f"""\
* 6T+1T+1C CIM Bitcell (LR Topology + Capacitive Coupling) — SKY130
* Based on C3SRAM capacitive coupling approach (Jiang et al., JSSC 2020)
* Generated by rekolektion
*
* Ports: BL BLB WL MWL MBL VDD VSS
* NOTE: w/l values are unitless — ngspice hsa mode applies 1e-6 scaling

.subckt sky130_sram_6t_cim_lr BL BLB WL MWL MBL VDD VSS

* === 6T SRAM Core ===
XPD_L Q  QB  VSS VSS {NMOS_MODEL} w={pd_w} l={GATE_LENGTH}
XPD_R QB Q   VSS VSS {NMOS_MODEL} w={pd_w} l={GATE_LENGTH}
XPU_L Q  QB  VDD VDD {PMOS_MODEL} w={pu_w} l={GATE_LENGTH}
XPU_R QB Q   VDD VDD {PMOS_MODEL} w={pu_w} l={GATE_LENGTH}
XPG_L BL WL  Q   VSS {NMOS_MODEL} w={pg_w} l={GATE_LENGTH}
XPG_R BLB WL QB  VSS {NMOS_MODEL} w={pg_w} l={GATE_LENGTH}

* === CIM Capacitive Coupling ===
* Pass transistor T7: Q → C_C, gated by MWL
XT7 Q_cap MWL Q VSS {NMOS_MODEL} w={T7_WIDTH} l={GATE_LENGTH}

* MIM coupling capacitor: Q side → MBL
XCC Q_cap MBL sky130_fd_pr__cap_mim_m3_1 w={mim_w} l={mim_l}

.ends sky130_sram_6t_cim_lr
"""
    path.write_text(netlist)


# ---------------------------------------------------------------------------
# Output generation
# ---------------------------------------------------------------------------

def generate_cim_bitcell(
    output_path: str = "sky130_6t_cim_lr.gds",
    generate_spice: bool = True,
    pd_w: float = PD_WIDTH, pg_w: float = PG_WIDTH, pu_w: float = PU_WIDTH,
    mim_w: float = MIM_MIN_WIDTH, mim_l: float = MIM_MIN_LENGTH,
) -> Path:
    """Generate the CIM bitcell GDS and optionally SPICE netlist."""
    cell = create_cim_bitcell(pd_w=pd_w, pg_w=pg_w, pu_w=pu_w,
                               mim_w=mim_w, mim_l=mim_l)
    lib = gdstk.Library(name="rekolektion_cim_lr", unit=1e-6, precision=5e-9)
    lib.add(cell)
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    lib.write_gds(str(out))

    g = _compute_cell_geometry(pd_w, pg_w, pu_w)
    base_area = g["cell_w"] * g["cell_h"]
    mim_area = mim_w * mim_l
    cap_fF = mim_w * mim_l * 2.0  # ~2 fF/um²

    print(f"Generated CIM bitcell (6T+1T+1C, LR topology): {out}")
    print(f"  6T core: {g['cell_w']:.3f} x {g['cell_h']:.3f} um = {base_area:.3f} um²")
    print(f"  MIM cap: {mim_w:.1f} × {mim_l:.1f} um = {mim_area:.1f} um² (~{cap_fF:.0f} fF)")
    print(f"  Pass transistor: T7={T7_WIDTH:.2f}/{GATE_LENGTH:.2f}")

    if generate_spice:
        spice_path = out.with_suffix(".spice")
        _write_cim_spice_netlist(spice_path, pd_w, pg_w, pu_w, mim_w, mim_l)
        print(f"  SPICE netlist: {spice_path}")

    return out


if __name__ == "__main__":
    generate_cim_bitcell("output/sky130_6t_cim_lr.gds", generate_spice=True)
