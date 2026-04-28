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
from rekolektion.macro.routing import draw_pin_with_label

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
    # The precharge row has a horizontal poly stripe spanning all 64
    # cells at row-local Y=_PRE_MBL_PRE_LY.  Add a poly licon stack
    # at one column position to reach met1, then a met1 stub up to
    # the macro's TOP edge with the MBL_PRE label.
    mbl_pre_abs_y = pre_y + _PRE_MBL_PRE_LY
    mbl_pre_abs_x = pre_x + p.cell_pitch_x  # col 1 (any col is fine)
    # poly→li1 contact
    _poly_to_li1_contact(top, mbl_pre_abs_x, mbl_pre_abs_y)
    # li1→met1 mcon
    draw_via_stack(top, from_layer="li1", to_layer="met1",
                   position=(mbl_pre_abs_x, mbl_pre_abs_y))
    # met1 stub up to top edge
    _draw_vert_strap(top, "met1", mbl_pre_abs_x, mbl_pre_abs_y, macro_h)
    # macro pin at top edge
    draw_pin_with_label(top, text="MBL_PRE", layer="met1",
                        rect=(mbl_pre_abs_x - 0.07, macro_h - 0.14,
                              mbl_pre_abs_x + 0.07, macro_h))

    # ---- VREF external pin (TOP edge) ----
    vref_abs_y = pre_y + _PRE_VREF_LY
    vref_abs_x = pre_x + 3 * p.cell_pitch_x  # different col to avoid collision
    # li1→met1 mcon
    draw_via_stack(top, from_layer="li1", to_layer="met1",
                   position=(vref_abs_x, vref_abs_y))
    _draw_vert_strap(top, "met1", vref_abs_x, vref_abs_y, macro_h)
    draw_pin_with_label(top, text="VREF", layer="met1",
                        rect=(vref_abs_x - 0.07, macro_h - 0.14,
                              vref_abs_x + 0.07, macro_h))

    # ---- VBIAS external pin (BOTTOM edge) ----
    vbias_abs_y = sense_y + _SENSE_VBIAS_LY
    vbias_abs_x = sense_x + p.cell_pitch_x
    _poly_to_li1_contact(top, vbias_abs_x, vbias_abs_y)
    draw_via_stack(top, from_layer="li1", to_layer="met1",
                   position=(vbias_abs_x, vbias_abs_y))
    _draw_vert_strap(top, "met1", vbias_abs_x, 0.0, vbias_abs_y)
    draw_pin_with_label(top, text="VBIAS", layer="met1",
                        rect=(vbias_abs_x - 0.07, 0.0,
                              vbias_abs_x + 0.07, 0.14))

    # ---- VPWR external pin (TOP-RIGHT corner) ----
    vpwr_abs_y = pre_y + _PRE_VPWR_LY
    vpwr_abs_x = pre_x + (p.cols - 2) * p.cell_pitch_x
    _draw_vert_strap(top, "met1", vpwr_abs_x, vpwr_abs_y, macro_h)
    draw_pin_with_label(top, text="VPWR", layer="met1",
                        rect=(vpwr_abs_x - 0.07, macro_h - 0.14,
                              vpwr_abs_x + 0.07, macro_h))

    # ---- VGND external pin (BOTTOM-RIGHT corner) ----
    vgnd_abs_y = sense_y + _SENSE_VSS_TAP_LY
    vgnd_abs_x = sense_x + (p.cols - 2) * p.cell_pitch_x
    _draw_vert_strap(top, "met1", vgnd_abs_x, 0.0, vgnd_abs_y)
    draw_pin_with_label(top, text="VGND", layer="met1",
                        rect=(vgnd_abs_x - 0.07, 0.0,
                              vgnd_abs_x + 0.07, 0.14))

    # ---- MBL_OUT[col] external pins (BOTTOM edge) ----
    # Each sense cell exposes MBL_OUT[col] on li1 at row-local
    # (_SENSE_MBL_OUT_LX, _SENSE_MBL_OUT_LY).  Add a li1 stub from
    # that pad down to the macro bottom (y=0), labeled MBL_OUT[col].
    sense_x_offset = (p.cell_pitch_x - sense_row.cell_w) / 2.0
    for col in range(p.cols):
        cx = sense_x + col * p.cell_pitch_x + sense_x_offset + _SENSE_MBL_OUT_LX
        _draw_vert_strap(top, "li1", cx, 0.0, sense_y + _SENSE_MBL_OUT_LY)
        draw_pin_with_label(top, text=f"MBL_OUT[{col}]", layer="li1",
                            rect=(cx - 0.07, 0.0, cx + 0.07, 0.14))

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

    # Strap spans from sense_mbl_abs_y up to pre_mbl_abs_y.
    strap_y_bot = min(sense_mbl_abs_y, pre_mbl_abs_y) - 0.30
    strap_y_top = max(sense_mbl_abs_y, pre_mbl_abs_y) + 0.30

    pre_x_offset = (p.cell_pitch_x - pre_row.cell_w) / 2.0

    for col in range(p.cols):
        strap_x = array_x_pos + bitcell_mbl_lx + col * p.cell_pitch_x

        # Vertical MET4 strap
        top.add(gdstk.rectangle(
            (strap_x - _STRAP_HALF, strap_y_bot),
            (strap_x + _STRAP_HALF, strap_y_top),
            layer=m4_id, datatype=m4_dt,
        ))
        # Per-column MBL[c] label on the strap so each column has a
        # unique net name (vs all sharing "MBL" if labeled in the cell).
        # Use texttype=20 (drawing layer) — matches the convention the
        # bitcell uses for its MET4 MBL label, which Magic recognizes.
        strap_mid_y = (strap_y_bot + strap_y_top) / 2.0
        top.add(gdstk.Label(
            f"MBL[{col}]", (strap_x, strap_mid_y),
            layer=m4_id, texttype=20,
        ))

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
        _poly_to_li1_contact(top, sense_mbl_abs_x, sense_mbl_abs_y)
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


