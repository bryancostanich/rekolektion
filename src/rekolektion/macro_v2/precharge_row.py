"""Per-mux-group precharge row for v2 SRAM macros.

Tiles rekolektion's custom `precharge_0` cell at `mux_ratio × bitcell_width`
pitch — one precharge cell per mux group. Each precharge cell drives the
BL/BR pair of the selected column within its group (the column mux row
below picks which of the mux_ratio columns is active).

At mux=4 or mux=8, precharge_0's 3.12 µm width fits comfortably in the mux
group's 5.24 / 10.48 µm pitch. At mux=2 (2.62 µm pitch), the cell doesn't
fit — the generator raises ValueError in that case.
"""
from __future__ import annotations

from pathlib import Path

import gdstk


_BITCELL_WIDTH: float = 1.31
_PRECHARGE_WIDTH: float = 3.12
_PRECHARGE_HEIGHT: float = 3.98
_PRECHARGE_CELL_NAME: str = "precharge_0"
_PRECHARGE_GDS: Path = (
    Path(__file__).parent.parent / "peripherals/cells/precharge_0.gds"
)


class PrechargeRow:
    """Row of precharge cells pitched to the bitcell array's mux groups."""

    def __init__(self, bits: int, mux_ratio: int, name: str | None = None):
        if bits < 1:
            raise ValueError(f"bits must be >=1; got {bits}")
        if mux_ratio not in (2, 4, 8):
            raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
        pitch = mux_ratio * _BITCELL_WIDTH
        if _PRECHARGE_WIDTH > pitch:
            raise ValueError(
                f"precharge_0 ({_PRECHARGE_WIDTH} um) does not fit in "
                f"mux_ratio={mux_ratio} pitch ({pitch} um). Need mux_ratio >= 4."
            )
        self.bits = bits
        self.mux_ratio = mux_ratio
        self.pitch = pitch
        self.top_cell_name = name or f"precharge_row_{bits}_mux{mux_ratio}"

    @property
    def width(self) -> float:
        return self.bits * self.pitch

    @property
    def height(self) -> float:
        return _PRECHARGE_HEIGHT

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        pc_cell = self._import_cell(lib)
        for i in range(self.bits):
            origin = (i * self.pitch, 0.0)
            top.add(gdstk.Reference(pc_cell, origin=origin))

        lib.add(top)
        return lib

    def _import_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        src = gdstk.read_gds(str(_PRECHARGE_GDS))
        imported: dict[str, gdstk.Cell] = {}
        for c in src.cells:
            copy = c.copy(c.name)
            imported[c.name] = copy
            lib.add(copy)
        return imported[_PRECHARGE_CELL_NAME]
