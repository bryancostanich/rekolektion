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

    # Row decoder to the left of the array, bottom aligned to y=0.
    dec_w = _DECODER_W_ESTIMATE
    positions["row_decoder"] = (
        -(dec_w + _DECODER_TO_ARRAY_GAP),
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

    # C6.5 — top-level pins + power grid.
    _place_top_pins(top, p, fp)
    _place_power_grid(top, p, fp)

    lib.add(top)
    return lib


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
    """Wire row-decoder NAND output to array WL poly for every row.

    Route per row:
      1. li1→met1 via stack at NAND output pin.
      2. Horizontal met1 run at NAND-output y across the decoder-array channel.
      3. Short vertical met1 jog to the array WL y (rows alternate up/down).
      4. met1→li1→poly via stack at (array_left - gap, array_wl_y) to
         land on the array's spanning WL poly strip.
    """
    dec_origin = fp.positions["row_decoder"]
    dec_size = fp.sizes["row_decoder"]
    array_origin = fp.positions["array"]

    # Which NAND column inside the decoder? For a single-predecoder split
    # it sits at x=0 inside the decoder top cell; otherwise it sits after
    # the predecoder block at x = pred_block_width + _PREDECODER_TO_NAND_GAP.
    # For tiny (rows=8, split=(3,)) the NAND column is at x=0.
    if len(_SPLIT_TABLE[p.rows]) == 1:
        nand_x_in_dec = 0.0
        k_fanin = _SPLIT_TABLE[p.rows][0]
    else:
        # Multi-predecoder case: conservative estimate 4 NAND2 widths for
        # the widest predecoder + gap. Refined when we exercise larger N.
        nand_x_in_dec = 4 * 4.77 + 2.0
        k_fanin = len(_SPLIT_TABLE[p.rows])

    array_left = array_origin[0]
    via_x = array_left - _WL_VIA_ARRAY_GAP

    for row in range(p.rows):
        nand_out_x, nand_out_y = _nand_output_absolute(
            dec_origin, nand_x_in_dec, k_fanin, row,
        )
        wl_y = _array_wl_y_absolute(array_origin[1], row)

        # (1) li1 → met1 via stack at NAND output
        draw_via_stack(
            top, from_layer="li1", to_layer="met1",
            position=(nand_out_x, nand_out_y),
        )

        # (2) Horizontal met1 run at NAND-output y
        draw_wire(
            top, start=(nand_out_x, nand_out_y),
            end=(via_x, nand_out_y), layer="met1",
        )

        # (3) Vertical met1 jog to the array WL y (only if different)
        if abs(wl_y - nand_out_y) > 1e-6:
            draw_wire(
                top, start=(via_x, nand_out_y),
                end=(via_x, wl_y), layer="met1",
            )

        # (4) met1 → poly via stack at array edge
        draw_via_stack(
            top, from_layer="poly", to_layer="met1",
            position=(via_x, wl_y),
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
# Precharge p_en_bar: met2 at (1.560, 0.125)
_PRECHARGE_EN_X_LOCAL: float = 1.560
_PRECHARGE_EN_Y_LOCAL: float = 0.125
# Sense-amp EN: met1 at (0.615, 10.97) (centre of the RECT)
_SA_EN_X_LOCAL: float = 0.615
_SA_EN_Y_LOCAL: float = 10.970
# Write-driver EN: met1 at (1.498, 0.625) (centre of the main RECT)
_WD_EN_X_LOCAL: float = 1.498
_WD_EN_Y_LOCAL: float = 0.625

# ControlLogic DFF placement (from control_logic.py): DFFs at x=0, 6.2,
# 12.4, 18.6 (width 6.2, gap=0 per abutting std-cell convention).
_DFF_W: float = 6.2

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

    # --- p_en_bar: met2 end to end ------------------------------------
    dff_q = _dff_q_absolute(ctrl_origin, 1)
    # Rail runs through the precharge row's lower band (empty of
    # internal met2 below the en_bar pin centre at y=0.125 local).
    rail_y = prec_y + _PRECHARGE_EN_Y_LOCAL + 0.2  # 0.2 um above pin centre
    # Vertical run up from DFF Q to rail
    draw_wire(top, start=dff_q, end=(dff_q[0], rail_y), layer="met2")
    # Horizontal run to last bit + margin
    rail_x_end = prec_x + (p.bits - 1) * mux_pitch + _PRECHARGE_EN_X_LOCAL + 1.0
    draw_wire(top, start=(dff_q[0], rail_y), end=(rail_x_end, rail_y),
              layer="met2")
    for bit in range(p.bits):
        pin_x = prec_x + bit * mux_pitch + _PRECHARGE_EN_X_LOCAL
        pin_y = prec_y + _PRECHARGE_EN_Y_LOCAL
        draw_wire(top, start=(pin_x, rail_y), end=(pin_x, pin_y),
                  layer="met2")

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


def _place_top_pins(
    top: gdstk.Cell,
    p: MacroV2Params,
    fp: Floorplan,
) -> None:
    """Place LEF-style met3 pins for addr, din, dout, clk, we, cs at
    the top (inputs) and bottom (outputs) of the macro.
    """
    array_x, array_y = fp.positions["array"]
    array_w, array_h = fp.sizes["array"]
    prec_y, prec_h = fp.positions["precharge"][1], fp.sizes["precharge"][1]
    prec_top = prec_y + prec_h

    # y positions for top-of-macro and bottom-of-macro pin rows
    pins_top_y = prec_top + _PDN_STRAP_MARGIN + _PDN_STRAP_W + _PDN_STRAP_MARGIN
    wd_bot = fp.positions["write_driver"][1]
    pins_bot_y = wd_bot - _PDN_STRAP_MARGIN - _PDN_STRAP_W - _PDN_STRAP_MARGIN

    # Layout of input pins on the top row, left to right.
    # Order: addr[0..N-1], clk, we, cs, din[0..B-1]
    input_names: list[str] = []
    for i in range(p.num_addr_bits):
        input_names.append(f"addr[{i}]")
    input_names += ["clk", "we", "cs"]
    for i in range(p.bits):
        input_names.append(f"din[{i}]")

    # Place inputs evenly across the array's x-span
    total_inputs = len(input_names)
    x0 = array_x + 1.0
    x_end = array_x + array_w - 1.0
    step = (x_end - x0) / max(total_inputs - 1, 1) if total_inputs > 1 else 0.0
    for i, name in enumerate(input_names):
        px = x0 + i * step
        rect = (
            px - _PIN_STUB_W / 2,
            pins_top_y,
            px + _PIN_STUB_W / 2,
            pins_top_y + _PIN_STUB_LEN,
        )
        # drawing met3 rect
        draw_wire(
            top,
            start=(px, pins_top_y),
            end=(px, pins_top_y + _PIN_STUB_LEN),
            layer=_PIN_LAYER,
            width=_PIN_STUB_W,
        )
        draw_pin_with_label(top, text=name, layer=_PIN_LAYER, rect=rect)

    # Outputs (dout[0..B-1]) on the bottom row
    x0b = array_x + 1.0
    stepb = (x_end - x0b) / max(p.bits - 1, 1) if p.bits > 1 else 0.0
    for i in range(p.bits):
        px = x0b + i * stepb
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
