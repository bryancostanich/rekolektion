"""6T SRAM bitcell layout generator for SKY130.

Generates a DRC-compliant 6T SRAM cell for the SkyWater SKY130 130nm
process. The cell tiles by mirroring in both X and Y for shared power
rails and bit line pairs.

6T SRAM Cell Topology:
                    VDD
                     |
                BL  PU-L  PU-R  BLB
                |    |      |    |
                PG-L-+------+-PG-R      ← WL (word line)
                     |      |
                    PD-L  PD-R
                     |      |
                    VSS

Layout Topology (cross-section, bottom to top):
    VSS met1 rail + P-sub tap
    NMOS diff: PD source (VSS) → PD gate → internal node → PG gate → PG drain (BL)
    N-P gap (nwell boundary, 0.52 μm)
    PMOS diff: PU drain (internal node) → PU gate → PU source (VDD)
    VDD met1 rail + N-well tap

Key Design Decisions:
    - Separate poly gates per transistor (no continuous poly → no bends)
    - Poly widens to contact landing pads outside diff (for gate connections)
    - Left half built first, right half is mirror → perfect symmetry
    - Cross-coupling via li1 through center gap
    - All min-size transistors (PD=PG=PU W=0.42, L=0.15) for density
    - Cell ratio CR=1.0; increase PD for read stability at area cost
"""

from pathlib import Path

import gdstk
import numpy as np

from rekolektion.tech.sky130 import LAYERS, RULES, NMOS_MODEL, PMOS_MODEL


# ---------------------------------------------------------------------------
# Default transistor sizing — all minimum for maximum density
# ---------------------------------------------------------------------------

PD_WIDTH = 0.42    # Pull-down NMOS channel width (μm)
PG_WIDTH = 0.42    # Pass gate NMOS channel width (μm)
PU_WIDTH = 0.42    # Pull-up PMOS channel width (μm)
GATE_LENGTH = 0.15  # All gates (μm)


# ---------------------------------------------------------------------------
# Cell geometry computed from design rules
# ---------------------------------------------------------------------------

def _snap(val: float, grid: float = 0.01) -> float:
    """Snap a coordinate to Magic's internal grid (10nm for SKY130)."""
    return round(val / grid) * grid


