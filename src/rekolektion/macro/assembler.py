"""SRAM macro assembler.

Places all components — bitcell array, column mux, sense amplifiers,
write drivers, row decoder, precharge, and control logic — into a
single GDS macro.

The placement is approximate (no detailed inter-block routing) and is
intended to produce a structurally correct GDS with all pieces in
roughly the right positions.

Usage::

    from rekolektion.macro.assembler import generate_sram_macro
    generate_sram_macro(words=1024, bits=32, mux_ratio=8,
                        output_path="output/weight_macro.gds")
"""

from __future__ import annotations

import logging
import math
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Optional

import gdstk

from rekolektion.bitcell.base import BitcellInfo

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Macro parameters
# ---------------------------------------------------------------------------

@dataclass
class MacroParams:
    """Computed SRAM macro parameters."""
    words: int
    bits: int
    mux_ratio: int
    rows: int          # = words / mux_ratio
    cols: int          # = bits * mux_ratio
    num_addr_bits: int
    num_row_bits: int
    num_col_bits: int  # log2(mux_ratio)
    cell_name: str = ""
    cell_width: float = 0.0
    cell_height: float = 0.0
    macro_width: float = 0.0
    macro_height: float = 0.0


def compute_macro_params(
    words: int,
    bits: int,
    mux_ratio: int,
) -> MacroParams:
    """Compute array dimensions and address partitioning."""
    if mux_ratio not in (1, 2, 4, 8):
        raise ValueError(f"mux_ratio must be 1, 2, 4, or 8; got {mux_ratio}")
    if words < 1 or bits < 1:
        raise ValueError("words and bits must be >= 1")
    if words % mux_ratio != 0:
        raise ValueError(
            f"words ({words}) must be divisible by mux_ratio ({mux_ratio})"
        )

    rows = words // mux_ratio
    cols = bits * mux_ratio
    num_addr_bits = int(math.ceil(math.log2(words))) if words > 1 else 1
    num_row_bits = int(math.ceil(math.log2(rows))) if rows > 1 else 1
    num_col_bits = int(math.log2(mux_ratio)) if mux_ratio > 1 else 0

    return MacroParams(
        words=words,
        bits=bits,
        mux_ratio=mux_ratio,
        rows=rows,
        cols=cols,
        num_addr_bits=num_addr_bits,
        num_row_bits=num_row_bits,
        num_col_bits=num_col_bits,
    )


# ---------------------------------------------------------------------------
# Helper: add cell from a library into the target library
# ---------------------------------------------------------------------------

def _add_cell_to_lib(
    lib: gdstk.Library,
    cell_map: Dict[str, gdstk.Cell],
    source_cell: gdstk.Cell,
) -> gdstk.Cell:
    """Add a cell to the library, avoiding duplicates."""
    if source_cell.name in cell_map:
        return cell_map[source_cell.name]
    new_cell = source_cell.copy(source_cell.name)
    cell_map[source_cell.name] = new_cell
    lib.add(new_cell)
    return new_cell


def _add_gds_to_lib(
    lib: gdstk.Library,
    cell_map: Dict[str, gdstk.Cell],
    gds_path: Path,
    cell_name: str,
) -> gdstk.Cell:
    """Load a cell from a GDS file into the library."""
    if cell_name in cell_map:
        return cell_map[cell_name]
    src_lib = gdstk.read_gds(str(gds_path))
    target = None
    for c in src_lib.cells:
        if c.name == cell_name:
            target = c
            break
    if target is None:
        target = src_lib.cells[0]
    for c in src_lib.cells:
        if c.name not in cell_map:
            new_cell = c.copy(c.name)
            cell_map[c.name] = new_cell
            lib.add(new_cell)
    return cell_map[target.name]


# ---------------------------------------------------------------------------
# Main assembler
# ---------------------------------------------------------------------------

