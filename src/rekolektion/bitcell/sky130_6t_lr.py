"""6T SRAM bitcell layout generator — LEFT/RIGHT (NMOS-left, PMOS-right) topology.

Inspired by the SkyWater foundry cell which uses an NMOS-left / PMOS-right
split rather than the more common NMOS-bottom / PMOS-top arrangement.

Key advantages of left/right topology:
1. N-well boundary is VERTICAL — when cells tile in columns (X-mirrored),
   adjacent PMOS sides share the nwell. The N-P gap cost is amortized.
2. Power rails run VERTICALLY at cell edges — shared between adjacent cells.
3. Poly gates run HORIZONTALLY across the full cell width, crossing both
   NMOS and PMOS diff. This enables continuous poly (no separate gates).
4. Cross-coupling is shorter — PD drain and PU drain are at the same Y
   level on opposite sides, connected by horizontal li1.

Layout (left to right):
    VGND (met1 vert) | NMOS diff | N-P gap (nwell) | PMOS diff | VPWR (met1 vert)

Y levels (bottom to top):
    WL_bottom: PG-bottom gate (horizontal poly, full width)
    gate_A: cross-coupled gate (poly, full width + pad in gap for QB)
    gate_B: cross-coupled gate (poly, full width + pad on NMOS outer for Q)
    WL_top: PG-top gate (horizontal poly, full width)

Cross-coupling strategy:
    gate_A is driven by QB (PMOS drain) — contact pad in the N-P gap,
    short horizontal li1 from PMOS int_bot contact.
    gate_B is driven by Q (NMOS drain) — contact pad on the NMOS outer
    (left) side, short vertical li1 from NMOS int_top contact.
    This avoids routing two nets through the same gap corridor.

All dimensions in micrometers. Grid snapped to 5nm for SKY130.
"""

from pathlib import Path

import gdstk

from rekolektion.tech.sky130 import LAYERS, RULES, NMOS_MODEL, PMOS_MODEL


# ---------------------------------------------------------------------------
# Default transistor sizing
# ---------------------------------------------------------------------------

PD_WIDTH = 0.42
PG_WIDTH = 0.42
PU_WIDTH = 0.42
GATE_LENGTH = 0.15


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _snap(val: float, grid: float = 0.005) -> float:
    """Snap a coordinate to Magic's internal grid (5nm for SKY130)."""
    return round(val / grid) * grid


def _rect(cell: gdstk.Cell, layer: tuple[int, int],
          x0: float, y0: float, x1: float, y1: float) -> None:
    cell.add(gdstk.rectangle((_snap(x0), _snap(y0)), (_snap(x1), _snap(y1)),
                              layer=layer[0], datatype=layer[1]))


def _label(cell: gdstk.Cell, text: str, layer: tuple[int, int],
           x: float, y: float) -> None:
    cell.add(gdstk.Label(text, (x, y), layer=layer[0], texttype=layer[1]))


def _contact(cell: gdstk.Cell, cx: float, cy: float,
             contact_layer: tuple[int, int], size: float) -> None:
    hs = size / 2.0
    _rect(cell, contact_layer, cx - hs, cy - hs, cx + hs, cy + hs)


def _li_pad(cell: gdstk.Cell, cx: float, cy: float,
            li_w: float, li_h: float) -> None:
    _rect(cell, LAYERS.LI1.as_tuple,
          cx - li_w / 2, cy - li_h / 2, cx + li_w / 2, cy + li_h / 2)


# ---------------------------------------------------------------------------
# Cell geometry computation
# ---------------------------------------------------------------------------