def _compute_cell_geometry(
    pd_w: float = PD_WIDTH, pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH, gate_l: float = GATE_LENGTH,
) -> dict:
    """Compute all cell coordinates from design rules and transistor sizing.

    Returns a dict of named coordinates. All values in μm, snapped to 5nm grid.
    Y = 0 at cell bottom, X = 0 at cell left.
    """
    R = RULES
    g = {}  # geometry dict

    # --- DRC constants ---
    diff_ext = 0.27        # Diff extension past gate for contact landing
                           # = gate-to-licon(0.055) + licon(0.17) + diff-encl(0.04)
    inter_gate = 0.29      # Gap between PD and PG gates (for internal node contact)
                           # = gate-to-licon(0.055) + licon(0.17) + licon-to-gate(0.055)
    poly_endcap = R.POLY_MIN_EXTENSION_PAST_DIFF  # 0.13
    nwell_to_ndiff = 0.34  # diff/tap.9
    nwell_encl_pdiff = R.DIFF_MIN_ENCLOSURE_BY_NWELL  # 0.18
    nmos_diff_w = max(pd_w, pg_w)
    pmos_diff_w = pu_w

    # --- Poly contact pad dimensions ---
    # Poly must widen outside diff to host a licon contact.
    # licon needs poly enclosure: 0.08 (one dir) + 0.05 (other dir)
    # Pad width (Y dir): licon(0.17) + 2×0.05 = 0.27 (symmetric about gate center)
    # Pad length (X dir): licon(0.17) + 0.08 + 0.05 = 0.30
    g["pad_width_y"] = R.LICON_SIZE + 2 * R.LICON_POLY_ENCLOSURE  # 0.27
    # Pad length in X: licon + asymmetric enclosure (0.05 inner + 0.08 outer)
    g["pad_length_x"] = R.LICON_POLY_ENCLOSURE + R.LICON_SIZE + R.LICON_POLY_ENCLOSURE_OTHER  # 0.05+0.17+0.08=0.30
    # But the gate poly already extends poly_endcap (0.13) past diff, which
    # provides part of this. The pad only needs to add the remainder:
    g["pad_extra_x"] = max(0.0, g["pad_length_x"] - poly_endcap)  # 0.30-0.13=0.17

    # --- Y coordinates (bottom to top) ---

    # VSS rail
    g["vss_bot"] = 0.00
    g["vss_top"] = 0.20  # Wide rail for met1 area rule + mcon

    # NMOS diffusion
    g["nmos_diff_bot"] = 0.06  # Overlaps with VSS zone for source contact sharing
    g["pd_gate_bot"] = g["nmos_diff_bot"] + diff_ext               # 0.33
    g["pd_gate_top"] = g["pd_gate_bot"] + gate_l                   # 0.48
    g["pg_gate_bot"] = g["pd_gate_top"] + inter_gate                # 0.77
    g["pg_gate_top"] = g["pg_gate_bot"] + gate_l                    # 0.92
    g["nmos_diff_top"] = g["pg_gate_top"] + diff_ext                # 1.19

    # Internal node contact (between PD and PG gates)
    g["int_node_y"] = (g["pd_gate_top"] + g["pg_gate_bot"]) / 2.0  # center of gap

    # BL contact (PG drain, at top of NMOS diff)
    g["bl_contact_y"] = g["pg_gate_top"] + 0.055 + R.LICON_DIFF_ENCLOSURE  # just above PG gate

    # N-well boundary
    g["nwell_bot"] = g["nmos_diff_top"] + nwell_to_ndiff            # 1.53
    g["pmos_diff_bot"] = g["nwell_bot"] + nwell_encl_pdiff          # 1.71

    # PMOS diffusion
    g["pu_gate_bot"] = g["pmos_diff_bot"] + diff_ext                # 1.98
    g["pu_gate_top"] = g["pu_gate_bot"] + gate_l                    # 2.13
    g["pmos_diff_top"] = g["pu_gate_top"] + diff_ext                # 2.40

    # PU drain contact (internal node, between pmos_diff_bot and PU gate)
    g["pu_drain_y"] = (g["pmos_diff_bot"] + g["pu_gate_bot"]) / 2.0

    # VDD rail
    g["vdd_bot"] = g["pmos_diff_top"] - 0.06  # Overlap with PU source zone
    g["vdd_top"] = g["pmos_diff_top"] + 0.14

    # Cell height
    g["cell_h"] = g["vdd_top"]

    # --- X coordinates (left half — right half is mirror) ---

    # Diff strips
    # Margin: licon.14 spacing (0.19) from diff to licon center edge,
    # then licon half + outer poly enclosure.
    # licon center at diff_edge + 0.19 + licon/2 from diff edge.
    # Pad outer edge = licon center + licon/2 + enclosure(0.08)
    poly_contact_to_diff = 0.19  # licon.14: min licon-to-diff
    g["margin"] = poly_contact_to_diff + R.LICON_SIZE + R.LICON_POLY_ENCLOSURE_OTHER + 0.02
    g["diff_l_x0"] = g["margin"]                   # left diff left edge
    g["diff_l_x1"] = g["diff_l_x0"] + nmos_diff_w  # left diff right edge

    # Center gap: needs room for WL poly + cross-coupling li1
    # At PG level: poly endcap (0.13) from each diff + WL span through center
    # At other levels: poly contact pads + li1 routing
    # Pad protrudes from diff edge. The poly endcap (0.13) is part of the pad.
    # Total protrusion from diff edge = pad_length_x (pad includes endcap).
    # Two pads face each other with li1 spacing between their inner edges.
    # Total protrusion from diff edge = poly_endcap + pad_extra
    total_protrusion = poly_endcap + g["pad_extra_x"]  # 0.13 + 0.17 = 0.30
    center_gap = 2 * total_protrusion + R.LI1_MIN_SPACING  # 0.60 + 0.17 = 0.77
    # But the li1 wires don't need to fit between pads — they route at different
    # Y levels. Only the WL poly and cross-coupling li1 need to fit.
    # Minimum: just enough for the WL poly to span + li1 at other Y levels.
    # Gate pads are on outer edges, so center gap only needs WL poly endcaps.
    # The WL poly crosses the gap as a continuous strip.
    # PD/PU gates have endcaps extending into the gap too → need poly spacing.
    center_gap = 2 * poly_endcap + R.POLY_MIN_SPACING  # 0.13+0.21+0.13 = 0.47

    g["diff_r_x0"] = g["diff_l_x1"] + center_gap
    g["diff_r_x1"] = g["diff_r_x0"] + nmos_diff_w
    g["cell_w"] = g["diff_r_x1"] + g["margin"]

    g["mid_x"] = g["cell_w"] / 2.0

    # PMOS diff X (same as NMOS for symmetry)
    g["pdiff_l_x0"] = g["diff_l_x0"]
    g["pdiff_l_x1"] = g["diff_l_x0"] + pmos_diff_w
    g["pdiff_r_x0"] = g["diff_r_x0"] + (nmos_diff_w - pmos_diff_w)  # right-align with NMOS
    g["pdiff_r_x1"] = g["diff_r_x1"]

    # Diff center X (for contact placement)
    g["nl_cx"] = (g["diff_l_x0"] + g["diff_l_x1"]) / 2.0
    g["nr_cx"] = (g["diff_r_x0"] + g["diff_r_x1"]) / 2.0
    g["pl_cx"] = (g["pdiff_l_x0"] + g["pdiff_l_x1"]) / 2.0
    g["pr_cx"] = (g["pdiff_r_x0"] + g["pdiff_r_x1"]) / 2.0

    # Gate poly contact pad positions (inner side of each diff, facing center)
    total_protrusion_x = poly_endcap + g["pad_extra_x"]

    # Left gates: pad on LEFT side of left diff (facing outward)
    g["lpad_x0"] = g["diff_l_x0"] - total_protrusion_x
    g["lpad_x1"] = g["diff_l_x0"]

    # Right gates: pad on RIGHT side of right diff (facing outward, mirror)
    g["rpad_x0"] = g["diff_r_x1"]
    g["rpad_x1"] = g["diff_r_x1"] + total_protrusion_x

    # Snap ALL coordinates to the manufacturing grid
    for key in g:
        if isinstance(g[key], float):
            g[key] = _snap(g[key])

    return g


