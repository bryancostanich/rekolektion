"""6T SRAM bitcell layout generator for SKY130.

Generates a DRC-aware 6T SRAM cell optimized for density on the SkyWater
SKY130 130nm process. The cell is designed to tile in both X and Y with
proper mirroring for shared power rails and bit line pairs.

6T SRAM Cell Topology:
                    VDD
                     |
                BL  PU-L  PU-R  BLB
                |    |      |    |
                PG-L-+------+-PG-R
                     |      |
                    PD-L  PD-R
                     |      |
                    VSS

    PU = pull-up PMOS (cross-coupled inverter)
    PD = pull-down NMOS (cross-coupled inverter)
    PG = pass gate NMOS (access transistor)
    BL/BLB = bit line / bit line bar
    WL = word line (gates of PG-L and PG-R)

Cell Layout Strategy:
    - NMOS (PD + PG) on bottom, PMOS (PU) on top
    - VSS rail at bottom, VDD rail at top
    - Cell mirrors in Y for row-to-row tiling (shared power rails)
    - Cell mirrors in X for column-to-column tiling (shared bit lines)
    - Word line runs horizontally in poly
    - Bit lines run vertically in met2 at array level (met1 stub in cell)
    - VDD/VSS run horizontally in met1

Transistor Sizing:
    Default: all minimum-size for maximum density.
    - PD (pull-down): W=0.42μm, L=0.15μm
    - PG (pass gate):  W=0.42μm, L=0.15μm
    - PU (pull-up):    W=0.42μm, L=0.15μm
    Cell ratio (CR = PD/PG) = 1.0. SPICE must validate read stability.
    For improved read margin, increase PD to 0.64μm (CR=1.5) at area cost.

    To use the "stable" sizing instead: create_bitcell(pd_w=0.64)
"""

from pathlib import Path

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES, NMOS_MODEL, PMOS_MODEL


# ---------------------------------------------------------------------------
# Default cell parameters — all minimum-size for density
# ---------------------------------------------------------------------------

PD_WIDTH = 0.42    # Pull-down NMOS channel width (μm)
PG_WIDTH = 0.42    # Pass gate NMOS channel width (μm)
PU_WIDTH = 0.42    # Pull-up PMOS channel width (μm)
GATE_LENGTH = 0.15  # All gates (μm)

# Cell pitch dimensions, derived from design rules and transistor sizing.
# Height is the dominant dimension, driven by:
#   - 3 gate pitches (PD, PG on NMOS side; PU on PMOS side)
#   - Source/drain contact landings (0.25μm each)
#   - N-well to N-diff spacing (~0.34μm)
#   - Power rails (met1, 0.14μm each)
CELL_WIDTH = 1.04    # μm — X dimension (column / bit-line pitch)
CELL_HEIGHT = 1.90   # μm — Y dimension (row / word-line pitch)


def _rect(cell: gdstk.Cell, layer: tuple[int, int],
          x0: float, y0: float, x1: float, y1: float) -> gdstk.Polygon:
    """Add a rectangle to a cell and return it."""
    r = gdstk.rectangle((x0, y0), (x1, y1), layer=layer[0], datatype=layer[1])
    cell.add(r)
    return r


def _label(cell: gdstk.Cell, text: str, layer: tuple[int, int],
           x: float, y: float) -> None:
    """Add a label to a cell."""
    lbl = gdstk.Label(text, (x, y), layer=layer[0], texttype=layer[1])
    cell.add(lbl)


