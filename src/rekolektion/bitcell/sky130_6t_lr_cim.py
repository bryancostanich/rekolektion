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

# MIM cap design rules (from SKY130 DRC: capm.*)
MIM_MIN_WIDTH = 2.0          # Minimum MIM cap width (um)
MIM_MIN_LENGTH = 2.0         # Minimum MIM cap length (um)
MIM_M3_ENCLOSURE = 0.14     # M3 enclosure of MIM cap edge (capm.3)
MIM_M4_OVERLAP = 0.14       # M4 minimum overlap past MIM edge
MIM_VIA2_SPACING = 0.10     # Via2 to MIM cap spacing (capm.8)

# Via2 design rules
VIA2_SIZE = 0.200            # Via2 is 200nm square
VIA2_M2_ENCL = 0.085        # M2 enclosure of via2 (wider direction, via2.4a)
VIA2_M2_ENCL_OTHER = 0.065  # M2 enclosure of via2 (narrower direction, via2.4)
VIA2_M3_ENCL = 0.085        # M3 enclosure of via2 (wider direction, via2.5a)
VIA2_M3_ENCL_OTHER = 0.065  # M3 enclosure of via2 (narrower direction, via2.5)

# Pass transistor sizing (same as access gates for symmetry)
T7_WIDTH = 0.42

# T7 geometry parameters
_T7_DIFF_OVERHANG = 0.33    # Diff extension past poly (> poly.7 min 0.25, + licon encl)
_T7_LICON_TO_GATE = 0.090   # Licon edge to gate edge (> licon.11 min 0.055, + li.3 margin)


