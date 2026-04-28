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

    # NOTE: The MWL driver row already exposes MWL_EN[row] li1 .pin
    # shapes at its WEST edge (x=0), which is the macro's west edge —
    # no separate macro-level pin needed.  Same for MWL[row] at the
    # row's east edge (where the bitcell array abuts).
    #
    # MBL_OUT[col], MBL_PRE, VREF, VBIAS, VPWR/VGND, and MBL[col]
    # column straps still need explicit macro-level routing.  TODO.

    p.macro_width = macro_w
    p.macro_height = macro_h
    return out_lib, p


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
