"""Top-level SRAM macro assembler (v2).

Composes the C3 bitcell array, C4 peripherals, and C5 row decoder +
control logic into a complete, electrically-wired SRAM macro GDS.

Phase C6 builds this incrementally:
    C6.0 — MacroParams + build_floorplan   (this file at minimum)
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

from rekolektion.macro.bitcell_array import (
    BitcellArray,
    _FOUNDRY_WL_LABEL_Y,
)
from rekolektion.macro.column_mux_row import ColumnMuxRow
from rekolektion.macro.control_logic import ControlLogic
from rekolektion.macro.precharge_row import PrechargeRow
from rekolektion.macro.row_decoder import (
    RowDecoder,
    _NAND_DEC_PITCH,
    _SPLIT_TABLE,
)
from rekolektion.macro.nets_tracker import NetClass, NetsTracker
from rekolektion.macro.routing import (
    draw_label,
    draw_pdn_strap,
    draw_pin,
    draw_pin_with_label,
    draw_via_stack,
    draw_wire,
)
from rekolektion.macro.sense_amp_row import SenseAmpRow
from rekolektion.macro.wl_driver_row import WlDriverRow
from rekolektion.macro.write_driver_row import WriteDriverRow


# Inter-block gaps (um). Loose initially; tightened after C6.1 DRC sweep.
_ARRAY_TO_PERIPH_GAP: float = 1.0
_DECODER_TO_ARRAY_GAP: float = 2.0
_CONTROL_ABOVE_GAP: float = 2.0

# Peripheral row heights — authoritative values in the row modules'
# module-level constants (SA/WD/Precharge/ColMux). Importing the
# .height property would require building the row, which defeats the
# purpose of a pure-geometry floorplan. Import the constants instead.
from rekolektion.macro.sense_amp_row import _SA_HEIGHT as _SA_H  # noqa: E402
from rekolektion.macro.write_driver_row import _WD_HEIGHT as _WD_H  # noqa: E402
from rekolektion.macro.precharge_row import _PRECHARGE_HEIGHT as _PRECHARGE_H  # noqa: E402
from rekolektion.macro.column_mux_row import _COLMUX_HEIGHT_BY_MUX  # noqa: E402

# ControlLogic stack height — DFF row + inter-row gap + NAND2 row.
_CTRL_H: float = 7.545 + 2.0 + 2.69

# Row decoder width estimate.  Returns the bbox width the
# row_decoder cell will actually occupy — must match row_decoder.py's
# _build_multi_predecoder / _emit_vertical_nand_column geometry so
# the floorplan reserves enough room to the left of wl_driver.
#
# Previous fixed 25.0 µm estimate was valid only for the single-
# predecoder case (num_rows ≤ 8, a single NAND3 column ~ 7.5 µm).
# For 128-row macros the multi-predecoder block is ~66 µm wide plus
# the final NAND column (~7.5 µm) plus gaps ≈ 76 µm — placing
# wl_driver and the array at x=27 on top of that row_decoder, which
# shorts every bitcell BL/BR/WL to every row_decoder addr rail.
def _decoder_w_estimate(rows: int) -> float:
    """Width the row_decoder will draw, in µm, for `rows` rows."""
    # Must match row_decoder._build_multi_predecoder:
    #   addr_rail_x0=0.3, addr_rail_pitch=0.7
    #   pred_area_x0 = 0.3 + total_addr*0.7 + 0.5
    #   pred block right = pred_area_x0 + max(2**k for k in split) * nand_w
    #   final NAND col at pred_right + _PREDECODER_TO_NAND_GAP
    #   total width = final NAND col + nand_w_of_final + small margin
    from rekolektion.macro.row_decoder import (
        _SPLIT_TABLE, _PREDECODER_TO_NAND_GAP, _NAND_CELL_NAMES,
        _NAND_GDS_PATHS,
    )
    import gdstk as _g
    split = _SPLIT_TABLE.get(rows)
    if split is None:
        return 25.0  # fall back — unsupported row count
    if len(split) == 1:
        # Single-predecoder case: width is just the NAND column.
        k = split[0]
        nand_src = _g.read_gds(str(_NAND_GDS_PATHS[k]))
        nand_cell = next(c for c in nand_src.cells
                         if c.name == _NAND_CELL_NAMES[k])
        (bx0, _), (bx1, _) = nand_cell.bounding_box()
        return (bx1 - bx0) + 1.0
    # Multi-predecoder.
    total_addr = sum(split)
    addr_rail_pitch = 0.7
    pred_area_x0 = 0.3 + total_addr * addr_rail_pitch + 0.5
    # Peek at each stage's NAND cell width to find the widest stage.
    pred_block_w = 0.0
    for k in split:
        nand_src = _g.read_gds(str(_NAND_GDS_PATHS[k]))
        nand_cell = next(c for c in nand_src.cells
                         if c.name == _NAND_CELL_NAMES[k])
        (bx0, _), (bx1, _) = nand_cell.bounding_box()
        nand_w = bx1 - bx0
        pred_block_w = max(pred_block_w, (2 ** k) * nand_w)
    # Final column fan-in = len(split); width = that NAND cell width.
    final_fanin = len(split)
    final_src = _g.read_gds(str(_NAND_GDS_PATHS[final_fanin]))
    final_cell = next(c for c in final_src.cells
                      if c.name == _NAND_CELL_NAMES[final_fanin])
    (bx0, _), (bx1, _) = final_cell.bounding_box()
    final_nand_w = bx1 - bx0
    return (
        pred_area_x0
        + pred_block_w
        + _PREDECODER_TO_NAND_GAP
        + final_nand_w
        + 1.0  # right-edge margin
    )


@dataclass
class MacroParams:
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


def build_floorplan(p: MacroParams) -> Floorplan:
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
    # Use BitcellArray's actual pitch (bridged wrapper, 1.31 × 2.22 µm)
    # and include strap-column width.  Computing from foundry pitch
    # (1.31 × 1.58) underestimated array_h by ~40% and caused periphery
    # to be placed inside the upper rows of the bitcell array.
    _array_for_size = BitcellArray(rows=p.rows, cols=p.cols)
    array_w = _array_for_size.width
    array_h = _array_for_size.height

    positions: dict[str, tuple[float, float]] = {}
    sizes: dict[str, tuple[float, float]] = {}

    positions["array"] = (0.0, 0.0)
    sizes["array"] = (array_w, array_h)

    # Precharge row sits above the array.
    positions["precharge"] = (0.0, array_h + _ARRAY_TO_PERIPH_GAP)
    sizes["precharge"] = (array_w, _PRECHARGE_H)

    # Stack col_mux / sense_amp / write_driver below the array.
    # col_mux height is mux_ratio-specific — use the actual cell height
    # so the assembler bridge from col_mux_top to array reaches the
    # actual col_mux top (not a phantom phantom top defined by an
    # over-reserved floorplan slot).
    y = -_ARRAY_TO_PERIPH_GAP
    for name, h in (
        ("col_mux", _COLMUX_HEIGHT_BY_MUX[p.mux_ratio]),
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
    dec_w = _decoder_w_estimate(p.rows)
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
    p: MacroParams,
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

    # Pass the array's strap_interval to every periphery generator so
    # each periphery cell's column lattice matches the array (each
    # array column lands at the same physical X as the corresponding
    # periphery slot).  Without this match the BL/BR routing strips
    # connect array col c to periphery col c+(c//strap_interval) — a
    # silicon-killer column-misalignment bug.  See T2.1-PROD-F audit.
    _strap_interval = array.strap_interval

    precharge = PrechargeRow(
        bits=p.bits, mux_ratio=p.mux_ratio, name=f"pre_{name_tag}",
        strap_interval=_strap_interval,
    )
    blocks["precharge"] = (precharge, precharge.build())

    col_mux = ColumnMuxRow(
        bits=p.bits, mux_ratio=p.mux_ratio, name=f"mux_{name_tag}",
        strap_interval=_strap_interval,
    )
    blocks["col_mux"] = (col_mux, col_mux.build())

    sense_amp = SenseAmpRow(
        bits=p.bits, mux_ratio=p.mux_ratio, name=f"sa_{name_tag}",
        strap_interval=_strap_interval,
    )
    blocks["sense_amp"] = (sense_amp, sense_amp.build())

    write_driver = WriteDriverRow(
        bits=p.bits, mux_ratio=p.mux_ratio, name=f"wd_{name_tag}",
        strap_interval=_strap_interval,
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


def assemble(p: MacroParams) -> tuple[gdstk.Library, NetsTracker]:
    """Compose all C3/C4/C5 blocks into a top-level macro GDS.

    C6.1 placement only — no inter-block routing. Subsequent tasks
    (C6.2-C6.5) wire the blocks and add top-level pins + PDN.

    Returns ``(library, tracker)`` where ``tracker`` is a
    :class:`NetsTracker` recording each top-level net's polygon
    references. Pass ``tracker.write(gds_path, macro_name)`` after
    ``library.write_gds(...)`` to emit the ``<gds>.nets.json`` sidecar
    consumed by the F# rekolektion-viz tool.
    """
    fp = build_floorplan(p)
    blocks = _build_block_libraries(p)

    lib = gdstk.Library(name=f"{p.top_cell_name}_lib")
    top = gdstk.Cell(p.top_cell_name)
    tracker = NetsTracker()

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
    _route_wl(top, p, fp, tracker=tracker)

    # C6.3 — extend BL/BR met1 strips through peripheral rows.
    _route_bl(top, p, fp, tracker=tracker)

    # C6.3b — bridge muxed_BL / muxed_BR from col_mux -> sense_amp ->
    # write_driver across their abutment gaps.
    _route_muxed_bl_br(top, p, fp, tracker=tracker)

    # C6.3c — DIN and DOUT routing.
    _route_din(top, p, fp, tracker=tracker)
    _route_dout(top, p, fp, tracker=tracker)

    # C6.4 — route control signals from control_logic to peripherals.
    _route_control(top, p, fp, tracker=tracker)

    # C6.4b — wire top-level clk/we/cs into ctrl_logic DFFs/NAND2s and
    # NAND2 outputs back to DFF D inputs so Magic promotes CLK/D/A/B/Z
    # as ports on each cell during LVS extraction.
    _route_ctrl_internal(top, p, fp, tracker=tracker)

    # C6.4c — addr fan-in to row_decoder NAND_dec inputs.
    _route_addr(top, p, fp, tracker=tracker)

    # C6.5 — top-level signal pins + proper macro PDN (FIX-A).
    # _place_power_grid drew met4 straps for an old design that
    # isn't compatible with chip-level PDN; _draw_power_network
    # replaces it with met2 rails + met3 straps that align with the
    # LEF power pin stubs.
    _place_top_pins(top, p, fp, tracker=tracker)
    _draw_power_network(top, p, fp, tracker=tracker)

    # C6.6 — SRAM core marker (81/2). Waives sky130's min-width /
    # min-spacing rules inside the marker for li1/met1/met2/poly/diff,
    # which is what lets the foundry bitcell (pitch 1.31 µm, W=0.14
    # access transistors) pass DRC. Cover the bitcell array plus the
    # precharge/col_mux rows above and below it.
    _draw_sram_core_marker(top, p, fp)

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

    return lib, tracker


def _macro_shift_origin(p: MacroParams, fp: Floorplan) -> tuple[float, float]:
    """Compute the (xs_lo, ys_lo) origin used to shift the top cell so
    its bounding box begins at (0, 0).  Single source of truth for the
    formulas — `_shift_top_to_zero_origin` shifts by (-xs_lo, -ys_lo),
    the LEF generator emits pin RECTs in the same frame, and tests
    that compare against pre-shift floorplan coordinates add the
    inverse to their expected values.
    """
    xs_lo = min(x for x, _ in fp.positions.values()) - 1.0
    prec_top = fp.positions["precharge"][1] + fp.sizes["precharge"][1]
    wd_bot = fp.positions["write_driver"][1]
    pins_top_y = prec_top + _PDN_STRAP_MARGIN + _PDN_STRAP_W + _PDN_STRAP_MARGIN
    pins_bot_y = (
        wd_bot
        - _DIN_BAND_EXTENSION
        - _PDN_STRAP_MARGIN - _PDN_STRAP_W - _PDN_STRAP_MARGIN
    )
    ys_lo = pins_bot_y - 0.5
    return xs_lo, ys_lo


def _shift_top_to_zero_origin(
    top: gdstk.Cell,
    p: MacroParams,
    fp: Floorplan,
) -> None:
    """Translate every shape in `top` by (-xs_lo, -ys_lo) so the cell's
    bounding box begins at (0, 0).  Uses the same xs_lo/ys_lo formulas
    the LEF generator uses, so the GDS and LEF agree on coordinates."""
    xs_lo, ys_lo = _macro_shift_origin(p, fp)
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
    # Bridged wrapper pitch (1.31 × 2.22). Foundry pitch (1.58) was the
    # pre-Option-B value; using it here placed WL routing wires at the
    # wrong Y for every row > 0 (drift of 0.64 µm per row), so wires
    # missed the actual WL poly strip in rows ≥ 1.
    from rekolektion.macro.bitcell_array import _BRIDGED_CELL_H
    cell_h = _BRIDGED_CELL_H
    row_y0 = array_origin_y + row * cell_h
    if row % 2 == 0:
        return row_y0 + _FOUNDRY_WL_LABEL_Y
    return row_y0 + cell_h - _FOUNDRY_WL_LABEL_Y


def _route_wl(
    top: gdstk.Cell,
    p: MacroParams,
    fp: Floorplan,
    tracker: NetsTracker | None = None,
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
        # Final NAND column lives east of the predecoder block.  Must
        # match `RowDecoder._build_multi_predecoder` exactly:
        #   pred_area_x0      = addr_rail_x0 (0.3) + total_addr * pitch (0.7) + 0.5
        #   pred_block_right_x = pred_area_x0 + max(2^k * nand_w_k for k in split)
        #   nand_x            = pred_block_right_x + _PREDECODER_TO_NAND_GAP (2.0)
        #
        # An earlier hard-coded value (4 * 4.77 + 2.0 = 21.08) was sized
        # for a different predecoder layout (4 NAND2s wide).  After the
        # multi-predecoder rewrite the predecoder block grew to 8 NAND3s
        # wide, so the stale offset landed every dec_out via stack INSIDE
        # the stage-2 cells (cell-local x≈28 cell-local instead of x≈68);
        # the parent's via stacks shorted onto stage-2 Z li1 strips,
        # chaining dec_out_0..dec_out_127 through 8 stage-2 outputs and
        # creating 95 spurious `merge dec_out_<i> dec_out_<i+1>` lines
        # in the parent .ext.
        from rekolektion.macro.row_decoder import _PREDECODER_TO_NAND_GAP
        # Foundry NAND cell widths (matches row_decoder.py's runtime
        # bbox computation): NAND2 = 4.77 µm, NAND3 = 7.53 µm.
        _NAND_W = {2: 4.77, 3: 7.53}
        split = _SPLIT_TABLE[p.rows]
        total_addr = sum(split)
        pred_area_x0 = 0.3 + total_addr * 0.7 + 0.5
        pred_block_right_x = pred_area_x0 + max(
            (2 ** k) * _NAND_W[k] for k in split
        )
        nand_x_in_dec = pred_block_right_x + _PREDECODER_TO_NAND_GAP
        k_fanin = len(split)

    # WL driver row (WlDriverRow) places one NAND3_dec per row at
    # (0, row*1.58) cell-local, with X-mirror on odd rows (same pattern
    # as the row decoder). Get pin positions from the class.
    wld = WlDriverRow(num_rows=p.rows)

    array_left = array_origin[0]
    # Stagger the poly→met1 via stack x by row parity.  The poly pad
    # of the via stack is 0.43 µm × 0.43 µm; adjacent rows' wl_y values
    # differ by only 0.39 µm (= 1.58 cell pitch − 2*FOUNDRY_WL_LABEL_Y).
    # If both parities used the same x, the poly pads would overlap by
    # 40 nm, merging wl[0]↔wl[1], wl[2]↔wl[3], … in the extracted
    # netlist (visible at parent .ext as `merge wl_driver_0/wl_1
    # wl_driver_0/wl_0`).  Offset odd rows west by 0.7 µm so poly pads
    # sit at distinct x columns and never overlap; the met1 wire
    # stretches an extra 0.7 µm to reach the new pad x — the pads are
    # still well east of wl_driver (>2 µm clearance) and well west of
    # the array's first cell so no other geometry conflicts.
    _WL_VIA_X_ODD_OFFSET: float = 0.7
    via_x_even = array_left - _WL_VIA_ARRAY_GAP
    via_x_odd = via_x_even - _WL_VIA_X_ODD_OFFSET

    for row in range(p.rows):
        via_x_at_array = via_x_even if row % 2 == 0 else via_x_odd
        # --- Segment 1: decoder Z → WL driver A ---
        dec_out_x, dec_out_y = _nand_output_absolute(
            dec_origin, nand_x_in_dec, k_fanin, row,
        )
        wld_a_local = wld.a_pin_absolute(row)
        wld_a_x = wld_origin[0] + wld_a_local[0]
        wld_a_y = wld_origin[1] + wld_a_local[1]

        dec_out_net = f"dec_out[{row}]"
        draw_via_stack(
            top, from_layer="li1", to_layer="met1",
            position=(dec_out_x, dec_out_y),
            tracker=tracker, net=dec_out_net,
        )
        draw_wire(
            top, start=(dec_out_x, dec_out_y),
            end=(wld_a_x, dec_out_y), layer="met1",
            tracker=tracker, net=dec_out_net,
        )
        if abs(wld_a_y - dec_out_y) > 1e-6:
            draw_wire(
                top, start=(wld_a_x, dec_out_y),
                end=(wld_a_x, wld_a_y), layer="met1",
                tracker=tracker, net=dec_out_net,
            )
        draw_via_stack(
            top, from_layer="li1", to_layer="met1",
            position=(wld_a_x, wld_a_y),
            tracker=tracker, net=dec_out_net,
        )
        # Label the decoder→wl_driver wire as dec_out_{row} so the
        # extracted net matches the reference SPICE's internal signal
        # name.  Without this label, Magic names the net by whatever
        # subcell port it encounters first (e.g. `wl_driver_0/sky130_
        # fd_bd_sram__openram_sp_nand3_dec_61/A`), causing LVS pin-
        # matching to fail even though topology is correct.
        draw_label(
            top, text=f"dec_out_{row}", layer="met1",
            position=(dec_out_x + 0.5, dec_out_y),
        )

        # --- Segment 2: WL driver Z → array WL poly ---
        wld_z_local = wld.z_pin_absolute(row)
        wld_z_x = wld_origin[0] + wld_z_local[0]
        wld_z_y = wld_origin[1] + wld_z_local[1]
        wl_y = _array_wl_y_absolute(array_origin[1], row)

        wl_net = f"WL[{row}]"
        draw_via_stack(
            top, from_layer="li1", to_layer="met1",
            position=(wld_z_x, wld_z_y),
            tracker=tracker, net=wl_net,
        )
        draw_wire(
            top, start=(wld_z_x, wld_z_y),
            end=(via_x_at_array, wld_z_y), layer="met1",
            tracker=tracker, net=wl_net,
        )
        if abs(wl_y - wld_z_y) > 1e-6:
            draw_wire(
                top, start=(via_x_at_array, wld_z_y),
                end=(via_x_at_array, wl_y), layer="met1",
                tracker=tracker, net=wl_net,
            )
        draw_via_stack(
            top, from_layer="poly", to_layer="met1",
            position=(via_x_at_array, wl_y),
            tracker=tracker, net=wl_net,
        )


# ---------------------------------------------------------------------------
# C6.3 — BL/BR fanout: extend array strips through peripheral rows
# ---------------------------------------------------------------------------

# Bitcell pin positions (absolute, relative to array origin at (0,0)).
# From the foundry LEF sky130_fd_bd_sram__sram_sp_cell_opt1.magic.lef:
#   BL  met1 rail  RECT 0.350 0.000 0.490 1.435  -> x-centre 0.420
#   BR  met1 rail  RECT 0.710 0.145 0.850 1.580  -> x-centre 0.780
# The peripheral cells (precharge, col_mux) were laid out with BL at
# 0.0425 and BR at 1.1575 (assumed pair-boundary positions). Each of
# them emits adapter jogs at its abutting edge to bridge internal
# 0.0425/1.1575 to external 0.420/0.780 — see `precharge._draw_bl_br_jogs`
# and `column_mux._draw_bl_br_jogs`.
_BITCELL_BL_X_OFFSET: float = 0.420
# Centralised strap-aware bit-X helper used by every routing function
# below.  Reads BitcellArray.__init__'s default `strap_interval` (single
# source of truth — same value the macro was instantiated with in
# `_build_block_libraries`).  When strap_interval > 0, a periphery slot
# at `bit_idx` (= bitcell column `bit_idx * mux_ratio`) shifts east by
# `(col // strap_interval) * strap_width` per inserted strap.
def _periphery_bit_x(bit_idx: int, mux_ratio: int) -> float:
    """X-offset for a periphery slot at `bit_idx`, strap-aware."""
    from rekolektion.macro.bitcell_array import (
        strap_aware_col_x as _col_x_fn, BitcellArray as _BCA,
    )
    _strap_interval = _BCA.__init__.__defaults__[1] if _BCA.__init__.__defaults__ else 8
    return _col_x_fn(
        bit_idx * mux_ratio, _BITCELL_WIDTH, _strap_interval, 1.41,
    )
_BITCELL_BR_X_OFFSET: float = 0.780
_BITCELL_WIDTH: float = 1.31
_BL_STRIP_W: float = 0.14


def _route_bl(top: gdstk.Cell, p: MacroParams, fp: Floorplan,
              tracker: NetsTracker | None = None) -> None:
    """Bridge BL/BR between bitcell array and peripherals.

    The bitcell's own BL/BR rails at x=0.420 / 0.780 are continuous
    through the array via cell abutment. This function adds short
    met1 bridges across the 1 µm array-to-peripheral gaps so the
    array's BL/BR connect to the precharge (above) and col_mux (below).

    BL/BR do not extend below col_mux — col_mux produces muxed_BL/
    muxed_BR on its bottom edge for sense_amp/write_driver; raw BL
    doesn't need to reach SA/WD.
    """
    array_x, array_y = fp.positions["array"]
    array_w, array_h = fp.sizes["array"]

    prec_x, prec_y = fp.positions["precharge"]
    col_mux_x, col_mux_y = fp.positions["col_mux"]
    col_mux_w, col_mux_h = fp.sizes["col_mux"]
    col_mux_top = col_mux_y + col_mux_h

    # For each column, bridge BL/BR across both array-peripheral gaps.
    # Use the array's strap-aware column X so the BL/BR strips land at
    # the actual bitcell positions (with strap_interval > 0 the array's
    # col `c` sits at c*pitch + (c // strap_interval) * strap_width,
    # NOT at uniform c*pitch).  Periphery cells are also generated
    # strap-aware so col `c` of every block sits at the same X.
    from rekolektion.macro.bitcell_array import (
        strap_aware_col_x as _col_x_fn, BitcellArray as _BCA,
    )
    # Strap_interval defaults to BitcellArray's class default; matches
    # the value the array+periphery were instantiated with in
    # `_build_block_libraries`.  Single source of truth.
    _strap_interval = _BCA.__init__.__defaults__[1] if _BCA.__init__.__defaults__ else 8
    _strap_w = 1.41
    for col in range(p.cols):
        col_x0 = array_x + _col_x_fn(col, _BITCELL_WIDTH, _strap_interval, _strap_w)
        for x_offset, side in (
            (_BITCELL_BL_X_OFFSET, "BL"),
            (_BITCELL_BR_X_OFFSET, "BR"),
        ):
            strip_x = col_x0 + x_offset
            net_name = f"{side}[{col}]"
            # Above array: from array top up to precharge bottom
            draw_wire(
                top,
                start=(strip_x, array_y + array_h),
                end=(strip_x, prec_y),
                layer="met1",
                width=_BL_STRIP_W,
                tracker=tracker, net=net_name,
            )
            # Below array: from array bottom down to col_mux top
            draw_wire(
                top,
                start=(strip_x, col_mux_top),
                end=(strip_x, array_y),
                layer="met1",
                width=_BL_STRIP_W,
                tracker=tracker, net=net_name,
            )


# ---------------------------------------------------------------------------
# C6.3b — muxed_BL / muxed_BR bridges: col_mux -> SA -> WD
# ---------------------------------------------------------------------------
#
# col_mux emits muxed_bl/muxed_br as per-bit met1 exit stubs on its
# bottom edge at cell-local x=_MUX_BL_X=0.350 / _MUX_BR_X=0.800 per
# mux group (i.e., per bit).  sense_amp and write_driver expect BL/BR
# at their own pin x's — different from 0.350/0.800.  In the 1 µm
# gaps between col_mux/SA and SA/WD we draw short L-shape met1 jogs
# that bridge from the one x to the other.

# col_mux muxed exit x per bit (cell-local, matches column_mux.py
# _MUX_BL_X / _MUX_BR_X).
_MUX_MBL_X_LOCAL: float = 0.350
_MUX_MBR_X_LOCAL: float = 0.800

# sense_amp BL/BR pin x (cell-local, per foundry LEF).  Both pins run
# from cell-local y=0 to y=11.28 on met1.
_SA_BL_X_LOCAL: float = 1.065   # x-centre of met1 rail [0.98, 1.15]
_SA_BR_X_LOCAL: float = 1.430   # x-centre of met1 rail [1.36, 1.50]

# write_driver BL/BR pin x (per LEF).  BL/BR pins sit only at the
# TOP of the WD cell (y near 10.055 cell-local).
_WD_BL_X_LOCAL: float = 0.770   # x-centre of BL pin (near top)
_WD_BR_X_LOCAL: float = 1.650


def _route_muxed_bl_br(top: gdstk.Cell, p: MacroParams, fp: Floorplan,
                       tracker: NetsTracker | None = None) -> None:
    """Bridge muxed_BL / muxed_BR from col_mux through SA into WD.

    Three segments per bit per side:
      1. mux_bottom -> SA_top (1 µm gap): L-shape met1 jog from
         mux exit x (0.350 for BL, 0.800 for BR) to SA BL/BR x
         (1.065 / 1.430).
      2. SA internal rail: the foundry sense_amp already has a
         full-cell-height BL/BR rail on met1 — no routing needed.
      3. SA_bottom -> WD_top (1 µm gap): L-shape met1 jog from
         SA BL/BR x (1.065 / 1.430) to WD BL/BR x (0.770 / 1.650).

    BL jogs run at the UPPER half of each gap, BR jogs at the LOWER
    half, so BL and BR don't short to each other at the jog y.
    """
    mux_x, mux_y = fp.positions["col_mux"]
    mux_w, mux_h = fp.sizes["col_mux"]
    mux_bottom_y = mux_y

    sa_x, sa_y = fp.positions["sense_amp"]
    sa_w, sa_h = fp.sizes["sense_amp"]
    sa_top_y = sa_y + sa_h
    sa_bottom_y = sa_y

    wd_x, wd_y = fp.positions["write_driver"]
    wd_w, wd_h = fp.sizes["write_driver"]
    wd_top_y = wd_y + wd_h

    mux_pitch = p.mux_ratio * _BITCELL_WIDTH

    # Strap-aware bit-X helper: with strap_interval > 0, every periphery
    # cell (col_mux/SA/WD) places its per-bit slot at the same column-
    # lattice X as the array's strap-shifted columns.  Use the same
    # helper as `_route_bl` so all routing aligns.
    from rekolektion.macro.bitcell_array import (
        strap_aware_col_x as _col_x_fn, BitcellArray as _BCA,
    )
    _strap_interval = _BCA.__init__.__defaults__[1] if _BCA.__init__.__defaults__ else 8
    _strap_w = 1.41

    def _bit_x(bit: int) -> float:
        """X-offset for periphery bit `bit` (relative to block origin)."""
        return _col_x_fn(bit * p.mux_ratio, _BITCELL_WIDTH, _strap_interval, _strap_w)

    def _L_jog(x_from: float, y_from: float, x_to: float, y_to: float,
               y_mid: float, net: str | None = None) -> None:
        """Draw an L-shape met1 jog from (x_from, y_from) to (x_to, y_to)
        by going vertical to y_mid, horizontal to x_to, then vertical
        to y_to."""
        draw_wire(top, start=(x_from, y_from), end=(x_from, y_mid),
                  layer="met1", width=_BL_STRIP_W,
                  tracker=tracker, net=net)
        if abs(x_to - x_from) > 1e-6:
            draw_wire(top, start=(x_from, y_mid), end=(x_to, y_mid),
                      layer="met1", width=_BL_STRIP_W,
                      tracker=tracker, net=net)
        draw_wire(top, start=(x_to, y_mid), end=(x_to, y_to),
                  layer="met1", width=_BL_STRIP_W,
                  tracker=tracker, net=net)

    # col_mux -> SA: 1 µm gap at y=[sa_top_y, mux_bottom_y] (sa_top_y
    # < mux_bottom_y).
    # Within a single bit, mux_BR x (0.800) is between mux_BL x (0.350)
    # and SA_BL x (1.065). A BL horizontal jog at an "upper" y would
    # cross BR's vertical segment (which covers y from mux_bottom down
    # to br_mid). To avoid the crossing, make BL's mid-y LOWER than
    # BR's mid-y: BL first drops past BR's vertical y range, then
    # jogs horizontally.
    mid_top = (mux_bottom_y + sa_top_y) / 2 + 0.20  # closer to mux
    mid_bot = (mux_bottom_y + sa_top_y) / 2 - 0.20  # closer to SA
    for bit in range(p.bits):
        bx = _bit_x(bit)
        # BR uses UPPER mid-y (closer to mux bottom)
        _L_jog(
            x_from=mux_x + bx + _MUX_MBR_X_LOCAL, y_from=mux_bottom_y,
            x_to=sa_x + bx + _SA_BR_X_LOCAL, y_to=sa_top_y,
            y_mid=mid_top,
            net=f"muxed_BR[{bit}]",
        )
        # BL uses LOWER mid-y (closer to SA top), below BR's vertical
        _L_jog(
            x_from=mux_x + bx + _MUX_MBL_X_LOCAL, y_from=mux_bottom_y,
            x_to=sa_x + bx + _SA_BL_X_LOCAL, y_to=sa_top_y,
            y_mid=mid_bot,
            net=f"muxed_BL[{bit}]",
        )

    # SA -> WD: 1 µm gap at y=[wd_top_y, sa_bottom_y].
    # Here BR x goes from SA (1.430) to WD (1.650), BL x goes from SA
    # (1.065) to WD (0.770). BR's x is again right of BL's, so same
    # rule: BL uses the LOWER mid-y.
    mid_top = (sa_bottom_y + wd_top_y) / 2 + 0.20
    mid_bot = (sa_bottom_y + wd_top_y) / 2 - 0.20
    for bit in range(p.bits):
        bx = _bit_x(bit)
        _L_jog(
            x_from=sa_x + bx + _SA_BR_X_LOCAL, y_from=sa_bottom_y,
            x_to=wd_x + bx + _WD_BR_X_LOCAL, y_to=wd_top_y,
            y_mid=mid_top,
            net=f"muxed_BR[{bit}]",
        )
        _L_jog(
            x_from=sa_x + bx + _SA_BL_X_LOCAL, y_from=sa_bottom_y,
            x_to=wd_x + bx + _WD_BL_X_LOCAL, y_to=wd_top_y,
            y_mid=mid_bot,
            net=f"muxed_BL[{bit}]",
        )


# ---------------------------------------------------------------------------
# C6.3c — din / dout routing
# ---------------------------------------------------------------------------

# WD DIN pin cell-local x (from foundry LEF): pin on met1 at
# x=[1.275, 1.575], y=[0.020, 0.300]. Pin centre at x=1.425, at the
# BOTTOM of the WD cell (y very small).
#
# Y_LOCAL placed at the pin's *low* y so the via-stack pad stays well
# below the wd_m4 W_EN rail (cell-local y=0.47–0.64).  Previous value
# 0.160 put the 0.32 µm pad's top edge at y_local=0.32, leaving only
# 0.150 µm to the EN rail bottom — at the met1 min-spacing limit.  In
# the ext file Magic merged each DIN via pad with the EN rail, then
# transitively chained writedriver_21..30 DINs (10 consecutive bits)
# into one net (equiv "din[10]" "din[1..9]").
# 0.060 puts pad y range [-0.100, 0.220] in cell-local — still
# overlaps the foundry DIN strip (y=0.020–0.300 at x=[1.275, 1.575])
# but clears the EN rail by 0.250 µm.
_WD_DIN_X_LOCAL: float = 1.425
_WD_DIN_Y_LOCAL: float = 0.060

# SA DOUT pin cell-local x (from foundry LEF): pin on met1 at
# x=[0.520, 0.750], y=[0.000, 1.270]. Pin centre at x=0.635, at the
# BOTTOM of the SA cell (extending UP to y=1.27).
_SA_DOUT_X_LOCAL: float = 0.635
_SA_DOUT_Y_LOCAL: float = 0.0   # bottom edge

# Pitch between trunk y's for per-bit horizontal jogs on met3.
_BIT_TRUNK_PITCH: float = 0.85  # 0.49 met3.6-clamped via-stack pad +
# 0.36 clearance.  Old value 0.70 was sized for the 0.30 met3 wires
# but each trunk endpoint draws a draw_via_stack with a met3 pad
# clamped up to sqrt(MET3_MIN_AREA) = 0.49, and at 0.70 pitch
# adjacent endpoints' pads sat 0.21 µm apart, tripping met3.2.
# Was 0.60 (= width + min spacing exactly).  At pitch 0.60 the
# adjacent trunks' edges sit AT the met3 min-spacing boundary, and
# Magic's extract treats them as electrically merged — every adjacent
# pair of bits whose trunk x-ranges overlap gets unified into one
# net.  For weight_bank that produced equiv "din[10]" "din[1..9]"
# (10 consecutive bits whose trunks all overlapped each other in x).
# 0.70 gives 0.40 µm edge spacing — clearly above the 0.30 min.


def _route_din(top: gdstk.Cell, p: MacroParams, fp: Floorplan,
               tracker: NetsTracker | None = None) -> None:
    """Route top-level din[i] pins to WD[i].DIN.

    Per-bit path (top to bottom):
      din[i] pin (met3 stub at pins_top_y)
        -> via3 -> met4 short vertical to trunk_y[i] (above precharge)
        -> via3 -> met3 horizontal trunk trunk_y[i] from din_pin_x[i] to drop_x[i]
        -> via3 -> met4 long vertical from trunk_y[i] down to drop_y[i]
        -> via stack met4->met1 at (drop_x[i], drop_y[i])
        -> met1 horizontal jog from drop_x[i] to wd_din_x[i] at drop_y[i]
        -> via met1->met2 at (wd_din_x[i], drop_y[i])
        -> met2 vertical up at wd_din_x[i] from drop_y[i] to wd_din_y_abs
        -> via met2->met1 at (wd_din_x[i], wd_din_y_abs) onto WD DIN pin

    Two per-bit y bands (pitch 0.60, 8 tracks each):
      trunk_y[i] (above precharge) — horizontal met3 trunks.
      drop_y[i]  (below DOUT band, below WD) — horizontal met1 jogs.

    Both bands use staircased per-bit y's so each bit owns unique
    horizontal runs.  The met1 horizontal at drop_y[i] and met2
    vertical at wd_din_x[i] are on different layers, so they can
    cross other bits' runs in space without shorting.

    drop_x[i] is chosen to avoid:
      - PDN straps (VPWR centre, VGND left/right) with keepout
        covering both the met4 long vertical and its via pad.
      - Every din_pin_x[j] (met4 pad collision at the upper trunk
        end) with _DROP_MARGIN clearance.
      - Collision-neighbour wd_din_x[j] (so bit i's jog doesn't cross
        bit j's met2 vertical at (wd_din_x[j], drop_y[i]) — they are
        different layers, so no short, but also cheaper to avoid).
    """
    wd_x, wd_y = fp.positions["write_driver"]
    prec_y, prec_h = fp.positions["precharge"][1], fp.sizes["precharge"][1]
    prec_top = prec_y + prec_h

    positions, pins_top_y, pins_bot_y = _top_pin_layout(p, fp)
    mux_pitch = p.mux_ratio * _BITCELL_WIDTH

    # PDN strap x-ranges (must match `_draw_power_network`).
    xs_lo = min(x for x, _ in fp.positions.values()) - 1.0
    xs_hi = max(fp.positions[n][0] + fp.sizes[n][0] for n in fp.positions) + 1.0
    macro_w = xs_hi - xs_lo
    strap_half = _PDN_STRAP_W / 2
    edge_margin = strap_half + 0.5
    strap_x_centers = [
        xs_lo + macro_w / 2,        # VPWR centre
        xs_lo + edge_margin,        # VGND left
        xs_hi - edge_margin,        # VGND right
    ]

    _MET4_PAD_HALF: float = 0.60
    # Keep drop straps clear of PDN strap edges by
    #   (PDN half-width) + (drop half-width) + met4 min-spacing.
    # Previous formula (_MET4_PAD_HALF + 0.30 = 0.90) only accounted
    # for the drop pad, not the PDN strap's own 1.6 µm width.  Result:
    # din[10]'s drop at x=130.89 overlapped VPWR's strap at x=129.18-
    # 130.78 by 0.04 µm (same met4 layer = short).
    _STRAP_KEEPOUT: float = _PDN_STRAP_W / 2 + 0.15 + 0.30
    _DROP_MARGIN: float = 0.70

    # Collect clearance targets.
    din_pin_xs = [positions[f"din[{b}]"][0] for b in range(p.bits)]
    dout_pin_xs = [positions[f"dout[{b}]"][0] for b in range(p.bits)]
    wd_din_xs = [wd_x + _periphery_bit_x(b, p.mux_ratio) + _WD_DIN_X_LOCAL for b in range(p.bits)]
    sa_dout_xs = [wd_x + _periphery_bit_x(b, p.mux_ratio) + _SA_DOUT_X_LOCAL for b in range(p.bits)]

    # Upper trunk band (met3 horizontal): 0.30 above precharge top.
    din_trunk_base_y = prec_top + 0.30
    # Lower drop band (met1 horizontal): below DOUT band.
    # DOUT uses wd_y-0.30 .. wd_y-4.50.  DIN drop band sits 0.60 µm
    # below the DOUT band and staircases downward per bit.
    drop_base_y = wd_y - 5.10

    def _strap_conflict(x: float) -> bool:
        return any(
            abs(x - sx) <= _STRAP_KEEPOUT for sx in strap_x_centers
        )

    chosen_drop_xs: list[float] = []

    def _pick_drop_x(bit_i: int) -> float:
        """Pick drop_x[i].  Must clear:
          - every PDN strap (with _STRAP_KEEPOUT)
          - din_pin_x[j] for j <= bit_i  (only those bits' met4
            verticals at din_pin_x[j] from pins_top_y down to
            trunk_y[j] reach trunk_y[bit_i]; higher-index bits stop
            above trunk_y[bit_i] and don't conflict)
          - every dout_pin_x (DOUT's met3 vertical at dout_pin_x crosses
            the DIN band — a via-stack met3 pad at (drop_x, drop_y) on
            top of it would short DIN to DOUT).
          - every already-picked drop_x (the met4 long vertical at that
            x runs the full macro height; two such verticals at the
            same x merge, and within met4 min-space they short).
        Prefer drop_x = wd_din_x[i] when it clears everything.

        EARLIER VERSION blocked OTHER bits' wd_din_xs and sa_dout_xs.
        At mux=2 packing (pitch 2.62), those constraints forced drop_x
        up to 11.5 µm east of wd_din_x for many bits, creating long
        met1 jogs at drop_y that crossed multiple other bits' met2
        verticals (causing spurious din[X]↔din[Y] equivs).  Removing
        those two constraints lets drop_x = wd_din_x for the common
        case.

        ALSO: an earlier version checked ALL 64 din_pin_xs for the via
        pad clearance, but a higher-index bit j>i has trunk_y[j] >
        trunk_y[i], so its met4 vertical (pins_top_y → trunk_y[j])
        does NOT reach trunk_y[i] — there is no met4 at din_pin_x[j]
        at the pad's y.  Restricting to j<=i removes a phantom
        constraint that prevented bit 51 from finding a valid x in the
        ±12 µm search range, falling back to wd_din_x[51] which
        actually conflicted with din_pin_x[49] only 0.29 µm away.
        """
        wd_din_x_i = wd_din_xs[bit_i]
        # Only bits j<=bit_i contribute met4 at trunk_y[bit_i].
        relevant_din_pin_xs = din_pin_xs[: bit_i + 1]

        def _ok(x: float) -> bool:
            if _strap_conflict(x):
                return False
            if any(abs(x - px) < _DROP_MARGIN for px in relevant_din_pin_xs):
                return False
            if any(abs(x - px) < _DROP_MARGIN for px in dout_pin_xs):
                return False
            for dx in chosen_drop_xs:
                if abs(x - dx) < _DROP_MARGIN:
                    return False
            return True

        if _ok(wd_din_x_i):
            return wd_din_x_i
        # Search outward from wd_din_x_i in 0.5 µm steps up to ±12 µm.
        steps = [0.5 * k for k in range(1, 25)]
        for delta in steps:
            for sign in (1, -1):
                cand = wd_din_x_i + sign * delta
                if _ok(cand):
                    return cand
        # Fallback — take the first clear x east of the VPWR strap.
        for pad in (0.5, 1.0, 1.5, 2.0, 3.0, 5.0):
            cand = xs_lo + pad
            if _ok(cand):
                return cand
        return wd_din_x_i  # give up — will produce a short

    wd_din_y_abs = wd_y + _WD_DIN_Y_LOCAL

    for bit in range(p.bits):
        din_pin_x = positions[f"din[{bit}]"][0]
        wd_din_x_abs = wd_din_xs[bit]
        trunk_y = din_trunk_base_y + bit * _BIT_TRUNK_PITCH
        drop_y = drop_base_y - bit * _BIT_TRUNK_PITCH
        drop_x = _pick_drop_x(bit)
        chosen_drop_xs.append(drop_x)

        din_net = f"din[{bit}]"
        # 1. Via3 at top pin stub (met3 -> met4).
        draw_via_stack(top, from_layer="met3", to_layer="met4",
                       position=(din_pin_x, pins_top_y),
                       tracker=tracker, net=din_net)
        # 2. Met4 short vertical at din_pin_x from pins_top_y to trunk_y.
        draw_wire(top, start=(din_pin_x, pins_top_y),
                  end=(din_pin_x, trunk_y), layer="met4",
                  tracker=tracker, net=din_net)
        # 3. Via3 met4 -> met3 at (din_pin_x, trunk_y).
        draw_via_stack(top, from_layer="met3", to_layer="met4",
                       position=(din_pin_x, trunk_y),
                       tracker=tracker, net=din_net)
        # 4. Met3 horizontal trunk from din_pin_x to drop_x.
        if abs(drop_x - din_pin_x) > 1e-6:
            draw_wire(top, start=(din_pin_x, trunk_y),
                      end=(drop_x, trunk_y), layer="met3",
                      tracker=tracker, net=din_net)
        # 5. Via3 met3 -> met4 at (drop_x, trunk_y).
        draw_via_stack(top, from_layer="met3", to_layer="met4",
                       position=(drop_x, trunk_y),
                       tracker=tracker, net=din_net)
        # 6. Met4 long vertical at drop_x from trunk_y DOWN to drop_y.
        draw_wire(top, start=(drop_x, trunk_y),
                  end=(drop_x, drop_y), layer="met4",
                  tracker=tracker, net=din_net)
        # 7. Via stack met4 -> met1 at (drop_x, drop_y).
        draw_via_stack(top, from_layer="met1", to_layer="met4",
                       position=(drop_x, drop_y),
                       tracker=tracker, net=din_net)
        # 8. Met1 horizontal jog from drop_x to wd_din_x at drop_y.
        if abs(drop_x - wd_din_x_abs) > 1e-6:
            draw_wire(top, start=(drop_x, drop_y),
                      end=(wd_din_x_abs, drop_y), layer="met1",
                      tracker=tracker, net=din_net)
        # 9. Via met1 -> met2 at (wd_din_x, drop_y).
        draw_via_stack(top, from_layer="met1", to_layer="met2",
                       position=(wd_din_x_abs, drop_y),
                       tracker=tracker, net=din_net)
        # 10. Met2 vertical from (wd_din_x, drop_y) UP to wd_din_y_abs.
        draw_wire(top, start=(wd_din_x_abs, drop_y),
                  end=(wd_din_x_abs, wd_din_y_abs), layer="met2",
                  tracker=tracker, net=din_net)
        # 11. Via met2 -> met1 at (wd_din_x, wd_din_y_abs) to connect
        #     onto the WD DIN pin (met1 inside the cell).
        draw_via_stack(top, from_layer="met1", to_layer="met2",
                       position=(wd_din_x_abs, wd_din_y_abs),
                       tracker=tracker, net=din_net)


def _route_dout(top: gdstk.Cell, p: MacroParams, fp: Floorplan,
                tracker: NetsTracker | None = None) -> None:
    """Route SA[i].DOUT to the dout[i] bottom pin of the macro.

    SA DOUT sits at cell-local (0.635, 0.0) on met1 — i.e., the SA
    cell's BOTTOM edge. We bring it DOWN on met2 through the WD cell
    (WD has no met2 internally, so this is safe), then jog east/west
    on met3 in the gap below WD to the dout pin x, then met3 down to
    the dout pin stub.

    Per-bit trunks use different y's below WD so they don't merge.
    """
    sa_x, sa_y = fp.positions["sense_amp"]
    wd_x, wd_y = fp.positions["write_driver"]
    positions, pins_top_y, pins_bot_y = _top_pin_layout(p, fp)
    mux_pitch = p.mux_ratio * _BITCELL_WIDTH

    dout_trunk_base_y = wd_y - 0.30
    for bit in range(p.bits):
        dout_pin_x = positions[f"dout[{bit}]"][0]
        sa_dout_x_abs = sa_x + _periphery_bit_x(bit, p.mux_ratio) + _SA_DOUT_X_LOCAL
        trunk_y = dout_trunk_base_y - bit * _BIT_TRUNK_PITCH

        dout_net = f"dout[{bit}]"
        # 1. Via at SA DOUT pin (met1 -> met2).
        draw_via_stack(top, from_layer="met1", to_layer="met2",
                       position=(sa_dout_x_abs, sa_y),
                       tracker=tracker, net=dout_net)
        # 2. Met2 vertical from SA bottom DOWN through WD to trunk_y.
        draw_wire(top, start=(sa_dout_x_abs, sa_y),
                  end=(sa_dout_x_abs, trunk_y), layer="met2",
                  tracker=tracker, net=dout_net)
        # 3. Via2 met2 -> met3 at (sa_dout_x, trunk_y).
        draw_via_stack(top, from_layer="met2", to_layer="met3",
                       position=(sa_dout_x_abs, trunk_y),
                       tracker=tracker, net=dout_net)
        # 4. Horizontal met3 trunk from sa_dout_x to dout_pin_x.
        draw_wire(top, start=(sa_dout_x_abs, trunk_y),
                  end=(dout_pin_x, trunk_y), layer="met3",
                  tracker=tracker, net=dout_net)
        # 5. Met3 vertical at (dout_pin_x, trunk_y) connecting the H
        #    trunk to the dout pin stub.  V's y-range is extended 0.15
        #    PAST trunk_y on the side opposite the pin stub so V fully
        #    straddles the 0.30-tall H trunk; without this overlap,
        #    Magic decomposes the L-corner into 0.15-tall strips that
        #    fail met3.1 (width <0.30).
        pin_stub_top_y = pins_bot_y + _PIN_STUB_LEN
        if trunk_y > pin_stub_top_y:
            v_top, v_bot = trunk_y + 0.15, pin_stub_top_y
        else:
            v_top, v_bot = pin_stub_top_y, trunk_y - 0.15
        draw_wire(top, start=(dout_pin_x, v_bot),
                  end=(dout_pin_x, v_top), layer="met3",
                  tracker=tracker, net=dout_net)


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
    p: MacroParams,
    fp: Floorplan,
    tracker: NetsTracker | None = None,
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
    ctrl_x, ctrl_y = ctrl_origin
    ctrl_w, ctrl_h = fp.sizes["control_logic"]
    prec_x, prec_y = fp.positions["precharge"]
    sa_x, sa_y = fp.positions["sense_amp"]
    wd_x, wd_y = fp.positions["write_driver"]

    mux_pitch = p.mux_ratio * _BITCELL_WIDTH

    # Routing convention (sky130 preferred directions):
    #   met2 = vertical  (used for short drops to cell pins)
    #   met3 = horizontal (used for long trunks across the macro)
    #   met4 = vertical (PDN straps only)
    #
    # For each control signal we do:
    #   1. DFF Q (met2 pin) -> vertical met2 UP to a trunk y
    #      above/below the ctrl_logic block (never sideways on met2).
    #   2. Via stack met2->met3 at the trunk.
    #   3. Horizontal met3 trunk from DFF column to target x.
    #   4. Vertical met2 down to target pin; via stack to pin layer.
    #
    # The trunk y's are chosen so no horizontal met3 rail passes
    # through any cell's internal met3 rail (cells use met3 at
    # cell-local y's listed below; trunk y's are offset from those).

    # Safe trunk y's for the three signals:
    #   p_en_bar: below the ctrl_logic block (room: wd_y < y < ctrl_y)
    #   s_en:     between sense_amp and col_mux (EN pin at sa_y+10.97)
    #   w_en:     below write_driver (wd_y + 0.3)
    # NOTE: the precharge p_en_bar rail at prec_y+0.28 is above the
    # array, so the DFF-to-precharge trunk must traverse the full
    # macro height on met3.
    #
    # Helper: run vertical met2 up from DFF Q to trunk_y, horizontal
    # met3 from feeder_x to target_x, vertical met2 down to dest_y.
    def _wire_dff_to_pin(
        dff_idx: int,
        trunk_y: float,
        target_x: float,
        target_y: float,
        target_layer: str,
        net: str | None = None,
    ) -> None:
        dff_q_x, dff_q_y = _dff_q_absolute(ctrl_origin, dff_idx)
        # 1. Vertical met2 from DFF Q to trunk y
        draw_wire(top, start=(dff_q_x, dff_q_y),
                  end=(dff_q_x, trunk_y), layer="met2",
                  tracker=tracker, net=net)
        # 2. Via stack met2 -> met3 at the trunk
        draw_via_stack(top, from_layer="met2", to_layer="met3",
                       position=(dff_q_x, trunk_y),
                       tracker=tracker, net=net)
        # 3. Horizontal met3 trunk
        draw_wire(top, start=(dff_q_x, trunk_y),
                  end=(target_x, trunk_y), layer="met3",
                  tracker=tracker, net=net)
        # 4. Via stack met3 -> met2 at target column
        draw_via_stack(top, from_layer="met2", to_layer="met3",
                       position=(target_x, trunk_y),
                       tracker=tracker, net=net)
        # 5. Vertical met2 from trunk down (or up) to target pin
        draw_wire(top, start=(target_x, trunk_y),
                  end=(target_x, target_y), layer="met2",
                  tracker=tracker, net=net)
        # 6. Via stack met2 -> target_layer (met1 or met3)
        if target_layer == "met1":
            draw_via_stack(top, from_layer="met1", to_layer="met2",
                           position=(target_x, target_y),
                           tracker=tracker, net=net)
        elif target_layer == "met3":
            draw_via_stack(top, from_layer="met2", to_layer="met3",
                           position=(target_x, target_y),
                           tracker=tracker, net=net)
        # met2 target: no via needed (met2 already)

    # Trunk y's. Control signals route from ctrl_logic (which sits
    # below the row_decoder, x < 0) across to the peripheral rows
    # (x >= 0). The horizontal trunk must clear both ctrl_logic's
    # top (ctrl_y + ctrl_h = -2.0) and any met3 feature in the row
    # between ctrl_logic and the peripheral rows.
    #
    # Use y = ctrl_y + ctrl_h + margin for trunks that head UP to
    # precharge (crosses through empty space above ctrl_logic, below
    # the array).
    # Use y = ctrl_y - 0.6 for trunks that head DOWN to sense_amp and
    # write_driver (empty space below ctrl_logic).
    trunk_y_up = ctrl_y + ctrl_h + 0.3          # just above ctrl_logic
    trunk_y_dn = ctrl_y - 0.6                    # just below ctrl_logic
    # Separate the three signals' trunk y's so they don't merge:
    trunk_y_p_en_bar = trunk_y_up                # DFF1 -> precharge (up)
    trunk_y_s_en = trunk_y_dn                    # DFF2 -> sense_amp (down)
    trunk_y_w_en = trunk_y_dn - 0.8              # DFF3 -> write_driver (farther down)

    # --- p_en_bar (DFF 1 Q -> precharge met3 rail) ---------------------
    # Precharge p_en_bar is a full-width met3 rail at prec_y+0.28.
    # Routing constraint: a horizontal met3 trunk at y=trunk_y_p_en_bar
    # (≈ -1.7) that enters col_mux x-range (>=0) would overlap col_sel
    # rails (full-width met3 at y={-3.54, -2.74, -1.94, -1.14} inside
    # col_mux). We keep the met3 trunk WEST of col_mux (x<0) and use a
    # vertical met2 riser in the empty gap between wl_driver (ends
    # x=-2.0) and array (starts x=0) to reach the precharge rail y.
    # From there a short met3 jog enters the rail's x-range.
    dff_q_x, dff_q_y = _dff_q_absolute(ctrl_origin, 1)
    p_rail_y = prec_y + _PRECHARGE_EN_Y_LOCAL
    # Riser column: between wl_driver east edge (x=-2.0) and array x=0.
    riser_x = -0.5
    # Landing x: just inside the rail's x-range.
    landing_x = 0.3
    # 1. Transition to met3 IMMEDIATELY at DFF.Q so the long vertical
    #    riser segment is on met3, not met2.  A met2 riser at this x
    #    would cross every ctrl_logic clk/we/cs met2 horizontal rail
    #    on its way up (DFF.Q is below all 3 rails, trunk is above all
    #    3) — same-layer met2 crossings merge p_en_bar with cs/we/clk
    #    and propagate through into the VPWR taps of wl_driver/wd/sa
    #    via the parent's drops on the same rails.  Met3 vertical
    #    crosses met2 rails on a different layer (no merge).
    draw_via_stack(top, from_layer="met2", to_layer="met3",
                   position=(dff_q_x, dff_q_y),
                   tracker=tracker, net="p_en_bar")
    draw_wire(top, start=(dff_q_x, dff_q_y),
              end=(dff_q_x, trunk_y_p_en_bar), layer="met3",
              tracker=tracker, net="p_en_bar")
    # 2. Met3 trunk from DFF column EAST to riser_x (outside col_mux)
    draw_wire(top, start=(dff_q_x, trunk_y_p_en_bar),
              end=(riser_x, trunk_y_p_en_bar), layer="met3",
              tracker=tracker, net="p_en_bar")
    # 3. Via met3->met2 at riser bottom
    draw_via_stack(top, from_layer="met2", to_layer="met3",
                   position=(riser_x, trunk_y_p_en_bar),
                   tracker=tracker, net="p_en_bar")
    # 4. Met2 vertical from riser bottom UP to rail y (in empty gap)
    draw_wire(top, start=(riser_x, trunk_y_p_en_bar),
              end=(riser_x, p_rail_y), layer="met2",
              tracker=tracker, net="p_en_bar")
    # 5. Via met2->met3 at riser top (rail y)
    draw_via_stack(top, from_layer="met2", to_layer="met3",
                   position=(riser_x, p_rail_y),
                   tracker=tracker, net="p_en_bar")
    # 6. Short met3 jog from riser into the precharge rail
    draw_wire(top, start=(riser_x, p_rail_y),
              end=(landing_x, p_rail_y), layer="met3",
              tracker=tracker, net="p_en_bar")

    # --- s_en (DFF 2 Q -> sense_amp EN pins, one per bit) -------------
    # Per-bit SA EN pin is met1 at (sa_x + bit*mux_pitch + 0.615,
    # sa_y + 10.97). Trunk at trunk_y_s_en (below ctrl_logic); for
    # each bit we drop met2 from trunk down to the pin, then via
    # to met1.
    #
    # Drop layer: MET4 (not met2).  DFF 2's Q sits at ctrl_logic-local
    # x=17.975, which is INSIDE the cell-internal D-row met2 wire (drawn
    # by control_logic._draw_nand_z_to_d_pair connecting NAND2_1.Z →
    # DFF2.D and DFF3.D, spanning x=[13.25, max(19.45, NAND_Z_x)]).  A
    # parent met2 vertical drop straight down from DFF2.Q would
    # physically overlap that D-row wire on met2 at y=2.82, bridging
    # s_en into nand1_z.  Switching the drop to met4 keeps the only
    # met-2 contact AT DFF2.Q itself (same net = Q), and the long
    # vertical run is on met4 which the DFF foundry cell does not use.
    # Requires NAND_Z_LOCAL relocated to x=4.0 in control_logic so the
    # via-stack's intermediate met3 pad at DFF2.Q clears NAND2_1's met3
    # drop (otherwise the met3 pad would bridge them).
    dff_q_x, dff_q_y = _dff_q_absolute(ctrl_origin, 2)
    # via stack met2->met4 at DFF.Q (lands met2 pad on the foundry Q
    # metal; intermediate met2/met3 pads sit clear of cell-internal
    # met2/met3 because (a) D-row met2 wire is at y=2.82, well below
    # the pad at y=dff_q_y=3.175, and (b) NAND2_1's met3 drop is now
    # at NAND2_1.Z + 4.0 = 20.215, far from DFF2.Q at 17.975).
    draw_via_stack(top, from_layer="met2", to_layer="met4",
                   position=(dff_q_x, dff_q_y),
                   tracker=tracker, net="s_en")
    # met4 vertical from DFF.Q down to trunk_y (cell-internal layers
    # are met1/met2/met3 only; met4 traverses cleanly).
    draw_wire(top, start=(dff_q_x, dff_q_y),
              end=(dff_q_x, trunk_y_s_en), layer="met4",
              tracker=tracker, net="s_en")
    # met4 -> met3 stack at trunk_y (met3 pad lands on the trunk
    # below, no met2 pad in this stack so no extra collisions).
    draw_via_stack(top, from_layer="met3", to_layer="met4",
                   position=(dff_q_x, trunk_y_s_en),
                   tracker=tracker, net="s_en")
    # Trunk extends from DFF column east to last bit's SA EN x + margin.
    rail_x_end_s = sa_x + _periphery_bit_x(p.bits - 1, p.mux_ratio) + _SA_EN_X_LOCAL + 1.0
    draw_wire(top, start=(dff_q_x, trunk_y_s_en),
              end=(rail_x_end_s, trunk_y_s_en), layer="met3",
              tracker=tracker, net="s_en")
    # Per-bit drop: trunk -> met2 vertical -> met1 SA EN pin
    for bit in range(p.bits):
        pin_x = sa_x + _periphery_bit_x(bit, p.mux_ratio) + _SA_EN_X_LOCAL
        pin_y = sa_y + _SA_EN_Y_LOCAL
        draw_via_stack(top, from_layer="met2", to_layer="met3",
                       position=(pin_x, trunk_y_s_en),
                       tracker=tracker, net="s_en")
        draw_wire(top, start=(pin_x, trunk_y_s_en),
                  end=(pin_x, pin_y), layer="met2",
                  tracker=tracker, net="s_en")
        draw_via_stack(top, from_layer="met1", to_layer="met2",
                       position=(pin_x, pin_y),
                       tracker=tracker, net="s_en")

    # --- w_en (DFF 3 Q -> write_driver EN pins, one per bit) ---------
    dff_q_x, dff_q_y = _dff_q_absolute(ctrl_origin, 3)
    draw_wire(top, start=(dff_q_x, dff_q_y),
              end=(dff_q_x, trunk_y_w_en), layer="met2",
              tracker=tracker, net="w_en")
    draw_via_stack(top, from_layer="met2", to_layer="met3",
                   position=(dff_q_x, trunk_y_w_en),
                   tracker=tracker, net="w_en")
    rail_x_end_w = wd_x + _periphery_bit_x(p.bits - 1, p.mux_ratio) + _WD_EN_X_LOCAL + 1.0
    draw_wire(top, start=(dff_q_x, trunk_y_w_en),
              end=(rail_x_end_w, trunk_y_w_en), layer="met3",
              tracker=tracker, net="w_en")
    for bit in range(p.bits):
        pin_x = wd_x + _periphery_bit_x(bit, p.mux_ratio) + _WD_EN_X_LOCAL
        pin_y = wd_y + _WD_EN_Y_LOCAL
        draw_via_stack(top, from_layer="met2", to_layer="met3",
                       position=(pin_x, trunk_y_w_en),
                       tracker=tracker, net="w_en")
        draw_wire(top, start=(pin_x, trunk_y_w_en),
                  end=(pin_x, pin_y), layer="met2",
                  tracker=tracker, net="w_en")
        draw_via_stack(top, from_layer="met1", to_layer="met2",
                       position=(pin_x, pin_y),
                       tracker=tracker, net="w_en")


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
    p: MacroParams,
    fp: Floorplan,
    tracker: NetsTracker | None = None,
) -> None:
    """Land top-level clk/we/cs pins onto ctrl_logic's internal
    labeled rails.

    control_logic.py now draws all internal wiring (clk met2 rail
    spanning all 4 DFFs, we met3 rail above NAND2, cs met2 rail
    below NAND2, nand_z drops from NAND2 Z to DFF D, VPWR/VGND
    rails with pin labels) inside the cell.  This function's only
    job is to BRIDGE the top-level pin stubs (met3 at pins_top_y)
    DOWN to a point on each labeled rail so Magic's hierarchical
    extractor promotes clk/we/cs to ctrl_logic subckt ports.

    clk  rail — met2 at local y=3.62, x-span -0.5..cell_w+0.5
    we   rail — met3 at local y=11.3, x-span A0..A1
    cs   rail — met2 at local y=9.625, x-span B0..B1
    """
    return _route_ctrl_external_pins(top, p, fp, tracker=tracker)


def _route_ctrl_external_pins(
    top: gdstk.Cell,
    p: MacroParams,
    fp: Floorplan,
    tracker: NetsTracker | None = None,
) -> None:
    ctrl_origin = fp.positions["control_logic"]
    ctrl_x, ctrl_y = ctrl_origin
    _, ctrl_h = fp.sizes["control_logic"]
    positions, pins_top_y, _pins_bot_y = _top_pin_layout(p, fp)
    top_stub_top_y = pins_top_y + _PIN_STUB_LEN

    # Must match control_logic.py rail y's (cell-local).
    _CLK_RAIL_Y_LOCAL: float = 3.620
    _WE_RAIL_Y_LOCAL: float = 11.300
    _CS_RAIL_Y_LOCAL: float = 10.100

    # Each signal lands at a UNIQUE x.  Drops MUST sit east of the
    # row_decoder block to avoid the met2 vertical drop crossing the
    # row_decoder's internal met2 (specifically stage 0/1 NAND2
    # Z→pred_out wires).  Row decoder right edge maps to
    # ctrl_logic-local x = 64.66 for the current floorplan; the gap
    # between row_decoder and wl_driver runs from x_local 64.66 to
    # 66.66 (2 µm wide).
    #
    # Drop x ORDER matters: each met2 vertical drop traverses every
    # met2 horizontal rail in ctrl_logic on its way down to its
    # target rail's y.  If a drop's x is INSIDE another rail's east
    # extent, the drop crosses that rail and merges nets (this was
    # the residual `we`/clk/p_en_bar super-net after the first
    # iteration).  Pair each drop with the per-rail east extent in
    # control_logic.py:
    #   we drop  → westernmost  (we_rail_y is the shallowest target)
    #   cs drop  → middle
    #   clk drop → easternmost  (clk_rail_y is the deepest target)
    # Each rail terminates JUST EAST of its own drop x.
    _WE_LAND_X_LOCAL: float = 65.10  # 0.44 east of row_decoder right edge
    _CS_LAND_X_LOCAL: float = 65.66
    _CLK_LAND_X_LOCAL: float = 66.22  # 0.44 west of wl_driver left edge

    clk_land_x = ctrl_x + _CLK_LAND_X_LOCAL
    we_land_x = ctrl_x + _WE_LAND_X_LOCAL
    cs_land_x = ctrl_x + _CS_LAND_X_LOCAL
    clk_land_y = ctrl_y + _CLK_RAIL_Y_LOCAL
    we_land_y = ctrl_y + _WE_RAIL_Y_LOCAL
    cs_land_y = ctrl_y + _CS_RAIL_Y_LOCAL

    def _drop_met3_to_rail(
        pin_x: float, pin_y_top: float,
        land_x: float, land_y: float,
        rail_layer: str,
        trunk_y: float,
        net: str | None = None,
        cls: "NetClass" = "signal",  # noqa: F821
    ) -> None:
        # Horizontal met3 trunk from pin_x to land_x at trunk_y.
        # Caller supplies a unique trunk_y per signal so trunks from
        # different signals don't short on met3.
        draw_wire(
            top,
            start=(pin_x, pin_y_top),
            end=(pin_x, trunk_y + _CTRL_TRUNK_HALF_W),
            layer="met3", width=_CTRL_TRUNK_W,
            tracker=tracker, net=net, cls=cls,
        )
        west = min(pin_x, land_x) - _CTRL_TRUNK_HALF_W
        east = max(pin_x, land_x) + _CTRL_TRUNK_HALF_W
        draw_wire(
            top,
            start=(west, trunk_y), end=(east, trunk_y),
            layer="met3", width=_CTRL_TRUNK_W,
            tracker=tracker, net=net, cls=cls,
        )
        # Vertical drop from trunk to land — single met4 segment.
        # Earlier version used met2 long + met4 short with a met2→met4
        # via stack at _LAYER_TRANSITION_Y; the intermediate met3 pad
        # in that stack (clamped to met3 min-area = 0.49×0.49) collided
        # with neighbouring signals' pads at the same y (clk/cs/we land
        # x's are 0.56 µm apart in this gap) — driving ~120 met3.2
        # tiles.  Going straight from trunk_y to land_y on met4
        # eliminates the intermediate transition entirely; the only
        # met3 pads now sit at (a) trunk_y (each signal at a unique
        # trunk_y, 0.80 µm apart) and (b) land_y (each signal at a
        # unique land_y per ctrl rail, 1+ µm apart) — both clear of
        # the 0.30 µm met3.2 minimum.
        draw_via_stack(
            top, from_layer="met3", to_layer="met4",
            position=(land_x, trunk_y),
            tracker=tracker, net=net, cls=cls,
        )
        draw_wire(
            top,
            start=(land_x, trunk_y),
            end=(land_x, land_y),
            layer="met4", width=_CTRL_TRUNK_W,
            tracker=tracker, net=net, cls=cls,
        )
        # Land: via stack from met4 down to the rail's layer.  For met2
        # rail, met4→met2 stack deposits a met2 pad at (land_x, land_y)
        # that overlaps the rail and merges nets.
        if rail_layer == "met2":
            draw_via_stack(
                top, from_layer="met2", to_layer="met4",
                position=(land_x, land_y),
                tracker=tracker, net=net, cls=cls,
            )
        elif rail_layer == "met3":
            draw_via_stack(
                top, from_layer="met3", to_layer="met4",
                position=(land_x, land_y),
                tracker=tracker, net=net, cls=cls,
            )

    # Unique trunk y per signal (met3 horizontal at each y won't short).
    # Stack above the pin stub top with _CTRL_TRUNK_PITCH spacing.
    # Slots 1..total_addr are used by the row-decoder addr feeders
    # (_route_addr_multi_predecoder); ctrl starts AFTER them so trunks
    # don't overlap on met3 and short addr[i] to clk/we/cs.
    split = _SPLIT_TABLE[p.rows]
    total_addr = sum(split)
    clk_trunk_y = top_stub_top_y + (total_addr + 1) * _CTRL_TRUNK_PITCH
    we_trunk_y = top_stub_top_y + (total_addr + 2) * _CTRL_TRUNK_PITCH
    cs_trunk_y = top_stub_top_y + (total_addr + 3) * _CTRL_TRUNK_PITCH

    clk_pin_x, _ = positions["clk"]
    _drop_met3_to_rail(clk_pin_x, top_stub_top_y,
                       clk_land_x, clk_land_y, "met2", clk_trunk_y,
                       net="clk", cls="clock")

    we_pin_x, _ = positions["we"]
    _drop_met3_to_rail(we_pin_x, top_stub_top_y,
                       we_land_x, we_land_y, "met2", we_trunk_y,
                       net="we", cls="signal")

    cs_pin_x, _ = positions["cs"]
    _drop_met3_to_rail(cs_pin_x, top_stub_top_y,
                       cs_land_x, cs_land_y, "met2", cs_trunk_y,
                       net="cs", cls="signal")


def _OLD_route_ctrl_internal(
    top: gdstk.Cell,
    p: MacroParams,
    fp: Floorplan,
) -> None:
    """Original per-DFF/NAND2 routing — kept for reference, no longer
    called.  ctrl_logic now handles its own internal wiring; see
    _route_ctrl_external_pins for the simplified external connection.
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
        from rekolektion.macro.sky130_drc import layer_min_width
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
# NAND_dec input-pin cell-local coords (from the cell GDS's met1.label /
# li1.label text positions — NOT from LEF, which lists bbox RECTs; pin
# center placements are on met1 where the addr sidebar's li1→met3 stack
# lands).  NAND2 and NAND3 have completely different pin layouts — each
# is only valid for its own k_fanin.
_NAND_DEC_PIN_POS: dict[int, dict[str, tuple[float, float]]] = {
    # NAND2: A and B both on met1 at x=0.405.  From GDS labels A=(0.405,
    # 1.095) B=(0.405, 0.555).
    2: {
        "A": (0.405, 1.095),
        "B": (0.405, 0.555),
    },
    # NAND3: A/B/C at distinct x's on met1.  From GDS labels A=(1.265,
    # 0.410) B=(0.715, 0.770) C=(0.165, 1.130).
    3: {
        "A": (1.265, 0.410),
        "B": (0.715, 0.770),
        "C": (0.165, 1.130),
    },
}

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
    if k_fanin not in _NAND_DEC_PIN_POS:
        raise ValueError(
            f"no pin-position table for NAND fan-in {k_fanin}"
        )
    pos_table = _NAND_DEC_PIN_POS[k_fanin]
    if pin not in pos_table:
        raise ValueError(
            f"NAND{k_fanin}_dec has no {pin!r} input pin"
        )
    x_local, y_local = pos_table[pin]
    dec_x, dec_y = dec_origin
    if row % 2 == 0:
        abs_y = dec_y + row * _NAND_DEC_PITCH + y_local
    else:
        abs_y = dec_y + (row + 1) * _NAND_DEC_PITCH - y_local
    return (dec_x + x_local, abs_y)


def _route_addr(
    top: gdstk.Cell,
    p: MacroParams,
    fp: Floorplan,
    tracker: NetsTracker | None = None,
) -> None:
    """Route top-level addr[0..sum(split)-1] pins to the row_decoder's
    internal addr rails.

    Single-predecoder case: row_decoder's _add_addr_rails draws labeled
    met3 rails inside the cell; we additionally drop per-row li1
    spurs from a sidebar into each NAND input pin — double-wired but
    consistent.

    Multi-predecoder case: row_decoder._build_multi_predecoder draws
    labeled met3 addr rails inside its own footprint.  We just need to
    land each top-level addr[i] pin onto the corresponding internal
    rail.  Magic's hierarchical extractor uses the label to promote
    addr{i} as a subckt port of row_decoder.
    """
    split = _SPLIT_TABLE[p.rows]
    total_addr = sum(split)

    if len(split) != 1:
        _route_addr_multi_predecoder(top, p, fp, total_addr, tracker=tracker)
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
        addr_net = addr_pin_key
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
            tracker=tracker, net=addr_net,
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
            tracker=tracker, net=addr_net,
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
            tracker=tracker, net=addr_net,
        )
        # --- (4)(5) per-row: via stack + li1 horizontal into pin -----
        for r, pin_y in enumerate(per_row_pin_ys):
            draw_via_stack(
                top, from_layer="li1", to_layer="met3",
                position=(sidebar_x, pin_y),
                tracker=tracker, net=addr_net,
            )
            # li1 horizontal from sidebar to pin
            li_lo = sidebar_x - layer_min_width_half("li1")
            li_hi = nand_pin_x + layer_min_width_half("li1")
            draw_wire(
                top,
                start=(li_lo, pin_y),
                end=(li_hi, pin_y),
                layer="li1",
                tracker=tracker, net=addr_net,
            )