# Module-level geometry for default sizing (used by tests, CLI)
_DEFAULT_GEOM = _compute_cell_geometry()
CELL_WIDTH = _DEFAULT_GEOM["cell_w"]
CELL_HEIGHT = _DEFAULT_GEOM["cell_h"]


# ---------------------------------------------------------------------------
# Layout helpers
# ---------------------------------------------------------------------------

def _rect(cell: gdstk.Cell, layer: tuple[int, int],
          x0: float, y0: float, x1: float, y1: float) -> None:
    """Add a rectangle to a cell."""
    cell.add(gdstk.rectangle((x0, y0), (x1, y1),
                              layer=layer[0], datatype=layer[1]))


def _label(cell: gdstk.Cell, text: str, layer: tuple[int, int],
           x: float, y: float) -> None:
    """Add a label to a cell."""
    cell.add(gdstk.Label(text, (x, y), layer=layer[0], texttype=layer[1]))


def _contact(cell: gdstk.Cell, cx: float, cy: float,
             contact_layer: tuple[int, int], size: float) -> None:
    """Place a square contact centered at (cx, cy)."""
    hs = size / 2.0
    _rect(cell, contact_layer, cx - hs, cy - hs, cx + hs, cy + hs)


def _li_pad(cell: gdstk.Cell, cx: float, cy: float,
            li_w: float, li_h: float) -> None:
    """Place an li1 rectangle centered at (cx, cy)."""
    _rect(cell, LAYERS.LI1.as_tuple,
          cx - li_w / 2, cy - li_h / 2, cx + li_w / 2, cy + li_h / 2)


# ---------------------------------------------------------------------------
# Main cell generator
# ---------------------------------------------------------------------------