def create_bitcell(
    pd_w: float = PD_WIDTH,
    pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH,
    gate_l: float = GATE_LENGTH,
) -> gdstk.Cell:
    """Create a 6T SRAM bitcell layout for SKY130.

    The cell is designed to tile by mirroring:
    - Mirror in X (about right edge) for adjacent columns
    - Mirror in Y (about top edge) for adjacent rows
    This shares power rails between rows and bit lines between columns.

    Args:
        pd_w: Pull-down NMOS width
        pg_w: Pass gate NMOS width
        pu_w: Pull-up PMOS width
        gate_l: Gate length for all transistors

    Returns:
        gdstk.Cell containing the bitcell layout
    """
    cell = gdstk.Cell("sky130_sram_6t_bitcell")
    L = LAYERS
    R = RULES

    cw = CELL_WIDTH
    ch = CELL_HEIGHT
    mid_x = cw / 2.0

    # Common dimensions from design rules
    poly_ext = R.POLY_MIN_EXTENSION_PAST_DIFF  # 0.13 — poly endcap past diff (X dir)
    diff_ext = R.DIFF_EXTENSION_PAST_POLY       # 0.25 — diff past gate (Y dir, for contacts)
    nsdm_enc = R.NSDM_ENCLOSURE_OF_DIFF         # 0.125
    psdm_enc = R.PSDM_ENCLOSURE_OF_DIFF         # 0.125
    li_w = R.LI1_MIN_WIDTH                       # 0.17
    licon_sz = R.LICON_SIZE                      # 0.17
    mcon_sz = R.MCON_SIZE                        # 0.17
    met1_w = R.MET1_MIN_WIDTH                    # 0.14

    # -----------------------------------------------------------------------
    # Y-coordinate plan (bottom to top):
    #
    #  0.00          VSS met1 rail bottom
    #  0.14          VSS met1 rail top
    #  0.16          NMOS diff bottom (PD source zone — VSS contact landing)
    #  0.41          PD gate bottom (0.16 + diff_ext)
    #  0.56          PD gate top (0.41 + gate_l)
    #  0.86          PG gate bottom (0.56 + 0.30 inter-gate gap for contact)
    #  1.01          PG gate top (0.86 + gate_l)
    #  1.26          NMOS diff top (1.01 + diff_ext, BL contact zone)
    #  ~1.26-1.33    N-P transition (nwell edge, spacing)
    #  1.33          PMOS diff bottom (PU drain — internal node contact)
    #  1.58          PU gate bottom (1.33 + diff_ext)
    #  1.73          PU gate top (1.58 + gate_l)
    #  1.76          PMOS diff top (with VDD contact, 1.73 + 0.03 compressed)
    #  1.76          VDD met1 rail bottom
    #  1.90          VDD met1 rail top / cell top
    #
    # Note: PMOS top is compressed — VDD contact shares space with rail.
    # The 0.07μm gap between nwell and NMOS diff top is tight; DRC may
    # require adjustment. This is our most aggressive spacing.
    # -----------------------------------------------------------------------

    # Key Y coordinates
    vss_rail_bot = 0.00
    vss_rail_top = 0.14

    nmos_diff_bot = 0.16
    pd_gate_bot = nmos_diff_bot + diff_ext          # 0.41
    pd_gate_top = pd_gate_bot + gate_l              # 0.56
    inter_gate_gap = 0.30  # Must fit licon contact between gates
    pg_gate_bot = pd_gate_top + inter_gate_gap      # 0.86
    pg_gate_top = pg_gate_bot + gate_l              # 1.01
    nmos_diff_top = pg_gate_top + diff_ext          # 1.26

    # N-well boundary — aggressive: nwell edge at midpoint of N-P transition
    nwell_bot = nmos_diff_top + 0.07
    pmos_diff_bot = nwell_bot + R.DIFF_MIN_ENCLOSURE_BY_NWELL  # +0.18 = 1.51

    pu_gate_bot = pmos_diff_bot + diff_ext          # 1.76... too tall
    # Compress: PU source (VDD side) contact can share with rail zone
    # Use reduced diff extension on VDD side where contact is integrated with rail
    pmos_diff_bot_actual = 1.33  # Override — pack tighter, validate with DRC
    pu_gate_bot = pmos_diff_bot_actual + diff_ext   # 1.58
    pu_gate_top = pu_gate_bot + gate_l              # 1.73
    pmos_diff_top = pu_gate_top + 0.03              # 1.76 — compressed, VDD contact in rail
    nwell_bot = nmos_diff_top + 0.07                # 1.33 — just above NMOS diff

    vdd_rail_bot = ch - met1_w                      # 1.76
    vdd_rail_top = ch                               # 1.90

    # NMOS diffusion width in X = max of PD and PG width
    nmos_diff_w = max(pd_w, pg_w)

    # ===== N-WELL (upper half for PMOS) =====
    # Extends past cell edges for tiling continuity
    _rect(cell, L.NWELL.as_tuple, -0.10, nwell_bot, cw + 0.10, ch + 0.10)

    # ===== NMOS DIFFUSION =====
    # Left NMOS: continuous strip hosting PD-L (bottom) and PG-L (top)
    nl_x0 = 0.08
    nl_x1 = nl_x0 + nmos_diff_w
    _rect(cell, L.DIFF.as_tuple, nl_x0, nmos_diff_bot, nl_x1, nmos_diff_top)

    # Right NMOS: mirror
    nr_x0 = cw - nl_x1
    nr_x1 = cw - nl_x0
    _rect(cell, L.DIFF.as_tuple, nr_x0, nmos_diff_bot, nr_x1, nmos_diff_top)

    # NSDM implant over NMOS
    _rect(cell, L.NSDM.as_tuple,
          nl_x0 - nsdm_enc, nmos_diff_bot - nsdm_enc,
          nl_x1 + nsdm_enc, nmos_diff_top + nsdm_enc)
    _rect(cell, L.NSDM.as_tuple,
          nr_x0 - nsdm_enc, nmos_diff_bot - nsdm_enc,
          nr_x1 + nsdm_enc, nmos_diff_top + nsdm_enc)

    # ===== PMOS DIFFUSION =====
    pmos_diff_w = pu_w
    pl_x0 = 0.08
    pl_x1 = pl_x0 + pmos_diff_w
    _rect(cell, L.DIFF.as_tuple, pl_x0, pmos_diff_bot_actual, pl_x1, pmos_diff_top)

    pr_x0 = cw - pl_x1
    pr_x1 = cw - pl_x0
    _rect(cell, L.DIFF.as_tuple, pr_x0, pmos_diff_bot_actual, pr_x1, pmos_diff_top)

    # PSDM implant over PMOS
    _rect(cell, L.PSDM.as_tuple,
          pl_x0 - psdm_enc, pmos_diff_bot_actual - psdm_enc,
          pl_x1 + psdm_enc, pmos_diff_top + psdm_enc)
    _rect(cell, L.PSDM.as_tuple,
          pr_x0 - psdm_enc, pmos_diff_bot_actual - psdm_enc,
          pr_x1 + psdm_enc, pmos_diff_top + psdm_enc)

    # ===== POLYSILICON GATES =====
    # Poly gates run horizontally (in X), crossing vertical diff strips.
    # Poly extends past diff edges by poly_ext (0.13) in X.

    # PD gates
    _rect(cell, L.POLY.as_tuple,
          nl_x0 - poly_ext, pd_gate_bot, nl_x1 + poly_ext, pd_gate_top)
    _rect(cell, L.POLY.as_tuple,
          nr_x0 - poly_ext, pd_gate_bot, nr_x1 + poly_ext, pd_gate_top)

    # PG gates (also form the word line within the cell)
    _rect(cell, L.POLY.as_tuple,
          nl_x0 - poly_ext, pg_gate_bot, nl_x1 + poly_ext, pg_gate_top)
    _rect(cell, L.POLY.as_tuple,
          nr_x0 - poly_ext, pg_gate_bot, nr_x1 + poly_ext, pg_gate_top)

    # Word line: poly strip connecting left PG and right PG through center
    _rect(cell, L.POLY.as_tuple,
          nl_x1 + poly_ext, pg_gate_bot,
          nr_x0 - poly_ext, pg_gate_top)

    # PU gates
    _rect(cell, L.POLY.as_tuple,
          pl_x0 - poly_ext, pu_gate_bot, pl_x1 + poly_ext, pu_gate_top)
    _rect(cell, L.POLY.as_tuple,
          pr_x0 - poly_ext, pu_gate_bot, pr_x1 + poly_ext, pu_gate_top)

    # ===== GATE INTERCONNECT (cross-coupled inverters) =====
    # Left inverter gate stack: PD-L and PU-L gates connected by vertical poly
    nl_cx = (nl_x0 + nl_x1) / 2.0  # Center X of left NMOS diff
    pl_cx = (pl_x0 + pl_x1) / 2.0  # Center X of left PMOS diff
    nr_cx = (nr_x0 + nr_x1) / 2.0
    pr_cx = (pr_x0 + pr_x1) / 2.0

    left_gate_x = nl_cx - gate_l / 2.0
    right_gate_x = nr_cx - gate_l / 2.0

    # Vertical poly connecting PD-L gate to PU-L gate (left inverter)
    _rect(cell, L.POLY.as_tuple,
          left_gate_x, pd_gate_bot,
          left_gate_x + gate_l, pu_gate_top)

    # Vertical poly connecting PD-R gate to PU-R gate (right inverter)
    _rect(cell, L.POLY.as_tuple,
          right_gate_x, pd_gate_bot,
          right_gate_x + gate_l, pu_gate_top)

    # ===== LOCAL INTERCONNECT (LI1) =====

    # --- VSS contacts (PD source, bottom of NMOS diff) ---
    vss_contact_y = nmos_diff_bot + 0.04  # Center licon in source zone
    for cx in (nl_cx, nr_cx):
        _rect(cell, L.LICON1.as_tuple,
              cx - licon_sz / 2, vss_contact_y,
              cx + licon_sz / 2, vss_contact_y + licon_sz)
        _rect(cell, L.LI1.as_tuple,
              cx - li_w / 2, vss_rail_bot,
              cx + li_w / 2, vss_contact_y + licon_sz + 0.02)

    # --- VDD contacts (PU source, top of PMOS diff) ---
    vdd_contact_y = pmos_diff_top - licon_sz - 0.01
    for cx in (pl_cx, pr_cx):
        _rect(cell, L.LICON1.as_tuple,
              cx - licon_sz / 2, vdd_contact_y,
              cx + licon_sz / 2, vdd_contact_y + licon_sz)
        _rect(cell, L.LI1.as_tuple,
              cx - li_w / 2, vdd_contact_y - 0.02,
              cx + li_w / 2, vdd_rail_top)

    # --- Internal node Q (between PD-L drain and PG-L source) ---
    q_contact_y = (pd_gate_top + pg_gate_bot) / 2.0 - licon_sz / 2.0
    _rect(cell, L.LICON1.as_tuple,
          nl_cx - licon_sz / 2, q_contact_y,
          nl_cx + licon_sz / 2, q_contact_y + licon_sz)

    # --- Internal node QB (between PD-R drain and PG-R source) ---
    _rect(cell, L.LICON1.as_tuple,
          nr_cx - licon_sz / 2, q_contact_y,
          nr_cx + licon_sz / 2, q_contact_y + licon_sz)

    # --- Internal node Q on PMOS side (PU-L drain) ---
    q_pmos_y = pmos_diff_bot_actual + 0.04
    _rect(cell, L.LICON1.as_tuple,
          pl_cx - licon_sz / 2, q_pmos_y,
          pl_cx + licon_sz / 2, q_pmos_y + licon_sz)

    # --- Internal node QB on PMOS side (PU-R drain) ---
    _rect(cell, L.LICON1.as_tuple,
          pr_cx - licon_sz / 2, q_pmos_y,
          pr_cx + licon_sz / 2, q_pmos_y + licon_sz)

    # --- Cross-coupling in LI1 ---
    # Q net: left NMOS drain + left PMOS drain → right inverter gate
    # Route Q as horizontal li1 from left drain contacts to right gate poly contact
    q_li_y = q_contact_y
    _rect(cell, L.LI1.as_tuple,
          nl_cx - li_w / 2, q_li_y,
          right_gate_x + gate_l + 0.05, q_li_y + li_w)

    # Poly contact for right gate (Q drives right inverter)
    _rect(cell, L.LICON1.as_tuple,
          right_gate_x, q_li_y,
          right_gate_x + licon_sz, q_li_y + licon_sz)

    # QB net: right NMOS drain + right PMOS drain → left inverter gate
    qb_li_y = q_li_y + li_w + R.LI1_MIN_SPACING
    _rect(cell, L.LI1.as_tuple,
          left_gate_x - 0.05, qb_li_y,
          nr_cx + li_w / 2, qb_li_y + li_w)

    # Poly contact for left gate (QB drives left inverter)
    _rect(cell, L.LICON1.as_tuple,
          left_gate_x, qb_li_y,
          left_gate_x + licon_sz, qb_li_y + licon_sz)

    # Q net vertical: connect NMOS Q node to PMOS Q node via li1
    _rect(cell, L.LI1.as_tuple,
          nl_cx - li_w / 2, q_li_y,
          nl_cx + li_w / 2, q_pmos_y + licon_sz)
    _rect(cell, L.LI1.as_tuple,
          pl_cx - li_w / 2, q_pmos_y,
          pl_cx + li_w / 2, q_pmos_y + licon_sz)

    # QB net vertical: connect NMOS QB node to PMOS QB node via li1
    _rect(cell, L.LI1.as_tuple,
          nr_cx - li_w / 2, qb_li_y,
          nr_cx + li_w / 2, q_pmos_y + licon_sz)
    _rect(cell, L.LI1.as_tuple,
          pr_cx - li_w / 2, q_pmos_y,
          pr_cx + li_w / 2, q_pmos_y + licon_sz)

    # ===== BIT LINE CONTACTS (PG drain, top of NMOS diff) =====
    bl_contact_y = nmos_diff_top - licon_sz - 0.04
    # BL (left)
    _rect(cell, L.LICON1.as_tuple,
          nl_cx - licon_sz / 2, bl_contact_y,
          nl_cx + licon_sz / 2, bl_contact_y + licon_sz)
    _rect(cell, L.LI1.as_tuple,
          nl_cx - li_w / 2, bl_contact_y - 0.02,
          nl_cx + li_w / 2, bl_contact_y + licon_sz + 0.02)
    _rect(cell, L.MCON.as_tuple,
          nl_cx - mcon_sz / 2, bl_contact_y,
          nl_cx + mcon_sz / 2, bl_contact_y + mcon_sz)
    _rect(cell, L.MET1.as_tuple,
          nl_cx - met1_w, bl_contact_y - 0.03,
          nl_cx + met1_w, bl_contact_y + mcon_sz + 0.03)

    # BLB (right)
    _rect(cell, L.LICON1.as_tuple,
          nr_cx - licon_sz / 2, bl_contact_y,
          nr_cx + licon_sz / 2, bl_contact_y + licon_sz)
    _rect(cell, L.LI1.as_tuple,
          nr_cx - li_w / 2, bl_contact_y - 0.02,
          nr_cx + li_w / 2, bl_contact_y + licon_sz + 0.02)
    _rect(cell, L.MCON.as_tuple,
          nr_cx - mcon_sz / 2, bl_contact_y,
          nr_cx + mcon_sz / 2, bl_contact_y + mcon_sz)
    _rect(cell, L.MET1.as_tuple,
          nr_cx - met1_w, bl_contact_y - 0.03,
          nr_cx + met1_w, bl_contact_y + mcon_sz + 0.03)

    # ===== METAL 1 POWER RAILS =====
    _rect(cell, L.MET1.as_tuple, 0.0, vss_rail_bot, cw, vss_rail_top)
    _rect(cell, L.MET1.as_tuple, 0.0, vdd_rail_bot, cw, vdd_rail_top)

    # MCON from li1 to met1 for power rails
    for cx in (nl_cx, nr_cx):
        _rect(cell, L.MCON.as_tuple,
              cx - mcon_sz / 2, vss_rail_bot + 0.00,
              cx + mcon_sz / 2, vss_rail_bot + mcon_sz)
    for cx in (pl_cx, pr_cx):
        _rect(cell, L.MCON.as_tuple,
              cx - mcon_sz / 2, vdd_rail_top - mcon_sz,
              cx + mcon_sz / 2, vdd_rail_top)

    # ===== SUBSTRATE/WELL TAPS =====
    tap_w = R.TAP_MIN_WIDTH  # 0.26

    # P-sub tap in center (NMOS region) — connects to VSS
    psub_tap_y = 0.02
    _rect(cell, L.TAP.as_tuple,
          mid_x - tap_w / 2, psub_tap_y,
          mid_x + tap_w / 2, psub_tap_y + tap_w)
    _rect(cell, L.NSDM.as_tuple,
          mid_x - tap_w / 2 - nsdm_enc, psub_tap_y - nsdm_enc,
          mid_x + tap_w / 2 + nsdm_enc, psub_tap_y + tap_w + nsdm_enc)
    _rect(cell, L.LI1.as_tuple,
          mid_x - li_w / 2, psub_tap_y + (tap_w - li_w) / 2,
          mid_x + li_w / 2, psub_tap_y + (tap_w + li_w) / 2)
    _rect(cell, L.LICON1.as_tuple,
          mid_x - licon_sz / 2, psub_tap_y + (tap_w - licon_sz) / 2,
          mid_x + licon_sz / 2, psub_tap_y + (tap_w + licon_sz) / 2)

    # N-well tap in center (PMOS region) — connects to VDD
    nw_tap_y = ch - 0.02 - tap_w
    _rect(cell, L.TAP.as_tuple,
          mid_x - tap_w / 2, nw_tap_y,
          mid_x + tap_w / 2, nw_tap_y + tap_w)
    _rect(cell, L.PSDM.as_tuple,
          mid_x - tap_w / 2 - psdm_enc, nw_tap_y - psdm_enc,
          mid_x + tap_w / 2 + psdm_enc, nw_tap_y + tap_w + psdm_enc)
    _rect(cell, L.LI1.as_tuple,
          mid_x - li_w / 2, nw_tap_y + (tap_w - li_w) / 2,
          mid_x + li_w / 2, nw_tap_y + (tap_w + li_w) / 2)
    _rect(cell, L.LICON1.as_tuple,
          mid_x - licon_sz / 2, nw_tap_y + (tap_w - licon_sz) / 2,
          mid_x + licon_sz / 2, nw_tap_y + (tap_w + licon_sz) / 2)

    # ===== CELL BOUNDARY =====
    _rect(cell, L.BOUNDARY.as_tuple, 0.0, 0.0, cw, ch)

    # ===== LABELS =====
    _label(cell, "VSS", L.MET1_LABEL.as_tuple, mid_x, (vss_rail_bot + vss_rail_top) / 2)
    _label(cell, "VDD", L.MET1_LABEL.as_tuple, mid_x, (vdd_rail_bot + vdd_rail_top) / 2)
    _label(cell, "BL", L.MET1_LABEL.as_tuple, nl_cx, bl_contact_y + licon_sz / 2)
    _label(cell, "BLB", L.MET1_LABEL.as_tuple, nr_cx, bl_contact_y + licon_sz / 2)
    _label(cell, "WL", L.POLY_LABEL.as_tuple, mid_x, (pg_gate_bot + pg_gate_top) / 2)

    return cell