def _route_addr_multi_predecoder(
    top: gdstk.Cell,
    p: MacroParams,
    fp: Floorplan,
    total_addr: int,
    tracker: NetsTracker | None = None,
) -> None:
    """Land each top-level addr[i] pin onto row_decoder's internal
    addr{i} met3 rail.

    The rails are drawn inside row_decoder at cell-local
    x = addr_rail_x0 + i * addr_rail_pitch, spanning the predecoder
    block height (y=0..stack_y).  They're labeled addr{i} on met3.

    We draw a met3 feeder from each top-level addr pin down to the
    rail's absolute position; Magic merges them at the label.
    """
    positions, pins_top_y, _ = _top_pin_layout(p, fp)
    dec_origin = fp.positions["row_decoder"]
    dec_x, dec_y = dec_origin

    # Must match row_decoder._build_multi_predecoder.
    _ADDR_RAIL_X0_LOCAL: float = 0.3
    _ADDR_RAIL_PITCH: float = 0.7

    top_stub_top_y = pins_top_y + _PIN_STUB_LEN
    addr_trunk_y_base = top_stub_top_y + _CTRL_TRUNK_PITCH

    for i in range(total_addr):
        addr_pin_key = f"addr[{i}]"
        if addr_pin_key not in positions:
            continue  # should not happen for multi-predecoder configs
        addr_net = addr_pin_key
        addr_pin_x, _ = positions[addr_pin_key]
        rail_y_trunk = addr_trunk_y_base + i * _CTRL_TRUNK_PITCH

        rail_x_abs = dec_x + _ADDR_RAIL_X0_LOCAL + i * _ADDR_RAIL_PITCH
        # Landing y: must be ABOVE the predecoder block.  The rails
        # extend the full row_decoder cell height (up to row_count *
        # pitch ≈ 202 µm), so any land_y above pred_block_top is
        # safely on the rail.
        #
        # Earlier value of 2.0 µm landed INSIDE the predecoder block
        # (pred_top ≈ 14 µm for our [2,2,3] split).  Each parent met2
        # drop at rail_x_abs crossing the predecoder's y range
        # collided with the cell-internal addr-feed met2 spurs:
        #   stage-0 (NAND2) A/B spurs at cell-local y=1.095, 0.555
        #   stage-1 (NAND2) A/B spurs at cell-local y=5.685, 5.245
        #   stage-2 (NAND3) DETOUR spurs at cell-local y=7.88..8.88
        # Each stage's A or B spur is a met2 horizontal that extends
        # from the rail's x EAST through every higher-index rail
        # (the spur services every cell in the stage column).  So a
        # parent drop on the same met2 layer crossing that spur's y
        # bridges multiple rails at once — observed at activation_bank
        # as row_decoder/addr[2..6] all merging into one parent net.
        # Landing 1 µm above pred_top (≈ 14) clears every spur.
        _PRED_TOP_SAFE: float = 15.0
        land_y = dec_y + _PRED_TOP_SAFE

        # (1) met3 feeder from top pin up/down to horizontal trunk y.
        draw_wire(
            top,
            start=(addr_pin_x, top_stub_top_y),
            end=(addr_pin_x, rail_y_trunk + _CTRL_TRUNK_HALF_W),
            layer="met3",
            width=_CTRL_TRUNK_W,
            tracker=tracker, net=addr_net,
        )
        # (2) met3 horizontal trunk at rail_y_trunk from addr_pin_x to
        #     rail_x_abs.
        trunk_west = rail_x_abs - _CTRL_TRUNK_HALF_W
        trunk_east = addr_pin_x + _CTRL_TRUNK_HALF_W
        draw_wire(
            top,
            start=(trunk_west, rail_y_trunk),
            end=(trunk_east, rail_y_trunk),
            layer="met3",
            width=_CTRL_TRUNK_W,
            tracker=tracker, net=addr_net,
        )
        # (3) met2 vertical DROP from rail_y_trunk DOWN to land_y, at
        #     rail_x_abs.  Must be on met2 (not met3) because each
        #     bit's drop passes THROUGH every other bit's horizontal
        #     trunk at (rail_x_abs_i, trunk_y_j) — same-layer met3
        #     crossings there would merge addr[i] with addr[j].
        #     Met4 is also unavailable: addr[0]/addr[1] drop x values
        #     (1.3, 2.0) fall inside the west VGND PDN strap's x range
        #     (~[0.5, 2.1]), so met4 drops would short to VGND.
        #     via1+via2 at each end transitions met2↔met3.
        draw_via_stack(
            top, from_layer="met2", to_layer="met3",
            position=(rail_x_abs, rail_y_trunk),
            tracker=tracker, net=addr_net,
        )
        draw_wire(
            top,
            start=(rail_x_abs, land_y),
            end=(rail_x_abs, rail_y_trunk),
            layer="met2",
            width=_CTRL_TRUNK_W,
            tracker=tracker, net=addr_net,
        )
        # Custom met2+via2+met3 emit at land_y, mirroring Fix #2 v2 in
        # row_decoder._via2_on_rail.  draw_via_stack would emit a 0.49 ×
        # 0.49 met3.6-clamped square pad here; with all 8 addr drops at
        # the same y on 0.7 µm pitch, adjacent pads sit 0.21 µm apart and
        # trip met3.2 (≥0.30 spacing).  A 0.36 × 0.68 rectangular pad
        # gives 0.34 µm pad-to-pad clearance and clears met3.4 + via2.5a.
        from rekolektion.macro.sky130_drc import GDS_LAYER as _GDS_LAYER
        _met2_l, _met2_d = _GDS_LAYER["met2"]
        _via2_l, _via2_d = _GDS_LAYER["via2"]
        _met3_l, _met3_d = _GDS_LAYER["met3"]
        _MET2_PAD_HALF: float = 0.185
        _VIA2_HALF: float = 0.10
        _MET3_PAD_HALF_X: float = 0.18
        _MET3_PAD_HALF_Y: float = 0.34
        top.add(gdstk.rectangle(
            (rail_x_abs - _MET2_PAD_HALF, land_y - _MET2_PAD_HALF),
            (rail_x_abs + _MET2_PAD_HALF, land_y + _MET2_PAD_HALF),
            layer=_met2_l, datatype=_met2_d,
        ))
        top.add(gdstk.rectangle(
            (rail_x_abs - _VIA2_HALF, land_y - _VIA2_HALF),
            (rail_x_abs + _VIA2_HALF, land_y + _VIA2_HALF),
            layer=_via2_l, datatype=_via2_d,
        ))
        top.add(gdstk.rectangle(
            (rail_x_abs - _MET3_PAD_HALF_X, land_y - _MET3_PAD_HALF_Y),
            (rail_x_abs + _MET3_PAD_HALF_X, land_y + _MET3_PAD_HALF_Y),
            layer=_met3_l, datatype=_met3_d,
        ))


