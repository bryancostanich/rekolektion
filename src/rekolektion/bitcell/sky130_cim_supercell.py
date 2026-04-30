"""CIM supercell layout generator (foundry-cell-based, V1+).

Wraps the SkyWater foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` 6T core
unmodified plus:
  - Q tap at the foundry's NMOS PD drain diff (LI1 + LICON + label + pin)
  - T7 NMOS pass transistor in the annex region above the foundry cell
  - MIM cap (cap_mim_m3_1) on M3 + capm layers
  - Wrapper-level routing: Q → T7 source, T7 drain → cap bottom plate

Per-variant cap dimensions per
`conductor/projects/production_features/tracks/05_cim_tapeout_audit/
foundry_migration_spec.md`.

Validated mechanisms (foundry tiler tests):
  FT3:  X-mirror at LEF pitch produces shared-rail abutment
  FT8b: parent-level dual WL POLY strips bridge wl_top + wl_bot per row
  FT7c: LI1 + LICON1 stack on NMOS PD drain diff exposes Q as port
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import gdstk

# Foundry cell GDS path
_FOUNDRY_CELL_GDS = (
    Path(__file__).parent / "cells"
    / "sky130_fd_bd_sram__sram_sp_cell_opt1.gds"
)
_FOUNDRY_CELL_NAME = "sky130_fd_bd_sram__sram_sp_cell_opt1"

# Foundry cell dimensions (LEF SIZE — placement pitch)
_FOUNDRY_LEF_W: float = 1.310
_FOUNDRY_LEF_H: float = 1.580

# Foundry cell internal Q diff coordinates (NMOS PD drain).
# Validated via Magic extraction in FT7c: a label + LICON + LI1 pad
# centred at this position taps a_38_212# (NMOS storage Q net).
_FOUNDRY_Q_TAP_X: float = 0.30
_FOUNDRY_Q_TAP_Y: float = 1.12

# T7 transistor parameters
_T7_W: float = 0.42       # NMOS width
_T7_L: float = 0.15       # gate length

# Cap-to-cap spacing (capm.5b)
_CAP_TO_CAP_SPACING: float = 0.84

# T7 vertical envelope (annex Y extent).
# T7 needs:
#   - 0.34 µm clearance above foundry top (diff.9 NMOS-to-NWELL spacing)
#   - 2 × 0.36 diff overhang + 0.15 gate = 0.87 diff height
#   - 0.10 margin to top of annex
# Total: 0.34 + 0.87 + 0.10 = 1.31. Round up.
_T7_ENVELOPE_H: float = 1.35


@dataclass(frozen=True)
class SupercellVariant:
    """Per-variant CIM supercell configuration.

    Cap dimensions preserve the variant's target capacitance;
    aspect ratio chosen for tiling efficiency given foundry cell width.
    """
    name: str
    cap_w: float        # MIM cap width  (X dim)
    cap_l: float        # MIM cap length (Y dim)
    cap_fF: float       # nominal capacitance (informational)
    rows: int
    cols: int

    @property
    def supercell_w(self) -> float:
        # X pitch.  Three constraints:
        #   1. cap-to-cap spacing (capm.5b = 0.84): pitch ≥ cap_w + 0.84
        #   2. T7 NMOS in east annex: needs 0.34 clearance from foundry NWELL
        #      (which extends to x=1.325 with overhang).  T7 diff_x ≥ 1.665.
        #      T7 diff_x1 = T7_DIFF_X + 0.42 = 2.085.  Plus 0.125 NSDM enclosure
        #      and 0.10 supercell margin → 2.31 total width.
        #   3. ≥ foundry LEF width (trivially satisfied by 1+2)
        T7_X_MIN_FROM_NWELL_OVERHANG = 0.34  # diff/tap.9
        FOUNDRY_NWELL_RIGHT_EDGE = 1.325  # NWELL extends past LEF
        T7_DIFF_W = 0.42
        T7_NSDM_ENC = 0.125
        SUPER_MARGIN_RIGHT = 0.10
        t7_w_pitch = (
            FOUNDRY_NWELL_RIGHT_EDGE
            + T7_X_MIN_FROM_NWELL_OVERHANG
            + T7_DIFF_W
            + T7_NSDM_ENC
            + SUPER_MARGIN_RIGHT
        )
        return max(
            _FOUNDRY_LEF_W,
            self.cap_w + _CAP_TO_CAP_SPACING,
            t7_w_pitch,
        )

    @property
    def supercell_h(self) -> float:
        # Y pitch — max of (foundry + T7 annex) vs (cap + cap-spacing).
        # The cap can sit on M3 over both foundry cell and annex; only
        # silicon-plane (T7) requires its own annex Y region.
        h_silicon = _FOUNDRY_LEF_H + _T7_ENVELOPE_H
        h_cap = self.cap_l + _CAP_TO_CAP_SPACING
        return max(h_silicon, h_cap)


# Per-variant configurations.
# cap dims preserved from current LR-CIM (decisions.md Decision 2 revised);
# can be reshaped per V3 finding (cap reshape ±2.5%) when DRC needs it.
CIM_SUPERCELL_VARIANTS: dict[str, SupercellVariant] = {
    "SRAM-A": SupercellVariant("SRAM-A", 1.30, 3.10, 8.1, rows=256, cols=64),
    "SRAM-B": SupercellVariant("SRAM-B", 1.10, 2.65, 5.8, rows=256, cols=64),
    "SRAM-C": SupercellVariant("SRAM-C", 1.10, 1.80, 4.0, rows= 64, cols=64),
    "SRAM-D": SupercellVariant("SRAM-D", 1.00, 1.45, 2.9, rows= 64, cols=64),
}


# ---------------------------------------------------------------------------
# GDS layer constants (sky130 standard mapping)
# ---------------------------------------------------------------------------

_LAYER_NWELL    = (64, 20)
_LAYER_DIFF     = (65, 20)
_LAYER_POLY     = (66, 20)
_LAYER_LICON1   = (66, 44)
_LAYER_LI1      = (67, 20)
_LAYER_LI1_LBL  = (67, 5)
_LAYER_LI1_PIN  = (67, 16)
_LAYER_MCON     = (67, 44)
_LAYER_MET1     = (68, 20)
_LAYER_MET1_LBL = (68, 5)
_LAYER_VIA1     = (68, 44)
_LAYER_MET2     = (69, 20)
_LAYER_VIA2     = (69, 44)
_LAYER_MET3     = (70, 20)
_LAYER_MET3_LBL = (70, 5)
_LAYER_MET3_PIN = (70, 16)
_LAYER_VIA3     = (70, 44)
_LAYER_MIMCAP   = (89, 44)  # MIM cap top plate
_LAYER_MET4     = (71, 20)
_LAYER_MET4_LBL = (71, 5)
_LAYER_MET4_PIN = (71, 16)
_LAYER_NSDM     = (93, 44)
_LAYER_PSDM     = (94, 20)
# SRAM-core area ID — enables relaxed DRC rules (li.c1, li.c2, etc.)
# inside the marked region.  Used by Magic's sky130 DRC deck to apply
# foundry-cell relaxations (the foundry SRAM bitcell is designed for
# these relaxed rules; without the areaid it triggers ~600 false-positive
# DRC errors per cell from the standard rule deck).
_LAYER_AREAID_SRAM = (81, 2)


def _rect(cell: gdstk.Cell, layer: tuple[int, int], x0: float, y0: float,
          x1: float, y1: float) -> None:
    cell.add(gdstk.rectangle(
        (x0, y0), (x1, y1), layer=layer[0], datatype=layer[1]
    ))


def _label(cell: gdstk.Cell, text: str, layer: tuple[int, int],
           x: float, y: float) -> None:
    cell.add(gdstk.Label(text, (x, y), layer=layer[0], texttype=layer[1]))


# ---------------------------------------------------------------------------
# Foundry cell loader with Q tap added
# ---------------------------------------------------------------------------

def _load_foundry_cell_with_q_tap() -> gdstk.Cell:
    """Load the foundry sram_sp_cell_opt1 GDS and add a Q tap + east-edge
    LI1 extension so Q can be electrically exposed to the supercell wrapper.

    Q exposure path (validated by visual + extraction):
      - LICON1 contact at (_FOUNDRY_Q_TAP_X, _FOUNDRY_Q_TAP_Y) on the foundry's
        NMOS PD drain diff (a_38_212#).  Validated FT7c — Q net captured.
      - LI1 polygon spanning from the licon RIGHT to the foundry cell's east
        edge (x=1.31), at Y inside Q's existing wide LI1 stripe (Net 42 at
        y=1.225-1.365).  This LI1 OVERLAPS Q's existing LI1 → merges into one
        net with foundry's internal Q.  Path Y centred at y=1.20 with width
        0.17 → spans y=[1.115, 1.285], inside Net 42's footprint and 0.21 µm
        clear of BL LI1 stripe at top (li.3 0.17 OK).
      - Q label + pin shape near the east-edge end of the LI1 stripe so
        Magic flat-extracts Q as a port and supercell-level wrapper LI1
        can abut at the cell boundary.

    The foundry cell's electrical content is unchanged (no new transistors,
    no rail modifications) — only LI1+licon added to expose an existing
    internal net.  Cell remains DRC/LVS-equivalent to upstream for all
    other ports; just gains a Q port.

    Returns: a gdstk.Cell named `sky130_fd_bd_sram__sram_sp_cell_opt1_qtap`
    suitable for use as a sub-cell instance in the supercell.
    """
    src = gdstk.read_gds(str(_FOUNDRY_CELL_GDS))
    foundry = next(c for c in src.cells if c.name == _FOUNDRY_CELL_NAME)

    cell = foundry.copy(_FOUNDRY_CELL_NAME + "_qtap")

    # Strip the foundry's "WL" label so the supercell array's per-row
    # wl_0_<row> labels (added at the array parent level) win Magic's
    # name resolution.  Without this, the foundry's global "WL" label
    # dominates and merges all rows' WL nets into one electrical net.
    # Same logic as F11 / macro/bitcell_array.py:_import_bitcell_into.
    for label in [l for l in cell.labels if l.text == "WL"]:
        cell.remove(label)

    qx, qy = _FOUNDRY_Q_TAP_X, _FOUNDRY_Q_TAP_Y  # (0.30, 1.12)
    LICON_HALF = 0.085
    PIN_HALF = 0.04

    # LICON1 contact on Q diff (LI1↔diff at the storage node)
    _rect(cell, _LAYER_LICON1,
          qx - LICON_HALF, qy - LICON_HALF,
          qx + LICON_HALF, qy + LICON_HALF)

    # LI1 stripe spanning from the licon RIGHT to the cell east edge.
    # Y range chosen to merge with Q's existing wide LI1 stripe (y=1.225-1.365)
    # while clearing BL LI1 (y_min=1.495) by ≥ li.3 (0.17 µm).
    LI_W = 0.17  # min LI1 width
    li_y_center = 1.20
    li_y_lo = li_y_center - LI_W / 2  # 1.115
    li_y_hi = li_y_center + LI_W / 2  # 1.285
    # Stripe from licon X (with LI1 enclosure ≥ 0.08 to the left) to cell
    # east edge.  We start the stripe at x=qx-0.165 to enclose the licon
    # with li1.5 enclosure (0.08 + licon half 0.085 = 0.165).
    stripe_x0 = qx - 0.165
    stripe_x1 = _FOUNDRY_LEF_W  # 1.31, foundry cell east edge
    _rect(cell, _LAYER_LI1, stripe_x0, li_y_lo, stripe_x1, li_y_hi)

    # Vertical LI1 stub from the licon down/up to ensure full LI1 enclosure
    # of the licon vertically too (li1.5: 0.08 enclosure on both sides of
    # licon).  The horizontal stripe at y=[1.115, 1.285] already covers
    # licon at qy=1.12 with 0.005 below and 0.165 above — bottom enclosure
    # is too small.  Add a small LI1 pad below the licon to extend down.
    _rect(cell, _LAYER_LI1,
          qx - 0.165, qy - 0.165,
          qx + 0.165, li_y_hi)

    # Q label + pin shape near the east end of the stripe (where the
    # supercell wrapper will connect).  Place at (1.20, li_y_center).
    q_label_x = 1.20
    _label(cell, "Q", _LAYER_LI1_LBL, q_label_x, li_y_center)
    _rect(cell, _LAYER_LI1_PIN,
          q_label_x - PIN_HALF, li_y_center - PIN_HALF,
          q_label_x + PIN_HALF, li_y_center + PIN_HALF)

    return cell


# ---------------------------------------------------------------------------
# Supercell builder (per-variant)
# ---------------------------------------------------------------------------

def create_cim_supercell(variant: str) -> tuple[gdstk.Library, SupercellVariant]:
    """Create a CIM supercell GDS for one variant.

    Returns (library, variant config). Library contains the supercell as
    its top cell, with the foundry cell as a referenced sub-cell.

    LAYOUT (foundry-cell-internal Y in cell-local frame):
      y = 0.000 ... 1.580 : foundry 6T cell (instanced from sram_sp_cell_opt1
                            with our Q tap modification)
      y = 1.580 ... 1.580+_T7_ENVELOPE_H : T7 NMOS annex region
      MIM cap on M3 + capm: covers Y range [cap_y_start, cap_y_start+cap_l],
      centred per variant.
    """
    if variant not in CIM_SUPERCELL_VARIANTS:
        raise ValueError(f"Unknown variant: {variant}")
    cfg = CIM_SUPERCELL_VARIANTS[variant]

    cell_name = f"sky130_cim_supercell_{variant.lower().replace('-', '_')}"
    lib = gdstk.Library(name=cell_name + "_lib", unit=1e-6, precision=5e-9)

    # Foundry cell with Q tap — sub-cell
    foundry = _load_foundry_cell_with_q_tap()
    lib.add(foundry)

    # Top supercell
    super_cell = gdstk.Cell(cell_name)
    lib.add(super_cell)

    # Place foundry cell instance at origin
    super_cell.add(gdstk.Reference(foundry, origin=(0.0, 0.0)))

    # SRAM areaid covering ONLY the foundry cell footprint — enables Magic's
    # relaxed DRC rules for the foundry cell's compact features.  Restricting
    # to foundry footprint avoids T7 / cap routing being subjected to relaxed
    # rules they aren't designed for.
    _rect(super_cell, _LAYER_AREAID_SRAM,
          0.0, 0.0, _FOUNDRY_LEF_W, _FOUNDRY_LEF_H)

    # ---- NWELL extension through annex (issue #9 — body-bias) ----
    # Foundry qtap NWELL spans cell-local x=[0.72, 1.20] y=[0, 1.58] (foundry
    # cell extent only).  In the supercell array, supercells stack at pitch
    # supercell_h (2.93 for SRAM-D), so the annex region y=[1.58, supercell_h]
    # has no NWELL.  At every supercell-row boundary, the foundry NWELL is
    # 1.35 µm away from the boundary — too far to merge with adjacent rows
    # NWELLs via parent-level bridge.
    #
    # Extending the NWELL up through the annex (at the same X range as the
    # foundry NWELL) bridges to the supercell-Y boundary, where the parent
    # array can add row-NWELL strips that merge across columns.  T7 NMOS at
    # x=[1.665, 2.085] is well clear of x=[0.72, 1.20] — no NSDM/PSDM
    # conflict.
    _NWELL_X0 = 0.72   # match foundry NWELL X range
    _NWELL_X1 = 1.20
    _rect(super_cell, _LAYER_NWELL,
          _NWELL_X0, _FOUNDRY_LEF_H,
          _NWELL_X1, cfg.supercell_h)

    # Forward foundry cell's external pin labels (BL, BR, VGND, VPWR, VPB, VNB)
    # — these are already in the foundry cell as labels at specific
    # coordinates; the instance carries them. We don't need to re-add them.

    # ---- T7 NMOS placement ----
    # T7 source must connect to Q (foundry cell exposes Q at east edge
    # (x=1.31, y=1.20) via LI1).  Place T7 in the right-side annex
    # (x > foundry cell) so source can connect directly via wrapper LI1
    # without crossing the foundry cell's M1 / LI1 features.
    #
    # T7 layout: vertical orientation — gate poly horizontal, diff vertical.
    # Source at lower end of diff (closer to Q east-edge exit y=1.20).
    # Drain at upper end (toward cap on M3+).
    #
    # Sizing (V2 — DRC clean):
    #   T7_DIFF_W = 0.42 (matches access tx — diff/tap.2 ≥ 0.42)
    #   T7_DIFF_OVERHANG ≥ 0.36 (poly.7 0.25 + licon-to-gate 0.055 + licon margin)
    T7_DIFF_W = _T7_W
    T7_GATE_L = _T7_L
    T7_DIFF_OVERHANG = 0.36

    # T7 placed on east side: x=[T7_DIFF_X, T7_DIFF_X + T7_DIFF_W].
    # Center T7 X within the right-of-foundry margin in the supercell.
    # For SRAM-D supercell_w=1.84, foundry occupies 0-1.31, so right margin
    # is 1.31-1.84 (0.53 wide).  T7 diff (0.42 wide) + NSDM (relax 0.06 each
    # side) needs ≥ 0.54 — fits with sram-relaxed enclosure.
    # T7 X must clear foundry's NWELL (extends to 1.325) by ≥ 0.34 (diff/tap.9)
    T7_DIFF_X = 1.325 + 0.34  # 1.665 — diff/tap.9 NMOS-to-NWELL spacing

    # T7 source at the bottom of diff (y close to Q exit y=1.20).
    # Diff Y range: [t7_diff_y0, t7_diff_y0 + diff_h]
    # diff_h = 2 * overhang + gate_l = 0.87
    # Source center at (t7_diff_y0 + overhang/2)
    # We want source near y=1.20 (Q exit Y), so t7_diff_y0 ≈ 1.20 - overhang/2
    # = 1.20 - 0.18 = 1.02.  But diff at y=1.02 to 1.89 — overlaps foundry
    # cell's Y range (0-1.58).  Move T7 up so its diff is in the annex.
    # Set t7_diff_y0 = _FOUNDRY_LEF_H + 0.10 = 1.68.  Source at y=1.86.
    t7_y_base = _FOUNDRY_LEF_H + 0.10  # just above foundry cell
    t7_diff_y0 = t7_y_base
    t7_diff_y1 = t7_diff_y0 + 2 * T7_DIFF_OVERHANG + T7_GATE_L  # 1.68 + 0.87 = 2.55

    # T7 diff
    _rect(super_cell, _LAYER_DIFF,
          T7_DIFF_X, t7_diff_y0,
          T7_DIFF_X + T7_DIFF_W, t7_diff_y1)
    # T7 NSDM (NMOS implant)
    NSDM_ENC = 0.125
    _rect(super_cell, _LAYER_NSDM,
          T7_DIFF_X - NSDM_ENC, t7_diff_y0 - NSDM_ENC,
          T7_DIFF_X + T7_DIFF_W + NSDM_ENC,
          t7_diff_y1 + NSDM_ENC)
    # T7 gate poly (horizontal stripe across diff at midpoint)
    POLY_EXT = 0.13
    t7_gate_y = t7_diff_y0 + T7_DIFF_OVERHANG
    _rect(super_cell, _LAYER_POLY,
          T7_DIFF_X - POLY_EXT, t7_gate_y,
          T7_DIFF_X + T7_DIFF_W + POLY_EXT, t7_gate_y + T7_GATE_L)

    # T7 gate label (MWL — will be connected at macro level)
    _label(super_cell, "MWL", (66, 5),
           T7_DIFF_X + T7_DIFF_W / 2, t7_gate_y + T7_GATE_L / 2)

    # T7 source contact (lower diff) → connect to Q
    LICON_HALF = 0.085
    LI_PAD_HALF = 0.165
    t7_src_cy = t7_diff_y0 + T7_DIFF_OVERHANG / 2
    t7_src_cx = T7_DIFF_X + T7_DIFF_W / 2
    _rect(super_cell, _LAYER_LICON1,
          t7_src_cx - LICON_HALF, t7_src_cy - LICON_HALF,
          t7_src_cx + LICON_HALF, t7_src_cy + LICON_HALF)
    _rect(super_cell, _LAYER_LI1,
          t7_src_cx - LI_PAD_HALF, t7_src_cy - LI_PAD_HALF,
          t7_src_cx + LI_PAD_HALF, t7_src_cy + LI_PAD_HALF)

    # Q-to-T7-source routing: foundry cell exposes Q via LI1 at east edge
    # (x=1.31, y=1.20).  T7 source is now placed in the right-side annex
    # so its source LI1 abuts the foundry's east-edge Q exposure.
    #
    # Wrapper LI1 from foundry east edge to T7 source: horizontal stripe
    # at y around 1.20 from x=1.31 (foundry edge) to T7 source X.  Then
    # vertical LI1 from y=1.20 up to T7 source Y in annex.
    LI_W = 0.17
    qx_exit = _FOUNDRY_LEF_W   # 1.31, Q exits foundry at this X
    qy_exit = 1.20             # at this Y inside Q's wide LI1 stripe
    # Horizontal LI1 from foundry east edge to T7 source X
    _rect(super_cell, _LAYER_LI1,
          qx_exit, qy_exit - LI_W / 2,
          t7_src_cx + LI_W / 2, qy_exit + LI_W / 2)
    # Vertical LI1 from y=qy_exit (inside foundry) to t7_src_cy (in annex)
    _rect(super_cell, _LAYER_LI1,
          t7_src_cx - LI_W / 2, qy_exit - LI_W / 2,
          t7_src_cx + LI_W / 2, t7_src_cy + LI_W / 2)

    # T7 drain contact (upper diff) → up via stack to M3 (cap bottom plate)
    t7_drn_cy = t7_diff_y1 - T7_DIFF_OVERHANG / 2
    t7_drn_cx = T7_DIFF_X + T7_DIFF_W / 2
    # licon to diff
    _rect(super_cell, _LAYER_LICON1,
          t7_drn_cx - LICON_HALF, t7_drn_cy - LICON_HALF,
          t7_drn_cx + LICON_HALF, t7_drn_cy + LICON_HALF)
    # LI1 pad
    _rect(super_cell, _LAYER_LI1,
          t7_drn_cx - LI_PAD_HALF, t7_drn_cy - LI_PAD_HALF,
          t7_drn_cx + LI_PAD_HALF, t7_drn_cy + LI_PAD_HALF)
    # mcon → M1
    M1_PAD_HALF = 0.16
    _rect(super_cell, _LAYER_MCON,
          t7_drn_cx - LICON_HALF, t7_drn_cy - LICON_HALF,
          t7_drn_cx + LICON_HALF, t7_drn_cy + LICON_HALF)
    _rect(super_cell, _LAYER_MET1,
          t7_drn_cx - M1_PAD_HALF, t7_drn_cy - M1_PAD_HALF,
          t7_drn_cx + M1_PAD_HALF, t7_drn_cy + M1_PAD_HALF)
    # via1 → M2 (M2 enclosure of via1 ≥ 0.085)
    VIA_HALF = 0.075       # via1 size 0.15
    M2_PAD_HALF = 0.085 + VIA_HALF  # 0.16
    _rect(super_cell, _LAYER_VIA1,
          t7_drn_cx - VIA_HALF, t7_drn_cy - VIA_HALF,
          t7_drn_cx + VIA_HALF, t7_drn_cy + VIA_HALF)
    _rect(super_cell, _LAYER_MET2,
          t7_drn_cx - M2_PAD_HALF, t7_drn_cy - M2_PAD_HALF,
          t7_drn_cx + M2_PAD_HALF, t7_drn_cy + M2_PAD_HALF)
    # via2 → M3.  via2 size 0.20.  M2 enclosure of via2 ≥ 0.085
    # (via2.4a directional ≥ 0.045 — needs WIDER M2 pad than for via1).
    VIA2_HALF = 0.10
    # M2 must enclose BOTH via1 and via2 within this stack — use the larger
    # of the two enclosures.  For via2, M2 needs 0.085 enclosure.  Pad half
    # = via2_half (0.10) + 0.085 = 0.185.  Bump M2 pad to this size.
    M2_PAD_HALF = max(M2_PAD_HALF, 0.185)
    # Re-emit M2 with larger pad (overlaps with previous, merges as one)
    _rect(super_cell, _LAYER_MET2,
          t7_drn_cx - M2_PAD_HALF, t7_drn_cy - M2_PAD_HALF,
          t7_drn_cx + M2_PAD_HALF, t7_drn_cy + M2_PAD_HALF)
    M3_PAD_HALF = 0.10 + 0.085  # via2 half + M3 enclosure 0.085 = 0.185
    _rect(super_cell, _LAYER_VIA2,
          t7_drn_cx - VIA2_HALF, t7_drn_cy - VIA2_HALF,
          t7_drn_cx + VIA2_HALF, t7_drn_cy + VIA2_HALF)
    _rect(super_cell, _LAYER_MET3,
          t7_drn_cx - M3_PAD_HALF, t7_drn_cy - M3_PAD_HALF,
          t7_drn_cx + M3_PAD_HALF, t7_drn_cy + M3_PAD_HALF)

    # ---- MIM cap ----
    # Cap centred in supercell, with 0.42 µm margin top and bottom of supercell
    # (so that Y-mirrored adjacent supercells produce 0.84 cap-to-cap spacing).
    super_h = cfg.supercell_h
    cap_margin_y = (super_h - cfg.cap_l) / 2
    cap_y0 = cap_margin_y
    cap_y1 = cap_margin_y + cfg.cap_l
    # Cap centred in X within supercell
    super_w = cfg.supercell_w
    cap_x0 = (super_w - cfg.cap_w) / 2
    cap_x1 = cap_x0 + cfg.cap_w

    # M3 bottom plate (with enclosure of 0.14 around capm)
    M3_ENC_CAPM = 0.14
    _rect(super_cell, _LAYER_MET3,
          cap_x0 - M3_ENC_CAPM, cap_y0 - M3_ENC_CAPM,
          cap_x1 + M3_ENC_CAPM, cap_y1 + M3_ENC_CAPM)
    # capm top plate (89, 44)
    _rect(super_cell, _LAYER_MIMCAP,
          cap_x0, cap_y0, cap_x1, cap_y1)

    # M3 strap from T7 drain to cap bottom plate (horizontal at t7_drn_cy)
    if t7_drn_cy < cap_y0:
        # Need to extend M3 from t7_drn_cy to cap_y0
        _rect(super_cell, _LAYER_MET3,
              cap_x0 - M3_ENC_CAPM, t7_drn_cy - M3_PAD_HALF,
              t7_drn_cx + M3_PAD_HALF, cap_y0)

    # MBL pin: cap top plate connects up to M4. Add via3 + M4 pad over cap.
    VIA3_HALF = 0.10
    M4_PAD_HALF = 0.18
    mbl_cx = (cap_x0 + cap_x1) / 2
    mbl_cy = (cap_y0 + cap_y1) / 2
    _rect(super_cell, _LAYER_VIA3,
          mbl_cx - VIA3_HALF, mbl_cy - VIA3_HALF,
          mbl_cx + VIA3_HALF, mbl_cy + VIA3_HALF)
    _rect(super_cell, _LAYER_MET4,
          mbl_cx - M4_PAD_HALF, mbl_cy - M4_PAD_HALF,
          mbl_cx + M4_PAD_HALF, mbl_cy + M4_PAD_HALF)
    # MBL label + pin
    _label(super_cell, "MBL", _LAYER_MET4_LBL, mbl_cx, mbl_cy)
    PIN_HALF = 0.04
    _rect(super_cell, _LAYER_MET4_PIN,
          mbl_cx - PIN_HALF, mbl_cy - PIN_HALF,
          mbl_cx + PIN_HALF, mbl_cy + PIN_HALF)

    return lib, cfg


def generate_supercell_gds(variant: str, output_path: str | Path) -> Path:
    """Generate the CIM supercell GDS for a variant and write to disk."""
    lib, _ = create_cim_supercell(variant)
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    lib.write_gds(str(out))
    return out


def generate_all_supercells(output_dir: str | Path = "output/cim_supercells") -> None:
    """Generate GDS files for all 4 supercell variants."""
    out_dir = Path(output_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    for variant in CIM_SUPERCELL_VARIANTS:
        slug = variant.lower().replace("-", "_")
        gds_path = out_dir / f"sky130_cim_supercell_{slug}.gds"
        generate_supercell_gds(variant, gds_path)
        cfg = CIM_SUPERCELL_VARIANTS[variant]
        print(
            f"  {variant}: supercell {cfg.supercell_w:.3f} × "
            f"{cfg.supercell_h:.3f} µm² ({cfg.supercell_w * cfg.supercell_h:.2f}) "
            f"→ {gds_path}"
        )


if __name__ == "__main__":
    generate_all_supercells()
