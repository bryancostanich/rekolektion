"""k-to-2^k hierarchical predecoder (k in {2, 3}) for v2 SRAM row decoder.

A predecoder takes k address bits and produces 2^k one-hot outputs
(exactly one output HIGH for each unique address value).

This phase emits *structural placement only* — NAND cells for each output
are tiled in a row, but the internal wiring (which input/inverse-input
goes to which NAND) happens during C6 assembler when the decoder is
wired up alongside the array. Inverters for NOT-addr lines are also
deferred to wiring time (constructed as NAND2-with-inputs-tied).
"""
from __future__ import annotations

from pathlib import Path

import gdstk

from rekolektion.macro_v2.row_decoder import _NAND_CELL_NAMES, _NAND_GDS_PATHS


class Predecoder:
    """k-to-2^k one-hot predecoder. k in {2, 3}."""

    def __init__(self, num_inputs: int, name: str | None = None):
        if num_inputs not in (2, 3):
            raise ValueError(
                f"num_inputs must be 2 or 3; got {num_inputs}"
            )
        self.num_inputs = num_inputs
        self.num_outputs = 2 ** num_inputs
        self.top_cell_name = name or f"predecoder_{num_inputs}to{self.num_outputs}"

    @property
    def _nand_cell_name(self) -> str:
        # 2-input predecoder uses NAND2, 3-input uses NAND3
        return _NAND_CELL_NAMES[self.num_inputs]

    @property
    def _nand_gds_path(self) -> Path:
        return _NAND_GDS_PATHS[self.num_inputs]

    def build(self) -> gdstk.Library:
        """Generate the predecoder GDS — placement only, no routing."""
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        nand_cell = self._import_nand(lib)
        bb = nand_cell.bounding_box()
        nand_w = bb[1][0] - bb[0][0]

        # Tile `num_outputs` NAND cells horizontally
        for i in range(self.num_outputs):
            top.add(gdstk.Reference(nand_cell, origin=(i * nand_w, 0.0)))

        lib.add(top)
        return lib

    def _import_nand(self, lib: gdstk.Library) -> gdstk.Cell:
        src = gdstk.read_gds(str(self._nand_gds_path))
        imported: dict[str, gdstk.Cell] = {}
        for c in src.cells:
            copy = c.copy(c.name)
            imported[c.name] = copy
            lib.add(copy)
        return imported[self._nand_cell_name]
