"""Column mux row for v2 SRAM macros.

Tiles rekolektion's custom `single_level_column_mux` cell at
`mux_ratio × bitcell_width` pitch. Each column mux cell selects one
BL/BR pair from its mux group to pass up to the sense amp / down from
the write driver.

Column mux cell is 3.37 µm wide; fits at mux=4 (5.24 µm pitch) and
mux=8. At mux=2 (2.62 µm pitch), the cell doesn't fit — generator
raises ValueError.
"""
from __future__ import annotations

from pathlib import Path

import gdstk


_BITCELL_WIDTH: float = 1.31
_COLMUX_WIDTH: float = 3.37
_COLMUX_HEIGHT: float = 6.82
_COLMUX_CELL_NAME: str = "single_level_column_mux"
_COLMUX_GDS: Path = (
    Path(__file__).parent.parent
    / f"peripherals/cells/{_COLMUX_CELL_NAME}.gds"
)


class ColumnMuxRow:
    """Row of column-mux cells pitched to the bitcell array's mux groups."""

    def __init__(self, bits: int, mux_ratio: int, name: str | None = None):
        if bits < 1:
            raise ValueError(f"bits must be >=1; got {bits}")
        if mux_ratio not in (2, 4, 8):
            raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
        pitch = mux_ratio * _BITCELL_WIDTH
        if _COLMUX_WIDTH > pitch:
            raise ValueError(
                f"single_level_column_mux ({_COLMUX_WIDTH} um) does not fit "
                f"in mux_ratio={mux_ratio} pitch ({pitch} um). "
                f"Need mux_ratio >= 4."
            )
        self.bits = bits
        self.mux_ratio = mux_ratio
        self.pitch = pitch
        self.top_cell_name = name or f"column_mux_row_{bits}_mux{mux_ratio}"

    @property
    def width(self) -> float:
        return self.bits * self.pitch

    @property
    def height(self) -> float:
        return _COLMUX_HEIGHT

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        cm_cell = self._import_cell(lib)
        for i in range(self.bits):
            origin = (i * self.pitch, 0.0)
            top.add(gdstk.Reference(cm_cell, origin=origin))

        lib.add(top)
        return lib

    def _import_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        src = gdstk.read_gds(str(_COLMUX_GDS))
        imported: dict[str, gdstk.Cell] = {}
        for c in src.cells:
            copy = c.copy(c.name)
            imported[c.name] = copy
            lib.add(copy)
        return imported[_COLMUX_CELL_NAME]
