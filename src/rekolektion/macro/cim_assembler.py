"""CIM SRAM macro assembler — v2-style modular block placement.

Builds a CIM macro by composing four block-builder classes:

    CIMBitcellArray     — tiled custom CIM bitcell (sky130_6t_lr_cim)
    MWLDriverRow        — vertical stack of MWL drivers, LEFT of array
    MBLPrechargeRow     — horizontal row of MBL precharges, ABOVE array
    MBLSenseRow         — horizontal row of MBL sense buffers, BELOW array

Floorplan (cell-local coords, origin at bottom-left of macro):

    +--------------------------------------------------+
    |                                                  |  <- pre row (TOP)
    |                                                  |
    |  MWL    +-----------------------------+          |
    |  drvs   |                             |          |
    |  (LEFT) |       BITCELL ARRAY         |          |
    |         |                             |          |
    |         +-----------------------------+          |
    |                                                  |  <- sense row (BOTTOM)
    +--------------------------------------------------+

The legacy public API `generate_cim_macro(variant, ...)` is preserved
as a thin wrapper around `assemble_cim()` so external callers
(particularly khalkulo) keep working.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import gdstk

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS, load_cim_bitcell
from rekolektion.macro.cim_bitcell_array import CIMBitcellArray
from rekolektion.macro.cim_mwl_driver_row import MWLDriverRow
from rekolektion.macro.cim_mbl_precharge_row import MBLPrechargeRow
from rekolektion.macro.cim_mbl_sense_row import MBLSenseRow
from rekolektion.macro.routing import (
    draw_pin_with_label,
    draw_poly_to_li1_contact,
    draw_vert_strap,
)

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Params
# ---------------------------------------------------------------------------

@dataclass
class CIMMacroParams:
    """Parameters for a CIM SRAM macro.

    Variant-driven (one of "SRAM-A".."SRAM-D") — the variant determines
    bitcell dimensions, MIM cap geometry, and array shape.  All other
    fields are computed from the variant.
    """
    variant: str
    rows: int = 0
    cols: int = 0
    mim_w: float = 0.0
    mim_l: float = 0.0
    cap_fF: float = 0.0
    cell_pitch_x: float = 0.0
    cell_pitch_y: float = 0.0
    macro_width: float = 0.0
    macro_height: float = 0.0

    @classmethod
    def from_variant(cls, variant: str) -> "CIMMacroParams":
        if variant not in CIM_VARIANTS:
            raise ValueError(
                f"Unknown CIM variant {variant!r}. "
                f"Valid: {sorted(CIM_VARIANTS)}"
            )
        v = CIM_VARIANTS[variant]
        return cls(
            variant=variant,
            rows=v["rows"], cols=v["cols"],
            mim_w=v["mim_w"], mim_l=v["mim_l"],
            cap_fF=v["mim_w"] * v["mim_l"] * 2.0,
        )

    @property
    def top_cell_name(self) -> str:
        slug = self.variant.lower().replace("-", "_")
        return f"cim_{slug}_{self.rows}x{self.cols}"


# ---------------------------------------------------------------------------
# Floorplan
# ---------------------------------------------------------------------------

# Margins between blocks (μm).
_LEFT_GAP: float = 1.0     # between MWL driver column and array
_TOP_GAP: float = 0.5      # between array top and MBL precharge row
_BOTTOM_GAP: float = 0.5   # between MBL sense row and array bottom


@dataclass
class CIMFloorplan:
    """Absolute (x, y) positions and sizes of every CIM block."""
    positions: dict[str, tuple[float, float]] = field(default_factory=dict)
    sizes: dict[str, tuple[float, float]] = field(default_factory=dict)
    macro_size: tuple[float, float] = (0.0, 0.0)


def build_cim_floorplan(p: CIMMacroParams) -> CIMFloorplan:
    """Compute placement coordinates for every block in the CIM macro."""
    # Materialise the bitcell to read its pitches.
    bca = CIMBitcellArray(p.variant, p.rows, p.cols)
    cell_w = bca.cell_pitch_x
    cell_h = bca.cell_pitch_y

    # Stash pitches on the params for downstream consumers.
    p.cell_pitch_x = cell_w
    p.cell_pitch_y = cell_h

    array_w = p.cols * cell_w
    array_h = p.rows * cell_h

    mwl_row = MWLDriverRow(rows=p.rows, row_pitch=cell_h)
    pre_row = MBLPrechargeRow(cols=p.cols, col_pitch=cell_w)
    sense_row = MBLSenseRow(cols=p.cols, col_pitch=cell_w)

    fp = CIMFloorplan()

    # MWL driver column at the LEFT (x=0).
    fp.positions["mwl_driver"] = (0.0, _BOTTOM_GAP + sense_row.height)
    fp.sizes["mwl_driver"] = (mwl_row.width, mwl_row.height)

    # Bitcell array east of the MWL drivers.
    array_x = mwl_row.width + _LEFT_GAP
    array_y = _BOTTOM_GAP + sense_row.height
    fp.positions["array"] = (array_x, array_y)
    fp.sizes["array"] = (array_w, array_h)

    # MBL precharge row at the TOP of the array.
    fp.positions["mbl_precharge"] = (array_x, array_y + array_h + _TOP_GAP)
    fp.sizes["mbl_precharge"] = (pre_row.width, pre_row.height)

    # MBL sense row at the BOTTOM of the array.
    fp.positions["mbl_sense"] = (array_x, 0.0)
    fp.sizes["mbl_sense"] = (sense_row.width, sense_row.height)

    macro_w = array_x + array_w + 1.0    # +1 µm right margin
    macro_h = (
        _BOTTOM_GAP + sense_row.height +
        array_h + _TOP_GAP + pre_row.height
    )
    fp.macro_size = (macro_w, macro_h)
    return fp


# ---------------------------------------------------------------------------
# Assemble
# ---------------------------------------------------------------------------

def assemble_cim(p: CIMMacroParams) -> tuple[gdstk.Library, CIMMacroParams]:
    """Build the full CIM macro library by composing the four block builders.

    Returns (library, populated CIMMacroParams).  The library has a
    single top cell named `p.top_cell_name`; all sub-blocks live as
    referenced sub-cells.
    """
    fp = build_cim_floorplan(p)

    # Build each block library independently.
    bca = CIMBitcellArray(p.variant, p.rows, p.cols)
    array_lib = bca.build()
    array_cell_in_src = bca.array_cell(array_lib)

    mwl_row = MWLDriverRow(rows=p.rows, row_pitch=p.cell_pitch_y)
    mwl_lib = mwl_row.build()
    mwl_cell_in_src = next(
        c for c in mwl_lib.cells if c.name == mwl_row.top_cell_name
    )

    pre_row = MBLPrechargeRow(cols=p.cols, col_pitch=p.cell_pitch_x)
    pre_lib = pre_row.build()
    pre_cell_in_src = next(
        c for c in pre_lib.cells if c.name == pre_row.top_cell_name
    )

    sense_row = MBLSenseRow(cols=p.cols, col_pitch=p.cell_pitch_x)
    sense_lib = sense_row.build()
    sense_cell_in_src = next(
        c for c in sense_lib.cells if c.name == sense_row.top_cell_name
    )

    # Compose into a single parent library.
    out_lib = gdstk.Library(name=f"{p.top_cell_name}_lib")
    cell_map: dict[str, gdstk.Cell] = {}
    for src_lib in (array_lib, mwl_lib, pre_lib, sense_lib):
        for c in src_lib.cells:
            if c.name in cell_map:
                continue
            copy = c.copy(c.name)
            cell_map[c.name] = copy
            out_lib.add(copy)

    top = gdstk.Cell(p.top_cell_name)
    out_lib.add(top)

    # Place each block at its floorplan position.
    for block_name, src_cell in (
        ("array",         array_cell_in_src),
        ("mwl_driver",    mwl_cell_in_src),
        ("mbl_precharge", pre_cell_in_src),
        ("mbl_sense",     sense_cell_in_src),
    ):
        local = cell_map[src_cell.name]
        x, y = fp.positions[block_name]
        top.add(gdstk.Reference(local, origin=(x, y)))

    macro_w, macro_h = fp.macro_size

    # ---- Macro-level routing ----
    # Connect each row builder's shared-bus internal nets to the macro's
    # external pins, and stitch per-column / per-row signals between
    # blocks.  Pin shapes here become top-level macro ports.
    _add_macro_routing(top, p, fp, mwl_row, pre_row, sense_row)

    p.macro_width = macro_w
    p.macro_height = macro_h
    return out_lib, p


# ---------------------------------------------------------------------------
# Macro-level routing
# ---------------------------------------------------------------------------

# Pin position constants from the row builders (mirrored here for
# absolute-coord macro routing).
_PRE_MBL_PRE_LY: float = 0.705   # poly Y inside cim_mbl_precharge
_PRE_VREF_LY:    float = 0.955   # li1 Y
_PRE_VPWR_LY:    float = 0.705   # met1 Y (n-tap row)
_PRE_MBL_LX:     float = 0.720   # x of MBL drain in cell-local coords
_PRE_MBL_LY:     float = 0.455   # li1 Y for MBL drain

_SENSE_VBIAS_LY: float = 0.405   # poly Y
_SENSE_MBL_LX:   float = 0.170   # poly X for MBL gate input
_SENSE_MBL_LY:   float = 1.075   # poly Y
_SENSE_VSS_LY:   float = 0.155   # li1 Y for source-bottom + body tap
_SENSE_VDD_LY:   float = 1.325   # li1 Y for driver-drain
_SENSE_MBL_OUT_LY: float = 0.740  # li1 Y for source-follower output
_SENSE_MBL_OUT_LX: float = 0.800  # li1 X
_SENSE_VSS_TAP_LY: float = 0.155  # met1 Y at p-tap


def _add_macro_routing(
    top: gdstk.Cell,
    p: CIMMacroParams,
    fp: CIMFloorplan,
    mwl_row: MWLDriverRow,
    pre_row: MBLPrechargeRow,
    sense_row: MBLSenseRow,
) -> None:
    """Add top-level routing wires + macro pin labels."""
    from rekolektion.macro.routing import draw_pin_with_label, draw_via_stack
    from rekolektion.macro.sky130_drc import GDS_LAYER

    poly_id, poly_dt = GDS_LAYER["poly"]
    li1_id, li1_dt = GDS_LAYER["li1"]
    m1_id, m1_dt = GDS_LAYER["met1"]
    m2_id, m2_dt = GDS_LAYER["met2"]
    m4_id, m4_dt = GDS_LAYER["met4"]

    macro_w, macro_h = fp.macro_size
    pre_x, pre_y = fp.positions["mbl_precharge"]
    sense_x, sense_y = fp.positions["mbl_sense"]
    array_x, array_y = fp.positions["array"]

    # ---- MBL_PRE external pin (TOP edge) ----
    # MBL_PRE poly bus is at Y=0.705 inside the precharge row, where
    # the VPWR met1 bus also lives.  A direct poly→li1→met1 via stack
    # at this Y would land its met1 pad on the VPWR bus, shorting
    # MBL_PRE to VPWR.  Instead: extend MBL_PRE upward as a vertical
    # poly stripe to Y=pre_top - 0.10 (above both VPWR and VREF rails),
    # then do the poly→li1→met1 via stack there.
    mbl_pre_abs_y_in = pre_y + _PRE_MBL_PRE_LY
    mbl_pre_abs_x = pre_x + p.cell_pitch_x  # col 1
    pre_top = pre_y + pre_row.height
    mbl_pre_via_y = pre_top + 0.20    # outside the precharge cell vertically
    # Vertical poly stripe from MBL_PRE bus Y up past the cell top
    poly_id_bus, poly_dt_bus = GDS_LAYER["poly"]
    top.add(gdstk.rectangle(
        (mbl_pre_abs_x - 0.075, mbl_pre_abs_y_in),
        (mbl_pre_abs_x + 0.075, mbl_pre_via_y + 0.10),
        layer=poly_id_bus, datatype=poly_dt_bus,
    ))
    draw_poly_to_li1_contact(top, mbl_pre_abs_x, mbl_pre_via_y)
    draw_via_stack(top, from_layer="li1", to_layer="met1",
                   position=(mbl_pre_abs_x, mbl_pre_via_y))
    draw_vert_strap(top, "met1", mbl_pre_abs_x, mbl_pre_via_y, macro_h)
    draw_pin_with_label(top, text="MBL_PRE", layer="met1",
                        rect=(mbl_pre_abs_x - 0.07, macro_h - 0.14,
                              mbl_pre_abs_x + 0.07, macro_h))

    # ---- VREF external pin (TOP edge) ----
    vref_abs_y = pre_y + _PRE_VREF_LY
    vref_abs_x = pre_x + 3 * p.cell_pitch_x  # different col to avoid collision
    # li1→met1 mcon
    draw_via_stack(top, from_layer="li1", to_layer="met1",
                   position=(vref_abs_x, vref_abs_y))
    draw_vert_strap(top, "met1", vref_abs_x, vref_abs_y, macro_h)
    draw_pin_with_label(top, text="VREF", layer="met1",
                        rect=(vref_abs_x - 0.07, macro_h - 0.14,
                              vref_abs_x + 0.07, macro_h))

    # ---- VBIAS external pin (BOTTOM edge) ----
    # VBIAS poly bus is at sense-cell-local Y=0.405, but the strap
    # path from VBIAS Y down to the macro bottom edge crosses the
    # sense row's VSS-tap met1 bus at Y=0.155.  Route on met2 (above
    # met1) to avoid shorting VBIAS to VGND.  The via stack's met1
    # intermediate hop is at Y=0.405 (above the VSS bus at Y=0.155),
    # so it doesn't conflict.
    #
    # X position: place the poly licon in col 0's LEFT MARGIN.  The
    # sense cell's NMOS diff spans x_local=[0.30, 1.30] and the
    # P-tap spans x_local=[1.57, 1.87], so any licon dropped onto
    # the VBIAS poly bus inside a cell either lands on the diff
    # (turning the licon into a diff contact and shorting VBIAS to
    # an NMOS S/D) or on the tap (shorting VBIAS to VSS).  The
    # cell's left margin x_local=[0, 0.30] has no diff and no tap;
    # placing the licon at x_local=0.05 puts the 0.33 µm li1 pad at
    # x_local=[-0.115, 0.215], which clears every per-cell li1/diff
    # feature.  The pad's western half spills into the LEFT_GAP
    # between the row builder and the sense row, where there is
    # nothing else routed.
    vbias_abs_y = sense_y + _SENSE_VBIAS_LY
    vbias_abs_x = sense_x + 0.05
    draw_poly_to_li1_contact(top, vbias_abs_x, vbias_abs_y)
    draw_via_stack(top, from_layer="li1", to_layer="met2",
                   position=(vbias_abs_x, vbias_abs_y))
    draw_vert_strap(top, "met2", vbias_abs_x, 0.0, vbias_abs_y)
    draw_pin_with_label(top, text="VBIAS", layer="met2",
                        rect=(vbias_abs_x - 0.07, 0.0,
                              vbias_abs_x + 0.07, 0.14))

    # ---- VPWR external pin (TOP-RIGHT corner) ----
    vpwr_abs_y = pre_y + _PRE_VPWR_LY
    vpwr_abs_x = pre_x + (p.cols - 2) * p.cell_pitch_x
    draw_vert_strap(top, "met1", vpwr_abs_x, vpwr_abs_y, macro_h)
    draw_pin_with_label(top, text="VPWR", layer="met1",
                        rect=(vpwr_abs_x - 0.07, macro_h - 0.14,
                              vpwr_abs_x + 0.07, macro_h))

    # ---- VGND external pin (BOTTOM-RIGHT corner) ----
    vgnd_abs_y = sense_y + _SENSE_VSS_TAP_LY
    vgnd_abs_x = sense_x + (p.cols - 2) * p.cell_pitch_x
    draw_vert_strap(top, "met1", vgnd_abs_x, 0.0, vgnd_abs_y)
    draw_pin_with_label(top, text="VGND", layer="met1",
                        rect=(vgnd_abs_x - 0.07, 0.0,
                              vgnd_abs_x + 0.07, 0.14))

    # ---- MBL_OUT[col] external pins (BOTTOM edge) ----
    # Each sense cell exposes MBL_OUT[col] on li1 at row-local
    # (_SENSE_MBL_OUT_LX, _SENSE_MBL_OUT_LY).  A li1 stub down to y=0
    # would cross the sense row's VSS li1 bus at y=0.155.  A met1
    # stub would cross the VSS-tap met1 bus at y=0.155.  Route on
    # met2 (above met1) — the via stack's met1 intermediate is at
    # cell_mbl_out_y=0.740 (above both VSS buses), no conflict.
    sense_x_offset = (p.cell_pitch_x - sense_row.cell_w) / 2.0
    for col in range(p.cols):
        cx = sense_x + col * p.cell_pitch_x + sense_x_offset + _SENSE_MBL_OUT_LX
        cell_mbl_out_y = sense_y + _SENSE_MBL_OUT_LY
        # li1 → met2 via stack at the cell's MBL_OUT pad
        draw_via_stack(top, from_layer="li1", to_layer="met2",
                       position=(cx, cell_mbl_out_y))
        # met2 vertical stub from cell MBL_OUT y down to y=0
        draw_vert_strap(top, "met2", cx, 0.0, cell_mbl_out_y)
        # met2 pin label at the bottom edge
        draw_pin_with_label(top, text=f"MBL_OUT[{col}]", layer="met2",
                            rect=(cx - 0.07, 0.0, cx + 0.07, 0.14))

    # ---- MWL[row] horizontal bridges: driver east → array MWL poly ----
    # Each row builder exposes MWL[r] on li1 at the row builder's east
    # edge.  The bitcell array's MWL poly stripe (per row, in
    # mirror-pair tiling) enters at the array's west edge at a
    # different Y per row.  Bridge with: li1 horizontal in LEFT_GAP →
    # poly licon → vertical poly to align Y → enters array MWL poly.
    from rekolektion.bitcell.sky130_6t_lr_cim import (
        load_cim_bitcell as _load_bc_mwl,
        generate_cim_bitcell as _gen_bc_mwl,
        CIM_VARIANTS as _cv_mwl,
    )
    _v_mwl = _cv_mwl[p.variant]
    _bc_gds_mwl = Path("output/cim_variants") / f"sky130_6t_cim_lr_{p.variant.lower().replace('-', '_')}.gds"
    if not _bc_gds_mwl.exists():
        _bc_gds_mwl.parent.mkdir(parents=True, exist_ok=True)
        _gen_bc_mwl(str(_bc_gds_mwl), mim_w=_v_mwl["mim_w"], mim_l=_v_mwl["mim_l"])
    _bc = _load_bc_mwl(str(_bc_gds_mwl), variant=p.variant)
    mwl_x_local, mwl_y_base = fp.positions["mwl_driver"]
    array_x_pos_local, array_y_pos_local = fp.positions["array"]
    rb_w = mwl_row.width
    slack_y = p.cell_pitch_y - mwl_row.driver_h
    _BUF2_X_OUT_Y = 1.87       # buf_2's X pin Y in cell-local coords

    # Bitcell MWL local Y (poly stripe centre Y in bitcell coords).
    bitcell_mwl_local_y = _bc.pins["MWL"].ports[0][1]
    # Mirror-row placement origin Y (within a pair).  tile_array's
    # `_place_cell` uses `oy = origin_y + cell_height` where
    # `cell_height` = `bitcell.geometry_height` (the cell's GDS bbox
    # height — 3.535 µm for every variant), NOT `cell_pitch_y` (the
    # row pitch — varies per variant: 3.915 for D, 5.155 for A, etc.).
    # So a y-mirrored row's MWL ends up at:
    #   y_array = (cell_pitch_y + geometry_height) - bitcell_mwl_local_y
    # within its pair, NOT at `2*cell_pitch_y - bitcell_mwl_local_y`
    # (which would be the formula if the placement used the row pitch
    # for the mirror).  Using the per-variant geometry_height makes
    # the bridge align with all 4 macros — hardcoding 7.45 only worked
    # for SRAM-C/D (where 3.915+3.535=7.45).
    _mirror_oy = p.cell_pitch_y + _bc.geometry_height

    li1_id_bridge, li1_dt_bridge = GDS_LAYER["li1"]
    poly_id_bridge, poly_dt_bridge = GDS_LAYER["poly"]

    for row in range(p.rows):
        rb_mwl_y = mwl_y_base + row * p.cell_pitch_y + slack_y / 2.0 + _BUF2_X_OUT_Y
        # Array MWL Y for row r (pair-mirror tiling).
        pair_idx = row // 2
        if row % 2 == 0:
            arr_mwl_y = array_y_pos_local + pair_idx * (2 * p.cell_pitch_y) + bitcell_mwl_local_y
        else:
            arr_mwl_y = array_y_pos_local + pair_idx * (2 * p.cell_pitch_y) + _mirror_oy - bitcell_mwl_local_y

        # 1. met1 stub from row builder east edge across LEFT_GAP.
        #    The row builder's MWL[r] stub is on met1 (was li1, but
        #    that shorted to the buf_2 VPWR li1 rail extension), so
        #    the bridge stays on met1 here too.  bridge_x sits inside
        #    the empty LEFT_GAP region, kept far enough from the array
        #    west edge that the via-stack mcon's met1 pad (half-width
        #    ~0.145 µm) does NOT extend past the array boundary onto
        #    the bitcell's VGND/VPWR met1 rails (which start at
        #    array_x_pos_local).
        bridge_x_west = rb_w
        bridge_x = array_x_pos_local - 0.30
        m1_id_bridge, m1_dt_bridge = GDS_LAYER["met1"]
        top.add(gdstk.rectangle(
            (bridge_x_west, rb_mwl_y - 0.075),
            (bridge_x, rb_mwl_y + 0.075),
            layer=m1_id_bridge, datatype=m1_dt_bridge,
        ))
        # 2. Via stack at bridge_x: met1 → mcon → li1.  Lands the
        #    horizontal met1 stub onto the li1 pad we'll convert to
        #    poly via licon below.
        draw_via_stack(top, from_layer="li1", to_layer="met1",
                       position=(bridge_x, rb_mwl_y))
        # 3. Poly licon at bridge_x converts li1 → poly at rb_mwl_y.
        #    Co-located with the via stack pad — overlapping li1
        #    pads merge.
        draw_poly_to_li1_contact(top, bridge_x, rb_mwl_y)
        # 4. Vertical poly stripe at bridge_x from rb_mwl_y to
        #    arr_mwl_y (entirely in the empty LEFT_GAP — no diff
        #    crossings).
        y_lo = min(rb_mwl_y, arr_mwl_y)
        y_hi = max(rb_mwl_y, arr_mwl_y)
        top.add(gdstk.rectangle(
            (bridge_x - 0.075, y_lo),
            (bridge_x + 0.075, y_hi),
            layer=poly_id_bridge, datatype=poly_dt_bridge,
        ))
        # 5. Horizontal poly stub from bridge_x east into the array's
        #    MWL poly stripe (which spans x=0..cw within the bitcell
        #    on the same Y), overlapping the leftmost column's MWL
        #    poly to make the connection.
        top.add(gdstk.rectangle(
            (bridge_x - 0.075, arr_mwl_y - 0.075),
            (array_x_pos_local + 0.20, arr_mwl_y + 0.075),
            layer=poly_id_bridge, datatype=poly_dt_bridge,
        ))

    # ---- MBL[col] vertical MET4 column straps ----
    # For each column, draw a met4 strap that spans from the precharge
    # MBL pin (top of array) to the sense MBL pin (bottom of array),
    # passing over each bitcell's met4 MBL pad in that column.  The
    # strap X aligns with the bitcell's MBL pin X (cell_cx within each
    # column's pitch-relative position).
    array_x_pos, array_y_pos = fp.positions["array"]
    array_h = fp.sizes["array"][1]
    array_w = fp.sizes["array"][0]

    # Bitcell MBL is at cell_cx within the bitcell — equal to half the
    # geometric width.  For tile_array's mirrored pairing, MBL ends up
    # at a regular X offset of `bitcell_cx` from each column's start.
    # We compute it by loading the bitcell info.
    from rekolektion.bitcell.sky130_6t_lr_cim import load_cim_bitcell, generate_cim_bitcell, CIM_VARIANTS
    from pathlib import Path as _Path
    _v = CIM_VARIANTS[p.variant]
    _bc_gds = _Path("output/cim_variants") / f"sky130_6t_cim_lr_{p.variant.lower().replace('-', '_')}.gds"
    if not _bc_gds.exists():
        _bc_gds.parent.mkdir(parents=True, exist_ok=True)
        generate_cim_bitcell(str(_bc_gds), mim_w=_v["mim_w"], mim_l=_v["mim_l"])
    _bc = load_cim_bitcell(str(_bc_gds), variant=p.variant)
    bitcell_mbl_lx = _bc.pins["MBL"].ports[0][0]   # local x of MBL pin in bitcell
    bitcell_mbl_ly = _bc.pins["MBL"].ports[0][1]   # local y

    # MET4 strap parameters
    _STRAP_W = 0.40    # met4 strap width
    _STRAP_HALF = _STRAP_W / 2

    # Y span of the strap: from precharge MBL pin Y down to sense MBL pin Y
    # (extending slightly past so it overlaps the bitcell MBL pads).
    # Bitcell MBL pad on met4 is small (~ MIM_M4_OVERLAP-bound).
    pre_mbl_abs_y = pre_y + _PRE_MBL_LY    # where precharge cell's MBL li1 pad is
    sense_mbl_abs_y = sense_y + _SENSE_MBL_LY  # where sense cell's MBL gate is

    # Strap spans from sense_mbl_abs_y to pre_mbl_abs_y, then extended
    # north to the macro top edge so the net touches the cell boundary
    # (Magic's port-detection requires this for top-level promotion).
    strap_y_bot = min(sense_mbl_abs_y, pre_mbl_abs_y) - 0.30
    strap_y_top = macro_h

    pre_x_offset = (p.cell_pitch_x - pre_row.cell_w) / 2.0

    for col in range(p.cols):
        strap_x = array_x_pos + bitcell_mbl_lx + col * p.cell_pitch_x

        # Vertical MET4 strap
        top.add(gdstk.rectangle(
            (strap_x - _STRAP_HALF, strap_y_bot),
            (strap_x + _STRAP_HALF, strap_y_top),
            layer=m4_id, datatype=m4_dt,
        ))
        # Per-column MBL_<c> .pin + label at the TOP edge of the
        # macro (where the strap reaches the cell boundary).  This
        # makes MBL_<c> a top-level port that Magic promotes.
        draw_pin_with_label(
            top, text=f"MBL_{col}", layer="met4",
            rect=(strap_x - 0.07, macro_h - 0.14,
                  strap_x + 0.07, macro_h),
        )

        # Precharge MBL li1 pad → met4 strap (li1→m1→m2→m3→m4 via stack)
        pre_mbl_abs_x = pre_x + col * p.cell_pitch_x + pre_x_offset + _PRE_MBL_LX
        # Drop a via stack at the precharge MBL pin position
        draw_via_stack(top, from_layer="li1", to_layer="met4",
                       position=(pre_mbl_abs_x, pre_mbl_abs_y))
        # Horizontal met4 jog from precharge MBL X to strap X (at pre_mbl_abs_y)
        x_lo = min(pre_mbl_abs_x, strap_x) - _STRAP_HALF
        x_hi = max(pre_mbl_abs_x, strap_x) + _STRAP_HALF
        top.add(gdstk.rectangle(
            (x_lo, pre_mbl_abs_y - _STRAP_HALF),
            (x_hi, pre_mbl_abs_y + _STRAP_HALF),
            layer=m4_id, datatype=m4_dt,
        ))

        # Sense MBL poly gate → met4 strap (poly licon→li1→m1→m2→m3→m4)
        sense_mbl_abs_x = sense_x + col * p.cell_pitch_x + sense_x_offset + _SENSE_MBL_LX
        # Poly licon stack already wires the gate poly to li1 (need to add it)
        draw_poly_to_li1_contact(top, sense_mbl_abs_x, sense_mbl_abs_y)
        draw_via_stack(top, from_layer="li1", to_layer="met4",
                       position=(sense_mbl_abs_x, sense_mbl_abs_y))
        # Horizontal met4 jog from sense MBL X to strap X
        x_lo = min(sense_mbl_abs_x, strap_x) - _STRAP_HALF
        x_hi = max(sense_mbl_abs_x, strap_x) + _STRAP_HALF
        top.add(gdstk.rectangle(
            (x_lo, sense_mbl_abs_y - _STRAP_HALF),
            (x_hi, sense_mbl_abs_y + _STRAP_HALF),
            layer=m4_id, datatype=m4_dt,
        ))


# ---------------------------------------------------------------------------
# Legacy public API (back-compat for downstream scripts)
# ---------------------------------------------------------------------------

def generate_cim_macro(
    variant: str,
    output_path: Optional[str | Path] = None,
    macro_name: Optional[str] = None,
    *,
    flatten: bool = False,
) -> tuple[gdstk.Library, CIMMacroParams]:
    """Generate a complete CIM SRAM macro GDS.

    Thin wrapper around `assemble_cim()` for back-compat.  Optionally
    writes the GDS and optionally flattens the top cell (default
    hierarchical to avoid flatten distortion).
    """
    p = CIMMacroParams.from_variant(variant)
    if macro_name is not None:
        # Custom name override — assemble uses p.top_cell_name, so swap
        # by post-renaming the top cell after assembly.
        lib, p = assemble_cim(p)
        for c in lib.cells:
            if c.name == p.top_cell_name:
                c.name = macro_name
                break
    else:
        lib, p = assemble_cim(p)

    if flatten:
        for c in lib.cells:
            if c.name == (macro_name or p.top_cell_name):
                c.flatten()
                break
        # Drop sub-cells once flattened.
        sub_cells = [
            c for c in lib.cells
            if c.name != (macro_name or p.top_cell_name)
        ]
        for c in sub_cells:
            lib.remove(c)

    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))
        logger.info("Wrote CIM macro GDS to %s", out)

    return lib, p


def generate_all_cim_macros(output_dir: str = "output/cim_macros") -> None:
    """Generate all 4 CIM macro variants."""
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    for variant in CIM_VARIANTS:
        v = CIM_VARIANTS[variant]
        gds_name = (
            f"cim_{variant.lower().replace('-', '_')}_"
            f"{v['rows']}x{v['cols']}.gds"
        )
        lib, params = generate_cim_macro(
            variant,
            output_path=out / gds_name,
        )
        print(
            f"{variant}: {params.macro_width:.1f} x {params.macro_height:.1f} um "
            f"({params.rows}x{params.cols}, ~{params.cap_fF:.0f} fF cap)"
        )