def generate_bitcell(
    output_path: str = "sky130_sram_6t_bitcell.gds",
    generate_spice: bool = False,
    pd_w: float = PD_WIDTH,
    pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH,
) -> Path:
    """Generate the 6T bitcell and write to GDS (and optionally SPICE).

    Args:
        output_path: Path for the output GDS file.
        generate_spice: If True, also write a SPICE netlist.
        pd_w: Pull-down NMOS width.
        pg_w: Pass gate NMOS width.
        pu_w: Pull-up PMOS width.

    Returns:
        Path to the generated GDS file.
    """
    cell = create_bitcell(pd_w=pd_w, pg_w=pg_w, pu_w=pu_w)

    lib = gdstk.Library(name="rekolektion_sram", unit=1e-6, precision=1e-9)
    lib.add(cell)

    out = Path(output_path)
    lib.write_gds(str(out))

    area = CELL_WIDTH * CELL_HEIGHT
    cr = pd_w / pg_w if pg_w > 0 else 0
    print(f"Generated 6T bitcell: {out}")
    print(f"  Cell size: {CELL_WIDTH:.3f} x {CELL_HEIGHT:.3f} um = {area:.3f} um^2")
    print(f"  Transistors: PD={pd_w:.2f}/{GATE_LENGTH:.2f}, "
          f"PG={pg_w:.2f}/{GATE_LENGTH:.2f}, PU={pu_w:.2f}/{GATE_LENGTH:.2f}")
    print(f"  Cell ratio (PD/PG): {cr:.2f}")
    print(f"  Cell-only density: {1.0 / area * 1e6:,.0f} bits/mm^2")

    if generate_spice:
        spice_path = out.with_suffix(".spice")
        _write_spice_netlist(spice_path, pd_w, pg_w, pu_w)
        print(f"  SPICE netlist: {spice_path}")

    return out


