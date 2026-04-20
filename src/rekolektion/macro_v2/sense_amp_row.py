"""Sense amp row for v2 SRAM macros.

Tiles the foundry sense amp cell at `mux_ratio × bitcell_width` pitch —
one SA per bit (`bits` cells across). At mux_ratio >= 2, the foundry SA's
2.5 µm width fits in the mux group pitch.
"""
from __future__ import annotations

from pathlib import Path

import gdstk


_BITCELL_WIDTH: float = 1.31
_SA_WIDTH: float = 2.5
_SA_HEIGHT: float = 11.28
_SA_CELL_NAME: str = "sky130_fd_bd_sram__openram_sense_amp"
_SA_GDS: Path = (
    Path(__file__).parent.parent
    / f"peripherals/cells/{_SA_CELL_NAME}.gds"
)


class SenseAmpRow:
    """Row of sense amps pitched to the bitcell array's mux groups."""

    def __init__(self, bits: int, mux_ratio: int, name: str | None = None):
        if bits < 1:
            raise ValueError(f"bits must be >=1; got {bits}")
        if mux_ratio not in (2, 4, 8):
            raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
        pitch = mux_ratio * _BITCELL_WIDTH
        if _SA_WIDTH > pitch:
            raise ValueError(
                f"sense_amp ({_SA_WIDTH} um) does not fit in "
                f"mux_ratio={mux_ratio} pitch ({pitch} um)."
            )
        self.bits = bits
        self.mux_ratio = mux_ratio
        self.pitch = pitch
        self.top_cell_name = name or f"sense_amp_row_{bits}_mux{mux_ratio}"

    @property
    def width(self) -> float:
        return self.bits * self.pitch

    @property
    def height(self) -> float:
        return _SA_HEIGHT

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        sa_cell = self._import_cell(lib)
        for i in range(self.bits):
            origin = (i * self.pitch, 0.0)
            top.add(gdstk.Reference(sa_cell, origin=origin))

        lib.add(top)
        return lib

    def _import_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        src = gdstk.read_gds(str(_SA_GDS))
        imported: dict[str, gdstk.Cell] = {}
        for c in src.cells:
            copy = c.copy(c.name)
            imported[c.name] = copy
            lib.add(copy)
        return imported[_SA_CELL_NAME]