def layer_min_width_half(layer: str) -> float:
    """Half the min width for a layer (for endpoint extension)."""
    from rekolektion.macro.sky130_drc import layer_min_width
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

# Height reserved below the DOUT trunk band (wd_y-0.30 .. wd_y-4.50) for
# the DIN drop band (wd_y-5.10 .. wd_y-9.30). Consumed by _top_pin_layout
# when computing pins_bot_y. Matches _DIN_BAND_BASE_OFFSET + 0.60 gap +
# 8*0.60 pitch = 5.10 - 0.60 = 4.80 for the band height itself; we round
# to 5.0 for margin.
_DIN_BAND_EXTENSION: float = 5.0


def _top_pin_layout(
    p: MacroParams,
    fp: Floorplan,
) -> tuple[dict[str, tuple[float, float]], float, float]:
    """Compute the (x, y) of each top-level input/output pin.

    Returns (name -> position, pins_top_y, pins_bot_y).  Positions point
    at the *bottom* of the met3 stub (i.e. the tip facing the macro
    interior), which is the natural route entry point.

    pins_bot_y is extended below wd_bot to leave room for BOTH the DOUT
    trunk band (below WD, 8 tracks at 0.60 pitch starting at wd_y-0.30)
    AND the DIN drop band (8 tracks at 0.60 pitch starting at wd_y-5.10),
    plus a 0.60 µm margin to the bottom pin stubs.
    """
    array_x, _ = fp.positions["array"]
    array_w, _ = fp.sizes["array"]
    prec_y, prec_h = fp.positions["precharge"][1], fp.sizes["precharge"][1]
    prec_top = prec_y + prec_h
    # `_route_din` lays its met3 trunks at y = prec_top + 0.30 + i *
    # _BIT_TRUNK_PITCH for i in 0..bits-1. Each trunk runs the full
    # x-extent between din_pin_x[i] and drop_x[i] — i.e., it crosses
    # every other din pin's x position. If pins_top_y lands such that
    # the pin stub region [pins_top_y, pins_top_y + _PIN_STUB_LEN]
    # overlaps any trunk_y[i], that trunk's met3 wire merges with every
    # input pin stub it crosses in x, creating equiv chains like
    # din[0..9] (10 consecutive bits whose pin stubs all sit under one
    # offending trunk). Ensure pins_top_y sits above the entire trunk
    # band so all 32 trunks are below the stubs with met3 min spacing.
    din_trunks_top_y = (
        prec_top + 0.30 + (p.bits - 1) * _BIT_TRUNK_PITCH + 0.15
    )
    pins_top_y = max(
        prec_top + _PDN_STRAP_MARGIN + _PDN_STRAP_W + _PDN_STRAP_MARGIN,
        din_trunks_top_y + 0.30,
    )
    wd_bot = fp.positions["write_driver"][1]
    # Below WD we stack two horizontal routing bands before the bottom
    # pin stubs:
    #   DOUT band: wd_y-0.30 .. wd_y-4.50 (8 tracks at 0.60 pitch)
    #   DIN band:  wd_y-5.10 .. wd_y-9.30 (8 tracks at 0.60 pitch,
    #              0.60 µm gap from DOUT).
    # _DIN_BAND_EXTENSION adds exactly the height of the DIN band to the
    # pre-existing wd_bot-to-pins_bot_y clearance (which only accounted
    # for DOUT). Keeping the clearance calculation structure identical
    # preserves DOUT / PDN behaviour.
    pins_bot_y = (
        wd_bot
        - _DIN_BAND_EXTENSION
        - _PDN_STRAP_MARGIN - _PDN_STRAP_W - _PDN_STRAP_MARGIN
    )

    input_names: list[str] = []
    for i in range(p.num_addr_bits):
        input_names.append(f"addr[{i}]")
    input_names += ["clk", "we", "cs"]
    for i in range(p.bits):
        input_names.append(f"din[{i}]")

    # Compute the PDN strap x-ranges (centre ± half-width) so we can
    # keep top/bottom pin x-positions clear of them.  Without this,
    # the rightmost dout/din pins (e.g. dout[31] at x ≈ 258 when the
    # macro is ~260 wide) land directly under the east VGND strap on
    # met4; the pin's met3 stub overlaps the strap's met2 anchor pad
    # in 2D and Magic merges the pin net with VGND.
    xs_lo = min(x for x, _ in fp.positions.values())
    xs_hi = max(
        fp.positions[n][0] + fp.sizes[n][0] for n in fp.positions
    )
    macro_xs_lo = xs_lo - 1.0
    macro_xs_hi = xs_hi + 1.0
    macro_w_ext = macro_xs_hi - macro_xs_lo
    edge_margin = _PDN_STRAP_W / 2 + 0.5
    strap_centers = [
        macro_xs_lo + macro_w_ext / 2,        # VPWR centre
        macro_xs_lo + edge_margin,            # VGND left
        macro_xs_hi - edge_margin,            # VGND right
    ]
    strap_keepout = _PDN_STRAP_W / 2 + 0.5  # PDN half-width + clearance

    def _avoid_straps(x: float) -> float:
        """Nudge x clear of any PDN strap by at least strap_keepout."""
        for sc in strap_centers:
            if abs(x - sc) < strap_keepout:
                # Push x to whichever side gives the smaller jump.
                west = sc - strap_keepout
                east = sc + strap_keepout
                x = west if abs(x - west) < abs(x - east) else east
        return x

    x0 = array_x + 1.0
    x_end = array_x + array_w - 1.0
    step = (x_end - x0) / max(len(input_names) - 1, 1) if len(input_names) > 1 else 0.0
    positions: dict[str, tuple[float, float]] = {}
    for i, name in enumerate(input_names):
        positions[name] = (_avoid_straps(x0 + i * step), pins_top_y)

    x0b = array_x + 1.0
    stepb = (x_end - x0b) / max(p.bits - 1, 1) if p.bits > 1 else 0.0
    for i in range(p.bits):
        positions[f"dout[{i}]"] = (
            _avoid_straps(x0b + i * stepb), pins_bot_y,
        )
    return positions, pins_top_y, pins_bot_y