def _compute_cell_geometry(
    pd_w: float = PD_WIDTH, pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH, gate_l: float = GATE_LENGTH,
) -> dict:
    R = RULES
    g = {}

    nmos_diff_w = max(pd_w, pg_w)
    pmos_diff_w = pu_w

    poly_ext = R.POLY_MIN_EXTENSION_PAST_DIFF    # 0.13
    licon_sz = R.LICON_SIZE                       # 0.17
    li_w = R.LI1_MIN_WIDTH                        # 0.17
    li_encl = R.LI1_ENCLOSURE_OF_LICON            # 0.08
    mcon_sz = R.MCON_SIZE                          # 0.17
    nsdm_enc = R.NSDM_ENCLOSURE_OF_DIFF           # 0.125
    psdm_enc = R.PSDM_ENCLOSURE_OF_DIFF           # 0.125
    nwell_encl = R.DIFF_MIN_ENCLOSURE_BY_NWELL    # 0.18

    # Poly contact pad dims (licon on poly)
    pad_w_x = licon_sz + 2 * R.LICON_POLY_ENCLOSURE        # 0.27
    pad_h_y = licon_sz + 2 * R.LICON_POLY_ENCLOSURE_OTHER   # 0.33

    # li1 pad height around diff licon
    li_pad_h = licon_sz + 2 * li_encl  # 0.33

    # --- N-P gap in X ---
    # gate_A poly contact in gap needs clearance from both diffs.
    licon_to_ndiff = 0.19    # licon.14
    licon_to_pdiff = 0.235   # licon.9 + psdm.5a
    contact_gap = licon_to_ndiff + licon_sz + licon_to_pdiff  # 0.595
    structural_gap = 0.34 + nwell_encl  # 0.52
    np_gap = max(contact_gap, structural_gap)

    # Power rail width
    rail_w = max(R.MET1_MIN_WIDTH, mcon_sz + 2 * R.MET1_ENCLOSURE_OF_MCON_OTHER)  # 0.29

    # gate_B poly contact pad on NMOS outer (left) side.
    # Pad needs licon.14 (0.19) clearance from NMOS diff.
    # Licon center: nmos_diff_x0 - licon_to_ndiff - licon_sz/2
    # Pad outer edge: licon_center - pad_w_x/2
    licon_to_diff_pad = licon_to_ndiff  # 0.19

    # Margin from NMOS outer (left) side: enough for poly pad + rail
    # pad outer X from diff: licon_to_diff_pad + licon_sz/2 + pad_w_x/2
    #                      = 0.19 + 0.085 + 0.135 = 0.41
    # But we need nsdm clearance too. And the rail needs to fit.
    # Approach: set margin = distance from diff left edge to rail right edge
    outer_pad_extent = licon_to_diff_pad + licon_sz / 2.0 + pad_w_x / 2.0  # 0.41
    margin_left = max(outer_pad_extent + 0.05, 0.20)  # 0.46 — room for pad + buffer

    margin_right = 0.20  # PMOS side margin

    # --- X coordinates ---
    g["vgnd_x0"] = 0.0
    g["vgnd_x1"] = rail_w

    g["nmos_diff_x0"] = _snap(g["vgnd_x1"] + margin_left)
    g["nmos_diff_x1"] = _snap(g["nmos_diff_x0"] + nmos_diff_w)

    g["nwell_x0"] = _snap(g["nmos_diff_x1"] + 0.34)

    g["pmos_diff_x0"] = _snap(g["nmos_diff_x1"] + np_gap)
    g["pmos_diff_x1"] = _snap(g["pmos_diff_x0"] + pmos_diff_w)

    g["vpwr_x0"] = _snap(g["pmos_diff_x1"] + margin_right)
    g["vpwr_x1"] = _snap(g["vpwr_x0"] + rail_w)

    g["cell_w"] = g["vpwr_x1"]

    # Gap poly contact center X (for gate_A, QB net)
    g["gap_licon_cx"] = _snap(g["nmos_diff_x1"] + licon_to_ndiff + licon_sz / 2.0)

    # NMOS outer poly contact center X (for gate_B, Q net)
    g["nmos_outer_licon_cx"] = _snap(g["nmos_diff_x0"] - licon_to_diff_pad - licon_sz / 2.0)

    # Diff center X
    g["nmos_cx"] = _snap((g["nmos_diff_x0"] + g["nmos_diff_x1"]) / 2.0)
    g["pmos_cx"] = _snap((g["pmos_diff_x0"] + g["pmos_diff_x1"]) / 2.0)

    # --- Y coordinates ---
    gate_to_licon = 0.065  # > 0.055 licon.11 minimum

    # Contact zone must give li.3 spacing between adjacent li1 pads:
    # zone + gate_l >= li_pad_h + li_spacing → zone >= 0.33 + 0.17 - 0.15 = 0.35
    zone_from_licon = 2 * gate_to_licon + licon_sz  # 0.30
    zone_from_li = li_pad_h + R.LI1_MIN_SPACING - gate_l  # 0.35
    zone_single = max(zone_from_licon, zone_from_li)  # 0.35

    # Poly pad extends beyond gate stripe
    pad_ext_y = (pad_h_y - gate_l) / 2.0  # 0.09

    wl_to_cc_zone = max(zone_single, R.POLY_MIN_SPACING + pad_ext_y)
    cc_to_cc_zone = max(zone_single, R.POLY_MIN_SPACING + 2 * pad_ext_y)

    # Outer extension must ensure BL pad to int_node pad spacing >= li.3 (0.17)
    # BL contact center is at outer_ext - (gate_to_licon + licon_sz/2) from gate center
    # Actually: BL pad center is at diff_bot + LICON_DIFF_ENCLOSURE_OTHER + licon/2
    # int_bot pad center is at (wl_bot_y1 + gate_a_y0)/2
    # We need: int_bot_cy - bl_bot_cy >= li_pad_h + li_spacing = 0.33 + 0.17 = 0.50
    # int_bot_cy = wl_bot_cy + gate_l/2 + zone_single/2
    #            = (outer_ext + gate_l/2) + gate_l/2 + zone_single/2
    #            = outer_ext + gate_l + zone_single/2
    # bl_bot_cy = LICON_DIFF_ENCLOSURE_OTHER + licon_sz/2 = 0.06 + 0.085 = 0.145
    # Requirement: outer_ext + gate_l + zone_single/2 - 0.145 >= 0.50
    # outer_ext >= 0.50 + 0.145 - gate_l - zone_single/2
    #           = 0.50 + 0.145 - 0.15 - 0.175 = 0.32
    bl_bot_pos = R.LICON_DIFF_ENCLOSURE_OTHER + licon_sz / 2.0
    min_outer = (li_pad_h + R.LI1_MIN_SPACING + bl_bot_pos
                 - gate_l - zone_single / 2.0)
    outer_ext = max(R.DIFF_EXTENSION_PAST_POLY,
                    gate_to_licon + licon_sz + R.LICON_DIFF_ENCLOSURE_OTHER,
                    min_outer)

    g["diff_bot"] = 0.0
    wl_bot_cy = outer_ext + gate_l / 2.0
    gate_a_cy = wl_bot_cy + gate_l / 2.0 + wl_to_cc_zone + gate_l / 2.0
    gate_b_cy = gate_a_cy + gate_l / 2.0 + cc_to_cc_zone + gate_l / 2.0
    wl_top_cy = gate_b_cy + gate_l / 2.0 + wl_to_cc_zone + gate_l / 2.0
    g["diff_top"] = wl_top_cy + gate_l / 2.0 + outer_ext

    g["cell_h"] = g["diff_top"]

    g["wl_bot_y0"] = wl_bot_cy - gate_l / 2.0
    g["wl_bot_y1"] = wl_bot_cy + gate_l / 2.0
    g["gate_a_y0"] = gate_a_cy - gate_l / 2.0
    g["gate_a_y1"] = gate_a_cy + gate_l / 2.0
    g["gate_b_y0"] = gate_b_cy - gate_l / 2.0
    g["gate_b_y1"] = gate_b_cy + gate_l / 2.0
    g["wl_top_y0"] = wl_top_cy - gate_l / 2.0
    g["wl_top_y1"] = wl_top_cy + gate_l / 2.0

    g["wl_bot_cy"] = wl_bot_cy
    g["gate_a_cy"] = gate_a_cy
    g["gate_b_cy"] = gate_b_cy
    g["wl_top_cy"] = wl_top_cy

    # Contact Y positions centered in zones
    g["bl_bot_cy"] = _snap(g["diff_bot"] + R.LICON_DIFF_ENCLOSURE_OTHER + licon_sz / 2.0)
    g["int_bot_cy"] = _snap((g["wl_bot_y1"] + g["gate_a_y0"]) / 2.0)
    g["pwr_cy"] = _snap((g["gate_a_y1"] + g["gate_b_y0"]) / 2.0)
    g["int_top_cy"] = _snap((g["gate_b_y1"] + g["wl_top_y0"]) / 2.0)
    g["bl_top_cy"] = _snap(g["diff_top"] - R.LICON_DIFF_ENCLOSURE_OTHER - licon_sz / 2.0)

    # Store params
    for name, val in [
        ("gate_l", gate_l), ("nmos_diff_w", nmos_diff_w),
        ("pmos_diff_w", pmos_diff_w), ("np_gap", np_gap),
        ("rail_w", rail_w), ("poly_ext", poly_ext),
        ("licon_sz", licon_sz), ("li_w", li_w), ("li_encl", li_encl),
        ("mcon_sz", mcon_sz), ("nsdm_enc", nsdm_enc), ("psdm_enc", psdm_enc),
        ("outer_ext", outer_ext), ("pad_w_x", pad_w_x), ("pad_h_y", pad_h_y),
        ("nwell_encl", nwell_encl), ("li_pad_h", li_pad_h),
    ]:
        g[name] = val

    # Snap ALL
    for key in g:
        if isinstance(g[key], float):
            g[key] = _snap(g[key])

    return g


