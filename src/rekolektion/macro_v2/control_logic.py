"""Control block for v2 SRAM macros.

Generates internal enable signals from external clk/we/cs:
    clk_buf   — buffered clock for DFFs driving the rest of the macro
    wl_en     — word-line enable (gates the decoder output)
    p_en_bar  — precharge enable, active low
    s_en      — sense-amp enable (fires after RBL detect or delay expires)
    w_en      — write enable (gates the write drivers)

Two timing-source variants (spec decision 1):
    use_replica=True   — expects `rbl_in` pin driven by the replica column
                         in the bitcell array (wired in C6). Self-timed; robust
                         across PVT. This is the production config.
    use_replica=False  — generates s_en via a synthesised delay chain of
                         back-to-back NAND gates. Simpler but PVT-sensitive;
                         kept as a bring-up/diagnostic fallback.

C5 emits *structural placement only* — DFF + NAND cells are tiled, but the
internal logic wiring happens during C6 assembler alongside the bitcell
array. SPICE / LVS verification is deferred to C8.
"""
from __future__ import annotations

from pathlib import Path

import gdstk


_DFF_CELL_NAME = "sky130_fd_bd_sram__openram_dff"
_NAND2_CELL_NAME = "sky130_fd_bd_sram__openram_sp_nand2_dec"

_CELLS_DIR: Path = Path(__file__).parent.parent / "peripherals/cells"
_DFF_GDS_PATH: Path = _CELLS_DIR / f"{_DFF_CELL_NAME}.gds"
_NAND2_GDS_PATH: Path = _CELLS_DIR / f"{_NAND2_CELL_NAME}.gds"

# One DFF per output-enable register (clk_buf, p_en_bar, s_en, w_en).
_NUM_OUTPUT_DFFS: int = 4
# NAND2 gates used for combinational control logic (we∧cs, cs∧clk, etc.).
_NUM_CONTROL_NAND2S: int = 2
# Delay chain depth when use_replica=False — pairs of NAND2s form an inverter
# pair, giving a coarse self-timing pulse. 6 stages ≈ replica-column latency.
_DELAY_CHAIN_STAGES: int = 6

# OpenRAM std-cell-style cells (DFF / NAND_dec) are designed to abut edge-to-
# edge — their N-wells extend to the cell boundary and merge into one when
# tiled. Leaving even a small gap creates an N-well spacing violation
# (sky130 nwell.2a, min 1.27 µm).
_INTER_CELL_GAP: float = 0.0
# Vertical gap between the DFF row and the NAND row above. Nwell rules:
# DFF Nwell tops at y=7.335; NAND2 Nwell starts 0.7 µm below its origin.
# For nwell.2a (min 1.27 µm spacing): y_nand - 0.7 - 7.335 >= 1.27
# → y_nand >= 9.305, i.e. gap >= 9.305 - dff_h (7.545) = 1.76.
# Use 2.0 µm for a small margin.
_INTER_ROW_GAP: float = 2.0


class ControlLogic:
    """Placement-only skeleton of the SRAM control block.

    Exposes `use_replica` to select timing source; cell composition is
    identical up to the delay-chain extension when use_replica=False.
    """

    def __init__(
        self,
        use_replica: bool = True,
        name: str | None = None,
    ):
        self.use_replica = use_replica
        self.top_cell_name = name or (
            "ctrl_logic_rbl" if use_replica else "ctrl_logic_delay"
        )

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)
        seen: set[str] = set()

        dff_cell = self._import_cell(lib, _DFF_GDS_PATH, _DFF_CELL_NAME, seen)
        nand2_cell = self._import_cell(
            lib, _NAND2_GDS_PATH, _NAND2_CELL_NAME, seen
        )

        dff_bb = dff_cell.bounding_box()
        dff_w = dff_bb[1][0] - dff_bb[0][0]
        dff_h = dff_bb[1][1] - dff_bb[0][1]
        nand_bb = nand2_cell.bounding_box()
        nand_w = nand_bb[1][0] - nand_bb[0][0]

        # Row 0: DFFs for output enables.
        x = 0.0
        for _ in range(_NUM_OUTPUT_DFFS):
            top.add(gdstk.Reference(dff_cell, origin=(x, 0.0)))
            x += dff_w + _INTER_CELL_GAP

        # Row 1: NAND2 combinational gates (above DFFs).
        y_nand = dff_h + _INTER_ROW_GAP
        x = 0.0
        for _ in range(_NUM_CONTROL_NAND2S):
            top.add(gdstk.Reference(nand2_cell, origin=(x, y_nand)))
            x += nand_w + _INTER_CELL_GAP

        # Delay chain: NAND2s back-to-back when use_replica=False.
        if not self.use_replica:
            x_delay = x + _INTER_CELL_GAP
            for _ in range(_DELAY_CHAIN_STAGES):
                top.add(gdstk.Reference(nand2_cell, origin=(x_delay, y_nand)))
                x_delay += nand_w + _INTER_CELL_GAP

        lib.add(top)
        return lib

    def _import_cell(
        self,
        lib: gdstk.Library,
        gds_path: Path,
        cell_name: str,
        seen: set[str],
    ) -> gdstk.Cell:
        src = gdstk.read_gds(str(gds_path))
        for c in src.cells:
            if c.name in seen:
                continue
            lib.add(c.copy(c.name))
            seen.add(c.name)
        return next(c for c in lib.cells if c.name == cell_name)