def _place_top_pins(
    top: gdstk.Cell,
    p: MacroParams,
    fp: Floorplan,
    tracker: NetsTracker | None = None,
) -> None:
    """Place LEF-style met3 pins for addr, din, dout, clk, we, cs at
    the top (inputs) and bottom (outputs) of the macro.
    """
    positions, pins_top_y, pins_bot_y = _top_pin_layout(p, fp)

    for name, (px, _) in positions.items():
        if name.startswith("dout"):
            continue
        # clk is the only top-level clock signal; everything else is a
        # data/control signal.
        pin_cls: "NetClass" = "clock" if name == "clk" else "signal"  # noqa: F821
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
            tracker=tracker, net=name, cls=pin_cls,
        )
        draw_pin_with_label(top, text=name, layer=_PIN_LAYER, rect=rect,
                            tracker=tracker, net=name, cls=pin_cls)

    for i in range(p.bits):
        px, _ = positions[f"dout[{i}]"]
        dout_net = f"dout[{i}]"
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
            tracker=tracker, net=dout_net,
        )
        draw_pin_with_label(top, text=dout_net, layer=_PIN_LAYER, rect=rect,
                            tracker=tracker, net=dout_net)

    # col_sel_{k} pins — labeled on the col_mux cell's existing sel_{k}
    # met3 rails at the macro's WEST edge. The rails already span the
    # full col_mux width (= approx macro width), so the pin is just a
    # labeled portion of the rail near the west edge. No additional
    # routing is needed; external SoC routing drives the rail directly.
    col_mux_x, col_mux_y = fp.positions["col_mux"]
    # sel rail y in col_mux cell-local coords: sel_first_y + k*sel_pitch
    # (column_mux.py: sel_first_y=2.425, sel_pitch=0.80, rail_w=0.40)
    _COLMUX_SEL_FIRST_Y: float = 2.425
    _COLMUX_SEL_PITCH: float = 0.80
    _COLMUX_RAIL_W: float = 0.40
    for k in range(p.mux_ratio):
        rail_y_abs = col_mux_y + _COLMUX_SEL_FIRST_Y + k * _COLMUX_SEL_PITCH
        # Pin rect: 0.30 µm × rail width, inside the rail near west edge.
        rect = (
            col_mux_x + 0.10,
            rail_y_abs - _COLMUX_RAIL_W / 2,
            col_mux_x + 0.40,
            rail_y_abs + _COLMUX_RAIL_W / 2,
        )
        # Label with underscore notation to match the mux cell's
        # Magic-extracted col_sel_N port naming.
        col_sel_net = f"col_sel_{k}"
        draw_pin_with_label(top, text=col_sel_net,
                            layer=_PIN_LAYER, rect=rect,
                            tracker=tracker, net=col_sel_net)