_DEFAULT_GEOM = _compute_cell_geometry()
CELL_WIDTH = _DEFAULT_GEOM["cell_w"]
CELL_HEIGHT = _DEFAULT_GEOM["cell_h"]


# ---------------------------------------------------------------------------
# Main cell generator
# ---------------------------------------------------------------------------

def create_bitcell(
    pd_w: float = PD_WIDTH, pg_w: float = PG_WIDTH,
    pu_w: float = PU_WIDTH, gate_l: float = GATE_LENGTH,
) -> gdstk.Cell:
    """Create a 6T SRAM bitcell with left/right NMOS/PMOS topology."""
    cell = gdstk.Cell("sky130_sram_6t_bitcell_lr")
    L = LAYERS
    R = RULES

    g = _compute_cell_geometry(pd_w, pg_w, pu_w, gate_l)

    cw = g["cell_w"]
    ch = g["cell_h"]
    poly_ext = g["poly_ext"]
    licon_sz = g["licon_sz"]
    li_w = g["li_w"]
    li_encl = g["li_encl"]
    mcon_sz = g["mcon_sz"]
    nsdm_enc = g["nsdm_enc"]
    psdm_enc = g["psdm_enc"]
    pad_w_x = g["pad_w_x"]
    pad_h_y = g["pad_h_y"]
    gap_licon_cx = g["gap_licon_cx"]
    nmos_outer_cx = g["nmos_outer_licon_cx"]
    li_pad_h = g["li_pad_h"]

    # ===================================================================
    # N-WELL (PMOS side, extends past Y edges)
    # ===================================================================
    nwell_encl = g["nwell_encl"]
    _rect(cell, L.NWELL.as_tuple,
          g["nwell_x0"], g["diff_bot"] - nwell_encl,
          cw + 0.10, g["diff_top"] + nwell_encl)

    # ===================================================================
    # DIFFUSION
    # ===================================================================
    _rect(cell, L.DIFF.as_tuple,
          g["nmos_diff_x0"], g["diff_bot"],
          g["nmos_diff_x1"], g["diff_top"])
    _rect(cell, L.DIFF.as_tuple,
          g["pmos_diff_x0"], g["diff_bot"],
          g["pmos_diff_x1"], g["diff_top"])

    # ===================================================================
    # IMPLANTS
    # ===================================================================
    _rect(cell, L.NSDM.as_tuple,
          g["nmos_diff_x0"] - nsdm_enc, g["diff_bot"] - nsdm_enc,
          g["nmos_diff_x1"] + nsdm_enc, g["diff_top"] + nsdm_enc)
    _rect(cell, L.PSDM.as_tuple,
          g["pmos_diff_x0"] - psdm_enc, g["diff_bot"] - psdm_enc,
          g["pmos_diff_x1"] + psdm_enc, g["diff_top"] + psdm_enc)

    # ===================================================================
    # POLY GATES — 4 horizontal stripes across both diff regions
    # ===================================================================
    poly_x0 = g["nmos_diff_x0"] - poly_ext
    poly_x1 = g["pmos_diff_x1"] + poly_ext

    for y0_key, y1_key in [("wl_bot_y0", "wl_bot_y1"),
                            ("gate_a_y0", "gate_a_y1"),
                            ("gate_b_y0", "gate_b_y1"),
                            ("wl_top_y0", "wl_top_y1")]:
        _rect(cell, L.POLY.as_tuple, poly_x0, g[y0_key], poly_x1, g[y1_key])

    # Gate A poly contact pad in gap (for QB net)
    _rect(cell, L.POLY.as_tuple,
          gap_licon_cx - pad_w_x / 2.0, g["gate_a_cy"] - pad_h_y / 2.0,
          gap_licon_cx + pad_w_x / 2.0, g["gate_a_cy"] + pad_h_y / 2.0)

    # Gate B poly contact pad on NMOS outer (left) side (for Q net)
    _rect(cell, L.POLY.as_tuple,
          nmos_outer_cx - pad_w_x / 2.0, g["gate_b_cy"] - pad_h_y / 2.0,
          nmos_outer_cx + pad_w_x / 2.0, g["gate_b_cy"] + pad_h_y / 2.0)
    # Extend gate_B poly from its main stripe to the pad
    # The main stripe already extends to poly_x0 = nmos_diff_x0 - poly_ext.
    # The pad is at nmos_outer_cx which is further left.
    # Connect them if there's a gap (pad right edge may overlap with poly_x0).
    pad_right = nmos_outer_cx + pad_w_x / 2.0
    if pad_right < poly_x0:
        _rect(cell, L.POLY.as_tuple,
              pad_right, g["gate_b_y0"],
              poly_x0, g["gate_b_y1"])

    # ===================================================================
    # LICON ON DIFF + LI1 PADS
    # ===================================================================
    for cy_key in ("bl_bot_cy", "int_bot_cy", "pwr_cy", "int_top_cy", "bl_top_cy"):
        cy = g[cy_key]
        _contact(cell, g["nmos_cx"], cy, L.LICON1.as_tuple, licon_sz)
        _li_pad(cell, g["nmos_cx"], cy, li_w, li_pad_h)
        _contact(cell, g["pmos_cx"], cy, L.LICON1.as_tuple, licon_sz)
        _li_pad(cell, g["pmos_cx"], cy, li_w, li_pad_h)

    # ===================================================================
    # POLY CONTACTS FOR CROSS-COUPLING
    # ===================================================================

    # Gate A contact in gap (QB net)
    _contact(cell, gap_licon_cx, g["gate_a_cy"], L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, gap_licon_cx, g["gate_a_cy"], li_w + 2 * li_encl, li_w)

    # Gate B contact on NMOS outer side (Q net)
    _contact(cell, nmos_outer_cx, g["gate_b_cy"], L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, nmos_outer_cx, g["gate_b_cy"], li_w + 2 * li_encl, li_w)

    # ===================================================================
    # CROSS-COUPLING LI1 ROUTING
    # ===================================================================
    # Only use adjacent zone connections to avoid crossing the power zone:
    # - QB net: PMOS int_bot (zone 1) → gate_A (adjacent, no pwr crossing)
    # - Q net: NMOS int_top (zone 3) → gate_B (adjacent, no pwr crossing)

    # QB net: PMOS int_bot → gate_A via gap
    # The gate_A poly contact li1 pad is (li_w + 2*li_encl) wide.
    # Match vertical route width to pad width to avoid li.3 gaps.
    gate_a_pad_hw = (li_w + 2 * li_encl) / 2.0

    # Horizontal li1 from PMOS int_bot contact to gap
    _rect(cell, L.LI1.as_tuple,
          gap_licon_cx - gate_a_pad_hw, g["int_bot_cy"] - li_w / 2.0,
          g["pmos_cx"] + li_w / 2.0, g["int_bot_cy"] + li_w / 2.0)
    # Vertical li1 in gap from int_bot up to gate_A (same width as pad)
    _rect(cell, L.LI1.as_tuple,
          gap_licon_cx - gate_a_pad_hw, g["int_bot_cy"] - li_w / 2.0,
          gap_licon_cx + gate_a_pad_hw, g["gate_a_cy"] + li_w / 2.0)

    # Q net: NMOS int_top → gate_B via outer side
    # The gate_B poly contact li1 pad is (li_w + 2*li_encl) wide.
    # The vertical route must match that width to avoid li.3 gaps.
    gate_b_pad_hw = (li_w + 2 * li_encl) / 2.0  # half-width of gate_B li1 pad

    # Horizontal li1 from NMOS int_top contact to outer side
    _rect(cell, L.LI1.as_tuple,
          nmos_outer_cx - gate_b_pad_hw, g["int_top_cy"] - li_w / 2.0,
          g["nmos_cx"] + li_w / 2.0, g["int_top_cy"] + li_w / 2.0)
    # Vertical li1 on outer side from gate_B down to int_top (same width as pad)
    _rect(cell, L.LI1.as_tuple,
          nmos_outer_cx - gate_b_pad_hw, g["gate_b_cy"] - li_w / 2.0,
          nmos_outer_cx + gate_b_pad_hw, g["int_top_cy"] + li_w / 2.0)

    # ===================================================================
    # POWER ROUTING
    # ===================================================================
    _rect(cell, L.MET1.as_tuple, g["vgnd_x0"], 0.0, g["vgnd_x1"], ch)
    _rect(cell, L.MET1.as_tuple, g["vpwr_x0"], 0.0, g["vpwr_x1"], ch)

    vgnd_cx = _snap((g["vgnd_x0"] + g["vgnd_x1"]) / 2.0)
    vpwr_cx = _snap((g["vpwr_x0"] + g["vpwr_x1"]) / 2.0)

    # VSS: NMOS pwr contact → mcon at nmos_cx → met1 horizontal to VGND rail
    # Use met1 for the horizontal run to avoid li.3 with Q-net outer routing
    _contact(cell, g["nmos_cx"], g["pwr_cy"], L.MCON.as_tuple, mcon_sz)
    # met1 horizontal from VGND rail to NMOS pwr contact
    met1_pwr_h = max(R.MET1_MIN_WIDTH, mcon_sz + 2 * R.MET1_ENCLOSURE_OF_MCON)
    _rect(cell, L.MET1.as_tuple,
          g["vgnd_x0"], g["pwr_cy"] - met1_pwr_h / 2.0,
          g["nmos_cx"] + mcon_sz / 2.0 + R.MET1_ENCLOSURE_OF_MCON_OTHER,
          g["pwr_cy"] + met1_pwr_h / 2.0)

    # VDD: PMOS pwr contact → mcon at pmos_cx → met1 horizontal to VPWR rail
    _contact(cell, g["pmos_cx"], g["pwr_cy"], L.MCON.as_tuple, mcon_sz)
    _rect(cell, L.MET1.as_tuple,
          g["pmos_cx"] - mcon_sz / 2.0 - R.MET1_ENCLOSURE_OF_MCON_OTHER,
          g["pwr_cy"] - met1_pwr_h / 2.0,
          g["vpwr_x1"], g["pwr_cy"] + met1_pwr_h / 2.0)

    # ===================================================================
    # BIT LINE CONTACTS
    # ===================================================================
    met1_bl_w = max(R.MET1_MIN_WIDTH * 2, mcon_sz + 2 * R.MET1_ENCLOSURE_OF_MCON_OTHER)
    met1_bl_h = max(0.083 / met1_bl_w, mcon_sz + 2 * R.MET1_ENCLOSURE_OF_MCON)

    _contact(cell, g["nmos_cx"], g["bl_bot_cy"], L.MCON.as_tuple, mcon_sz)
    _rect(cell, L.MET1.as_tuple,
          g["nmos_cx"] - met1_bl_w / 2, g["bl_bot_cy"] - met1_bl_h / 2,
          g["nmos_cx"] + met1_bl_w / 2, g["bl_bot_cy"] + met1_bl_h / 2)

    _contact(cell, g["nmos_cx"], g["bl_top_cy"], L.MCON.as_tuple, mcon_sz)
    _rect(cell, L.MET1.as_tuple,
          g["nmos_cx"] - met1_bl_w / 2, g["bl_top_cy"] - met1_bl_h / 2,
          g["nmos_cx"] + met1_bl_w / 2, g["bl_top_cy"] + met1_bl_h / 2)

    # ===================================================================
    # CELL BOUNDARY + LABELS
    # ===================================================================
    _rect(cell, L.BOUNDARY.as_tuple, 0.0, 0.0, cw, ch)

    _label(cell, "VSS", L.MET1_LABEL.as_tuple, vgnd_cx, g["pwr_cy"])
    _label(cell, "VDD", L.MET1_LABEL.as_tuple, vpwr_cx, g["pwr_cy"])
    _label(cell, "BL", L.MET1_LABEL.as_tuple, g["nmos_cx"], g["bl_bot_cy"])
    _label(cell, "BLB", L.MET1_LABEL.as_tuple, g["nmos_cx"], g["bl_top_cy"])
    _label(cell, "WL", L.POLY_LABEL.as_tuple, _snap(cw / 2.0), g["wl_bot_cy"])

    return cell