def _write_spice_netlist(
    path: Path, pd_w: float, pg_w: float, pu_w: float
) -> None:
    """Write a SPICE subcircuit netlist for the 6T bitcell."""
    netlist = f"""\
* 6T SRAM Bitcell — SKY130
* Generated by rekolektion
*
* Ports: BL BLB WL VDD VSS
*
.subckt sky130_sram_6t_bitcell BL BLB WL VDD VSS

* Pull-down NMOS (left inverter)
XPD_L Q  QB  VSS VSS {NMOS_MODEL} w={pd_w}u l={GATE_LENGTH}u

* Pull-down NMOS (right inverter)
XPD_R QB Q   VSS VSS {NMOS_MODEL} w={pd_w}u l={GATE_LENGTH}u

* Pull-up PMOS (left inverter)
XPU_L Q  QB  VDD VDD {PMOS_MODEL} w={pu_w}u l={GATE_LENGTH}u

* Pull-up PMOS (right inverter)
XPU_R QB Q   VDD VDD {PMOS_MODEL} w={pu_w}u l={GATE_LENGTH}u

* Access transistor (left — BL side)
XPG_L BL WL  Q   VSS {NMOS_MODEL} w={pg_w}u l={GATE_LENGTH}u

* Access transistor (right — BLB side)
XPG_R BLB WL QB  VSS {NMOS_MODEL} w={pg_w}u l={GATE_LENGTH}u

.ends sky130_sram_6t_bitcell
"""
    path.write_text(netlist)
