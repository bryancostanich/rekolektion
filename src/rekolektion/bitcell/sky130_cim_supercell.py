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

from rekolektion.bitcell.sky130_cim_drain_bridge import (
    BRIDGE_H,
    create_drain_bridge_cell,
)

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
        # X pitch.  Four constraints:
        #   1. cap-to-cap spacing (capm.5b = 0.84): pitch ≥ cap_w + 0.84
        #   2. T7 NMOS in east annex: needs 0.34 clearance from foundry NWELL
        #      (which extends to x=1.325 with overhang).  T7 diff_x ≥ 1.665.
        #      T7 diff_x1 = T7_DIFF_X + 0.42 = 2.085.  Plus 0.125 NSDM enclosure
        #      and 0.10 supercell margin → 2.31 total width.
        #   3. ≥ foundry LEF width (trivially satisfied by 1+2)
        #   4. M3 spacing between THIS supercell's T7 drain pad east edge
        #      and the NEXT supercell's cap_bot M3 west edge: ≥ 0.30 µm
        #      (met3.2).  Only large-cap variants hit this.
        #        drain_pad_east  = (T7_DIFF_X + T7_DIFF_W/2) + M3_PAD_HALF
        #                        = (1.665 + 0.21)            + 0.185
        #                        = 2.060
        #        cap_bot_w_off   = cap_x1 - cap_w - M3_ENC_CAPM
        #                        = 1.475 - cap_w - 0.14
        #                        = 1.335 - cap_w   (in next supercell coords)
        #        require pitch + cap_bot_w_off ≥ drain_pad_east + 0.30
        #        ⟹ pitch ≥ 2.360 - (1.335 - cap_w) = 1.025 + cap_w
        #      For SRAM-A (cap_w=1.30) this is 2.325; +5 nm safety margin
        #      past the met3.2 boundary gives 2.330.  B/C/D (cap_w ≤ 1.10)
        #      stay at the t7_w_pitch limit (2.31) — gap ≥ 0.485 µm.
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
        m3_spacing_pitch = 1.025 + self.cap_w + 0.005   # 5 nm safety
        return max(
            _FOUNDRY_LEF_W,
            self.cap_w + _CAP_TO_CAP_SPACING,
            t7_w_pitch,
            m3_spacing_pitch,
        )

    @property
    def supercell_h(self) -> float:
        # Y pitch — max of (foundry + T7 annex) vs (cap + cap-spacing),
        # plus the bottom drain-bridge strap region (BRIDGE_H).
        h_silicon = BRIDGE_H + _FOUNDRY_LEF_H + _T7_ENVELOPE_H
        h_cap = BRIDGE_H + self.cap_l + _CAP_TO_CAP_SPACING
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

    # ---- Phase 2 fix: drain → BL/BR contact stack (issue #7) ----
    # The foundry cell ships with 0 LICON1 and 0 MCON internally — the
    # access-tx drain DIFFs are electrically isolated from the BL/BR
    # met1 rails.  Foundry intent was for adjacent strap/edge cells
    # (wlstrap, colend) to provide LICON1+LI1+MCON contacts; we
    # provide them inline in the supercell wrapper instead.
    #
    # Top access tx: drain DIFF (0.190, 1.460)-(0.330, 1.580).
    #   Foundry has wide LI1 stripe (0.070, 1.505)-(1.130, 1.580)
    #   over the drain.  BR met1 rail (0.710, 0.145)-(0.850, 1.580)
    #   overlaps the LI1 stripe at y=1.505-1.580.
    #   → LICON1 (drain→LI1) + MCON (LI1→BR met1) bridges drain→BR.
    #
    # Bottom access tx: drain DIFF (0.190, 0.000)-(0.330, 0.120).
    #   Foundry has wide LI1 stripe (0.070, 0.000)-(1.130, 0.075)
    #   over the drain.  BL met1 rail (0.350, 0.000)-(0.490, 1.435)
    #   overlaps the LI1 stripe at y=0.000-0.075.
    #   → LICON1 (drain→LI1) + MCON (LI1→BL met1) bridges drain→BL.
    #
    # LICON1 is 0.17×0.17 with DIFF enclosure ≥ 0.04 (sram-relaxed).
    # Drain DIFF is 0.14×0.12 — too small.  Wrapper extends the
    # drain DIFF + NSDM upward (top tx) / downward (bottom tx) into
    # the annex/below-cell region to fit LICON1.  Wrapper LI1 also
    # extends past the foundry stripe boundaries to satisfy LICON1
    # enclosure (li.5).  Wrapper met1 extends past BR/BL rail
    # boundaries to satisfy MCON enclosure (m1.4).

    # ----- TOP access tx → BR -----
    # DIFF extension only ABOVE foundry POLY (y_min ≥ 1.460 + 0.075 = 1.535)
    # to clear poly.4 spacing.  Extends UP into annex, abuts foundry drain
    # DIFF (0.190, 1.460)-(0.330, 1.580) at y=1.535-1.580 in X overlap.
    _DRN_T_X0, _DRN_T_X1 = 0.115, 0.405          # 0.29 wide for licon enclosure
    _DRN_T_Y0, _DRN_T_Y1 = 1.535, 1.860          # y_min ≥ poly+0.075
    _rect(cell, _LAYER_DIFF, _DRN_T_X0, _DRN_T_Y0, _DRN_T_X1, _DRN_T_Y1)
    # NSDM extension (foundry NSDM ends at y=1.705).
    _rect(cell, _LAYER_NSDM, _DRN_T_X0 - 0.05, _DRN_T_Y0 - 0.05,
          _DRN_T_X1 + 0.05, _DRN_T_Y1 + 0.05)
    # LICON1 placement constraints:
    #   - DIFF enclosure ≥ 0.06 in all four directions (licon.5c standard,
    #     not the sram-relaxed 0.04 — the former covers a wider DRC deck).
    #     DIFF Y range [1.535, 1.860]; LICON Y span 0.17 → centre between
    #     y=1.535+0.06+0.085=1.680 (south-floor) and 1.860-0.06-0.085=1.715
    #     (north-ceiling).  Pick 1.680 to stay maximally far from POLY.
    #   - LICON-to-POLY ≥ 0.075 (licon.5b): LICON y_min=1.595 vs POLY top
    #     y=1.460 → 0.135 margin ✓.
    _LIC_T_CX, _LIC_T_CY = 0.260, 1.680
    _rect(cell, _LAYER_LICON1,
          _LIC_T_CX - LICON_HALF, _LIC_T_CY - LICON_HALF,
          _LIC_T_CX + LICON_HALF, _LIC_T_CY + LICON_HALF)
    # LI1 wrapper around LICON1 (li.5 enclosure ≥ 0.08; this also overlaps
    # foundry's wide LI1 stripe at y=1.495-1.580 and merges with it).
    _rect(cell, _LAYER_LI1,
          0.095, 1.495, 0.550, 1.825)
    # LI1 extension east to BR MCON area.  Stay OUTSIDE NWELL X range
    # (NWELL at x≥0.72) to prevent inadvertent VPB net merging.
    # We rely on foundry's existing wide LI1 stripe (0.070, 1.495)-(1.130, 1.580)
    # to carry the connection from our LICON1 LI1 wrapper to the BR MCON area.
    # No additional LI1 polygon needed — foundry stripe spans the X range.
    # BUT we need wrapper LI1 covering MCON for li.5 enclosure.
    _rect(cell, _LAYER_LI1,
          0.660, 1.495, 0.990, 1.705)
    # MCON at BR rail.  BR rail x=[0.71, 0.85] is too narrow for MCON+M1
    # enclosure without conflicting with foundry M1 at (0.300-0.540, 1.435-1.580)
    # (need 0.14 gap → MCON center x ≥ 0.825).
    _MCON_T_BR_CX, _MCON_T_BR_CY = 0.825, 1.540
    _rect(cell, _LAYER_MCON,
          _MCON_T_BR_CX - LICON_HALF, _MCON_T_BR_CY - LICON_HALF,
          _MCON_T_BR_CX + LICON_HALF, _MCON_T_BR_CY + LICON_HALF)
    # M1 wrapper extends BR rail east; gap 0.14 from foundry M1 at x=0.540
    # (west edge ≥ 0.680) and from VPWR strap at x=1.130 (east edge ≤ 0.990).
    _rect(cell, _LAYER_MET1, 0.680, 1.395, 0.970, 1.685)

    # ----- BOTTOM access tx → BL -----
    # MOVED to external strap cell `sky130_cim_drain_bridge_v1`.
    # The bridge cell is instanced inside the supercell at cell-local
    # y=[0, BRIDGE_H], with the foundry cell shifted to cell-local
    # y=[BRIDGE_H, BRIDGE_H+1.58].  Its DIFF abuts foundry's bottom
    # drain DIFF at the cell boundary (DIFF abutment merges nets), so
    # the BL connection is electrically equivalent to the in-qtap
    # Phase 2 design without the Y-mirror conflict that previously
    # placed the LICON1 over the next row's WL_BOT POLY strip.
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

    # Drain-bridge cell (BL strap, external) — sub-cell
    bridge = create_drain_bridge_cell()
    lib.add(bridge)

    # Top supercell
    super_cell = gdstk.Cell(cell_name)
    lib.add(super_cell)

    # Bridge cell sits at supercell origin; foundry shifted up by BRIDGE_H.
    # Bridge DIFF abuts foundry's bottom drain DIFF at cell-local y=BRIDGE_H
    # (DIFF abutment merges access-tx drain into bridge net).  Bridge M1
    # wrapper abuts foundry BL met1 rail at the same boundary.
    super_cell.add(gdstk.Reference(bridge, origin=(0.0, 0.0)))
    super_cell.add(gdstk.Reference(foundry, origin=(0.0, BRIDGE_H)))

    # SRAM areaid spanning bridge + foundry + a small top margin for the
    # in-qtap BR drain bridge extension.  Phase 2 BR bridge polys still
    # live in the qtap and extend up to y_local=1.86; in supercell coords
    # they sit at y=BRIDGE_H+1.535..BRIDGE_H+1.86, well below the annex
    # top (handled separately).
    _rect(super_cell, _LAYER_AREAID_SRAM,
          0.0, 0.0,
          _FOUNDRY_LEF_W, BRIDGE_H + _FOUNDRY_LEF_H + 0.30)

    # ---- NWELL extension through annex (issue #9 — body-bias) ----
    # Wrapper extends NWELL up through the annex region so the parent-
    # level NWELL row strips (cim_supercell_array._add_nwell_row_bridges)
    # can bridge across columns.  Body bias to VPWR is provided by
    # periodic tap supercells (sky130_cim_tap_supercell) inserted in
    # the array — see conductor cim_tap_supercell_plan.md.  No per-cell
    # N-tap is emitted here; the tap supercell carries the foundry
    # sram_sp_wlstrap which provides the actual N+/P+ taps.
    _NWELL_X0 = 0.50
    _NWELL_X1 = 1.30
    _rect(super_cell, _LAYER_NWELL,
          _NWELL_X0, BRIDGE_H + _FOUNDRY_LEF_H,
          _NWELL_X1, cfg.supercell_h)

    # Re-emit the foundry/qtap external port labels at the supercell level
    # so Magic's hierarchical extraction promotes them as supercell ports
    # with their canonical names (`BL`, `BR`, `VPWR`, `VGND`, ...) instead
    # of the auto-prefixed `sky130_fd_bd_sram__sram_sp_cell_opt1_qtap_0/BL`
    # form.  Without these, the extracted supercell .subckt has port names
    # that don't match the reference .sp.
    #
    # The 4096 instance-prefixed VPWR/VGND/VPB/VNB ports that leak at the
    # array level are merged by the per-row MET2 power rails added in
    # `cim_supercell_array._add_vpwr_vgnd_m2_rails` — see conductor
    # cim_lvs_port_pattern_plan.md (Step 6).
    #
    # Coordinates in supercell-local frame: foundry-local (x, y) → (x, BRIDGE_H+y).
    # Layers match the foundry qtap labels (M1 text on (68,5), POLY on
    # (66,5), NWELL on (64,5), pwell on (64,59) for VNB).
    _foundry_port_labels = (
        ("BL",   0.420, 1.130, (68, 5)),
        ("BR",   0.780, 1.130, (68, 5)),
        ("VGND", 0.035, 0.037, (68, 5)),
        ("VPWR", 1.165, 0.037, (68, 5)),
        ("VPB",  1.165, 0.180, (64, 5)),
        ("VNB",  0.035, 0.200, (64, 59)),
    )
    for (text, fx, fy, layer) in _foundry_port_labels:
        _label(super_cell, text, layer, fx, BRIDGE_H + fy)
    # WL label — foundry has TWO POLY stripes per cell (wl_top at
    # foundry-local y=1.385, wl_bot at y=0.195), one for each access
    # transistor's gate.  Both carry the same WL signal but are
    # physically separate inside the foundry cell.  Without a label
    # at the supercell level, Magic's hierarchical extraction emits
    # them as TWO auto-named ports (`a_0_24#`, `a_0_262#`), which
    # forces netgen to flatten 4096 supercell instances into the top
    # and lose pin-matching uniqueness.  Labeling both POLY stripes
    # `WL` at the supercell level merges them by name into ONE port
    # — matching the reference `.subckt sky130_cim_supercell_sram_d
    # BL BR WL MWL MBL ...` port list.  The array-level `wl_0_<r>`
    # POLY strip still abuts at the parent level and carries the
    # net name parent-side; the supercell port `WL` binds to it via
    # boundary abutment (same mechanism as F11, applied one level lower).
    _label(super_cell, "WL", (66, 5), 0.0, BRIDGE_H + 1.385)
    _label(super_cell, "WL", (66, 5), 0.0, BRIDGE_H + 0.195)

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
    # diff_h = 2 * overhang + gate_l = 0.87.  T7 sits in the annex above
    # the foundry cell.  Foundry is now offset by BRIDGE_H (drain-bridge
    # strap occupies cell-local y=[0, BRIDGE_H]), so the annex starts at
    # cell-local y=BRIDGE_H+_FOUNDRY_LEF_H.
    t7_y_base = BRIDGE_H + _FOUNDRY_LEF_H + 0.10
    t7_diff_y0 = t7_y_base
    t7_diff_y1 = t7_diff_y0 + 2 * T7_DIFF_OVERHANG + T7_GATE_L

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
    qy_exit = BRIDGE_H + 1.20  # foundry-internal y=1.20 → supercell-local
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

    # ---- MIM cap ----
    # Cap centred in supercell Y, with 0.42 µm margin top and bottom of
    # supercell (so that Y-mirrored adjacent supercells produce 0.84
    # cap-to-cap spacing).
    #
    # X placement: shifted WEST so the cap east edge clears the T7
    # drain via2 by ≥ 0.30 µm capm.8 minimum (T2.1-CIM-B fix).  T7
    # drain via2 sits at t7_drn_cx (= T7_DIFF_X + T7_DIFF_W/2 = 1.875)
    # with via2 half-width 0.10, so via2 west edge = 1.775.  Cap east
    # must be ≤ 1.775 - 0.30 = 1.475.  We pin cap_x1 = 1.475 (max
    # allowed) — this gives the largest possible cap area without a
    # capm.8 violation.  Previously cap was centred (cap_x0 =
    # (super_w - cap_w)/2), which put cap_x1 at 1.755 µm for SRAM-A
    # — 180 K capm.8 tiles fired.
    super_h = cfg.supercell_h
    cap_margin_y = (super_h - cfg.cap_l) / 2
    cap_y0 = cap_margin_y
    cap_y1 = cap_margin_y + cfg.cap_l
    super_w = cfg.supercell_w
    _CAPM_TO_VIA2_SPACING = 0.30
    _T7_VIA2_WEST_EDGE = 1.775   # = t7_drn_cx (1.875) - via2_half (0.10)
    cap_x1 = _T7_VIA2_WEST_EDGE - _CAPM_TO_VIA2_SPACING   # 1.475
    cap_x0 = cap_x1 - cfg.cap_w
    if cap_x0 < 0:
        raise ValueError(
            f"cap_w {cfg.cap_w} too wide for variant {cfg.name} given "
            f"the T7 drain via2 west edge constraint cap_x1 ≤ 1.475."
        )
    M3_ENC_CAPM = 0.14

    # M3 bottom plate (with M3_ENC_CAPM=0.14 enclosure around capm,
    # defined above for the cap_x1 alignment computation).
    _rect(super_cell, _LAYER_MET3,
          cap_x0 - M3_ENC_CAPM, cap_y0 - M3_ENC_CAPM,
          cap_x1 + M3_ENC_CAPM, cap_y1 + M3_ENC_CAPM)

    # T7 drain M3 pad — encloses via2 and extends down to abut cap_bot M3
    # whenever T7 sits above the cap.  The pad's east edge must be at
    # least MET3_MIN_W past cap_bot M3's east edge so that the L-shape
    # union of the two polygons doesn't have a sub-min-width step on the
    # east side (met3.1).  The west edge must overlap cap_bot M3 in X to
    # ensure electrical continuity.  Via2 enclosure (≥ 0.085 µm in any
    # direction) is preserved on every side because the via2 sits at
    # t7_drn_cx and the pad always extends ≥ M3_PAD_HALF in every
    # direction from that center.
    MET3_MIN_W = 0.30
    VIA2_ENC = 0.085
    cap_bot_north = cap_y1 + M3_ENC_CAPM
    cap_bot_east = cap_x1 + M3_ENC_CAPM
    drain_m3_west = t7_drn_cx - M3_PAD_HALF
    drain_m3_east = t7_drn_cx + M3_PAD_HALF
    drain_m3_north = t7_drn_cy + M3_PAD_HALF
    drain_m3_south = t7_drn_cy - M3_PAD_HALF
    if t7_drn_cy >= cap_bot_north:
        # Extend T7 drain pad WEST so it overlaps cap_bot M3 by ≥
        # MET3_MIN_W in the X direction.  This guarantees the L-shape
        # union has a clean shared edge of width ≥ MET3_MIN_W at the
        # y=cap_bot_north boundary (no narrow finger).
        drain_m3_west = min(drain_m3_west, cap_bot_east - MET3_MIN_W)
        # Extend east edge ≥ MET3_MIN_W past cap_bot east so the east
        # step in the L-shape is also met3.1-clean.
        drain_m3_east = max(drain_m3_east, cap_bot_east + MET3_MIN_W)
        # Stretch south edge down to cap_bot M3 north edge.
        drain_m3_south = min(drain_m3_south, cap_bot_north)
    elif t7_drn_cy < cap_y0:
        drain_m3_north = max(drain_m3_north, cap_y0)
    else:
        # T7 drain Y is INSIDE cap Y range — happens for SRAM-A/B
        # (large caps span most of the supercell Y).  Extend T7 drain
        # M3 pad WEST to overlap cap_bot M3 east edge so the two
        # polygons merge into one continuous M3 region (cap_bot M3 is
        # already on the cap_bot net; T7 drain is already on the cap_bot
        # net; merging them resolves the met3.2 spacing rule that fired
        # at gap=0.075 µm after the T2.1-CIM-B cap-shift fix).
        drain_m3_west = min(drain_m3_west, cap_bot_east - MET3_MIN_W)
    # Sanity-check via2 enclosure on all sides.
    for side, edge in (
        ("east",  drain_m3_east  - t7_drn_cx),
        ("west",  t7_drn_cx      - drain_m3_west),
        ("north", drain_m3_north - t7_drn_cy),
        ("south", t7_drn_cy      - drain_m3_south),
    ):
        if edge < VIA2_ENC:
            raise ValueError(
                f"T7 drain M3 pad violates via2 enclosure on {side} side "
                f"for variant {variant}: enclosure {edge:.3f} µm < "
                f"{VIA2_ENC:.3f} µm."
            )
    _rect(super_cell, _LAYER_MET3,
          drain_m3_west, drain_m3_south,
          drain_m3_east, drain_m3_north)
    # capm top plate (89, 44)
    _rect(super_cell, _LAYER_MIMCAP,
          cap_x0, cap_y0, cap_x1, cap_y1)

    # T7-drain → cap_bot abutment is handled by the T7 drain M3 pad
    # extension above (single polygon; avoids the sub-min-spacing
    # sliver that a separate strap rect introduced).

    # MBL pin: cap top plate connects up to M4. Add via3 + M4 pad over cap.
    VIA3_HALF = 0.10
    # M4 pad half-size 0.25 → pad 0.50 × 0.50 = 0.25 µm² > 0.24 µm²
    # (met4.4a min area).  In the assembled macro, the parent's per-col
    # MBL M4 strip overlaps and merges this pad — but standalone DRC
    # doesn't see the strip, so size the pad to be self-sufficient.
    M4_PAD_HALF = 0.25
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