# ---------------------------------------------------------------------------
# Output generation
# ---------------------------------------------------------------------------

def generate_bitcell(
    output_path: str = "sky130_sram_6t_bitcell_lr.gds",
    generate_spice: bool = False,
    pd_w: float = PD_WIDTH, pg_w: float = PG_WIDTH, pu_w: float = PU_WIDTH,
) -> Path:
    cell = create_bitcell(pd_w=pd_w, pg_w=pg_w, pu_w=pu_w)
    lib = gdstk.Library(name="rekolektion_sram_lr", unit=1e-6, precision=5e-9)
    lib.add(cell)
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    lib.write_gds(str(out))

    g = _compute_cell_geometry(pd_w, pg_w, pu_w)
    area = g["cell_w"] * g["cell_h"]
    cr = pd_w / pg_w if pg_w > 0 else 0
    print(f"Generated 6T bitcell (LR topology): {out}")
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


def _write_spice_netlist(path: Path, pd_w: float, pg_w: float, pu_w: float) -> None:
    netlist = f"""\
* 6T SRAM Bitcell (LR Topology) — SKY130
* Generated by rekolektion
*
* Ports: BL BLB WL VDD VSS
* Topology: NMOS-left, PMOS-right, horizontal poly gates
*
.subckt sky130_sram_6t_bitcell_lr BL BLB WL VDD VSS

* Pull-down NMOS (lower inverter, gate_A)
XPD_L Q  QB  VSS VSS {NMOS_MODEL} w={pd_w}u l={GATE_LENGTH}u

* Pull-down NMOS (upper inverter, gate_B)
XPD_R QB Q   VSS VSS {NMOS_MODEL} w={pd_w}u l={GATE_LENGTH}u

* Pull-up PMOS (lower inverter, gate_A)
XPU_L Q  QB  VDD VDD {PMOS_MODEL} w={pu_w}u l={GATE_LENGTH}u

* Pull-up PMOS (upper inverter, gate_B)
XPU_R QB Q   VDD VDD {PMOS_MODEL} w={pu_w}u l={GATE_LENGTH}u

* Access transistor (bottom — BL side)
XPG_L BL WL  Q   VSS {NMOS_MODEL} w={pg_w}u l={GATE_LENGTH}u

* Access transistor (top — BLB side)
XPG_R BLB WL QB  VSS {NMOS_MODEL} w={pg_w}u l={GATE_LENGTH}u

.ends sky130_sram_6t_bitcell_lr
"""
    path.write_text(netlist)