def generate_sram_macro(
    words: int,
    bits: int,
    mux_ratio: int = 1,
    output_path: str | Path | None = None,
    macro_name: str | None = None,
    *,
    with_routing: bool = False,
) -> tuple[gdstk.Library, MacroParams]:
    """Generate a complete SRAM macro GDS.

    Parameters
    ----------
    words : int
        Number of words (memory depth).
    bits : int
        Word width (number of data bits).
    mux_ratio : int
        Column mux ratio (1, 2, 4, or 8).
    output_path : path, optional
        Write GDS to this file.
    macro_name : str, optional
        Name for the top-level cell.
    with_routing : bool
        Add WL/BL/power routing to the bitcell array.

    Returns
    -------
    (gdstk.Library, MacroParams)
    """
    params = compute_macro_params(words, bits, mux_ratio)
    name = macro_name or f"sram_{words}x{bits}_mux{mux_ratio}"

    # --- load bitcell ------------------------------------------------------
    from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
    bitcell = load_foundry_sp_bitcell()
    params.cell_name = bitcell.cell_name
    params.cell_width = bitcell.cell_width
    params.cell_height = bitcell.cell_height

    # --- tile the bitcell array -------------------------------------------
    from rekolektion.array.tiler import tile_array
    array_lib = tile_array(
        bitcell,
        num_rows=params.rows,
        num_cols=params.cols,
        with_routing=with_routing,
    )

    # Find the array cell
    array_cell = None
    for c in array_lib.cells:
        if "array" in c.name:
            array_cell = c
            break
    if array_cell is None:
        array_cell = array_lib.cells[0]

    array_bb = array_cell.bounding_box()
    if array_bb is not None:
        array_w = array_bb[1][0] - array_bb[0][0]
        array_h = array_bb[1][1] - array_bb[0][1]
    else:
        array_w = params.cols * bitcell.cell_width
        array_h = params.rows * bitcell.cell_height

    # --- build macro library -----------------------------------------------
    lib = gdstk.Library(name=f"{name}_lib")
    cell_map: Dict[str, gdstk.Cell] = {}

    # Add array and its dependencies
    for c in array_lib.cells:
        if c.name not in cell_map:
            new_cell = c.copy(c.name)
            cell_map[c.name] = new_cell
            lib.add(new_cell)

    # --- load peripheral cells --------------------------------------------
    from rekolektion.peripherals.foundry_cells import get_peripheral_cell
    from rekolektion.peripherals.column_mux import generate_column_mux
    from rekolektion.peripherals.precharge import generate_precharge

    # Sense amplifier
    try:
        sa_info = get_peripheral_cell("sense_amp")
        sa_cell = _add_gds_to_lib(lib, cell_map, sa_info.gds_path, sa_info.cell_name)
        sa_w, sa_h = sa_info.width, sa_info.height
    except Exception as e:
        logger.warning("Could not load sense_amp: %s", e)
        sa_cell = None
        sa_w = sa_h = 0.0

    # Write driver
    try:
        wd_info = get_peripheral_cell("write_driver")
        wd_cell = _add_gds_to_lib(lib, cell_map, wd_info.gds_path, wd_info.cell_name)
        wd_w, wd_h = wd_info.width, wd_info.height
    except Exception as e:
        logger.warning("Could not load write_driver: %s", e)
        wd_cell = None
        wd_w = wd_h = 0.0

    # NAND gates for decoder
    nand_cell = None
    nand_w = nand_h = 0.0
    try:
        nand_info = get_peripheral_cell("nand2_dec")
        nand_cell = _add_gds_to_lib(lib, cell_map, nand_info.gds_path, nand_info.cell_name)
        nand_w, nand_h = nand_info.width, nand_info.height
    except Exception as e:
        logger.warning("Could not load nand2_dec: %s", e)

    # Column mux
    mux_cell = None
    mux_h = 0.0
    if mux_ratio > 1:
        try:
            mux_cell_obj, mux_lib = generate_column_mux(
                num_cols=params.cols,
                mux_ratio=mux_ratio,
                bl_pitch=bitcell.cell_width,
            )
            for c in mux_lib.cells:
                if c.name not in cell_map:
                    new_cell = c.copy(c.name)
                    cell_map[c.name] = new_cell
                    lib.add(new_cell)
            mux_cell = cell_map[mux_cell_obj.name]
            mux_bb = mux_cell.bounding_box()
            mux_h = mux_bb[1][1] - mux_bb[0][1] if mux_bb else 2.0 * mux_ratio
        except Exception as e:
            logger.warning("Could not generate column_mux: %s", e)

    # Precharge
    try:
        pre_cell_obj, pre_lib = generate_precharge(
            num_cols=params.cols,
            bl_pitch=bitcell.cell_width,
        )
        for c in pre_lib.cells:
            if c.name not in cell_map:
                new_cell = c.copy(c.name)
                cell_map[c.name] = new_cell
                lib.add(new_cell)
        pre_cell = cell_map[pre_cell_obj.name]
        pre_bb = pre_cell.bounding_box()
        pre_h = pre_bb[1][1] - pre_bb[0][1] if pre_bb else 6.0
    except Exception as e:
        logger.warning("Could not generate precharge: %s", e)
        pre_cell = None
        pre_h = 0.0

    # --- placement ---------------------------------------------------------
    # Layout (bottom to top):
    #   1. Sense amps + write drivers
    #   2. Column mux
    #   3. Bitcell array
    #   4. Precharge
    # Row decoder on the left side

    top_cell = gdstk.Cell(name)
    lib.add(top_cell)

    # Decoder width (left margin)
    decoder_width = nand_w + 2.0 if nand_cell else 10.0
    x_offset = decoder_width  # Array starts after decoder

    current_y = 0.0

    # 1. Sense amplifiers + write drivers (one per output bit)
    sa_wd_height = max(sa_h, wd_h) if (sa_cell or wd_cell) else 0.0
    if sa_cell or wd_cell:
        for i in range(bits):
            x_sa = x_offset + i * mux_ratio * bitcell.cell_width
            if sa_cell:
                ref = gdstk.Reference(
                    cell_map[sa_info.cell_name],
                    origin=(x_sa, current_y),
                )
                top_cell.add(ref)
            if wd_cell:
                # Place write driver next to sense amp
                x_wd = x_sa + sa_w + 0.5 if sa_cell else x_sa
                ref = gdstk.Reference(
                    cell_map[wd_info.cell_name],
                    origin=(x_wd, current_y),
                )
                top_cell.add(ref)
        current_y += sa_wd_height + 1.0

    # 2. Column mux
    if mux_cell:
        ref = gdstk.Reference(mux_cell, origin=(x_offset, current_y))
        top_cell.add(ref)
        current_y += mux_h + 0.5

    # 3. Bitcell array
    array_ref = gdstk.Reference(
        cell_map[array_cell.name],
        origin=(x_offset, current_y),
    )
    top_cell.add(array_ref)
    array_bottom_y = current_y
    current_y += array_h + 0.5

    # 4. Precharge at top
    if pre_cell:
        ref = gdstk.Reference(pre_cell, origin=(x_offset, current_y))
        top_cell.add(ref)
        current_y += pre_h + 0.5

    # 5. Row decoder on the left side (stack of NAND gates)
    if nand_cell:
        for row in range(params.rows):
            y_dec = array_bottom_y + row * bitcell.cell_height
            ref = gdstk.Reference(
                cell_map[nand_info.cell_name],
                origin=(0, y_dec),
            )
            top_cell.add(ref)

    # --- compute final dimensions ------------------------------------------
    bb = top_cell.bounding_box()
    if bb is not None:
        params.macro_width = bb[1][0] - bb[0][0]
        params.macro_height = bb[1][1] - bb[0][1]
    else:
        params.macro_width = x_offset + array_w
        params.macro_height = current_y

    # --- write output ------------------------------------------------------
    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))
        logger.info("Wrote macro GDS to %s", out)

    return lib, params
