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
    BOTH gate_A and gate_B poly contacts are in the N-P gap at the
    same X position but different Y levels. This eliminates the wide
    left margin that was needed for the gate_B outer pad.
    - gate_A ← PMOS int_bot: horizontal li1 from PMOS, vertical in gap
    - gate_B ← NMOS int_top: horizontal li1 from NMOS, vertical in gap
    Li1 spacing between routes: 0.37 μm (> 0.17 li.3 min).

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

    # Power rail width — thin M1 vertical rails (current only flows vertically
    # to reach the M2 horizontal power straps above, not along the whole row)
    rail_w = R.MET1_MIN_WIDTH  # 0.14 μm — minimum width

    # Both poly contacts (gate_A and gate_B) are in the N-P gap at the same X
    # but different Y levels. No outer pad needed on NMOS left side.
    # Need met1.2 clearance between VGND via pad and BL met1 pad at nmos_cx.
    # VGND via pad right edge ~0.23, BL pad left ~nmos_cx-0.145.
    # margin_left must accommodate the gate_a outer-left poly tap (track
    # 05 cross-couple fix).  Required: nmos_diff_x0 >= 0.41 so that the
    # outer_tap_cx can sit at >= 0.135 (poly_pad_w_x/2, fits poly pad
    # within cell) AND have licon-to-ndiff clearance of 0.275 from
    # nmos_diff_x0.  With vgnd_x1 = rail_w = 0.14, that means
    # margin_left = 0.41 - 0.14 = 0.27.  Pre-fix this was 0.17 (just
    # MET1.spacing >= 0.14); +0.10 µm cell-width growth.
    margin_left = 0.27

    margin_right = 0.15  # PMOS side: just PSDM clearance (0.125) + buffer

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

    # Gap poly contact center X — gate_B licon lives here (gate_A's
    # licon was moved out of the gap to the outer-left to fix the
    # cross-couple short, see Route 1 / Route 2 below).  Clearance:
    # licon.14 from NMOS diff.
    g["gap_licon_cx"] = _snap(g["nmos_diff_x1"] + licon_to_ndiff + licon_sz / 2.0)

    # Outer-left poly tap X for gate_A (track 05 cross-couple fix).  The
    # licon sits on gate_A poly extension, licon_to_ndiff (0.19) from the
    # left edge of NMOS diff, with poly_pad enclosure of 0.05 each side
    # giving a 0.27-wide pad.  The pad's left edge sits at cell-boundary
    # x=0 when margin_left=0.27.
    g["outer_tap_cx"] = _snap(g["nmos_diff_x0"] - licon_to_ndiff - licon_sz / 2.0)

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

    # --- M2 horizontal power straps ---
    via_sz = R.VIA_SIZE                         # 0.15
    m1_enc_via = R.MET1_ENCLOSURE_OF_VIA        # 0.055
    m2_enc_via = R.MET2_ENCLOSURE_OF_VIA        # 0.055
    m2_stripe_w = max(0.28, via_sz + 2 * 0.09)  # ensure generous via enclosure (0.33)

    # M2 stripes run full cell width, centered on the power Y position
    # VGND stripe at bottom of cell, VPWR stripe at top of cell
    # Position them at cell edges so they tile/stitch between rows
    g["m2_stripe_w"] = m2_stripe_w
    g["m2_vgnd_y0"] = 0.0
    g["m2_vgnd_y1"] = m2_stripe_w
    g["m2_vpwr_y0"] = g["cell_h"] - m2_stripe_w
    g["m2_vpwr_y1"] = g["cell_h"]

    # Via parameters
    g["via_sz"] = via_sz
    g["m1_enc_via"] = m1_enc_via
    g["m2_enc_via"] = m2_enc_via

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
    outer_tap_cx = g["outer_tap_cx"]
    li_pad_h = g["li_pad_h"]

    # ===================================================================
    # N-WELL (PMOS side, extends past Y edges)
    # ===================================================================
    # Extend nwell 0.08 beyond diff enclosure in Y so that Y-mirrored
    # row pairs create a nwell overlap ≥ 0.84 μm (nwell.1 min width).
    # At y_pitch = cell_h - m2_stripe_w: overlap = 2*(nwell_encl+0.08) + m2_w - cell_h
    nwell_encl = g["nwell_encl"]
    nwell_y_ext = nwell_encl + 0.08  # 0.26 total past diff edge
    _rect(cell, L.NWELL.as_tuple,
          g["nwell_x0"], g["diff_bot"] - nwell_y_ext,
          cw + 0.10, g["diff_top"] + nwell_y_ext)

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
    # POLY GATES — 4 horizontal stripes
    # Inverter gates (gate_A, gate_B) span BOTH NMOS and PMOS diff so each
    # stripe forms one NMOS pull-down + one PMOS pull-up (the standard 6T
    # inverter pair).  WL access gates (wl_bot, wl_top) span ONLY NMOS diff
    # because the access transistors are NMOS — extending WL poly across
    # pdiff would create spurious PMOS gates that aren't in the schematic
    # (verified: pre-fix extraction reported 4 PMOS / 4 NMOS instead of
    # the schematic's 2 PMOS / 4 NMOS).  Cell-to-cell WL continuity is
    # now the array-level integrator's responsibility (poly tap → M2).
    # ===================================================================
    poly_x0 = g["nmos_diff_x0"] - poly_ext
    poly_x1_full = g["pmos_diff_x1"] + poly_ext     # spans both diffs
    poly_x1_nmos = g["nmos_diff_x1"] + poly_ext     # NMOS-only
    # gate_A poly leftmost X — extends past poly_x0 to the outer-left
    # poly tap area where the cross-couple licon lives.  Tap pad needs
    # pad_w_x (0.27) wide poly enclosure of the licon at outer_tap_cx.
    poly_x0_gate_a = outer_tap_cx - pad_w_x / 2.0

    # Inverter gate stripes: span both NMOS and PMOS diff so each forms
    # a PD+PU pair.  gate_A also extends LEFT to outer_tap_cx for the
    # cross-couple poly tap (no diff there — passes over substrate
    # only).  gate_B stays at standard poly_x0 (its tap is in the gap).
    _rect(cell, L.POLY.as_tuple,
          poly_x0_gate_a, g["gate_a_y0"], poly_x1_full, g["gate_a_y1"])
    _rect(cell, L.POLY.as_tuple,
          poly_x0, g["gate_b_y0"], poly_x1_full, g["gate_b_y1"])

    # WL access gates: NMOS-only.  Stops at right edge of NMOS diff +
    # the standard poly extension; never enters the PMOS diff.
    for y0_key, y1_key in [("wl_bot_y0", "wl_bot_y1"),
                            ("wl_top_y0", "wl_top_y1")]:
        _rect(cell, L.POLY.as_tuple, poly_x0, g[y0_key], poly_x1_nmos, g[y1_key])

    # Gate A poly contact pad — moved from gap to OUTER-LEFT so the
    # cross-couple Route 1 LI1 vertical at gap_licon_cx can pass over
    # gate_A_cy WITHOUT making contact (no licon in gap means no
    # spurious Q-to-QB short).  See Route 1 / Route 2 below.
    _rect(cell, L.POLY.as_tuple,
          outer_tap_cx - pad_w_x / 2.0, g["gate_a_cy"] - pad_h_y / 2.0,
          outer_tap_cx + pad_w_x / 2.0, g["gate_a_cy"] + pad_h_y / 2.0)

    # Gate B poly contact pad in gap (kept here — cross-couple Route 1
    # lands on gate_B's gap licon via M1 vertical from int_bot_cy).
    _rect(cell, L.POLY.as_tuple,
          gap_licon_cx - pad_w_x / 2.0, g["gate_b_cy"] - pad_h_y / 2.0,
          gap_licon_cx + pad_w_x / 2.0, g["gate_b_cy"] + pad_h_y / 2.0)

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

    # Gate A contact: OUTER-LEFT of NMOS diff (track 05 cross-couple fix).
    # Lives on the gate_A poly extension at outer_tap_cx.  LI1 pad uses
    # the X-narrow / Y-wide orientation since the Route 2 LI1 trace
    # arrives from above (vertical descent from int_top_cy).
    _contact(cell, outer_tap_cx, g["gate_a_cy"], L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, outer_tap_cx, g["gate_a_cy"], li_w, li_w + 2 * li_encl)

    # Gate B contact: in gap at gap_licon_cx (unchanged — Route 1 lands
    # here via LI1 vertical from int_bot_cy crossing pwr_cy).
    _contact(cell, gap_licon_cx, g["gate_b_cy"], L.LICON1.as_tuple, licon_sz)
    _li_pad(cell, gap_licon_cx, g["gate_b_cy"], li_w + 2 * li_encl, li_w)

    # ===================================================================
    # CROSS-COUPLING LI1 ROUTING (track 05 fix — was wired backwards)
    # ===================================================================
    # In a 6T latch, each inverter's OUTPUT drives the OPPOSITE inverter's
    # GATE.  With gate_A = QB-input and gate_B = Q-input:
    #   Route 1 (Q net):  inverter A output (PD_A || PU_A drains at
    #                     int_bot_cy) → gate_B poly tap (in gap at
    #                     gate_b_cy).
    #   Route 2 (QB net): inverter B output (PD_B || PU_B drains at
    #                     int_top_cy) → gate_A poly tap (now outer-left
    #                     at outer_tap_cx, gate_a_cy).
    # Each route crosses the central pwr_cy.  Routes are at DIFFERENT X
    # columns (Route 1 in gap, Route 2 outer-left) so their vertical
    # segments never overlap and Q stays isolated from QB.

    gap_pad_hw = (li_w + 2 * li_encl) / 2.0  # half-width of gap li1 pads

    # Route 1 (Q) — inverter A output collation + cross-couple to gate_B:
    #   Horizontal LI1 at int_bot_cy spans NMOS_int_bot → gap →
    #   PMOS_int_bot (forms PD_A || PU_A drain net = Q).  Vertical LI1
    #   in gap from int_bot_cy UP across pwr_cy to gate_b_cy lands on
    #   gate_B's in-gap LI1 pad.  The vertical passes OVER gate_a_cy
    #   without contact (gate_A's licon was moved to outer_tap_cx).
    _rect(cell, L.LI1.as_tuple,
          g["nmos_cx"] - li_w / 2.0, g["int_bot_cy"] - li_w / 2.0,
          g["pmos_cx"] + li_w / 2.0, g["int_bot_cy"] + li_w / 2.0)
    _rect(cell, L.LI1.as_tuple,
          gap_licon_cx - gap_pad_hw, g["int_bot_cy"] - li_w / 2.0,
          gap_licon_cx + gap_pad_hw, g["gate_b_cy"] + li_w / 2.0)

    # Route 2 (QB) — inverter B output collation + cross-couple to gate_A:
    #   PMOS_int_top to NMOS_int_top inverter-output bridge uses M1
    #   (with mcons at each diff column), NOT LI1, because an LI1
    #   spanning the gap at int_top_cy would sit only ~0.08 µm above
    #   gate_B's in-gap LI1 pad at gate_b_cy and violate li.3 (0.17 µm
    #   minimum spacing between different nets).  M1 over LI1 is a
    #   different layer, no conflict.  LI1 still carries Route 2 from
    #   NMOS_int_top leftward to the outer-left gate_A tap; the
    #   vertical LI1 at outer_tap_cx connects gate_A's tap pad to
    #   int_top_cy where it meets the M1 jumper via the NMOS_int_top
    #   mcon.
    # Use the LARGER of the two M1-enclosure-of-mcon rules (0.06) so the
    # mcons at each end have ≥0.06 µm enclosure on top+bottom.  The
    # default 0.03 only satisfies the other-direction rule (met1.4) but
    # the route is purely horizontal and depends on the wider Y to clear
    # met1.5 at the trace's ENDS where mcons sit.
    met1_jumper_h = max(R.MET1_MIN_WIDTH,
                        mcon_sz + 2 * R.MET1_ENCLOSURE_OF_MCON_OTHER)
    _rect(cell, L.MET1.as_tuple,
          g["nmos_cx"] - met1_jumper_h / 2.0, g["int_top_cy"] - met1_jumper_h / 2.0,
          g["pmos_cx"] + met1_jumper_h / 2.0, g["int_top_cy"] + met1_jumper_h / 2.0)
    _contact(cell, g["nmos_cx"], g["int_top_cy"], L.MCON.as_tuple, mcon_sz)
    _contact(cell, g["pmos_cx"], g["int_top_cy"], L.MCON.as_tuple, mcon_sz)
    # LI1 leg from NMOS_int_top leftward to gate_A's outer-left tap.
    _rect(cell, L.LI1.as_tuple,
          outer_tap_cx - li_w / 2.0, g["int_top_cy"] - li_w / 2.0,
          g["nmos_cx"] + li_w / 2.0, g["int_top_cy"] + li_w / 2.0)
    # Vertical LI1 at outer_tap_cx from gate_a_cy up to int_top_cy.
    _rect(cell, L.LI1.as_tuple,
          outer_tap_cx - li_w / 2.0, g["gate_a_cy"] - li_w / 2.0,
          outer_tap_cx + li_w / 2.0, g["int_top_cy"] + li_w / 2.0)

    # ===================================================================
    # POWER ROUTING
    # ===================================================================
    _rect(cell, L.MET1.as_tuple, g["vgnd_x0"], 0.0, g["vgnd_x1"], ch)
    _rect(cell, L.MET1.as_tuple, g["vpwr_x0"], 0.0, g["vpwr_x1"], ch)

    vgnd_cx = _snap((g["vgnd_x0"] + g["vgnd_x1"]) / 2.0)
    vpwr_cx = _snap((g["vpwr_x0"] + g["vpwr_x1"]) / 2.0)

    # VSS: NMOS pwr contact → mcon at nmos_cx → met1 horizontal to VGND rail
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
    # M2 HORIZONTAL POWER STRAPS + VIAS
    # ===================================================================
    via_sz = g["via_sz"]
    m1_enc_via = g["m1_enc_via"]
    m2_enc_via = g["m2_enc_via"]

    # M2 horizontal stripes — extend slightly past cell edges so that vias
    # at the thin M1 rails (near x=0 and x=cw) have full M2 enclosure.
    m2_x_ext = via_sz / 2.0 + m2_enc_via + 0.04  # generous overshoot
    _rect(cell, L.MET2.as_tuple, -m2_x_ext, g["m2_vgnd_y0"], cw + m2_x_ext, g["m2_vgnd_y1"])
    _rect(cell, L.MET2.as_tuple, -m2_x_ext, g["m2_vpwr_y0"], cw + m2_x_ext, g["m2_vpwr_y1"])

    # Via: VGND M1 rail → M2 VGND stripe
    # Place via where M1 rail overlaps M2 stripe, centered on rail
    via_hs = via_sz / 2.0
    vgnd_via_cy = _snap((g["m2_vgnd_y0"] + g["m2_vgnd_y1"]) / 2.0)
    vpwr_via_cy = _snap((g["m2_vpwr_y0"] + g["m2_vpwr_y1"]) / 2.0)

    # The thin M1 rail (0.14) is narrower than via + 2*enclosure (0.26).
    # We need a local M1 pad at the via location to meet enclosure rules.
    # Magic via.5a requires met1 overlap >= 0.085 on two opposite sides when
    # the other sides have only 0.055. Use generous enclosure to be safe.
    m1_via_enc = 0.085
    m1_via_pad_w = via_sz + 2 * m1_via_enc   # 0.32
    m1_via_pad_h = via_sz + 2 * m1_via_enc   # 0.32

    # VGND vias — on left rail
    _contact(cell, vgnd_cx, vgnd_via_cy, L.VIA.as_tuple, via_sz)
    _rect(cell, L.MET1.as_tuple,
          vgnd_cx - m1_via_pad_w / 2, vgnd_via_cy - m1_via_pad_h / 2,
          vgnd_cx + m1_via_pad_w / 2, vgnd_via_cy + m1_via_pad_h / 2)

    # VPWR vias — on right rail
    _contact(cell, vpwr_cx, vpwr_via_cy, L.VIA.as_tuple, via_sz)
    _rect(cell, L.MET1.as_tuple,
          vpwr_cx - m1_via_pad_w / 2, vpwr_via_cy - m1_via_pad_h / 2,
          vpwr_cx + m1_via_pad_w / 2, vpwr_via_cy + m1_via_pad_h / 2)

    # M2 labels for power nets
    _label(cell, "VSS", L.MET2_LABEL.as_tuple, _snap(cw / 2.0), vgnd_via_cy)
    _label(cell, "VDD", L.MET2_LABEL.as_tuple, _snap(cw / 2.0), vpwr_via_cy)

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
    # Label the n-well as VDD so Magic extracts the PMOS bulk node as
    # VDD instead of an auto-generated w_<x>_<y># name.  Without this,
    # the strict-LVS netgen run sees an unaliased well net and reports
    # a spurious mismatch.  Schematic XPU_L/XPU_R have B=VDD, so the
    # well IS supposed to be VDD; the label just makes extraction
    # explicit.
    _label(cell, "VDD", L.NWELL_LABEL.as_tuple,
           _snap((g["nwell_x0"] + cw) / 2.0), g["pwr_cy"])
    _label(cell, "BL", L.MET1_LABEL.as_tuple, g["nmos_cx"], g["bl_bot_cy"])
    _label(cell, "BLB", L.MET1_LABEL.as_tuple, g["nmos_cx"], g["bl_top_cy"])
    # WL has TWO poly stripes (top+bottom access gates).  Both need the
    # "WL" label so Magic merges them onto a single WL net — without
    # this, the truncated poly (NMOS-only since the spurious-PMOS fix)
    # has no shared structure between the two stripes and they extract
    # as separate anonymous gate nets.  Label X uses nmos_cx because the
    # poly only exists over NMOS diff after truncation.
    _label(cell, "WL", L.POLY_LABEL.as_tuple, g["nmos_cx"], g["wl_bot_cy"])
    _label(cell, "WL", L.POLY_LABEL.as_tuple, g["nmos_cx"], g["wl_top_cy"])

    # .pin purpose shapes on each labeled net.  Without these, Magic's
    # hierarchical extraction auto-merges abutting cell-boundary nets
    # (BL/BLB columns, WL/MWL rows) up to the parent and the bitcell
    # subckt loses those ports — netgen then flattens the bitcell at
    # macro LVS.  The .pin shape (datatype 16) anchors the label as a
    # definite port of the bitcell sub-cell.
    _PIN_HALF = 0.07
    for label, layer_drawing, cx, cy in (
        ("VSS",  L.MET1, vgnd_cx, g["pwr_cy"]),
        ("VDD",  L.MET1, vpwr_cx, g["pwr_cy"]),
        ("BL",   L.MET1, g["nmos_cx"], g["bl_bot_cy"]),
        ("BLB",  L.MET1, g["nmos_cx"], g["bl_top_cy"]),
        ("WL",   L.POLY, g["nmos_cx"], g["wl_bot_cy"]),
        ("WL",   L.POLY, g["nmos_cx"], g["wl_top_cy"]),
    ):
        # Pin layer for met1 = (68, 16); for poly = (66, 16).
        pin_layer = (layer_drawing.gds_layer, 16)
        _rect(cell, pin_layer,
              cx - _PIN_HALF, cy - _PIN_HALF,
              cx + _PIN_HALF, cy + _PIN_HALF)

    return cell


