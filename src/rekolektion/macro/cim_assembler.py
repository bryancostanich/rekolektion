"""CIM SRAM macro assembler.

Places all CIM components — bitcell array, MWL drivers, MBL precharge,
MBL sense buffers — into a single GDS macro.

Separate from the standard SRAM assembler because CIM macros have
fundamentally different peripherals (analog MBL path, no column mux,
no sense amp/write driver in the traditional sense).

Usage::

    from rekolektion.macro.cim_assembler import generate_cim_macro
    generate_cim_macro("SRAM-A", output_path="output/cim_sram_a.gds")
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from pathlib import Path
from typing import Dict

import gdstk

from rekolektion.bitcell.sky130_6t_lr_cim import (
    CIM_VARIANTS, load_cim_bitcell, generate_cim_bitcell,
)
from rekolektion.array.tiler import tile_array

logger = logging.getLogger(__name__)


@dataclass
class CIMMacroParams:
    """Parameters for a CIM SRAM macro."""
    variant: str       # "SRAM-A", "SRAM-B", etc.
    rows: int
    cols: int
    mim_w: float
    mim_l: float
    cap_fF: float
    cell_pitch_x: float
    cell_pitch_y: float
    macro_width: float = 0.0
    macro_height: float = 0.0


def generate_cim_macro(
    variant: str,
    output_path: str | Path | None = None,
    macro_name: str | None = None,
    *,
    flatten: bool = False,  # hierarchical GDS avoids flatten distortion
) -> tuple[gdstk.Library, CIMMacroParams]:
    """Generate a complete CIM SRAM macro GDS.

    Parameters
    ----------
    variant : str
        CIM variant name ("SRAM-A", "SRAM-B", "SRAM-C", "SRAM-D").
    output_path : path, optional
        Write GDS to this file.
    macro_name : str, optional
        Name for the top-level cell.
    flatten : bool
        Flatten before writing (default True).

    Returns
    -------
    (gdstk.Library, CIMMacroParams)
    """
    if variant not in CIM_VARIANTS:
        raise ValueError(f"Unknown CIM variant: {variant}. "
                         f"Valid: {sorted(CIM_VARIANTS.keys())}")

    v = CIM_VARIANTS[variant]
    rows, cols = v["rows"], v["cols"]
    mim_w, mim_l = v["mim_w"], v["mim_l"]
    name = macro_name or f"cim_{variant.lower().replace('-', '_')}_{rows}x{cols}"

    # --- Load CIM bitcell ---
    gds_dir = Path("output/cim_variants")
    gds_dir.mkdir(parents=True, exist_ok=True)
    cell_gds = gds_dir / f"sky130_6t_cim_lr_{variant.lower().replace('-', '_')}.gds"
    if not cell_gds.exists():
        generate_cim_bitcell(str(cell_gds), mim_w=mim_w, mim_l=mim_l)
    bitcell = load_cim_bitcell(str(cell_gds), variant=variant)

    params = CIMMacroParams(
        variant=variant,
        rows=rows,
        cols=cols,
        mim_w=mim_w,
        mim_l=mim_l,
        cap_fF=mim_w * mim_l * 2.0,
        cell_pitch_x=bitcell.cell_width,
        cell_pitch_y=bitcell.cell_height,
    )

    # --- Tile the bitcell array ---
    array_lib = tile_array(
        bitcell,
        num_rows=rows,
        num_cols=cols,
        with_routing=False,  # CIM routing handled separately
    )

    array_cell = None
    for c in array_lib.cells:
        if "array" in c.name:
            array_cell = c
            break
    if array_cell is None:
        array_cell = array_lib.cells[0]

    array_bb = array_cell.bounding_box()
    array_w = array_bb[1][0] - array_bb[0][0] if array_bb else cols * bitcell.cell_width
    array_h = array_bb[1][1] - array_bb[0][1] if array_bb else rows * bitcell.cell_height

    # --- Load CIM peripheral cells ---
    from rekolektion.array.support_cells import get_cim_peripheral

    mwl_info = get_cim_peripheral("cim_mwl_driver")
    pre_info = get_cim_peripheral("cim_mbl_precharge")
    sense_info = get_cim_peripheral("cim_mbl_sense")

    # --- Build macro library ---
    lib = gdstk.Library(name=f"{name}_lib")
    cell_map: Dict[str, gdstk.Cell] = {}

    # Add array and its dependencies
    for c in array_lib.cells:
        if c.name not in cell_map:
            new_cell = c.copy(c.name)
            cell_map[c.name] = new_cell
            lib.add(new_cell)

    # Load peripheral cells
    for info in [mwl_info, pre_info, sense_info]:
        src_lib = gdstk.read_gds(str(info.gds_path))
        for c in src_lib.cells:
            if c.name not in cell_map:
                new_cell = c.copy(c.name)
                cell_map[c.name] = new_cell
                lib.add(new_cell)

    mwl_cell = cell_map[mwl_info.cell_name]
    pre_cell = cell_map[pre_info.cell_name]
    sense_cell = cell_map[sense_info.cell_name]

    # --- Placement ---
    # Layout:
    #   MWL drivers (left) | Bitcell Array (center) | (right margin)
    #   MBL precharge at top of array
    #   MBL sense buffers at bottom of array

    top_cell = gdstk.Cell(name)
    lib.add(top_cell)

    mwl_margin = mwl_info.width + 1.0  # MWL drivers + gap
    pre_margin = pre_info.height + 0.5  # precharge + gap
    sense_margin = sense_info.height + 0.5

    x_offset = mwl_margin
    y_offset = sense_margin

    # Place array
    array_ref = gdstk.Reference(
        cell_map[array_cell.name],
        origin=(x_offset, y_offset),
    )
    top_cell.add(array_ref)

    # Place MWL drivers (one per row, left side)
    for row in range(rows):
        y_drv = y_offset + row * bitcell.cell_height + bitcell.cell_height / 2.0
        ref = gdstk.Reference(
            mwl_cell,
            origin=(0.0, y_drv - mwl_info.height / 2.0),
        )
        top_cell.add(ref)

    # Place MBL precharge (one per column, top)
    for col in range(cols):
        x_pre = x_offset + col * bitcell.cell_width + bitcell.cell_width / 2.0
        ref = gdstk.Reference(
            pre_cell,
            origin=(x_pre - pre_info.width / 2.0,
                    y_offset + array_h + 0.5),
        )
        top_cell.add(ref)

    # Place MBL sense buffers (one per column, bottom)
    for col in range(cols):
        x_sense = x_offset + col * bitcell.cell_width + bitcell.cell_width / 2.0
        ref = gdstk.Reference(
            sense_cell,
            origin=(x_sense - sense_info.width / 2.0, 0.0),
        )
        top_cell.add(ref)

    # --- Port labels ---
    _MET1 = (68, 20)
    _MET4 = (71, 20)
    _POLY = (66, 20)

    def _add_label(name, x, y, layer=_MET1):
        top_cell.add(gdstk.Label(name, (x, y), layer=layer[0], texttype=layer[1]))

    # MWL_EN pins (left edge, one per row)
    for row in range(rows):
        y_pin = y_offset + row * bitcell.cell_height + bitcell.cell_height / 2.0
        _add_label(f"MWL_EN[{row}]", 0.0, y_pin)

    # MBL_OUT pins (bottom edge, one per column)
    for col in range(cols):
        x_pin = x_offset + col * bitcell.cell_width + bitcell.cell_width / 2.0
        _add_label(f"MBL_OUT[{col}]", x_pin, 0.0)

    # Control pins
    macro_w = x_offset + array_w + 1.0
    macro_h = y_offset + array_h + pre_margin
    _add_label("MBL_PRE", macro_w / 2.0, macro_h, _POLY)
    _add_label("VREF", macro_w * 0.3, macro_h)
    _add_label("VBIAS", macro_w * 0.7, 0.0)
    _add_label("VDD", macro_w, macro_h / 2.0)
    _add_label("VSS", 0.0, 0.0)

    # --- Compute final dimensions ---
    bb = top_cell.bounding_box()
    if bb is not None:
        params.macro_width = bb[1][0] - bb[0][0]
        params.macro_height = bb[1][1] - bb[0][1]
    else:
        params.macro_width = macro_w
        params.macro_height = macro_h

    # --- Flatten ---
    if flatten:
        top_cell.flatten()
        sub_cells = [c for c in lib.cells if c.name != top_cell.name]
        for c in sub_cells:
            lib.remove(c)

    # --- Write ---
    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))
        logger.info("Wrote CIM macro GDS to %s", out)

    return lib, params


def generate_all_cim_macros(output_dir: str = "output/cim_macros") -> None:
    """Generate all 4 CIM macro variants."""
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    for variant in CIM_VARIANTS:
        v = CIM_VARIANTS[variant]
        gds_name = f"cim_{variant.lower().replace('-', '_')}_{v['rows']}x{v['cols']}.gds"
        lib, params = generate_cim_macro(
            variant,
            output_path=out / gds_name,
        )
        print(f"{variant}: {params.macro_width:.1f} x {params.macro_height:.1f} um "
              f"({params.rows}x{params.cols}, ~{params.cap_fF:.0f} fF cap)")