def _place_power_grid(
    top: gdstk.Cell,
    p: MacroParams,
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
# C6.6 — SRAM core marker (81/2)
# ---------------------------------------------------------------------------

def _draw_sram_core_marker(
    top: gdstk.Cell,
    p: MacroParams,
    fp: Floorplan,
) -> None:
    """Draw the sky130 SRAM core marker (GDS layer 81/2) over the
    bitcell array only. Inside the marker, sky130 relaxes
    li1/met1/met2/poly/diff width and spacing rules that are otherwise
    violated by the foundry bitcell's tight 1.31 µm pitch (e.g.,
    W=0.14 access transistor, 0.055 µm BL/BR met1 spacing).

    The COREID is intentionally limited to the array footprint and
    does NOT extend over the peripheral rows (write_driver / sense_amp
    / col_mux / precharge). The foundry peripheral cells are already
    DRC-clean against stock rules (verified standalone), and extending
    COREID over them activates the v1/m1 width≥0.26 rule on our top-
    level via1 stacks at SA-exit and WD/DIN drop points — those stacks
    have 0.085 µm m1 enclosure of a 0.15 µm via1 (= 0.32 µm pad), but
    Magic's COREID-mode v1/m1 derivation flags 0.01 µm slivers along
    the pad's edges. Restricting COREID to the array preserves the
    relaxations the bitcell needs without subjecting our routing to
    foundry-tile-only rules.
    """
    from rekolektion.tech.sky130 import LAYERS
    coreid_l, coreid_d = LAYERS.COREID.as_tuple
    array_x = fp.positions["array"][0]
    array_y = fp.positions["array"][1]
    array_w, array_h = fp.sizes["array"]
    # Snug fit to array bbox plus 0.30 µm border to absorb tile
    # boundary tolerance.
    x0 = array_x - 0.30
    x1 = array_x + array_w + 0.30
    y0 = array_y - 0.30
    y1 = array_y + array_h + 0.30
    top.add(gdstk.rectangle((x0, y0), (x1, y1),
                            layer=coreid_l, datatype=coreid_d))


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
    p: MacroParams,
    fp: Floorplan,
    tracker: NetsTracker | None = None,
) -> None:
    """Build the macro's internal power distribution network.

    Vertical met4 straps (VPWR at center, VGND at edges) span the
    macro's interior height between the LEF pin anchors. Each strap
    gets a local met2 pad at its top/bottom anchor so the LEF's
    met2 PORT rect has matching GDS metal (for PSM).

    Previous version drew full-width horizontal met2 rails; those
    passed through every vertical met2 drop in `_route_ctrl_internal`
    and caused cs/we/clk/addr[2] to merge. Replaced with per-strap
    local pads.
    """
    xs_lo = min(x for x, _ in fp.positions.values()) - 1.0
    xs_hi = max(
        fp.positions[n][0] + fp.sizes[n][0] for n in fp.positions
    ) + 1.0
    # Use the SAME pins_top_y / pins_bot_y as `_top_pin_layout` —
    # otherwise `top_rail_y` lands inside the DIN trunk band and the
    # PDN VPWR via stack's met3 pad physically collides with one of the
    # bits' met3 trunks (e.g. at mux=2 64-bit, bit 10 trunk_y=214.52
    # abs collides with mis-placed top_rail_y=214.28 abs, equivalencing
    # din[10] to VPWR at parent-extracted SPICE).  An earlier inline
    # calculation used `prec_top + 5.6` (pre-DIN-band) which yielded
    # the wrong pins_top_y; switching to the canonical layout function
    # makes the PDN strap top sit above the DIN trunks.
    _, pins_top_y, pins_bot_y = _top_pin_layout(p, fp)
    import math as _math
    _ROW_PITCH = 2.72
    _ys_lo = pins_bot_y - 0.5
    _ys_hi_tight = pins_top_y + _PIN_STUB_LEN + 0.5
    _macro_h_snapped = _math.ceil((_ys_hi_tight - _ys_lo) / _ROW_PITCH) * _ROW_PITCH
    _strap_half = _PDN_STRAP_W / 2
    top_rail_y = _ys_lo + _macro_h_snapped - _strap_half
    bot_rail_y = _ys_lo + _strap_half

    from rekolektion.macro.sky130_drc import GDS_LAYER
    met2_l, met2_d = GDS_LAYER["met2"]

    # Per-strap local met2 pad size. Wide enough to back the LEF met2
    # PORT rect and host the via2 stack, small enough to not cross
    # any signal met2 feature elsewhere in the macro.
    #
    # Shrunk from 1.10 µm half-width (= strap_half + 0.3) to 0.40 µm
    # half-width to clear the rightmost wd_din_x on dense mux=2 packs.
    # At mux=2 64-bit, bit 63's wd_din_x = 166.485 µm and the VGND
    # right strap centre = 167.380 µm; the old pad spanned x=[166.28,
    # 168.48], swallowing bit 63's met2 vertical and equivalencing
    # din[63] to VGND.  The pad only needs to host the via2 stack
    # (≈0.28 µm wide met2 enclosure) and back the LEF PORT rect; 0.80
    # µm wide is ample for both.
    _PAD_HALF_X: float = 0.40                        # 0.80 µm wide
    _PAD_HALF_Y: float = _PDN_MET2_RAIL_W / 2        # 0.20 µm tall

    def _draw_local_met2_pad(
        cx: float, cy: float,
        net: str | None = None,
        cls: "NetClass" = "signal",  # noqa: F821
    ) -> None:
        top.add(gdstk.rectangle(
            (cx - _PAD_HALF_X, cy - _PAD_HALF_Y),
            (cx + _PAD_HALF_X, cy + _PAD_HALF_Y),
            layer=met2_l, datatype=met2_d,
        ))
        if tracker is not None and net is not None:
            tracker.record(
                cell=top, layer=met2_l, datatype=met2_d,
                net=net, cls=cls,
            )

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
            tracker=tracker, net="VPWR", cls="power",
        )
        _draw_local_met2_pad(vx, top_rail_y, net="VPWR", cls="power")
        draw_via_stack(
            top, from_layer="met2", to_layer="met4",
            position=(vx, top_rail_y),
            tracker=tracker, net="VPWR", cls="power",
        )
        # Label the met2 pad as VPWR so Magic identifies the net as
        # the top-level VPWR port.  Needs BOTH a pin rect (met2.pin
        # dtype 16) and a label (met2.label dtype 5) for Magic's
        # ext2spice to promote the net to an external subckt port.
        draw_pin_with_label(
            top, text="VPWR", layer="met2",
            rect=(
                vx - _PAD_HALF_X, top_rail_y - _PAD_HALF_Y,
                vx + _PAD_HALF_X, top_rail_y + _PAD_HALF_Y,
            ),
            tracker=tracker, net="VPWR", cls="power",
        )

    for vx in vgnd_xs:
        draw_pdn_strap(
            top, orientation="vertical",
            center_coord=vx,
            span_start=bot_rail_y, span_end=top_rail_y,
            layer="met4", width=_PDN_STRAP_W,
            tracker=tracker, net="VGND", cls="ground",
        )
        _draw_local_met2_pad(vx, bot_rail_y, net="VGND", cls="ground")
        draw_via_stack(
            top, from_layer="met2", to_layer="met4",
            position=(vx, bot_rail_y),
            tracker=tracker, net="VGND", cls="ground",
        )
        draw_pin_with_label(
            top, text="VGND", layer="met2",
            rect=(
                vx - _PAD_HALF_X, bot_rail_y - _PAD_HALF_Y,
                vx + _PAD_HALF_X, bot_rail_y + _PAD_HALF_Y,
            ),
            tracker=tracker, net="VGND", cls="ground",
        )

    # Block-level VPWR / VGND taps.
    #
    # The PDN strap above only labels two met2 pads (one VPWR at top,
    # two VGND at edges).  Each block (row_decoder, wl_driver, etc.)
    # has its own VPWR / VGND ports anchored at cell-internal met1
    # rails — but those rails sit at distinct x positions inside each
    # block, none of which coincide with the strap x.  The strap
    # therefore can't electrically reach the block rails through the
    # geometry alone, so the parent extracted SPICE shows VPWR/VGND
    # disconnected at top level and emits the row_decoder / wl_driver
    # / etc. X-instance lines with `we` placeholders for their VPWR/
    # VGND port positions.
    #
    # Magic's ext2spice merges nets by label name across the hierarchy.
    # Dropping a parent-level met1 + met1.pin shape (same purpose as
    # the strap's met2 .pin) at one of each block's rail positions
    # makes the parent net "VPWR" share its name with the block's
    # internal "VPWR" port — netgen flattens both into a single net.
    # The taps overlay an already-existing met1 rail in the block (a
    # foundry cell's VDD/GND rail or the block's own power infrastructure)
    # so Magic also sees a physical metal merge at the same coords.
    _TAP_HALF = 0.07

    def _tap_block_power(
        block_name: str,
        vpwr_local: tuple[float, float],
        vgnd_local: tuple[float, float],
    ) -> None:
        bx, by = fp.positions[block_name]
        for net_name, (lx, ly) in (
            ("VPWR", vpwr_local), ("VGND", vgnd_local),
        ):
            tap_cls: "NetClass" = "power" if net_name == "VPWR" else "ground"  # noqa: F821
            draw_pin_with_label(
                top, text=net_name, layer="met1",
                rect=(
                    bx + lx - _TAP_HALF, by + ly - _TAP_HALF,
                    bx + lx + _TAP_HALF, by + ly + _TAP_HALF,
                ),
                tracker=tracker, net=net_name, cls=tap_cls,
            )

    # Tap point per block — cell-local rail positions that the block
    # itself labels VPWR/VGND.  Identical-name parent labels merge.
    # row_decoder multi-predecoder: final NAND3 column at cell-local
    # x = `_decoder_w_estimate` calc gives nand_x ≈ 67.94 in cell
    # coords; pick row 0 NAND3 VDD rail (cell-local x=4.38, y=0.85)
    # and GND rail (x=1.905, y=0.715).
    if len(_SPLIT_TABLE[p.rows]) == 1:
        _NAND_X_IN_DEC = 0.0
    else:
        # Final NAND column x in row_decoder cell-local — matches
        # `RowDecoder._build_multi_predecoder` and the corrected
        # _route_wl calc.
        from rekolektion.macro.row_decoder import _PREDECODER_TO_NAND_GAP
        _split = _SPLIT_TABLE[p.rows]
        _total_addr = sum(_split)
        _pred_area_x0 = 0.3 + _total_addr * 0.7 + 0.5
        _NAND_W = {2: 4.77, 3: 7.53}
        _pred_block_right_x = _pred_area_x0 + max(
            (2 ** k) * _NAND_W[k] for k in _split
        )
        _NAND_X_IN_DEC = _pred_block_right_x + _PREDECODER_TO_NAND_GAP
    _tap_block_power(
        "row_decoder",
        vpwr_local=(_NAND_X_IN_DEC + 4.38, 0.85),
        vgnd_local=(_NAND_X_IN_DEC + 1.905, 0.715),
    )
    _tap_block_power(
        "wl_driver",
        vpwr_local=(4.38, 0.85),
        vgnd_local=(1.905, 0.715),
    )
    _tap_block_power(
        "write_driver",
        vpwr_local=(1.48, 1.05),
        vgnd_local=(1.31, 3.13),
    )
    _tap_block_power(
        "sense_amp",
        vpwr_local=(1.89, 2.00),
        vgnd_local=(1.90, 0.385),
    )
    # ctrl_logic explicit power rails.  control_logic.py stacks DFF
    # row (y in [0, 7.545]) + inter-row gap (2.0) + NAND2 row (y in
    # [9.545, 12.235]).  VPWR rail at y = cell_h + 0.3 = 12.535;
    # VGND rail at y = -0.5.  An earlier comment misread cell_h as
    # just the NAND2 row's height (1.99) and put the VPWR tap at
    # y=2.29 — that location is INSIDE DFF_0's body, where the .pin
    # shape overlapped foundry-internal DFF metal and bridged the
    # parent VPWR net to dff_3/CLK (Magic's extractor traced the
    # short via the DFF's internal connectivity).  The bridge then
    # propagated VPWR ↔ row_decoder/VPWR ↔ wl_driver/VPWR ↔ sa/VPWR
    # ↔ wd/VPWR through the per-row WL/dec_out wires, leaving every
    # block's VPWR labeled `clk` instead of VPWR.
    _tap_block_power(
        "control_logic",
        vpwr_local=(2.0, 12.535),
        vgnd_local=(2.0, -0.5),
    )

    # Precharge: VPWR-only (no VGND — all-PMOS pull-up row).  The cell's
    # VPWR rail is on met3 (not met1 like the other blocks), so
    # _tap_block_power's met1 tap won't physically merge.  Drop a parent
    # met3 .pin label at the precharge VPWR rail centre so Magic merges
    # the precharge VPWR net with macro VPWR by name AND geometry.
    # Without this tap, precharge's 256 PMOS sources end up on a
    # floating `pre_<tag>_0/VPWR` auto-net at the parent extraction.
    from rekolektion.peripherals.precharge import _VDD_RAIL_Y as _PRE_VDD_Y
    _pre_x, _pre_y = fp.positions["precharge"]
    _pre_w = fp.sizes["precharge"][0]
    _pre_vpwr_x = _pre_x + _pre_w / 2.0
    _pre_vpwr_y_abs = _pre_y + _PRE_VDD_Y
    draw_pin_with_label(
        top, text="VPWR", layer="met3",
        rect=(
            _pre_vpwr_x - _TAP_HALF, _pre_vpwr_y_abs - _TAP_HALF,
            _pre_vpwr_x + _TAP_HALF, _pre_vpwr_y_abs + _TAP_HALF,
        ),
        tracker=tracker, net="VPWR", cls="power",
    )