def _poly_to_li1_contact(top: gdstk.Cell, cx: float, cy: float) -> None:
    """Drop a poly licon + li1 pad over a poly polygon at (cx, cy).

    Poly licon (layer 66/44 — same as LICON1 with a poly tag) connects
    poly to li1.  Use the same LICON1 layer Magic recognizes for both
    poly and diff contacts.
    """
    from rekolektion.macro.sky130_drc import GDS_LAYER
    licon_id, licon_dt = GDS_LAYER["pc"]   # poly licon (66/44)
    li1_id, li1_dt = GDS_LAYER["li1"]
    poly_id, poly_dt = GDS_LAYER["poly"]
    # Poly enclosure of licon: 0.08 µm
    LICON = 0.17
    POLY_ENC = 0.08
    LI_ENC = 0.08
    # Widen the poly under the licon (poly.5: licon enclosure 0.05/0.08)
    poly_pad = LICON + 2 * POLY_ENC
    top.add(gdstk.rectangle(
        (cx - poly_pad / 2, cy - poly_pad / 2),
        (cx + poly_pad / 2, cy + poly_pad / 2),
        layer=poly_id, datatype=poly_dt,
    ))
    # Licon
    top.add(gdstk.rectangle(
        (cx - LICON / 2, cy - LICON / 2),
        (cx + LICON / 2, cy + LICON / 2),
        layer=licon_id, datatype=licon_dt,
    ))
    # Li1 pad
    li_pad = LICON + 2 * LI_ENC
    top.add(gdstk.rectangle(
        (cx - li_pad / 2, cy - li_pad / 2),
        (cx + li_pad / 2, cy + li_pad / 2),
        layer=li1_id, datatype=li1_dt,
    ))


def _draw_vert_strap(top: gdstk.Cell, layer: str,
                     cx: float, y0: float, y1: float,
                     w: float = 0.20) -> None:
    """Draw a vertical metal strap from (cx, y0) to (cx, y1)."""
    from rekolektion.macro.sky130_drc import GDS_LAYER
    layer_id, layer_dt = GDS_LAYER[layer]
    yl, yh = (y0, y1) if y0 <= y1 else (y1, y0)
    top.add(gdstk.rectangle(
        (cx - w / 2, yl), (cx + w / 2, yh),
        layer=layer_id, datatype=layer_dt,
    ))


# ---------------------------------------------------------------------------
# Legacy public API (back-compat for khalkulo / scripts)
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
