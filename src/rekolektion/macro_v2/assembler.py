"""Top-level SRAM macro assembler (v2).

Composes the C3 bitcell array, C4 peripherals, and C5 row decoder +
control logic into a complete, electrically-wired SRAM macro GDS.

Phase C6 builds this incrementally:
    C6.0 — MacroV2Params + build_floorplan   (this file at minimum)
    C6.1 — assemble() structural placement
    C6.2 — WL fanout (decoder -> array)
    C6.3 — BL fanout (array <-> peripherals)
    C6.4 — control signal fanout
    C6.5 — top-level pins + power grid
    C6.7 — end-to-end DRC + LVS on sram_test_tiny
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field

import gdstk

from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
from rekolektion.macro_v2.bitcell_array import (
    BitcellArray,
    _FOUNDRY_WL_LABEL_Y,
)
from rekolektion.macro_v2.column_mux_row import ColumnMuxRow
from rekolektion.macro_v2.control_logic import ControlLogic
from rekolektion.macro_v2.precharge_row import PrechargeRow
from rekolektion.macro_v2.row_decoder import (
    RowDecoder,
    _NAND_DEC_PITCH,
    _SPLIT_TABLE,
)
from rekolektion.macro_v2.routing import (
    draw_label,
    draw_pdn_strap,
    draw_pin,
    draw_pin_with_label,
    draw_via_stack,
    draw_wire,
)
from rekolektion.macro_v2.sense_amp_row import SenseAmpRow
from rekolektion.macro_v2.wl_driver_row import WlDriverRow
from rekolektion.macro_v2.write_driver_row import WriteDriverRow


# Inter-block gaps (um). Loose initially; tightened after C6.1 DRC sweep.
_ARRAY_TO_PERIPH_GAP: float = 1.0
_DECODER_TO_ARRAY_GAP: float = 2.0
_CONTROL_ABOVE_GAP: float = 2.0

# Peripheral row heights — authoritative values in the row modules'
# module-level constants (SA/WD/Precharge/ColMux). Importing the
# .height property would require building the row, which defeats the
# purpose of a pure-geometry floorplan. Import the constants instead.
from rekolektion.macro_v2.sense_amp_row import _SA_HEIGHT as _SA_H  # noqa: E402
from rekolektion.macro_v2.write_driver_row import _WD_HEIGHT as _WD_H  # noqa: E402
from rekolektion.macro_v2.precharge_row import _PRECHARGE_HEIGHT as _PRECHARGE_H  # noqa: E402
from rekolektion.macro_v2.column_mux_row import _COLMUX_HEIGHT as _COLMUX_H  # noqa: E402

# ControlLogic stack height — DFF row + inter-row gap + NAND2 row.
_CTRL_H: float = 7.545 + 2.0 + 2.69

# Row decoder width — NAND cell width (for the narrower case) + margin.
# For num_rows in {4, 8} the decoder is a single NAND column (~7.5 um
# wide for NAND3); larger decoders add predecoder blocks on the left.
_DECODER_W_ESTIMATE: float = 25.0


@dataclass
class MacroV2Params:
    """Top-level macro parameters.

    words : word count (rows × mux_ratio)
    bits  : bit width of each word (cols / mux_ratio)
    mux_ratio : column mux ratio; powers of 2 >= 2 only
    """
    words: int
    bits: int
    mux_ratio: int

    def __post_init__(self):
        if self.mux_ratio < 2 or (self.mux_ratio & (self.mux_ratio - 1)) != 0:
            raise ValueError(
                f"mux_ratio must be a power of 2 >= 2; got {self.mux_ratio}"
            )
        if self.words % self.mux_ratio != 0:
            raise ValueError(
                f"words ({self.words}) must be divisible by "
                f"mux_ratio ({self.mux_ratio})"
            )
        if self.rows not in _SPLIT_TABLE:
            raise ValueError(
                f"rows {self.rows} not in decoder split table; "
                f"valid: {sorted(_SPLIT_TABLE.keys())}"
            )
        # num_addr_bits derivation assumes words is a power of 2.
        if self.words & (self.words - 1) != 0:
            raise ValueError(
                f"words must be a power of 2; got {self.words}"
            )

    @property
    def rows(self) -> int:
        return self.words // self.mux_ratio

    @property
    def cols(self) -> int:
        return self.bits * self.mux_ratio

    @property
    def num_addr_bits(self) -> int:
        return int(math.log2(self.words))

    @property
    def top_cell_name(self) -> str:
        return f"sram_{self.words}x{self.bits}_mux{self.mux_ratio}"


@dataclass
class Floorplan:
    """Absolute (x, y) positions and sizes of every block."""
    positions: dict[str, tuple[float, float]] = field(default_factory=dict)
    sizes: dict[str, tuple[float, float]] = field(default_factory=dict)
    macro_size: tuple[float, float] = (0.0, 0.0)


def build_floorplan(p: MacroV2Params) -> Floorplan:
    """Compute placement coordinates for every block in the macro.

    Layout (y=0 at array's bottom-left corner):
        array       at (0, 0)
        precharge   above the array (y > array top)
        col_mux     below the array (y < 0)
        sense_amp   below col_mux
        write_driver below sense_amp
        row_decoder left of array, y aligned to array y=0
        control_logic below the row decoder

    Returns absolute coordinates; caller is expected to translate to
    wherever the top-cell origin sits.
    """
    bc = load_foundry_sp_bitcell()
    array_w = p.cols * bc.cell_width
    array_h = p.rows * bc.cell_height

    positions: dict[str, tuple[float, float]] = {}
    sizes: dict[str, tuple[float, float]] = {}

    positions["array"] = (0.0, 0.0)
    sizes["array"] = (array_w, array_h)

    # Precharge row sits above the array.
    positions["precharge"] = (0.0, array_h + _ARRAY_TO_PERIPH_GAP)
    sizes["precharge"] = (array_w, _PRECHARGE_H)

    # Stack col_mux / sense_amp / write_driver below the array.
    y = -_ARRAY_TO_PERIPH_GAP
    for name, h in (
        ("col_mux", _COLMUX_H),
        ("sense_amp", _SA_H),
        ("write_driver", _WD_H),
    ):
        y -= h
        positions[name] = (0.0, y)
        sizes[name] = (array_w, h)
        y -= _ARRAY_TO_PERIPH_GAP

    # WL driver column sits immediately left of the array (needs to
    # align row-by-row). Width estimate from the WlDriverRow class —
    # NAND3 (7.51) + VDD rail + clearance ≈ 9.81 µm.
    wld_w = 9.81
    positions["wl_driver"] = (
        -(wld_w + _DECODER_TO_ARRAY_GAP),
        0.0,
    )
    sizes["wl_driver"] = (wld_w, array_h)

    # Row decoder to the left of the WL driver.
    dec_w = _DECODER_W_ESTIMATE
    positions["row_decoder"] = (
        -(wld_w + _DECODER_TO_ARRAY_GAP + dec_w + _DECODER_TO_ARRAY_GAP),
        0.0,
    )
    sizes["row_decoder"] = (dec_w, array_h)

    # Control logic below the row decoder.
    positions["control_logic"] = (
        -(dec_w + _DECODER_TO_ARRAY_GAP),
        -(_CONTROL_ABOVE_GAP + _CTRL_H),
    )
    sizes["control_logic"] = (dec_w, _CTRL_H)

    # Macro bounding box
    xs_lo = [x for x, _ in positions.values()]
    ys_lo = [y for _, y in positions.values()]
    xs_hi = [x + sizes[name][0] for name, (x, _) in positions.items()]
    ys_hi = [y + sizes[name][1] for name, (_, y) in positions.items()]
    macro_w = max(xs_hi) - min(xs_lo)
    macro_h = max(ys_hi) - min(ys_lo)

    return Floorplan(
        positions=positions,
        sizes=sizes,
        macro_size=(macro_w, macro_h),
    )


def _build_block_libraries(
    p: MacroV2Params,
) -> dict[str, tuple[object, "gdstk.Library"]]:
    """Build each subblock once and return (block_obj, block_lib) per name.

    Ordering here is the only place that knows which concrete block
    class serves each floorplan slot.
    """
    name_tag = f"m{p.mux_ratio}_{p.words}x{p.bits}"
    blocks: dict[str, tuple[object, gdstk.Library]] = {}

    array = BitcellArray(
        rows=p.rows, cols=p.cols, name=f"sram_array_{name_tag}",
    )
    blocks["array"] = (array, array.build())

    precharge = PrechargeRow(
        bits=p.bits, mux_ratio=p.mux_ratio, name=f"pre_{name_tag}",
    )
    blocks["precharge"] = (precharge, precharge.build())

    col_mux = ColumnMuxRow(
        bits=p.bits, mux_ratio=p.mux_ratio, name=f"mux_{name_tag}",
    )
    blocks["col_mux"] = (col_mux, col_mux.build())

    sense_amp = SenseAmpRow(
        bits=p.bits, mux_ratio=p.mux_ratio, name=f"sa_{name_tag}",
    )
    blocks["sense_amp"] = (sense_amp, sense_amp.build())

    write_driver = WriteDriverRow(
        bits=p.bits, mux_ratio=p.mux_ratio, name=f"wd_{name_tag}",
    )
    blocks["write_driver"] = (write_driver, write_driver.build())

    row_decoder = RowDecoder(
        num_rows=p.rows, name=f"row_decoder_{name_tag}",
    )
    blocks["row_decoder"] = (row_decoder, row_decoder.build())

    control_logic = ControlLogic(
        use_replica=True, name=f"ctrl_logic_{name_tag}",
    )
    blocks["control_logic"] = (control_logic, control_logic.build())

    wl_driver = WlDriverRow(
        num_rows=p.rows, name=f"wl_driver_{name_tag}",
    )
    blocks["wl_driver"] = (wl_driver, wl_driver.build())

    return blocks


def assemble(p: MacroV2Params) -> gdstk.Library:
    """Compose all C3/C4/C5 blocks into a top-level macro GDS.

    C6.1 placement only — no inter-block routing. Subsequent tasks
    (C6.2-C6.5) wire the blocks and add top-level pins + PDN.
    """
    fp = build_floorplan(p)
    blocks = _build_block_libraries(p)

    lib = gdstk.Library(name=f"{p.top_cell_name}_lib")
    top = gdstk.Cell(p.top_cell_name)

    # Merge every subblock's cells into the top-level library exactly once.
    seen: set[str] = set()
    for _, sub_lib in blocks.values():
        for c in sub_lib.cells:
            if c.name in seen:
                continue
            lib.add(c.copy(c.name))
            seen.add(c.name)

    # Place each block at its floorplan position.
    for name, (obj, _) in blocks.items():
        block_top_name = obj.top_cell_name
        block_cell = next(c for c in lib.cells if c.name == block_top_name)
        x, y = fp.positions[name]
        top.add(gdstk.Reference(block_cell, origin=(x, y)))

    # C6.2 — wire decoder output to array WL per row.
    _route_wl(top, p, fp)

    # C6.3 — extend BL/BR met1 strips through peripheral rows.
    _route_bl(top, p, fp)

    # C6.4 — route control signals from control_logic to peripherals.
    _route_control(top, p, fp)

    # C6.4b — wire top-level clk/we/cs into ctrl_logic DFFs/NAND2s and
    # NAND2 outputs back to DFF D inputs so Magic promotes CLK/D/A/B/Z
    # as ports on each cell during LVS extraction.
    _route_ctrl_internal(top, p, fp)

    # C6.4c — addr fan-in to row_decoder NAND_dec inputs.
    _route_addr(top, p, fp)

    # C6.5 — top-level signal pins + proper macro PDN (FIX-A).
    # _place_power_grid drew met4 straps for an old design that
    # isn't compatible with chip-level PDN; _draw_power_network
    # replaces it with met2 rails + met3 straps that align with the
    # LEF power pin stubs.
    _place_top_pins(top, p, fp)
    _draw_power_network(top, p, fp)

    lib.add(top)

    # Shift the top cell so its bounding-box lower-left lands at (0, 0),
    # matching the LEF ORIGIN 0 0 convention.  Without this shift the
    # GDS shapes sit at negative assembler-frame coords (e.g. xs_lo =
    # -39.81, ys_lo = -37.875) while the LEF declares pin PORT RECTs
    # in 0-origin macro-local space via `lef_generator.tx/ty`.
    # OpenROAD's PDN generator then can't find GDS metal at the
    # LEF-declared pin position and emits PDN-0232 "macro does not
    # contain any shapes or vias" for every macro instance.
    _shift_top_to_zero_origin(top, p, fp)

    return lib


def _shift_top_to_zero_origin(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Translate every shape in `top` by (-xs_lo, -ys_lo) so the cell's
    bounding box begins at (0, 0).  Uses the same xs_lo/ys_lo formulas
    the LEF generator uses, so the GDS and LEF agree on coordinates."""
    xs_lo = min(x for x, _ in fp.positions.values()) - 1.0
    prec_top = fp.positions["precharge"][1] + fp.sizes["precharge"][1]
    wd_bot = fp.positions["write_driver"][1]
    pins_top_y = prec_top + _PDN_STRAP_MARGIN + _PDN_STRAP_W + _PDN_STRAP_MARGIN
    pins_bot_y = wd_bot - _PDN_STRAP_MARGIN - _PDN_STRAP_W - _PDN_STRAP_MARGIN
    ys_lo = pins_bot_y - 0.5
    dx, dy = -xs_lo, -ys_lo
    for poly in top.polygons:
        poly.translate(dx, dy)
    for path in top.paths:
        path.translate(dx, dy)
    for lbl in top.labels:
        lx, ly = lbl.origin
        lbl.origin = (lx + dx, ly + dy)
    for ref in top.references:
        ox, oy = ref.origin
        ref.origin = (ox + dx, oy + dy)


# ---------------------------------------------------------------------------
# C6.2 — WL fanout: decoder NAND output (li1) -> array WL poly strip
# ---------------------------------------------------------------------------

# NAND3 Z-pin wide li1 strip is at cell-local y=[0.200, 0.370], centred at
# y=0.285, extending x=[1.610, 7.510]. NAND2 and NAND4 Z pins share the
# same y geometry (bottom-of-cell horizontal strip at y~0.285). We drop
# the li1→met1 via near the cell's right edge where the strip is at its
# widest.
_NAND_OUTPUT_Y_CELL_LOCAL: float = 0.285
# x offset from NAND cell right edge at which to drop the li1→met1 via.
# Placed 0.5 um inside the cell so the mcon + met1 pad sit entirely on
# the existing li1 output strip.
_NAND_OUTPUT_X_OFFSET_FROM_RIGHT: float = 0.5
# Clearance between the array's left edge and the met1→poly via stack
# we drop to reach the array's WL poly strip.
_WL_VIA_ARRAY_GAP: float = 0.3


def _nand_right_edge_x_local(k_fanin: int) -> float:
    """Return the NAND_k cell's right-edge x in cell-local coords."""
    # Matched to the GDS bbox right edge of each NAND_dec cell.
    return {2: 4.770, 3: 7.510, 4: 9.685}[k_fanin]


def _nand_output_absolute(
    dec_origin: tuple[float, float],
    nand_x_in_dec: float,
    k_fanin: int,
    row: int,
) -> tuple[float, float]:
    """Absolute (x, y) where a li1→met1 mcon can be dropped on NAND_k's
    output li1 strip for the given row (honouring the X-mirror tiling)."""
    dec_x, dec_y = dec_origin
    right_edge = _nand_right_edge_x_local(k_fanin)
    out_x_local = right_edge - _NAND_OUTPUT_X_OFFSET_FROM_RIGHT
    abs_x = dec_x + nand_x_in_dec + out_x_local
    if row % 2 == 0:
        abs_y = dec_y + row * _NAND_DEC_PITCH + _NAND_OUTPUT_Y_CELL_LOCAL
    else:
        # Row origin = (row+1)*pitch with x_reflection; cell-local y=0.285
        # reflects to -0.285, absolute = (row+1)*pitch - 0.285.
        abs_y = dec_y + (row + 1) * _NAND_DEC_PITCH - _NAND_OUTPUT_Y_CELL_LOCAL
    return abs_x, abs_y


def _array_wl_y_absolute(array_origin_y: float, row: int) -> float:
    """Absolute y of the array's poly WL strip for the given row.

    Matches BitcellArray._add_wl_labels:
      even row (unmirrored):  row*cell_h + _FOUNDRY_WL_LABEL_Y
      odd row (X-mirrored):   row*cell_h + (cell_h - _FOUNDRY_WL_LABEL_Y)
    """
    cell_h = 1.58
    row_y0 = array_origin_y + row * cell_h
    if row % 2 == 0:
        return row_y0 + _FOUNDRY_WL_LABEL_Y
    return row_y0 + cell_h - _FOUNDRY_WL_LABEL_Y


def _route_wl(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Wire each row:  decoder Z → wl_driver A → wl_driver Z → array WL.

    The wl_driver (NAND3 w/ B,C tied to VDD) inverts the active-low
    decoder output into an active-high WL.  Without it the bitcell
    access transistors never turn on.
    """
    dec_origin = fp.positions["row_decoder"]
    wld_origin = fp.positions["wl_driver"]
    array_origin = fp.positions["array"]

    # Decoder NAND column inside row_decoder
    if len(_SPLIT_TABLE[p.rows]) == 1:
        nand_x_in_dec = 0.0
        k_fanin = _SPLIT_TABLE[p.rows][0]
    else:
        nand_x_in_dec = 4 * 4.77 + 2.0
        k_fanin = len(_SPLIT_TABLE[p.rows])

    # WL driver row (WlDriverRow) places one NAND3_dec per row at
    # (0, row*1.58) cell-local, with X-mirror on odd rows (same pattern
    # as the row decoder). Get pin positions from the class.
    wld = WlDriverRow(num_rows=p.rows)

    array_left = array_origin[0]
    via_x_at_array = array_left - _WL_VIA_ARRAY_GAP

    for row in range(p.rows):
        # --- Segment 1: decoder Z → WL driver A ---
        dec_out_x, dec_out_y = _nand_output_absolute(
            dec_origin, nand_x_in_dec, k_fanin, row,
        )
        wld_a_local = wld.a_pin_absolute(row)
        wld_a_x = wld_origin[0] + wld_a_local[0]
        wld_a_y = wld_origin[1] + wld_a_local[1]

        draw_via_stack(
            top, from_layer="li1", to_layer="met1",
            position=(dec_out_x, dec_out_y),
        )
        draw_wire(
            top, start=(dec_out_x, dec_out_y),
            end=(wld_a_x, dec_out_y), layer="met1",
        )
        if abs(wld_a_y - dec_out_y) > 1e-6:
            draw_wire(
                top, start=(wld_a_x, dec_out_y),
                end=(wld_a_x, wld_a_y), layer="met1",
            )
        draw_via_stack(
            top, from_layer="li1", to_layer="met1",
            position=(wld_a_x, wld_a_y),
        )

        # --- Segment 2: WL driver Z → array WL poly ---
        wld_z_local = wld.z_pin_absolute(row)
        wld_z_x = wld_origin[0] + wld_z_local[0]
        wld_z_y = wld_origin[1] + wld_z_local[1]
        wl_y = _array_wl_y_absolute(array_origin[1], row)

        draw_via_stack(
            top, from_layer="li1", to_layer="met1",
            position=(wld_z_x, wld_z_y),
        )
        draw_wire(
            top, start=(wld_z_x, wld_z_y),
            end=(via_x_at_array, wld_z_y), layer="met1",
        )
        if abs(wl_y - wld_z_y) > 1e-6:
            draw_wire(
                top, start=(via_x_at_array, wld_z_y),
                end=(via_x_at_array, wl_y), layer="met1",
            )
        draw_via_stack(
            top, from_layer="poly", to_layer="met1",
            position=(via_x_at_array, wl_y),
        )


# ---------------------------------------------------------------------------
# C6.3 — BL/BR fanout: extend array strips through peripheral rows
# ---------------------------------------------------------------------------

# Bitcell pin positions (absolute, relative to array origin at (0,0))
# Derived from bitcell_array.py: col_x0 = col * cell_width (1.31);
# BL at col_x0 + 0.0425, BR at col_x0 + 1.1575, strip width 0.14.
_BITCELL_BL_X_OFFSET: float = 0.0425
_BITCELL_BR_X_OFFSET: float = 1.1575
_BITCELL_WIDTH: float = 1.31
_BL_STRIP_W: float = 0.14


def _route_bl(top: gdstk.Cell, p: MacroV2Params, fp: Floorplan) -> None:
    """Extend the bitcell array's per-col BL/BR met1 strips up through
    the precharge row and down through the col_mux / sense_amp /
    write_driver rows.

    The array (C3) already emits spanning met1 strips at each column's
    BL and BR x-coordinate for y in [0, array_h]. Here we extend those
    strips into the peripheral y-range so the peripheral cells' met1
    BL/BR pins physically overlap the strip, giving Magic extraction a
    shared net.

    Only col-0 of each mux group connects to the peripheral pins at
    their specific cell-local x (see D2 in autonomous_decisions.md).
    The other 3 cols per mux group are extended to avoid DRC asymmetry
    but remain electrically isolated from their peripheral pins.
    """
    array_x, array_y = fp.positions["array"]
    array_w, array_h = fp.sizes["array"]

    prec_x, prec_y = fp.positions["precharge"]
    prec_w, prec_h = fp.sizes["precharge"]
    prec_top = prec_y + prec_h

    # Below-array stack: work out the lowest y touched by any
    # peripheral so the downward strip reaches all of them.
    below_y_min = min(
        fp.positions[name][1]
        for name in ("col_mux", "sense_amp", "write_driver")
    )

    # For each bitcell column, extend its BL/BR strips:
    #   up   from array_y + array_h to prec_top
    #   down from array_y down to below_y_min
    for col in range(p.cols):
        col_x0 = array_x + col * _BITCELL_WIDTH
        for x_offset in (_BITCELL_BL_X_OFFSET, _BITCELL_BR_X_OFFSET):
            strip_x = col_x0 + x_offset
            # Up-extension: from array top into precharge row
            draw_wire(
                top,
                start=(strip_x, array_y + array_h),
                end=(strip_x, prec_top),
                layer="met1",
                width=_BL_STRIP_W,
            )
            # Down-extension: from array bottom through col_mux/SA/WD
            draw_wire(
                top,
                start=(strip_x, below_y_min),
                end=(strip_x, array_y),
                layer="met1",
                width=_BL_STRIP_W,
            )


# ---------------------------------------------------------------------------
# C6.4 — Control signal fanout: control_logic DFF outputs -> peripheral EN pins
# ---------------------------------------------------------------------------

# DFF Q output pin (cell-local, met2). From DFF LEF:
#   PIN Q  met2 RECT 5.410 3.045 5.740 3.305
_DFF_Q_X_LOCAL: float = 5.575
_DFF_Q_Y_LOCAL: float = 3.175

# Peripheral EN pin positions (cell-local).
# Precharge p_en_bar: horizontal met3 rail, full cell width, centred at
# y = 0.28 µm in cell-local coords. Any x on the rail is a valid landing
# point (pick a position clear of BL/BR bitline stubs and the MP3 per-
# pair via2 drops at eq_poly_cx = 0.60 + k*pitch).
_PRECHARGE_EN_X_LOCAL: float = 0.50      # inside first pair's BL-BR gap
_PRECHARGE_EN_Y_LOCAL: float = 0.28      # met3 rail centre y
# Sense-amp EN: met1 at (0.615, 10.97) (centre of the RECT)
_SA_EN_X_LOCAL: float = 0.615
_SA_EN_Y_LOCAL: float = 10.970
# Write-driver EN: met1 at (1.498, 0.625) (centre of the main RECT)
_WD_EN_X_LOCAL: float = 1.498
_WD_EN_Y_LOCAL: float = 0.625

# ControlLogic DFF placement (from control_logic.py): DFFs at x=0, 6.2,
# 12.4, 18.6 (width 6.2, gap=0 per abutting std-cell convention).
_DFF_W: float = 6.2
# DFF CLK / D / Q pin positions (cell-local, all on met2). Q_N isn't
# labelled in the foundry GDS, so Magic never promotes it to a port.
_DFF_CLK_X_LOCAL: float = 1.980
_DFF_CLK_Y_LOCAL: float = 3.620
_DFF_D_X_LOCAL: float = 0.850
_DFF_D_Y_LOCAL: float = 2.820

# NAND2 pin positions (cell-local, all on li1). NAND2 placement inside
# ctrl_logic: NAND2_0 at (0, 9.545), NAND2_1 at (4.770, 9.545).
_NAND2_W: float = 4.770
_NAND2_ROW_Y: float = 9.545
_NAND2_A_X_LOCAL: float = 0.405
_NAND2_A_Y_LOCAL: float = 1.095
_NAND2_B_X_LOCAL: float = 0.405
_NAND2_B_Y_LOCAL: float = 0.555
_NAND2_Z_X_LOCAL: float = 2.635
_NAND2_Z_Y_LOCAL: float = 1.255

# Assign DFF index -> control signal name. The control block emits 4
# DFF-clocked output signals; we wire Q of each to its peripheral.
_CONTROL_SIGNAL_BY_DFF: dict[int, str] = {
    0: "clk_buf",   # DFF 0 Q — clk buffer, left unrouted for now (no sink
                    # in SA/WD/precharge in this minimal topology)
    1: "p_en_bar",  # DFF 1 Q -> precharge EN pins
    2: "s_en",      # DFF 2 Q -> sense-amp EN pins
    3: "w_en",      # DFF 3 Q -> write-driver EN pins
}


def _dff_q_absolute(
    ctrl_origin: tuple[float, float], dff_idx: int,
) -> tuple[float, float]:
    return (
        ctrl_origin[0] + dff_idx * _DFF_W + _DFF_Q_X_LOCAL,
        ctrl_origin[1] + _DFF_Q_Y_LOCAL,
    )


def _dff_clk_absolute(
    ctrl_origin: tuple[float, float], dff_idx: int,
) -> tuple[float, float]:
    return (
        ctrl_origin[0] + dff_idx * _DFF_W + _DFF_CLK_X_LOCAL,
        ctrl_origin[1] + _DFF_CLK_Y_LOCAL,
    )


def _dff_d_absolute(
    ctrl_origin: tuple[float, float], dff_idx: int,
) -> tuple[float, float]:
    return (
        ctrl_origin[0] + dff_idx * _DFF_W + _DFF_D_X_LOCAL,
        ctrl_origin[1] + _DFF_D_Y_LOCAL,
    )


def _nand2_pin_absolute(
    ctrl_origin: tuple[float, float],
    nand_idx: int,
    pin: str,
) -> tuple[float, float]:
    """Return absolute (x,y) of NAND2 pin label. `pin` in {'A','B','Z'}."""
    x_off, y_off = {
        "A": (_NAND2_A_X_LOCAL, _NAND2_A_Y_LOCAL),
        "B": (_NAND2_B_X_LOCAL, _NAND2_B_Y_LOCAL),
        "Z": (_NAND2_Z_X_LOCAL, _NAND2_Z_Y_LOCAL),
    }[pin]
    return (
        ctrl_origin[0] + nand_idx * _NAND2_W + x_off,
        ctrl_origin[1] + _NAND2_ROW_Y + y_off,
    )


def _route_control(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Route p_en_bar, s_en, w_en from control_logic DFF outputs to each
    bit's peripheral EN pin.

    Per signal:
      1. Start met2 at the DFF Q pin.
      2. L-shape run: vertical to a "rail y", then horizontal across the
         macro width to just past the last bit.
      3. Per bit: vertical met2 stub down/up to the peripheral pin x,
         then a via stack if the peripheral pin is on met1 (SA, WD).

    clk_buf has no destination in this topology (our SA/WD/precharge
    don't currently gate on clk); the DFF Q is left as an isolated pin.
    """
    ctrl_origin = fp.positions["control_logic"]
    prec_x, prec_y = fp.positions["precharge"]
    sa_x, sa_y = fp.positions["sense_amp"]
    wd_x, wd_y = fp.positions["write_driver"]

    mux_pitch = p.mux_ratio * _BITCELL_WIDTH

    # --- p_en_bar: met2 feeder from DFF Q, one via2 drop onto the
    # precharge cell's full-width met3 p_en_bar rail.
    #
    # The new precharge (Option II) exposes p_en_bar as a continuous
    # met3 rail spanning the entire cell width. A single via2 at any
    # safe x on that rail fans out to every column internally — no
    # per-bit stubs needed.
    dff_q = _dff_q_absolute(ctrl_origin, 1)
    drop_x = prec_x + _PRECHARGE_EN_X_LOCAL
    rail_y = prec_y + _PRECHARGE_EN_Y_LOCAL
    # Met2 from DFF Q to (drop_x, rail_y): L-shape (horizontal, vertical).
    draw_wire(top, start=dff_q, end=(drop_x, dff_q[1]), layer="met2")
    draw_wire(top, start=(drop_x, dff_q[1]), end=(drop_x, rail_y),
              layer="met2")
    # Via2 drop from met2 onto the met3 rail.
    draw_via_stack(top, from_layer="met2", to_layer="met3",
                   position=(drop_x, rail_y))

    # --- s_en: met2 rail, via stack to met1 at each SA EN pin ---------
    dff_q = _dff_q_absolute(ctrl_origin, 2)
    rail_y_s = sa_y + _SA_EN_Y_LOCAL + 0.3
    draw_wire(top, start=dff_q, end=(dff_q[0], rail_y_s), layer="met2")
    rail_x_end_s = sa_x + (p.bits - 1) * mux_pitch + _SA_EN_X_LOCAL + 1.0
    draw_wire(top, start=(dff_q[0], rail_y_s),
              end=(rail_x_end_s, rail_y_s), layer="met2")
    for bit in range(p.bits):
        pin_x = sa_x + bit * mux_pitch + _SA_EN_X_LOCAL
        pin_y = sa_y + _SA_EN_Y_LOCAL
        draw_wire(top, start=(pin_x, rail_y_s), end=(pin_x, pin_y),
                  layer="met2")
        # met2 -> met1 via stack to reach SA EN pin (met1)
        draw_via_stack(top, from_layer="met1", to_layer="met2",
                       position=(pin_x, pin_y))

    # --- w_en: met2 rail, via stack to met1 at each WD EN pin ---------
    dff_q = _dff_q_absolute(ctrl_origin, 3)
    rail_y_w = wd_y + _WD_EN_Y_LOCAL - 0.3   # rail below pin
    # Vertical down from DFF Q to rail (DFF is above WD)
    draw_wire(top, start=dff_q, end=(dff_q[0], rail_y_w), layer="met2")
    rail_x_end_w = wd_x + (p.bits - 1) * mux_pitch + _WD_EN_X_LOCAL + 1.0
    draw_wire(top, start=(dff_q[0], rail_y_w),
              end=(rail_x_end_w, rail_y_w), layer="met2")
    for bit in range(p.bits):
        pin_x = wd_x + bit * mux_pitch + _WD_EN_X_LOCAL
        pin_y = wd_y + _WD_EN_Y_LOCAL
        draw_wire(top, start=(pin_x, rail_y_w), end=(pin_x, pin_y),
                  layer="met2")
        draw_via_stack(top, from_layer="met1", to_layer="met2",
                       position=(pin_x, pin_y))


# ---------------------------------------------------------------------------
# C6.4b — Control logic internal wiring
# ---------------------------------------------------------------------------
#
# Connects top-level `clk`/`we`/`cs` into ctrl_logic's DFFs and NAND2s
# and wires NAND2 outputs back to DFF D inputs. Goal: give every DFF /
# NAND2 signal pin an external net so Magic promotes them to subckt
# ports at LVS extraction time.
#
# Mapping (driven by the partial "one-cycle latch" function the cell
# composition naturally implements; details don't matter for LVS so
# long as every pin has a unique net):
#   clk -> DFF0.CLK, DFF1.CLK, DFF2.CLK, DFF3.CLK
#   we  -> NAND2_0.A, NAND2_1.B
#   cs  -> NAND2_0.B, NAND2_1.A
#   NAND2_0.Z -> DFF0.D, DFF1.D
#   NAND2_1.Z -> DFF2.D, DFF3.D
#
# Routing strategy: a met3 trunk well above the ctrl_logic block for
# each signal (clk / we / cs / nand0_z / nand1_z), with via stacks down
# to the pin layer at each destination. Met3 sits above both the DFF
# row (y < 7.545) and the NAND2 row (9.545 < y < 12.235), so routes
# don't intersect internal cell metals.

# Met3 width for horizontal trunks (sky130 met3 min width = 0.30 um,
# min spacing = 0.30 um).  Via2 landing pads are 0.33 um wide, so pitch
# must cover pad_w + min_space = 0.33 + 0.30 = 0.63.  We use 0.80 for
# margin and to allow adjacent trunks to host via stacks on the same
# x without pad-to-pad DRC.
_CTRL_TRUNK_W: float = 0.30
_CTRL_TRUNK_PITCH: float = 0.80

# Half-width used to extend trunk endpoints past feeder/dest x so the
# vertical feeder doesn't protrude past the horizontal trunk (which
# would create a 0.15-wide notch and fail met3.1 width check).
_CTRL_TRUNK_HALF_W: float = _CTRL_TRUNK_W / 2

# Z-source rails (NAND2 outputs to DFF D) live in the 2 um gap between
# the ctrl_logic top edge and the bitcell array bottom edge.  Separated
# by full pitch to stay DRC-clean against each other.
_CTRL_Z0_RAIL_Y_ABOVE_CTRL: float = 0.5
_CTRL_Z1_RAIL_Y_ABOVE_CTRL: float = 1.3


def _route_ctrl_internal(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Wire clk/we/cs into ctrl_logic and NAND2 Z back to DFF D.

    Topology — all horizontal trunks are on met3, all vertical feeders
    and dest drops are on met3 with via stacks terminating on the
    destination pin's native layer (met2 for DFFs, li1 for NAND2s).

      - clk/we/cs: trunk at a unique y ABOVE the top input pin row
        (y > pins_top_y + stub_len).  Each trunk extends only from the
        leftmost destination to its own feeder x, so trunks do not
        overlap feeders of later signals.  The top-level pin stub (met3
        at pin_x) feeds into the trunk via a short vertical extension.

      - NAND2_Z -> DFF_D: trunks in the ctrl_logic / array gap, placed
        above the ctrl_logic bbox and below the array.  Feeders via
        stack up from li1 (NAND2 Z) to met3; dest drops via stack down
        to met2 (DFF D).

    Long vertical descents on met3 pass through row_decoder / wl_driver
    regions, which contain only NAND3 / bitcell cells (no internal
    met3), so no layer conflict.
    """
    ctrl_origin = fp.positions["control_logic"]
    ctrl_x, ctrl_y = ctrl_origin
    ctrl_w, ctrl_h = fp.sizes["control_logic"]

    positions, pins_top_y, _pins_bot_y = _top_pin_layout(p, fp)

    # Top-of-pin-stub y (pin stubs span [pins_top_y, pins_top_y+stub]).
    # Rails are allocated in ascending y above the pin stubs.  Later-
    # rail signals have pins that are *east* of earlier-rail signals'
    # pins (trunks extend west-to-east, clipped at their own pin_x),
    # so a higher y keeps a trunk clear of every later feeder.
    #
    # Pin-layout order is: addr[0..N-1], clk, we, cs, din[0..B-1].
    # addr[*] pins are westmost; clk/we/cs come after them.  We
    # therefore place ctrl rails ABOVE addr rails — otherwise a
    # west-extending clk/we/cs trunk would pass over the still-rising
    # addr[*] feeders and short to them.
    top_stub_top_y = pins_top_y + _PIN_STUB_LEN

    # Slots 0..(N_addr-1): addr rails.  Slots N_addr..N_addr+2: ctrl.
    addr_slots = p.num_addr_bits if len(_SPLIT_TABLE[p.rows]) == 1 else 0
    trunk_y_clk = top_stub_top_y + (addr_slots + 1) * _CTRL_TRUNK_PITCH
    trunk_y_we = trunk_y_clk + _CTRL_TRUNK_PITCH
    trunk_y_cs = trunk_y_we + _CTRL_TRUNK_PITCH

    # Z rails in the ctrl_logic/array gap.
    ctrl_top_y = ctrl_y + ctrl_h
    z0_rail_y = ctrl_top_y + _CTRL_Z0_RAIL_Y_ABOVE_CTRL
    z1_rail_y = ctrl_top_y + _CTRL_Z1_RAIL_Y_ABOVE_CTRL

    # ------------------------------------------------------------------
    # Helper: connect a top-level met3 pin at (pin_x, pins_top_y) up to
    # a horizontal met3 trunk at rail_y, then back DOWN on met3 verticals
    # at each dest_x, landing on the pin layer via via stack.
    # Trunk is clipped on the right at pin_x so it doesn't cross any
    # feeder for a signal that is farther right.
    # ------------------------------------------------------------------
    def _route_from_top_pin(
        pin_x: float,
        rail_y: float,
        dests: list[tuple[float, float, str]],
        drop_x_offset: float = 0.0,
    ) -> None:
        draw_wire(
            top,
            start=(pin_x, top_stub_top_y),
            end=(pin_x, rail_y + _CTRL_TRUNK_HALF_W),
            layer="met3",
            width=_CTRL_TRUNK_W,
        )
        # Trunk must span far enough west to cover the drop columns
        # (dest_x + offset) not just the pin positions themselves.
        west_x = min(dest_x + drop_x_offset for dest_x, _, _ in dests)
        west = west_x - _CTRL_TRUNK_HALF_W
        draw_wire(
            top,
            start=(west, rail_y),
            end=(pin_x + _CTRL_TRUNK_HALF_W, rail_y),
            layer="met3",
            width=_CTRL_TRUNK_W,
        )
        for dest_x, dest_y, dest_layer in dests:
            _drop_to_dest(dest_x, dest_y, dest_layer, rail_y, drop_x_offset)

    def _route_internal_trunk(
        src_x: float,
        src_y: float,
        src_layer: str,
        rail_y: float,
        dests: list[tuple[float, float, str]],
        feeder_jog_x: float | None = None,
        drop_x_offset: float = 0.0,
    ) -> None:
        """Route source pin via met3 up to rail_y, fan out to dests.

        If `feeder_jog_x` is given, the feeder first jogs horizontally
        from src_x to feeder_jog_x at the src_y level (on met3), then
        rises vertically at the jogged x.  Use this to keep the feeder
        clear of other rails' landing pads that sit directly above the
        src pin.
        """
        # Via stack at src pin up to met3.
        draw_via_stack(
            top, from_layer=src_layer, to_layer="met3",
            position=(src_x, src_y),
        )
        # Optional horizontal jog on met3 at src_y to move the feeder
        # column to a safe x before rising to the trunk.
        feeder_x = feeder_jog_x if feeder_jog_x is not None else src_x
        if feeder_jog_x is not None and feeder_jog_x != src_x:
            x_lo = min(src_x, feeder_jog_x) - _CTRL_TRUNK_HALF_W
            x_hi = max(src_x, feeder_jog_x) + _CTRL_TRUNK_HALF_W
            draw_wire(
                top,
                start=(x_lo, src_y),
                end=(x_hi, src_y),
                layer="met3",
                width=_CTRL_TRUNK_W,
            )
        lo_y = min(src_y, rail_y) - _CTRL_TRUNK_HALF_W
        hi_y = max(src_y, rail_y) + _CTRL_TRUNK_HALF_W
        draw_wire(
            top,
            start=(feeder_x, lo_y),
            end=(feeder_x, hi_y),
            layer="met3",
            width=_CTRL_TRUNK_W,
        )
        all_xs = [feeder_x] + [d[0] + drop_x_offset for d in dests]
        west = min(all_xs) - _CTRL_TRUNK_HALF_W
        east = max(all_xs) + _CTRL_TRUNK_HALF_W
        draw_wire(
            top,
            start=(west, rail_y),
            end=(east, rail_y),
            layer="met3",
            width=_CTRL_TRUNK_W,
        )
        for dest_x, dest_y, dest_layer in dests:
            _drop_to_dest(dest_x, dest_y, dest_layer, rail_y, drop_x_offset)

    def _drop_to_dest(
        dest_x: float,
        dest_y: float,
        dest_layer: str,
        rail_y: float,
        drop_x_offset: float = 0.0,
    ) -> None:
        """Drop from met3 rail at (dest_x+offset, rail_y) down to a
        pin at (dest_x, dest_y) on `dest_layer`.

        Always uses met2 as the intermediate vertical layer.  NAND2_dec
        cells use met1 internally for VDD/GND power pins; running a
        met1 vertical at any x inside a NAND2 cell footprint would
        merge with those power pins (observed shorting cs->VPWR and
        we->VGND).  Met2 has no NAND2 internal geometry, so met2
        verticals pass through safely.  A final via stack at the pin
        (li1->met2) provides the local connection.
        """
        drop_x = dest_x + drop_x_offset
        draw_via_stack(
            top, from_layer="met2", to_layer="met3",
            position=(drop_x, rail_y),
        )
        from rekolektion.macro_v2.sky130_drc import layer_min_width
        half_m2 = layer_min_width("met2") / 2
        lo_y = min(dest_y, rail_y) - half_m2
        hi_y = max(dest_y, rail_y) + half_m2
        draw_wire(
            top,
            start=(drop_x, lo_y),
            end=(drop_x, hi_y),
            layer="met2",
        )
        if drop_x_offset != 0.0:
            jog_lo = min(drop_x, dest_x) - half_m2
            jog_hi = max(drop_x, dest_x) + half_m2
            draw_wire(
                top,
                start=(jog_lo, dest_y),
                end=(jog_hi, dest_y),
                layer="met2",
            )
        if dest_layer == "li1":
            draw_via_stack(
                top, from_layer="li1", to_layer="met2",
                position=(dest_x, dest_y),
            )
        elif dest_layer == "met2":
            pass  # met2 vertical already overlaps the pin
        else:
            draw_via_stack(
                top, from_layer=dest_layer, to_layer="met2",
                position=(dest_x, dest_y),
            )

    # clk → DFF[0..3].CLK -------------------------------------------------
    clk_pin_x, _ = positions["clk"]
    clk_dests = [
        (*_dff_clk_absolute(ctrl_origin, i), "met2") for i in range(4)
    ]
    _route_from_top_pin(clk_pin_x, trunk_y_clk, clk_dests)

    # we → both NAND2 A pins; cs → both NAND2 B pins.  NAND2 A and B
    # share cell-local x (0.405), so without a lateral offset the
    # we and cs met1 drops would overlap at every NAND2 column and
    # short the two nets.  We stagger: we drops +0.8 east, cs drops
    # -0.8 west, with short met1 jogs on the pin layer to reach the
    # actual pin coordinate.
    we_pin_x, _ = positions["we"]
    we_dests = [
        (*_nand2_pin_absolute(ctrl_origin, 0, "A"), "li1"),
        (*_nand2_pin_absolute(ctrl_origin, 1, "A"), "li1"),
    ]
    _route_from_top_pin(we_pin_x, trunk_y_we, we_dests, drop_x_offset=+0.8)

    cs_pin_x, _ = positions["cs"]
    cs_dests = [
        (*_nand2_pin_absolute(ctrl_origin, 0, "B"), "li1"),
        (*_nand2_pin_absolute(ctrl_origin, 1, "B"), "li1"),
    ]
    _route_from_top_pin(cs_pin_x, trunk_y_cs, cs_dests, drop_x_offset=-0.8)

    # NAND2_0.Z → DFF_0.D, DFF_1.D ---------------------------------------
    z0_x, z0_y = _nand2_pin_absolute(ctrl_origin, 0, "Z")
    z0_dests = [
        (*_dff_d_absolute(ctrl_origin, 0), "met2"),
        (*_dff_d_absolute(ctrl_origin, 1), "met2"),
    ]
    _route_internal_trunk(z0_x, z0_y, "li1", z0_rail_y, z0_dests)

    # NAND2_1.Z → DFF_2.D, DFF_3.D ---------------------------------------
    # Jog the feeder ~1 um west so the vertical column doesn't run
    # within spacing distance of z0's DFF_1.D landing pad at
    # (ctrl_x + 7.05, z0_rail_y).
    z1_x, z1_y = _nand2_pin_absolute(ctrl_origin, 1, "Z")
    z1_feeder_jog = z1_x + 1.0
    z1_dests = [
        (*_dff_d_absolute(ctrl_origin, 2), "met2"),
        (*_dff_d_absolute(ctrl_origin, 3), "met2"),
    ]
    _route_internal_trunk(
        z1_x, z1_y, "li1", z1_rail_y, z1_dests,
        feeder_jog_x=z1_feeder_jog,
    )


# ---------------------------------------------------------------------------
# C6.4c — Address fan-in to the row_decoder NAND3 column
# ---------------------------------------------------------------------------
#
# For the single-predecoder case (rows <= 8, split = (k,)) the row
# decoder is just a vertical column of num_rows NAND_k cells — each
# NAND takes k address-bit inputs on its A/B/C/D pins.  The structural
# SPICE reference ties all instances' inputs to the same addr[0..k-1]
# lines (simplified decoder; real selection happens downstream).  We
# mirror that in GDS by driving all A pins with addr[0], all B with
# addr[1], all C with addr[2].
#
# NAND_dec pins (A, B, C) are on li1 at cell-local positions that are
# 0.55 um apart in x and 0.36 um apart in y — too tight to run three
# met3 landings at each pin.  The route therefore goes:
#
#   top-pin (met3) -> high trunk (met3) -> sidebar vertical (met3 at
#   x < row_decoder.left) -> per-row via stack met3->li1 -> short li1
#   horizontal east into the NAND_dec pin.
#
# NAND_dec pin cell-local coords (LEF / label dump):
#   A: (1.265, 0.410)   B: (0.715, 0.770)   C: (0.165, 1.130)
# X-mirror on odd rows (matching bitcell / wl_driver tiling).
_NAND_DEC_A_X_LOCAL: float = 1.265
_NAND_DEC_A_Y_LOCAL: float = 0.410
_NAND_DEC_B_X_LOCAL: float = 0.715
_NAND_DEC_B_Y_LOCAL: float = 0.770
_NAND_DEC_C_X_LOCAL: float = 0.165
_NAND_DEC_C_Y_LOCAL: float = 1.130

# Sidebar rail x-offsets (west of row_decoder's left edge).  Each addr
# signal gets a unique vertical rail, spaced > _CTRL_TRUNK_PITCH apart.
_ADDR_SIDEBAR_X_OFFSETS: tuple[float, ...] = (1.5, 3.0, 4.5)


def _nand_dec_pin_absolute(
    dec_origin: tuple[float, float],
    row: int,
    pin: str,
    k_fanin: int,
) -> tuple[float, float]:
    """Return absolute (x, y) of the NAND_k input pin label on the
    NAND cell for `row` in a vertically-tiled NAND column at `dec_origin`.

    Honours the X-mirror applied to odd rows (same tiling convention
    used in the wl_driver and row_decoder row_decoder NAND column).
    Only A/B/C inputs are supported (this helper is for addr fan-in).
    """
    if pin not in ("A", "B", "C"):
        raise ValueError(f"unsupported NAND input pin {pin!r}")
    x_local, y_local = {
        "A": (_NAND_DEC_A_X_LOCAL, _NAND_DEC_A_Y_LOCAL),
        "B": (_NAND_DEC_B_X_LOCAL, _NAND_DEC_B_Y_LOCAL),
        "C": (_NAND_DEC_C_X_LOCAL, _NAND_DEC_C_Y_LOCAL),
    }[pin]
    dec_x, dec_y = dec_origin
    if row % 2 == 0:
        abs_y = dec_y + row * _NAND_DEC_PITCH + y_local
    else:
        abs_y = dec_y + (row + 1) * _NAND_DEC_PITCH - y_local
    return (dec_x + x_local, abs_y)


def _route_addr(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Route top-level addr[0..k-1] pins to NAND_dec A/B/C inputs for
    a single-predecoder row decoder.  Skipped for multi-predecoder
    configs (rows > 8), where the full decoder already has its own
    internal wiring."""
    split = _SPLIT_TABLE[p.rows]
    if len(split) != 1:
        # Multi-predecoder not wired yet — LVS for those macros will
        # fall short of closure until the predecoder fan-in is routed.
        # Tracked separately from sram_test_tiny (rows=8).
        return
    k = split[0]
    if k not in (2, 3):
        return  # NAND4 unsupported here (no 4-pin routing)

    positions, pins_top_y, _ = _top_pin_layout(p, fp)
    dec_origin = fp.positions["row_decoder"]
    dec_x, _dec_y = dec_origin

    top_stub_top_y = pins_top_y + _PIN_STUB_LEN
    # Addr trunks sit BELOW the ctrl_logic clk/we/cs stack (closer to
    # the pin row).  Because addr[*] pins are westmost in the pin
    # layout, the addr trunks extend east only to addr_pin_x+half,
    # never crossing clk/we/cs feeders that rise at higher x.  Putting
    # ctrl trunks above them avoids the inverse conflict.
    addr_trunk_y_base = top_stub_top_y + _CTRL_TRUNK_PITCH

    # Sidebar rails live just west of the row_decoder's left edge.
    # One x per addr signal, all met3, spaced so via-stack landing pads
    # don't collide.
    pin_names = ["A", "B", "C"][:k]
    for i, pin_name in enumerate(pin_names):
        addr_pin_key = f"addr[{i}]"
        addr_pin_x, _ = positions[addr_pin_key]
        rail_y = addr_trunk_y_base + i * _CTRL_TRUNK_PITCH
        sidebar_x = dec_x - _ADDR_SIDEBAR_X_OFFSETS[i]

        # Pin-y list across all rows for this address input
        per_row_pin_ys = [
            _nand_dec_pin_absolute(dec_origin, r, pin_name, k)[1]
            for r in range(p.rows)
        ]
        # Pin-x is the same for every row (cell-local x constant)
        nand_pin_x = _nand_dec_pin_absolute(dec_origin, 0, pin_name, k)[0]

        # --- (1) met3 feeder from top pin up to the addr trunk -------
        draw_wire(
            top,
            start=(addr_pin_x, top_stub_top_y),
            end=(addr_pin_x, rail_y + _CTRL_TRUNK_HALF_W),
            layer="met3",
            width=_CTRL_TRUNK_W,
        )
        # --- (2) met3 trunk from addr_pin_x LEFT to sidebar_x --------
        trunk_west = sidebar_x - _CTRL_TRUNK_HALF_W
        trunk_east = addr_pin_x + _CTRL_TRUNK_HALF_W
        draw_wire(
            top,
            start=(trunk_west, rail_y),
            end=(trunk_east, rail_y),
            layer="met3",
            width=_CTRL_TRUNK_W,
        )
        # --- (3) sidebar vertical on met3 covering all pin y's -------
        rail_bot = min(per_row_pin_ys) - _CTRL_TRUNK_HALF_W
        rail_top = rail_y + _CTRL_TRUNK_HALF_W
        draw_wire(
            top,
            start=(sidebar_x, rail_bot),
            end=(sidebar_x, rail_top),
            layer="met3",
            width=_CTRL_TRUNK_W,
        )
        # --- (4)(5) per-row: via stack + li1 horizontal into pin -----
        for r, pin_y in enumerate(per_row_pin_ys):
            draw_via_stack(
                top, from_layer="li1", to_layer="met3",
                position=(sidebar_x, pin_y),
            )
            # li1 horizontal from sidebar to pin
            li_lo = sidebar_x - layer_min_width_half("li1")
            li_hi = nand_pin_x + layer_min_width_half("li1")
            draw_wire(
                top,
                start=(li_lo, pin_y),
                end=(li_hi, pin_y),
                layer="li1",
            )


def layer_min_width_half(layer: str) -> float:
    """Half the min width for a layer (for endpoint extension)."""
    from rekolektion.macro_v2.sky130_drc import layer_min_width
    return layer_min_width(layer) / 2


# ---------------------------------------------------------------------------
# C6.5 — Top-level pins + power grid
# ---------------------------------------------------------------------------

# OpenLane/OpenROAD reads LEF pins at their drawn rectangles. We follow
# the OpenRAM convention for SRAM macros: signal pins on met3 as short
# vertical stubs at interior x-positions, at the top (inputs) and
# bottom (outputs) edges of the macro. Power (VPWR/VGND) on met4 as
# horizontal straps spanning the full width.
_PIN_LAYER: str = "met3"
_PIN_STUB_LEN: float = 0.9        # vertical extent of a pin stub (um)
_PIN_STUB_W: float = 0.30         # met3 min width
_PIN_PITCH: float = 1.0           # min centre-to-centre between pins

_PDN_STRAP_W: float = 1.6         # met4 power strap width
_PDN_STRAP_LAYER: str = "met4"
_PDN_STRAP_MARGIN: float = 2.0    # gap between PDN strap and nearest block


def _top_pin_layout(
    p: MacroV2Params,
    fp: Floorplan,
) -> tuple[dict[str, tuple[float, float]], float, float]:
    """Compute the (x, y) of each top-level input/output pin.

    Returns (name -> position, pins_top_y, pins_bot_y).  Positions point
    at the *bottom* of the met3 stub (i.e. the tip facing the macro
    interior), which is the natural route entry point.
    """
    array_x, _ = fp.positions["array"]
    array_w, _ = fp.sizes["array"]
    prec_y, prec_h = fp.positions["precharge"][1], fp.sizes["precharge"][1]
    prec_top = prec_y + prec_h
    pins_top_y = prec_top + _PDN_STRAP_MARGIN + _PDN_STRAP_W + _PDN_STRAP_MARGIN
    wd_bot = fp.positions["write_driver"][1]
    pins_bot_y = wd_bot - _PDN_STRAP_MARGIN - _PDN_STRAP_W - _PDN_STRAP_MARGIN

    input_names: list[str] = []
    for i in range(p.num_addr_bits):
        input_names.append(f"addr[{i}]")
    input_names += ["clk", "we", "cs"]
    for i in range(p.bits):
        input_names.append(f"din[{i}]")

    x0 = array_x + 1.0
    x_end = array_x + array_w - 1.0
    step = (x_end - x0) / max(len(input_names) - 1, 1) if len(input_names) > 1 else 0.0
    positions: dict[str, tuple[float, float]] = {}
    for i, name in enumerate(input_names):
        positions[name] = (x0 + i * step, pins_top_y)

    x0b = array_x + 1.0
    stepb = (x_end - x0b) / max(p.bits - 1, 1) if p.bits > 1 else 0.0
    for i in range(p.bits):
        positions[f"dout[{i}]"] = (x0b + i * stepb, pins_bot_y)
    return positions, pins_top_y, pins_bot_y


def _place_top_pins(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Place LEF-style met3 pins for addr, din, dout, clk, we, cs at
    the top (inputs) and bottom (outputs) of the macro.
    """
    positions, pins_top_y, pins_bot_y = _top_pin_layout(p, fp)

    for name, (px, _) in positions.items():
        if name.startswith("dout"):
            continue
        rect = (
            px - _PIN_STUB_W / 2,
            pins_top_y,
            px + _PIN_STUB_W / 2,
            pins_top_y + _PIN_STUB_LEN,
        )
        draw_wire(
            top,
            start=(px, pins_top_y),
            end=(px, pins_top_y + _PIN_STUB_LEN),
            layer=_PIN_LAYER,
            width=_PIN_STUB_W,
        )
        draw_pin_with_label(top, text=name, layer=_PIN_LAYER, rect=rect)

    for i in range(p.bits):
        px, _ = positions[f"dout[{i}]"]
        rect = (
            px - _PIN_STUB_W / 2,
            pins_bot_y,
            px + _PIN_STUB_W / 2,
            pins_bot_y + _PIN_STUB_LEN,
        )
        draw_wire(
            top,
            start=(px, pins_bot_y),
            end=(px, pins_bot_y + _PIN_STUB_LEN),
            layer=_PIN_LAYER,
            width=_PIN_STUB_W,
        )
        draw_pin_with_label(top, text=f"dout[{i}]", layer=_PIN_LAYER, rect=rect)


def _place_power_grid(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Two horizontal met4 straps — VPWR at top, VGND at bottom —
    spanning the entire macro width. Each strap is pinned and labeled.
    """
    # Macro x-extent: from leftmost (row_decoder / control_logic) to
    # rightmost (array right edge).
    xs_lo = min(x for x, _ in fp.positions.values())
    xs_hi = max(
        fp.positions[n][0] + fp.sizes[n][0] for n in fp.positions
    )
    strap_x0 = xs_lo - 1.0
    strap_x1 = xs_hi + 1.0

    # VPWR: above precharge top
    prec_y, prec_h = fp.positions["precharge"][1], fp.sizes["precharge"][1]
    vpwr_y = prec_y + prec_h + _PDN_STRAP_MARGIN + _PDN_STRAP_W / 2
    draw_pdn_strap(
        top, orientation="horizontal",
        center_coord=vpwr_y,
        span_start=strap_x0, span_end=strap_x1,
        layer=_PDN_STRAP_LAYER, width=_PDN_STRAP_W,
    )
    vpwr_rect = (
        strap_x0, vpwr_y - _PDN_STRAP_W / 2,
        strap_x0 + 1.0, vpwr_y + _PDN_STRAP_W / 2,
    )
    draw_pin_with_label(top, text="VPWR", layer=_PDN_STRAP_LAYER,
                        rect=vpwr_rect)

    # VGND: below write_driver bottom
    wd_bot = fp.positions["write_driver"][1]
    vgnd_y = wd_bot - _PDN_STRAP_MARGIN - _PDN_STRAP_W / 2
    draw_pdn_strap(
        top, orientation="horizontal",
        center_coord=vgnd_y,
        span_start=strap_x0, span_end=strap_x1,
        layer=_PDN_STRAP_LAYER, width=_PDN_STRAP_W,
    )
    vgnd_rect = (
        strap_x0, vgnd_y - _PDN_STRAP_W / 2,
        strap_x0 + 1.0, vgnd_y + _PDN_STRAP_W / 2,
    )
    draw_pin_with_label(top, text="VGND", layer=_PDN_STRAP_LAYER,
                        rect=vgnd_rect)


# ---------------------------------------------------------------------------
# FIX-A: proper macro PDN (not a flatten-based hack)
# ---------------------------------------------------------------------------

# Horizontal met2 power rails along the top and bottom of the macro,
# connected by vertical met3 straps at regular intervals. li1→met1→met2
# via stacks tap into foundry-bitcell internal power rails.
_PDN_MET2_RAIL_W: float = 0.40          # horizontal met2 edge rails
_PDN_MET3_STRAP_W: float = 0.30         # vertical met3 straps
_PDN_MET3_STRAP_PITCH_UM: float = 10.48  # 2 mux groups at mux=4


def _draw_power_network(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Build the macro's internal power distribution network.

    Two horizontal met2 rails (VPWR top, VGND bottom) span the full
    macro width at the positions that match the LEF pin stub Y. A
    sequence of vertical met3 straps tie the rails together across
    the macro; li1→met2 via stacks drop into the bitcell array every
    few mux groups so foundry-cell internal rails are anchored.

    The goal: every LEF VPWR/VGND pin has at least one continuous
    metal path to every other same-net pin in the macro (what PSM
    checks). Full IR-drop fidelity requires more straps and is
    future work; this covers macro-level connectivity.
    """
    # Macro extent in assembler coords — matches what the LEF
    # generator uses so the met2 rails sit exactly at the LEF power
    # pin stub Y (assembler_ys_hi for top pins, assembler_ys_lo for
    # bottom pins).
    xs_lo = min(x for x, _ in fp.positions.values()) - 1.0
    xs_hi = max(
        fp.positions[n][0] + fp.sizes[n][0] for n in fp.positions
    ) + 1.0
    prec_top = fp.positions["precharge"][1] + fp.sizes["precharge"][1]
    wd_bot = fp.positions["write_driver"][1]
    pins_top_y = (
        prec_top + _PDN_STRAP_MARGIN + _PDN_STRAP_W + _PDN_STRAP_MARGIN
    )
    pins_bot_y = (
        wd_bot - _PDN_STRAP_MARGIN - _PDN_STRAP_W - _PDN_STRAP_MARGIN
    )
    # LEF coord ys_hi = pins_top_y + pin_stub_len + 0.5 (assembler frame);
    # ys_lo = pins_bot_y - 0.5.
    # Snap the top rail y UP to the nearest sky130_fd_sc_hd stdcell row
    # pitch (2.72 μm) so the LEF macro's top edge — and the met2 VPWR
    # pin that sits on it — align with a chip-level stdcell row
    # boundary (keeps `lef_generator._snap_macro_h_to_row_pitch` in
    # sync). Without this snap, 81/81 macro VPWRs come up PSM-0069
    # "Unconnected instance" at chip-level PDN.
    import math as _math
    _ROW_PITCH = 2.72
    _ys_lo = pins_bot_y - 0.5
    _ys_hi_tight = pins_top_y + _PIN_STUB_LEN + 0.5
    _macro_h_snapped = _math.ceil((_ys_hi_tight - _ys_lo) / _ROW_PITCH) * _ROW_PITCH
    # Offset rails inward by strap_half so the 1.6-μm-wide met4 PDN
    # straps sit FULLY INSIDE the macro bbox (required by OpenROAD's
    # PDN-0232 check — the default macro grid template looks for
    # shapes within the macro body, not straddling its boundary).
    _strap_half = _PDN_STRAP_W / 2
    top_rail_y = _ys_lo + _macro_h_snapped - _strap_half
    bot_rail_y = _ys_lo + _strap_half

    rail_half = _PDN_MET2_RAIL_W / 2
    from rekolektion.macro_v2.sky130_drc import GDS_LAYER
    met2_l, met2_d = GDS_LAYER["met2"]
    met3_l, met3_d = GDS_LAYER["met3"]

    # Top met2 rail (VPWR net). Pin stubs straddle the macro boundary;
    # the rail sits centred on the pin Y. Full width for connectivity.
    top.add(gdstk.rectangle(
        (xs_lo, top_rail_y - rail_half),
        (xs_hi, top_rail_y + rail_half),
        layer=met2_l, datatype=met2_d,
    ))
    # Bottom met2 rail (VGND net)
    top.add(gdstk.rectangle(
        (xs_lo, bot_rail_y - rail_half),
        (xs_hi, bot_rail_y + rail_half),
        layer=met2_l, datatype=met2_d,
    ))

    # Vertical met4 straps — chip-PDN interface layer.
    #
    # Why vertical (not horizontal): chip PDN has met4 vstripes +
    # met5 hstripes.  OpenROAD's default macro grid template has
    # exactly one `add_pdn_connect -layers "met4 met5"` rule, and
    # `Grid::getIntersections` (OpenROAD src/pdn/src/grid.cpp:521)
    # creates a via wherever a met4 shape overlaps a met5 shape on
    # the same net.  A horizontal met4 strap is only 1.6 μm tall in
    # y; chip met5 hstripes at 153.18 μm pitch almost never fall
    # within that narrow band, so no intersections → no vias → every
    # macro comes up PDN-0232 "does not contain any shapes or vias"
    # (observed 81/81 on RUN_2026-04-20_*).
    #
    # Full-height vertical straps are crossed by every chip met5
    # hstripe whose y falls inside the macro's y range — guaranteed
    # intersections → guaranteed vias.  Matches OpenRAM sky130 SRAM
    # reference LEF pattern.
    #
    # Strap distribution (OpenRAM-inspired):
    #   - 1 VPWR vertical strap at macro x-center, via-connected only
    #     to the top met2 VPWR rail at y=top_rail_y.  Does NOT via
    #     to bottom met2 rail (would short VPWR to VGND).
    #   - 2 VGND vertical straps at left + right edges, each via-
    #     connected only to the bottom met2 VGND rail at y=bot_rail_y.
    #
    # The floating end of each strap is physically met4 crossing the
    # wrong-net met2 rail in space, but different layers with no via
    # = no electrical connection = no short.
    macro_w = xs_hi - xs_lo
    strap_half = _PDN_STRAP_W / 2
    edge_margin = strap_half + 0.5
    vpwr_xs = [xs_lo + macro_w / 2]
    vgnd_xs = [xs_lo + edge_margin, xs_hi - edge_margin]

    for vx in vpwr_xs:
        draw_pdn_strap(
            top, orientation="vertical",
            center_coord=vx,
            span_start=bot_rail_y, span_end=top_rail_y,
            layer="met4", width=_PDN_STRAP_W,
        )
        draw_via_stack(
            top, from_layer="met2", to_layer="met4",
            position=(vx, top_rail_y),
        )

    for vx in vgnd_xs:
        draw_pdn_strap(
            top, orientation="vertical",
            center_coord=vx,
            span_start=bot_rail_y, span_end=top_rail_y,
            layer="met4", width=_PDN_STRAP_W,
        )
        draw_via_stack(
            top, from_layer="met2", to_layer="met4",
            position=(vx, bot_rail_y),
        )


