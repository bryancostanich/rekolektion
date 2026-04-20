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
from rekolektion.macro_v2.bitcell_array import BitcellArray
from rekolektion.macro_v2.column_mux_row import ColumnMuxRow
from rekolektion.macro_v2.control_logic import ControlLogic
from rekolektion.macro_v2.precharge_row import PrechargeRow
from rekolektion.macro_v2.row_decoder import RowDecoder, _SPLIT_TABLE
from rekolektion.macro_v2.sense_amp_row import SenseAmpRow
from rekolektion.macro_v2.write_driver_row import WriteDriverRow


# Inter-block gaps (um). Loose initially; tightened after C6.1 DRC sweep.
_ARRAY_TO_PERIPH_GAP: float = 1.0
_DECODER_TO_ARRAY_GAP: float = 2.0
_CONTROL_ABOVE_GAP: float = 2.0

# Peripheral row heights — approximate foundry/custom cell heights.
# Real values resolve at assemble() time from the row objects' own
# `.height` properties; these constants exist to keep build_floorplan
# free of gdstk I/O so pure-geometry tests stay fast.
_PRECHARGE_H: float = 4.475
_COLMUX_H: float = 3.37
_SA_H: float = 3.05
_WD_H: float = 3.05

# ControlLogic stack height — DFF row + inter-row gap + NAND2 row.
_CTRL_H: float = 7.545 + 2.0 + 2.69

# Row decoder width placeholder (until C6.1 measures from the real build).
# Derived from predecoder width (~4 NAND2 ≈ 19 um) + NAND column (~5 um).
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

    lib.add(top)
    return lib