def create_bitcell(
    pd_w: float = PD_WIDTH,
    pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH,
    gate_l: float = GATE_LENGTH,
) -> gdstk.Cell:
    """Create a 6T SRAM bitcell layout for SKY130.

    The cell tiles by mirroring in X (shared bit lines) and Y (shared
    power rails). Left and right halves are built as mirrors for
    guaranteed symmetry.
    """
    cell = gdstk.Cell("sky130_sram_6t_bitcell")
    L = LAYERS
    R = RULES

    g = _compute_cell_geometry(pd_w, pg_w, pu_w, gate_l)

    cw = g["cell_w"]
    ch = g["cell_h"]
    mid_x = g["mid_x"]
    poly_ext = R.POLY_MIN_EXTENSION_PAST_DIFF
    licon_sz = R.LICON_SIZE
    li_w = R.LI1_MIN_WIDTH
    li_encl = R.LI1_ENCLOSURE_OF_LICON
    mcon_sz = R.MCON_SIZE
    met1_w = R.MET1_MIN_WIDTH
    nsdm_enc = R.NSDM_ENCLOSURE_OF_DIFF
    psdm_enc = R.PSDM_ENCLOSURE_OF_DIFF

    # ===================================================================
    # N-WELL (upper portion of cell, extends past edges for tiling)
    # ===================================================================
    _rect(cell, L.NWELL.as_tuple,
          -0.10, g["nwell_bot"], cw + 0.10, ch + 0.10)

    # ===================================================================
    # DIFFUSION — built as left half then mirrored
    # ===================================================================

    # Left NMOS diff (hosts PD-L bottom, PG-L top)
    _rect(cell, L.DIFF.as_tuple,
          g["diff_l_x0"], g["nmos_diff_bot"],
          g["diff_l_x1"], g["nmos_diff_top"])
    # Right NMOS diff (mirror)
    _rect(cell, L.DIFF.as_tuple,
          g["diff_r_x0"], g["nmos_diff_bot"],
          g["diff_r_x1"], g["nmos_diff_top"])

    # Left PMOS diff
    _rect(cell, L.DIFF.as_tuple,
          g["pdiff_l_x0"], g["pmos_diff_bot"],
          g["pdiff_l_x1"], g["pmos_diff_top"])
    # Right PMOS diff (mirror)
    _rect(cell, L.DIFF.as_tuple,
          g["pdiff_r_x0"], g["pmos_diff_bot"],
          g["pdiff_r_x1"], g["pmos_diff_top"])

    # ===================================================================
    # IMPLANTS
    # ===================================================================
    for dx0, dx1 in [(g["diff_l_x0"], g["diff_l_x1"]),
                      (g["diff_r_x0"], g["diff_r_x1"])]:
        _rect(cell, L.NSDM.as_tuple,
              dx0 - nsdm_enc, g["nmos_diff_bot"] - nsdm_enc,
              dx1 + nsdm_enc, g["nmos_diff_top"] + nsdm_enc)

    for dx0, dx1 in [(g["pdiff_l_x0"], g["pdiff_l_x1"]),
                      (g["pdiff_r_x0"], g["pdiff_r_x1"])]:
        _rect(cell, L.PSDM.as_tuple,
              dx0 - psdm_enc, g["pmos_diff_bot"] - psdm_enc,
              dx1 + psdm_enc, g["pmos_diff_top"] + psdm_enc)

    # ===================================================================
    # POLYSILICON GATES — separate per transistor, with contact pads
    # ===================================================================

    pad_w_y = g["pad_width_y"]  # pad height in Y (wider than gate)
    pad_half_y = pad_w_y / 2.0

    # licon.14: poly contact (licon on poly) to diffusion spacing = 0.19
    licon_to_diff = 0.19
    # Poly enclosures around licon on pad
    encl_inner = R.LICON_POLY_ENCLOSURE       # 0.05 (toward diff)
    encl_outer = R.LICON_POLY_ENCLOSURE_OTHER  # 0.08 (away from diff)

    def _gate_with_pad(diff_x0, diff_x1, gate_bot, gate_top, pad_side):
        """Draw a gate poly crossing diff, with a contact pad on one side.

        The licon is placed at minimum licon.14 spacing from diff edge.
        The poly pad wraps around the licon with proper enclosure.
        Returns (pad_cx, pad_cy) = center of the licon on the pad.
        """
        gate_cy = (gate_bot + gate_top) / 2.0

        # Gate poly (crossing diff, with endcap)
        _rect(cell, L.POLY.as_tuple,
              diff_x0 - poly_ext, gate_bot,
              diff_x1 + poly_ext, gate_top)

        if pad_side == "right":
            # Licon center X: diff right edge + spacing + half licon
            licon_cx = _snap(diff_x1 + licon_to_diff + licon_sz / 2.0)
            # Pad extends from encl_inner before licon to encl_outer after
            pad_x0 = licon_cx - licon_sz / 2.0 - encl_inner
            pad_x1 = licon_cx + licon_sz / 2.0 + encl_outer
            # Continuous poly from gate endcap through pad
            _rect(cell, L.POLY.as_tuple,
                  diff_x1 + poly_ext, gate_bot,
                  pad_x1, gate_top)
            # Wider pad section
            _rect(cell, L.POLY.as_tuple,
                  pad_x0, gate_cy - pad_half_y,
                  pad_x1, gate_cy + pad_half_y)
            return (licon_cx, gate_cy)
        else:  # left
            licon_cx = _snap(diff_x0 - licon_to_diff - licon_sz / 2.0)
            pad_x0 = licon_cx - licon_sz / 2.0 - encl_outer
            pad_x1 = licon_cx + licon_sz / 2.0 + encl_inner
            # Continuous poly from pad through gate endcap
            _rect(cell, L.POLY.as_tuple,
                  pad_x0, gate_bot,
                  diff_x0 - poly_ext, gate_top)
            # Wider pad section
            _rect(cell, L.POLY.as_tuple,
                  pad_x0, gate_cy - pad_half_y,
                  pad_x1, gate_cy + pad_half_y)
            return (licon_cx, gate_cy)

    # --- Left inverter gates (PD-L + PU-L) — pads face LEFT (outward) ---
    pdl_pad = _gate_with_pad(g["diff_l_x0"], g["diff_l_x1"],
                              g["pd_gate_bot"], g["pd_gate_top"], "left")
    pul_pad = _gate_with_pad(g["pdiff_l_x0"], g["pdiff_l_x1"],
                              g["pu_gate_bot"], g["pu_gate_top"], "left")

    # --- Right inverter gates (PD-R + PU-R) — pads face RIGHT (outward) ---
    pdr_pad = _gate_with_pad(g["diff_r_x0"], g["diff_r_x1"],
                              g["pd_gate_bot"], g["pd_gate_top"], "right")
    pur_pad = _gate_with_pad(g["pdiff_r_x0"], g["pdiff_r_x1"],
                              g["pu_gate_bot"], g["pu_gate_top"], "right")

    # --- PG gates (word line) — no pads, WL connects through center ---
    _gate_with_pad(g["diff_l_x0"], g["diff_l_x1"],
                   g["pg_gate_bot"], g["pg_gate_top"], "left")
    _gate_with_pad(g["diff_r_x0"], g["diff_r_x1"],
                   g["pg_gate_bot"], g["pg_gate_top"], "right")

    # Word line connection: poly strip connecting PG-L pad to PG-R pad
    wl_cy = (g["pg_gate_bot"] + g["pg_gate_top"]) / 2.0
    _rect(cell, L.POLY.as_tuple,
          g["diff_l_x1"] + poly_ext, wl_cy - gate_l / 2.0,
          g["diff_r_x0"] - poly_ext, wl_cy + gate_l / 2.0)

    # ===================================================================
    # CONTACTS ON DIFFUSION (licon) + LI1 pads
    # ===================================================================

    # LI1 minimum area: 0.0561 μm². Minimum pad: 0.17 × 0.33 = 0.0561
    li_min_area = 0.0561
    li_pad_h = max(licon_sz + 2 * li_encl, li_min_area / li_w)  # ensure area rule
    li_pad_w = max(li_w, li_min_area / li_pad_h)  # ensure area rule

    # --- VSS source contacts (PD source, bottom of NMOS diff) ---
    vss_cy = g["nmos_diff_bot"] + 0.04 + licon_sz / 2.0
    for cx in (g["nl_cx"], g["nr_cx"]):
        _contact(cell, cx, vss_cy, L.LICON1.as_tuple, licon_sz)
        _li_pad(cell, cx, vss_cy, li_pad_w, li_pad_h)

    # --- Internal node contacts (between PD and PG, on NMOS diff) ---
    int_cy = g["int_node_y"]
    for cx in (g["nl_cx"], g["nr_cx"]):
        _contact(cell, cx, int_cy, L.LICON1.as_tuple, licon_sz)
        _li_pad(cell, cx, int_cy, li_pad_w, li_pad_h)

    # --- BL/BLB contacts (PG drain, top of NMOS diff) ---
    bl_cy = g["nmos_diff_top"] - 0.04 - licon_sz / 2.0
    for cx in (g["nl_cx"], g["nr_cx"]):
        _contact(cell, cx, bl_cy, L.LICON1.as_tuple, licon_sz)
        _li_pad(cell, cx, bl_cy, li_pad_w, li_pad_h)

    # --- PU drain contacts (internal node, bottom of PMOS diff) ---
    pu_drain_cy = g["pmos_diff_bot"] + 0.04 + licon_sz / 2.0
    for cx in (g["pl_cx"], g["pr_cx"]):
        _contact(cell, cx, pu_drain_cy, L.LICON1.as_tuple, licon_sz)
        _li_pad(cell, cx, pu_drain_cy, li_pad_w, li_pad_h)

    # --- VDD source contacts (PU source, top of PMOS diff) ---
    vdd_cy = g["pmos_diff_top"] - 0.04 - licon_sz / 2.0
    for cx in (g["pl_cx"], g["pr_cx"]):
        _contact(cell, cx, vdd_cy, L.LICON1.as_tuple, licon_sz)
        _li_pad(cell, cx, vdd_cy, li_pad_w, li_pad_h)

    # ===================================================================
    # GATE POLY CONTACTS (licon on poly pads) + LI1 pads
    # ===================================================================

    # Left inverter gate contacts (on PD-L and PU-L pads)
    _contact(cell, pdl_pad[0], pdl_pad[1], L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, pdl_pad[0], pdl_pad[1], li_w + 2 * li_encl, li_w)
    _contact(cell, pul_pad[0], pul_pad[1], L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, pul_pad[0], pul_pad[1], li_w + 2 * li_encl, li_w)

    # Right inverter gate contacts (on PD-R and PU-R pads)
    _contact(cell, pdr_pad[0], pdr_pad[1], L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, pdr_pad[0], pdr_pad[1], li_w + 2 * li_encl, li_w)
    _contact(cell, pur_pad[0], pur_pad[1], L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, pur_pad[0], pur_pad[1], li_w + 2 * li_encl, li_w)

    # ===================================================================
    # CROSS-COUPLING (li1 routing via outer edges)
    # ===================================================================
    # Pads are on the OUTER sides. Cross-coupling routes:
    # Q net: left drain contacts → left gate pads (same outer edge)
    #        → vertical li1 connecting PD-L pad to PU-L pad
    #        Left drains are the Q node; left gate is driven by QB.
    #        Wait — left gate is driven by QB (right output).
    #        And right gate is driven by Q (left output).
    #
    # So the cross-coupling is:
    # Q net (left output): left NMOS drain + left PMOS drain → right gate
    # QB net (right output): right NMOS drain + right PMOS drain → left gate
    #
    # With outer pads:
    # - Left gate pads (outer left) need to connect to right drain (inner right)
    # - Right gate pads (outer right) need to connect to left drain (inner left)
    # This means cross-coupling routes ACROSS the cell horizontally.

    # --- Q net: left drains → right gates (pads on right outer edge) ---
    # Horizontal li1 from left NMOS internal node across to right gate pad
    q_route_y = int_cy
    _rect(cell, L.LI1.as_tuple,
          g["nl_cx"] - li_w / 2, q_route_y - li_w / 2,
          pdr_pad[0] + li_w / 2, q_route_y + li_w / 2)

    # Vertical li1: left drain to left PU drain (Q node vertical)
    _rect(cell, L.LI1.as_tuple,
          g["nl_cx"] - li_w / 2, int_cy - li_w / 2,
          g["nl_cx"] + li_w / 2, pu_drain_cy + li_w / 2)

    # Vertical li1: right PD gate pad → right PU gate pad (right gate stack)
    _rect(cell, L.LI1.as_tuple,
          pdr_pad[0] - li_w / 2, pdr_pad[1] - li_w / 2,
          pdr_pad[0] + li_w / 2, pur_pad[1] + li_w / 2)

    # --- QB net: right drains → left gates (pads on left outer edge) ---
    qb_route_y = int_cy + li_w + R.LI1_MIN_SPACING
    # Extend right drain contact up to route Y
    _rect(cell, L.LI1.as_tuple,
          g["nr_cx"] - li_w / 2, int_cy - li_w / 2,
          g["nr_cx"] + li_w / 2, qb_route_y + li_w / 2)
    # Horizontal li1 from right drain across to left gate pad
    _rect(cell, L.LI1.as_tuple,
          pdl_pad[0] - li_w / 2, qb_route_y - li_w / 2,
          g["nr_cx"] + li_w / 2, qb_route_y + li_w / 2)

    # Vertical li1: right drain to right PU drain (QB node vertical)
    _rect(cell, L.LI1.as_tuple,
          g["nr_cx"] - li_w / 2, int_cy - li_w / 2,
          g["nr_cx"] + li_w / 2, pu_drain_cy + li_w / 2)

    # Vertical li1: left PD gate pad → left PU gate pad (left gate stack)
    _rect(cell, L.LI1.as_tuple,
          pdl_pad[0] - li_w / 2, pdl_pad[1] - li_w / 2,
          pdl_pad[0] + li_w / 2, pul_pad[1] + li_w / 2)

    # ===================================================================
    # MET1 POWER RAILS + MCON
    # ===================================================================

    # VSS rail (full width, bottom)
    _rect(cell, L.MET1.as_tuple, 0.0, g["vss_bot"], cw, g["vss_top"])

    # VDD rail (full width, top)
    _rect(cell, L.MET1.as_tuple, 0.0, g["vdd_bot"], cw, g["vdd_top"])

    # MCON + li1 extensions from source contacts to power rails
    for cx in (g["nl_cx"], g["nr_cx"]):
        # VSS: li1 down to rail, mcon in rail
        _rect(cell, L.LI1.as_tuple,
              cx - li_w / 2, g["vss_bot"],
              cx + li_w / 2, vss_cy + licon_sz / 2 + li_encl)
        _contact(cell, cx, g["vss_bot"] + mcon_sz / 2 + 0.03,
                 L.MCON.as_tuple, mcon_sz)

    for cx in (g["pl_cx"], g["pr_cx"]):
        # VDD: li1 up to rail, mcon in rail
        _rect(cell, L.LI1.as_tuple,
              cx - li_w / 2, vdd_cy - licon_sz / 2 - li_encl,
              cx + li_w / 2, g["vdd_top"])
        _contact(cell, cx, g["vdd_top"] - mcon_sz / 2 - 0.03,
                 L.MCON.as_tuple, mcon_sz)

    # ===================================================================
    # MET1 BIT LINE CONTACTS (BL, BLB) + MCON
    # ===================================================================
    # Met1 minimum area: 0.083 μm². Need ~0.29 × 0.29 or 0.23 × 0.36
    met1_bl_w = max(met1_w * 2, mcon_sz + 2 * R.MET1_ENCLOSURE_OF_MCON_OTHER)  # width
    met1_bl_h = max(0.083 / met1_bl_w, mcon_sz + 2 * R.MET1_ENCLOSURE_OF_MCON)  # height
    for cx in (g["nl_cx"], g["nr_cx"]):
        _contact(cell, cx, bl_cy, L.MCON.as_tuple, mcon_sz)
        _rect(cell, L.MET1.as_tuple,
              cx - met1_bl_w / 2, bl_cy - met1_bl_h / 2,
              cx + met1_bl_w / 2, bl_cy + met1_bl_h / 2)

    # ===================================================================
    # SUBSTRATE / WELL TAPS
    # ===================================================================
    # Tap minimum area: 0.07011 μm² → need at least sqrt(0.07011) ≈ 0.265 on a side
    tap_w = 0.27  # Slightly above minimum to ensure area rule
    tap_h = 0.27

    # P-sub tap (NMOS region → VSS) — must be OUTSIDE nwell, below nwell_bot
    # Place in the VSS rail zone, well away from nwell
    psub_tap_cy = (g["vss_bot"] + g["vss_top"]) / 2.0
    _rect(cell, L.TAP.as_tuple,
          mid_x - tap_w / 2, psub_tap_cy - tap_h / 2,
          mid_x + tap_w / 2, psub_tap_cy + tap_h / 2)
    _rect(cell, L.NSDM.as_tuple,
          mid_x - tap_w / 2 - nsdm_enc, psub_tap_cy - tap_h / 2 - nsdm_enc,
          mid_x + tap_w / 2 + nsdm_enc, psub_tap_cy + tap_h / 2 + nsdm_enc)
    _contact(cell, mid_x, psub_tap_cy, L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, mid_x, psub_tap_cy, li_w + 2 * li_encl, li_w + 2 * li_encl)

    # N-well tap (PMOS region → VDD) — must be INSIDE nwell
    # Place in VDD rail zone, well inside nwell (nwell_bot + 0.18 enclosure min)
    nw_tap_cy = (g["vdd_bot"] + g["vdd_top"]) / 2.0
    _rect(cell, L.TAP.as_tuple,
          mid_x - tap_w / 2, nw_tap_cy - tap_h / 2,
          mid_x + tap_w / 2, nw_tap_cy + tap_h / 2)
    _rect(cell, L.PSDM.as_tuple,
          mid_x - tap_w / 2 - psdm_enc, nw_tap_cy - tap_h / 2 - psdm_enc,
          mid_x + tap_w / 2 + psdm_enc, nw_tap_cy + tap_h / 2 + psdm_enc)
    _contact(cell, mid_x, nw_tap_cy, L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, mid_x, nw_tap_cy, li_w + 2 * li_encl, li_w + 2 * li_encl)

    # ===================================================================
    # CELL BOUNDARY + LABELS
    # ===================================================================
    _rect(cell, L.BOUNDARY.as_tuple, 0.0, 0.0, cw, ch)

    _label(cell, "VSS", L.MET1_LABEL.as_tuple, mid_x, (g["vss_bot"] + g["vss_top"]) / 2)
    _label(cell, "VDD", L.MET1_LABEL.as_tuple, mid_x, (g["vdd_bot"] + g["vdd_top"]) / 2)
    _label(cell, "BL", L.MET1_LABEL.as_tuple, g["nl_cx"], bl_cy)
    _label(cell, "BLB", L.MET1_LABEL.as_tuple, g["nr_cx"], bl_cy)
    _label(cell, "WL", L.POLY_LABEL.as_tuple, mid_x, wl_cy)

    return cell


# ---------------------------------------------------------------------------
# Output generation
# ---------------------------------------------------------------------------

def generate_bitcell(
    output_path: str = "sky130_sram_6t_bitcell.gds",
    generate_spice: bool = False,
    pd_w: float = PD_WIDTH,
    pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH,
) -> Path:
    """Generate the 6T bitcell and write to GDS (and optionally SPICE)."""
    cell = create_bitcell(pd_w=pd_w, pg_w=pg_w, pu_w=pu_w)

    lib = gdstk.Library(name="rekolektion_sram", unit=1e-6, precision=1e-9)
    lib.add(cell)

    out = Path(output_path)
    lib.write_gds(str(out))

    g = _compute_cell_geometry(pd_w, pg_w, pu_w)
    area = g["cell_w"] * g["cell_h"]
    cr = pd_w / pg_w if pg_w > 0 else 0
    print(f"Generated 6T bitcell: {out}")
    print(f"  Cell size: {g['cell_w']:.3f} x {g['cell_h']:.3f} um = {area:.3f} um^2")
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