def create_cim_bitcell(
    pd_w: float = PD_WIDTH,
    pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH,
    mim_w: float = MIM_MIN_WIDTH,
    mim_l: float = MIM_MIN_LENGTH,
) -> gdstk.Cell:
    """Create a 6T+1T+1C CIM bitcell.

    T7 pass transistor is placed above the 6T core with separate diff.
    T7 source connects to the latched Q net (Route 1 li1: PMOS int_bot +
    gate_A poly) via mcon → M1 route through the N-P gap.
    T7 drain connects to MIM cap M3 bottom plate via full via stack.

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
    nmos_cx = g["nmos_cx"]
    licon_sz = g["licon_sz"]
    li_w = g["li_w"]
    li_encl = g["li_encl"]
    mcon_sz = g["mcon_sz"]
    poly_ext = g["poly_ext"]

    # =================================================================
    # T7 PASS TRANSISTOR (separate NMOS diff above 6T core)
    # =================================================================
    # T7 diff is isolated from 6T diff by min spacing (0.27um).
    # MWL poly gate crosses T7 diff horizontally.

    t7_diff_bot = _snap(ch + RULES.DIFF_MIN_SPACING)  # 2.33 + 0.27 = 2.60
    mwl_cy = _snap(t7_diff_bot + _T7_DIFF_OVERHANG + GATE_LENGTH / 2.0)
    mwl_y0 = _snap(mwl_cy - GATE_LENGTH / 2.0)
    mwl_y1 = _snap(mwl_cy + GATE_LENGTH / 2.0)
    t7_diff_top = _snap(mwl_y1 + _T7_DIFF_OVERHANG)

    # T7 source and drain licon positions
    # licon center = gate edge + licon_to_gate + licon/2
    t7_src_cy = _snap(mwl_y0 - _T7_LICON_TO_GATE - licon_sz / 2.0)
    t7_drn_cy = _snap(mwl_y1 + _T7_LICON_TO_GATE + licon_sz / 2.0)

    # T7 diffusion (same X as 6T NMOS)
    _rect(cell, LAYERS.DIFF.as_tuple,
          g["nmos_diff_x0"], t7_diff_bot,
          g["nmos_diff_x1"], t7_diff_top)
    _rect(cell, LAYERS.NSDM.as_tuple,
          g["nmos_diff_x0"] - RULES.NSDM_ENCLOSURE_OF_DIFF,
          t7_diff_bot - RULES.NSDM_ENCLOSURE_OF_DIFF,
          g["nmos_diff_x1"] + RULES.NSDM_ENCLOSURE_OF_DIFF,
          t7_diff_top + RULES.NSDM_ENCLOSURE_OF_DIFF)

    # MWL poly gate — extend full cell width for array connectivity
    # At T7's Y position, only the T7 NMOS diff exists (PMOS diff is
    # at the 6T level below), so MWL creates only one transistor.
    _rect(cell, LAYERS.POLY.as_tuple,
          g["nmos_diff_x0"] - poly_ext, mwl_y0,
          g["pmos_diff_x1"] + poly_ext, mwl_y1)

    # T7 source licon + li1
    _contact(cell, nmos_cx, t7_src_cy, LAYERS.LICON1.as_tuple, licon_sz)
    li_pad_h = licon_sz + 2 * li_encl
    _li_pad(cell, nmos_cx, t7_src_cy, li_w, li_pad_h)

    # T7 drain licon + li1
    _contact(cell, nmos_cx, t7_drn_cy, LAYERS.LICON1.as_tuple, licon_sz)
    _li_pad(cell, nmos_cx, t7_drn_cy, li_w, li_pad_h)

    # =================================================================
    # Q-TO-T7 SOURCE ROUTE (M1 through N-P gap)
    # =================================================================
    # The latched Q net is on Route 1 li1 (PMOS int_bot + gate_A poly).
    # Tap into Route 1 li1 with mcon in the gap, run M1 vertically at
    # X=route_x, then horizontal to T7 source.

    route_x = _snap(1.10)  # in the N-P gap, clear of all 6T M1
    q_tap_y = g["int_bot_cy"]  # 0.645, on Route 1 li1

    # Mcon on Route 1 li1 → M1 access to Q
    _contact(cell, route_x, q_tap_y, LAYERS.MCON.as_tuple, mcon_sz)

    # M1 pad at Q tap
    m1_enc = RULES.MET1_ENCLOSURE_OF_MCON        # 0.03
    m1_enc_o = RULES.MET1_ENCLOSURE_OF_MCON_OTHER  # 0.06
    m1_pad_w = mcon_sz + 2 * m1_enc_o  # wider direction
    m1_pad_h = mcon_sz + 2 * m1_enc    # narrower direction

    # M1 vertical strip at route_x from Q tap up to T7 source level
    m1_route_hw = RULES.MET1_MIN_WIDTH / 2.0  # half-width of vertical M1
    m1_route_y_bot = _snap(q_tap_y - m1_pad_h / 2.0)
    m1_route_y_top = _snap(t7_src_cy + m1_pad_h / 2.0)
    _rect(cell, LAYERS.MET1.as_tuple,
          route_x - m1_route_hw, m1_route_y_bot,
          route_x + m1_route_hw, m1_route_y_top)

    # Widen M1 at Q tap for mcon enclosure
    _rect(cell, LAYERS.MET1.as_tuple,
          route_x - m1_pad_w / 2.0, q_tap_y - m1_pad_h / 2.0,
          route_x + m1_pad_w / 2.0, q_tap_y + m1_pad_h / 2.0)

    # M1 horizontal from route_x to nmos_cx at T7 source level
    _rect(cell, LAYERS.MET1.as_tuple,
          nmos_cx - m1_pad_w / 2.0, t7_src_cy - m1_route_hw,
          route_x + m1_route_hw, t7_src_cy + m1_route_hw)

    # Mcon + M1 pad at T7 source
    _contact(cell, nmos_cx, t7_src_cy, LAYERS.MCON.as_tuple, mcon_sz)
    _rect(cell, LAYERS.MET1.as_tuple,
          nmos_cx - m1_pad_w / 2.0, t7_src_cy - m1_pad_h / 2.0,
          nmos_cx + m1_pad_w / 2.0, t7_src_cy + m1_pad_h / 2.0)

    # =================================================================
    # T7 DRAIN TO MIM CAP VIA STACK
    # =================================================================
    # T7 drain → licon → li1 → mcon → M1 → via1 → M2 → via2 → M3

    # Mcon at T7 drain
    _contact(cell, nmos_cx, t7_drn_cy, LAYERS.MCON.as_tuple, mcon_sz)

    # M1 pad at drain
    _rect(cell, LAYERS.MET1.as_tuple,
          nmos_cx - m1_pad_w / 2.0, t7_drn_cy - m1_pad_h / 2.0,
          nmos_cx + m1_pad_w / 2.0, t7_drn_cy + m1_pad_h / 2.0)

    # Via1 (M1→M2) at drain
    via_sz = RULES.VIA_SIZE  # 0.15
    via_enc_m1 = RULES.MET1_ENCLOSURE_OF_VIA  # 0.055
    via_enc_m2 = RULES.MET2_ENCLOSURE_OF_VIA  # 0.055
    _contact(cell, nmos_cx, t7_drn_cy, LAYERS.VIA.as_tuple, via_sz)

    # M1 pad for via1 (may overlap with mcon pad — that's fine, same net)
    via_m1_pad = via_sz + 2 * 0.085  # generous enclosure
    _rect(cell, LAYERS.MET1.as_tuple,
          nmos_cx - via_m1_pad / 2.0, t7_drn_cy - via_m1_pad / 2.0,
          nmos_cx + via_m1_pad / 2.0, t7_drn_cy + via_m1_pad / 2.0)

    # --- MIM cap geometry ---
    cell_cx = cw / 2.0
    cell_cy = ch / 2.0
    cap_x0 = _snap(cell_cx - mim_w / 2.0)
    cap_x1 = _snap(cap_x0 + mim_w)
    cap_y0 = _snap(cell_cy - mim_l / 2.0)
    cap_y1 = _snap(cap_y0 + mim_l)

    # Via2 placement: above MIM cap top edge, above M2 VPWR stripe
    # Must satisfy: via2 ≥ 0.1um from MIM cap edge (capm.8)
    #               M2 around via2 ≥ 0.14um from M2 VPWR stripe
    m2_vpwr_top = g["m2_vpwr_y1"]  # 2.33
    via2_m2_bot = _snap(m2_vpwr_top + RULES.MET2_MIN_SPACING)  # 2.47
    via2_cy = _snap(via2_m2_bot + VIA2_M2_ENCL_OTHER + VIA2_SIZE / 2.0)

    # M2 strip from via1 (T7 drain) down to via2
    m2_strip_hw = _snap((VIA2_SIZE + 2 * VIA2_M2_ENCL) / 2.0)
    m2_y_bot = _snap(via2_cy - VIA2_SIZE / 2.0 - VIA2_M2_ENCL_OTHER)
    m2_y_top = _snap(t7_drn_cy + via_sz / 2.0 + via_enc_m2)
    _rect(cell, LAYERS.MET2.as_tuple,
          nmos_cx - m2_strip_hw, m2_y_bot,
          nmos_cx + m2_strip_hw, m2_y_top)

    # Via2 (M2→M3)
    _rect(cell, LAYER_VIA2,
          nmos_cx - VIA2_SIZE / 2.0, via2_cy - VIA2_SIZE / 2.0,
          nmos_cx + VIA2_SIZE / 2.0, via2_cy + VIA2_SIZE / 2.0)

    # =================================================================
    # MIM CAPACITOR (M3/M4, directly over 6T core)
    # =================================================================

    # M3 bottom plate (with correct 0.14um enclosure per capm.3)
    m3_x0 = _snap(cap_x0 - MIM_M3_ENCLOSURE)
    m3_y0 = _snap(cap_y0 - MIM_M3_ENCLOSURE)
    m3_x1 = _snap(cap_x1 + MIM_M3_ENCLOSURE)
    m3_y1 = _snap(cap_y1 + MIM_M3_ENCLOSURE)
    _rect(cell, LAYER_MET3, m3_x0, m3_y0, m3_x1, m3_y1)

    # M3 extension strip for via2 landing (above main M3 plate)
    m3_ext_hw = _snap((VIA2_SIZE + 2 * VIA2_M3_ENCL) / 2.0)
    m3_ext_top = _snap(via2_cy + VIA2_SIZE / 2.0 + VIA2_M3_ENCL_OTHER)
    _rect(cell, LAYER_MET3,
          nmos_cx - m3_ext_hw, m3_y1,
          nmos_cx + m3_ext_hw, m3_ext_top)

    # MIM cap layer
    _rect(cell, LAYER_MIMCAP, cap_x0, cap_y0, cap_x1, cap_y1)

    # M4 top plate (MBL connection)
    _rect(cell, LAYER_MET4,
          cap_x0 - MIM_M4_OVERLAP, cap_y0 - MIM_M4_OVERLAP,
          cap_x1 + MIM_M4_OVERLAP, cap_y1 + MIM_M4_OVERLAP)

    # =================================================================
    # LABELS
    # =================================================================
    _label(cell, "MWL", LAYERS.POLY.as_tuple,
           g["nmos_diff_x0"] - poly_ext, mwl_cy)
    _label(cell, "MBL", LAYER_MET4,
           cell_cx, cell_cy)

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


def load_cim_bitcell(
    gds_path: str | Path = "output/sky130_6t_cim_lr.gds",
) -> "BitcellInfo":
    """Return a BitcellInfo for the CIM bitcell, generating GDS if needed.

    Provides MWL and MBL pin metadata in addition to standard SRAM pins.
    """
    from rekolektion.bitcell.base import BitcellInfo, PinInfo

    gds_path = Path(gds_path)
    if not gds_path.exists():
        generate_cim_bitcell(str(gds_path))

    g = _compute_cell_geometry()
    cw, ch = g["cell_w"], g["cell_h"]

    # T7 geometry (must match create_cim_bitcell)
    t7_diff_bot = _snap(ch + RULES.DIFF_MIN_SPACING)
    mwl_cy = _snap(t7_diff_bot + _T7_DIFF_OVERHANG + GATE_LENGTH / 2.0)
    t7_diff_top = _snap(mwl_cy + GATE_LENGTH / 2.0 + _T7_DIFF_OVERHANG)

    cell_cx = cw / 2.0
    cell_cy = ch / 2.0

    # Pin positions for standard SRAM pins (same as 6T LR cell)
    vgnd_cx = g["rail_w"] / 2.0
    vpwr_cx = g["vpwr_x0"] + g["rail_w"] / 2.0
    pins = {
        "BL":   PinInfo("BL",   [(g["nmos_cx"], g["bl_bot_cy"], "met1")]),
        "BLB":  PinInfo("BLB",  [(g["nmos_cx"], g["bl_top_cy"], "met1")]),
        "WL":   PinInfo("WL",   [(cw / 2.0, g["wl_bot_cy"], "poly")]),
        "VGND": PinInfo("VGND", [(vgnd_cx, g["pwr_cy"], "met1")]),
        "VPWR": PinInfo("VPWR", [(vpwr_cx, g["pwr_cy"], "met1")]),
        # CIM pins
        "MWL":  PinInfo("MWL",  [(cw / 2.0, mwl_cy, "poly")]),
        "MBL":  PinInfo("MBL",  [(cell_cx, cell_cy, "met4")]),
    }

    # Tiling pitch — same X as 6T, Y increased for T7 overhead.
    # T7 diff extends to t7_diff_top. Add NSDM margin + some spacing
    # for Y-mirrored tiling.
    x_pitch = _snap(cw - g["rail_w"] + 0.03)  # same as 6T: 1.925
    # Y pitch: from 6T bottom to T7 top + margin for mirrored neighbor
    y_pitch = _snap(t7_diff_top + RULES.NSDM_ENCLOSURE_OF_DIFF + 0.10)

    return BitcellInfo(
        cell_name="sky130_sram_6t_cim_lr",
        cell_width=x_pitch,
        cell_height=y_pitch,
        pins=pins,
        gds_path=gds_path,
        origin_x=0.0,
        origin_y=0.0,
        geometry_width=cw,
        geometry_height=t7_diff_top + RULES.NSDM_ENCLOSURE_OF_DIFF,
    )


if __name__ == "__main__":
    generate_cim_bitcell("output/sky130_6t_cim_lr.gds", generate_spice=True)