# ---------------------------------------------------------------------------
# BitcellInfo for array tiler integration
# ---------------------------------------------------------------------------

def load_lr_bitcell(gds_path: str | Path = "output/sky130_6t_lr.gds") -> "BitcellInfo":
    """Return a BitcellInfo for the LR bitcell, generating GDS if needed.

    This provides the same interface as load_foundry_sp_bitcell() so the
    array tiler can work with either cell interchangeably.
    """
    from rekolektion.bitcell.base import BitcellInfo, PinInfo

    gds_path = Path(gds_path)
    if not gds_path.exists():
        generate_bitcell(str(gds_path))

    g = _compute_cell_geometry()
    cw, ch = g["cell_w"], g["cell_h"]

    # Pin positions (x_center, y_center, layer_name)
    vgnd_cx = g["rail_w"] / 2.0
    vpwr_cx = g["vpwr_x0"] + g["rail_w"] / 2.0
    pins = {
        "BL":   PinInfo("BL",   [(g["nmos_cx"], g["bl_bot_cy"], "met1")]),
        "BLB":  PinInfo("BLB",  [(g["nmos_cx"], g["bl_top_cy"], "met1")]),
        "WL":   PinInfo("WL",   [(cw / 2.0, g["wl_bot_cy"], "poly")]),
        "VGND": PinInfo("VGND", [(vgnd_cx, g["pwr_cy"], "met1")]),
        "VPWR": PinInfo("VPWR", [(vpwr_cx, g["pwr_cy"], "met1")]),
    }

    # Array tiling pitch — cells share power rails and M2 stripes at boundaries.
    # x_pitch = cell_w - rail_w + 0.03 (shared VPWR + poly.2 spacing)
    # y_pitch = 2.04 (minimum DRC-clean Y sharing)
    # The tiler places cells at cell_width/cell_height pitch, so we set these
    # to the tiling pitch. Geometry extends past the boundary by design —
    # adjacent cells' shared features (rails, M2, nwell) merge.
    x_pitch = _snap(cw - g["rail_w"] + 0.03)   # 1.925
    y_pitch = _snap(2.04)                       # minimum DRC-clean

    return BitcellInfo(
        cell_name="sky130_sram_6t_bitcell_lr",
        cell_width=x_pitch,       # tiling pitch
        cell_height=y_pitch,      # tiling pitch
        pins=pins,
        gds_path=gds_path,
        origin_x=0.0,
        origin_y=0.0,
        geometry_width=cw,        # actual GDS extent (for mirror offset)
        geometry_height=ch,       # actual GDS extent
    )


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
* NOTE: w/l values are unitless — ngspice hsa mode applies 1e-6 scaling
*
* Ports: BL BLB WL VDD VSS
* Topology: NMOS-left, PMOS-right, horizontal poly gates
*
.subckt sky130_sram_6t_bitcell_lr BL BLB WL VDD VSS

* Pull-down NMOS (lower inverter, gate_A)
XPD_L Q  QB  VSS VSS {NMOS_MODEL} w={pd_w} l={GATE_LENGTH}

* Pull-down NMOS (upper inverter, gate_B)
XPD_R QB Q   VSS VSS {NMOS_MODEL} w={pd_w} l={GATE_LENGTH}

* Pull-up PMOS (lower inverter, gate_A)
XPU_L Q  QB  VDD VDD {PMOS_MODEL} w={pu_w} l={GATE_LENGTH}

* Pull-up PMOS (upper inverter, gate_B)
XPU_R QB Q   VDD VDD {PMOS_MODEL} w={pu_w} l={GATE_LENGTH}

* Access transistor (bottom — BL side)
XPG_L BL WL  Q   VSS {NMOS_MODEL} w={pg_w} l={GATE_LENGTH}

* Access transistor (top — BLB side)
XPG_R BLB WL QB  VSS {NMOS_MODEL} w={pg_w} l={GATE_LENGTH}

.ends sky130_sram_6t_bitcell_lr
"""
    path.write_text(netlist)
