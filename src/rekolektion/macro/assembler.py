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
    write_enable: bool = False
    scan_chain: bool = False
    clock_gating: bool = False
    power_gating: bool = False
    wl_switchoff: bool = False
    burn_in: bool = False

    @property
    def num_ben_bits(self) -> int:
        """Number of byte-enable bits (1 per byte, minimum 1)."""
        if not self.write_enable:
            return 0
        return max(1, self.bits // 8)

    @property
    def num_scan_flops(self) -> int:
        """Number of scan flops in the chain (0 if scan_chain disabled)."""
        if not self.scan_chain:
            return 0
        # Chain order: addr → we → cs → din → [ben]
        return self.num_addr_bits + 2 + self.bits + self.num_ben_bits


def compute_macro_params(
    words: int,
    bits: int,
    mux_ratio: int,
    *,
    write_enable: bool = False,
    scan_chain: bool = False,
    clock_gating: bool = False,
    power_gating: bool = False,
    wl_switchoff: bool = False,
    burn_in: bool = False,
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
        write_enable=write_enable,
        scan_chain=scan_chain,
        clock_gating=clock_gating,
        power_gating=power_gating,
        wl_switchoff=wl_switchoff,
        burn_in=burn_in,
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
    cell_type: str = "foundry",
    with_routing: bool = False,
    flatten: bool = True,
    write_enable: bool = False,
    scan_chain: bool = False,
    clock_gating: bool = False,
    power_gating: bool = False,
    wl_switchoff: bool = False,
    burn_in: bool = False,
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
    cell_type : str
        Bitcell to use: "foundry" (default) or "lr" (custom LR topology).
    with_routing : bool
        Add WL/BL/power routing to the bitcell array.
    flatten : bool
        Flatten the top-level cell before writing GDS (default True).
        This inlines all sub-cell references so downstream tools (e.g.
        OpenLane GDS merge) don't need to resolve external cell names.

    Returns
    -------
    (gdstk.Library, MacroParams)
    """
    params = compute_macro_params(
        words, bits, mux_ratio,
        write_enable=write_enable, scan_chain=scan_chain,
        clock_gating=clock_gating, power_gating=power_gating,
        wl_switchoff=wl_switchoff, burn_in=burn_in,
    )
    name = macro_name or f"sram_{words}x{bits}_mux{mux_ratio}"

    # --- load bitcell ------------------------------------------------------
    if cell_type == "lr":
        from rekolektion.bitcell.sky130_6t_lr import load_lr_bitcell
        bitcell = load_lr_bitcell()
    else:
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
    from rekolektion.peripherals.write_enable_gate import generate_write_enable_gates

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

    # Column mux and precharge — use foundry cells for foundry bitcell,
    # custom generators for LR bitcell
    mux_cell = None
    mux_h = 0.0
    pre_cell = None
    pre_h = 0.0
    # Track foundry unit cells for tiled placement
    _foundry_mux_unit = None
    _foundry_pre_unit = None

    if cell_type == "foundry":
        # Use foundry peripheral cells (matched to foundry bitcell pitch)
        if mux_ratio > 1:
            try:
                mux_info = get_peripheral_cell("column_mux")
                _foundry_mux_unit = _add_gds_to_lib(
                    lib, cell_map, mux_info.gds_path, mux_info.cell_name)
                mux_h = mux_info.height if mux_info.height > 0 else 6.82
            except Exception as e:
                logger.warning("Could not load foundry column_mux: %s", e)
        try:
            pre_info = get_peripheral_cell("precharge")
            _foundry_pre_unit = _add_gds_to_lib(
                lib, cell_map, pre_info.gds_path, pre_info.cell_name)
            pre_h = pre_info.height if pre_info.height > 0 else 3.98
        except Exception as e:
            logger.warning("Could not load foundry precharge: %s", e)
    else:
        # LR cell — use custom generators (pitch-matched at 1.9+ um)
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

    # Write enable AND gates (BEN masking)
    we_gate_cell = None
    we_gate_h = 0.0
    if write_enable and params.num_ben_bits > 0:
        try:
            we_gate_obj, we_gate_lib = generate_write_enable_gates(
                num_bits=bits,
                ben_bits=params.num_ben_bits,
            )
            for c in we_gate_lib.cells:
                if c.name not in cell_map:
                    new_cell = c.copy(c.name)
                    cell_map[c.name] = new_cell
                    lib.add(new_cell)
            we_gate_cell = cell_map[we_gate_obj.name]
            we_bb = we_gate_cell.bounding_box()
            we_gate_h = we_bb[1][1] - we_bb[0][1] if we_bb else 6.0
        except Exception as e:
            logger.warning("Could not generate write_enable_gates: %s", e)

    # Power switch header (PMOS switches on VDD rail)
    pwr_sw_cell = None
    pwr_sw_h = 0.0
    if power_gating:
        from rekolektion.peripherals.power_switch import generate_power_switches
        try:
            # Scale switch count with macro size: ~1 switch per 8µm of width
            est_width = params.cols * bitcell.cell_width + (nand_w + 2.0 if nand_cell else 10.0)
            n_switches = max(2, int(est_width / 8.0))
            pwr_sw_obj, pwr_sw_lib = generate_power_switches(
                num_switches=n_switches,
                macro_width=est_width,
            )
            for c in pwr_sw_lib.cells:
                if c.name not in cell_map:
                    new_cell = c.copy(c.name)
                    cell_map[c.name] = new_cell
                    lib.add(new_cell)
            pwr_sw_cell = cell_map[pwr_sw_obj.name]
            pwr_bb = pwr_sw_cell.bounding_box()
            pwr_sw_h = pwr_bb[1][1] - pwr_bb[0][1] if pwr_bb else 4.0
        except Exception as e:
            logger.warning("Could not generate power_switches: %s", e)

    # --- placement ---------------------------------------------------------
    # Layout (bottom to top):
    #   0. Write enable AND gates (if enabled)
    #   1. Sense amps + write drivers
    #   2. Column mux
    #   3. Bitcell array
    #   4. Precharge
    #   5. Power switch header (if enabled)
    # Row decoder on the left side

    top_cell = gdstk.Cell(name)
    lib.add(top_cell)

    # Decoder width (left margin)
    decoder_width = nand_w + 2.0 if nand_cell else 10.0
    x_offset = decoder_width  # Array starts after decoder

    current_y = 0.0

    # 0. Write enable AND gates (byte-enable masking)
    if we_gate_cell:
        ref = gdstk.Reference(we_gate_cell, origin=(x_offset, current_y))
        top_cell.add(ref)
        current_y += we_gate_h + 0.5

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
    elif _foundry_mux_unit:
        # Tile foundry unit cell across the array width
        mux_bb = _foundry_mux_unit.bounding_box()
        mux_unit_w = mux_bb[1][0] - mux_bb[0][0] if mux_bb else 3.37
        n_mux = max(1, int(array_w / mux_unit_w))
        for i in range(n_mux):
            ref = gdstk.Reference(
                _foundry_mux_unit,
                origin=(x_offset + i * mux_unit_w, current_y),
            )
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
    elif _foundry_pre_unit:
        # Tile foundry unit cell across the array width
        pre_unit_bb = _foundry_pre_unit.bounding_box()
        pre_unit_w = pre_unit_bb[1][0] - pre_unit_bb[0][0] if pre_unit_bb else 3.12
        n_pre = max(1, int(array_w / pre_unit_w))
        for i in range(n_pre):
            ref = gdstk.Reference(
                _foundry_pre_unit,
                origin=(x_offset + i * pre_unit_w, current_y),
            )
            top_cell.add(ref)
        current_y += pre_h + 0.5

    # 5. Power switch header at top (if power gating enabled)
    if pwr_sw_cell:
        ref = gdstk.Reference(pwr_sw_cell, origin=(0, current_y))
        top_cell.add(ref)
        current_y += pwr_sw_h + 0.5

    # 6. Row decoder on the left side (stack of NAND gates + WL gating)
    wl_gate_cell = None
    if wl_switchoff:
        from rekolektion.peripherals.wl_gate import generate_wl_gate
        try:
            wl_gate_obj, wl_gate_lib = generate_wl_gate()
            for c in wl_gate_lib.cells:
                if c.name not in cell_map:
                    new_cell = c.copy(c.name)
                    cell_map[c.name] = new_cell
                    lib.add(new_cell)
            wl_gate_cell = cell_map[wl_gate_obj.name]
        except Exception as e:
            logger.warning("Could not generate wl_gate: %s", e)

    wl_mux_cell = None
    wl_mux_w = 0.0
    if burn_in:
        from rekolektion.peripherals.wl_mux import generate_wl_mux
        try:
            wl_mux_obj, wl_mux_lib = generate_wl_mux()
            for c in wl_mux_lib.cells:
                if c.name not in cell_map:
                    new_cell = c.copy(c.name)
                    cell_map[c.name] = new_cell
                    lib.add(new_cell)
            wl_mux_cell = cell_map[wl_mux_obj.name]
            mux_bb = wl_mux_cell.bounding_box()
            wl_mux_w = mux_bb[1][0] - mux_bb[0][0] if mux_bb else 3.5
        except Exception as e:
            logger.warning("Could not generate wl_mux: %s", e)

    if nand_cell:
        for row in range(params.rows):
            y_dec = array_bottom_y + row * bitcell.cell_height
            ref = gdstk.Reference(
                cell_map[nand_info.cell_name],
                origin=(0, y_dec),
            )
            top_cell.add(ref)
            # Place WL gate AND cell adjacent to decoder NAND
            wl_chain_x = nand_w + 0.5
            if wl_gate_cell:
                ref_wl = gdstk.Reference(
                    wl_gate_cell,
                    origin=(wl_chain_x, y_dec),
                )
                top_cell.add(ref_wl)
                wl_chain_x += 3.0 + 0.5  # wl_gate width + gap
            # Place WL mux for burn-in after WL gate (or after decoder)
            if wl_mux_cell:
                ref_mux = gdstk.Reference(
                    wl_mux_cell,
                    origin=(wl_chain_x, y_dec),
                )
                top_cell.add(ref_mux)

    # --- add port labels for SPICE extraction --------------------------------
    # Place met2 labels at macro edges so Magic can identify ports.
    # Positions mirror the LEF generator pin placement.
    _MET2 = (69, 20)  # met2 layer/datatype
    w_dim = x_offset + array_w  # approximate macro width
    h_dim = current_y           # approximate macro height

    def _add_port_label(name: str, x: float, y: float) -> None:
        top_cell.add(gdstk.Label(name, (x, y), layer=_MET2[0], texttype=_MET2[1]))

    # Address pins — left edge
    addr_step = h_dim * 0.8 / max(params.num_addr_bits, 1)
    for i in range(params.num_addr_bits):
        cy = h_dim * 0.1 + i * addr_step + addr_step / 2
        _add_port_label(f"addr[{i}]", 0.0, cy)

    # Data pins — right edge
    data_total = params.bits * 2
    data_step = h_dim * 0.9 / max(data_total, 1)
    for i in range(params.bits):
        cy = h_dim * 0.05 + i * data_step + data_step / 2
        _add_port_label(f"din[{i}]", w_dim, cy)
    for i in range(params.bits):
        cy = h_dim * 0.05 + (params.bits + i) * data_step + data_step / 2
        _add_port_label(f"dout[{i}]", w_dim, cy)

    # Power — top/bottom
    _add_port_label("VPWR", w_dim / 2, h_dim)
    _add_port_label("VGND", w_dim / 5, 0.0)

    # Control pins — bottom edge
    bottom_ctrl = ["clk", "we", "cs"]
    if params.num_ben_bits:
        bottom_ctrl += [f"ben[{i}]" for i in range(params.num_ben_bits)]
    if params.scan_chain:
        bottom_ctrl += ["scan_in", "scan_out", "scan_en"]
    if params.clock_gating:
        bottom_ctrl.append("cen")
    if params.power_gating:
        bottom_ctrl.append("sleep")
    if params.wl_switchoff:
        bottom_ctrl.append("wl_off")
    if params.burn_in:
        bottom_ctrl.append("tm")
    ctrl_step = w_dim / (len(bottom_ctrl) + 2)
    for idx, pname in enumerate(bottom_ctrl):
        _add_port_label(pname, ctrl_step * (idx + 2), 0.0)

    # --- compute final dimensions ------------------------------------------
    bb = top_cell.bounding_box()
    if bb is not None:
        params.macro_width = bb[1][0] - bb[0][0]
        params.macro_height = bb[1][1] - bb[0][1]
    else:
        params.macro_width = x_offset + array_w
        params.macro_height = current_y

    # --- flatten top cell --------------------------------------------------
    if flatten:
        top_cell.flatten()
        # Remove sub-cells that are now inlined into the top cell
        sub_cells = [c for c in lib.cells if c.name != top_cell.name]
        for c in sub_cells:
            lib.remove(c)
        logger.info("Flattened top cell %s (removed %d sub-cells)",
                     top_cell.name, len(sub_cells))

    # --- write output ------------------------------------------------------
    if output_path is not None:
        out = Path(output_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        lib.write_gds(str(out))
        logger.info("Wrote macro GDS to %s", out)

    return lib, params
