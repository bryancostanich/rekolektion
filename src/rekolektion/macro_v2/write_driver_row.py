"""Write driver row for v2 SRAM macros.

Tiles the foundry write driver cell at `mux_ratio × bitcell_width` pitch —
one WD per bit (`bits` cells across). At mux_ratio >= 2, the foundry WD's
2.5 µm width fits in the mux group pitch.
"""
from __future__ import annotations

from pathlib import Path

import gdstk


_BITCELL_WIDTH: float = 1.31
_WD_WIDTH: float = 2.5
_WD_HEIGHT: float = 10.055
_WD_CELL_NAME: str = "sky130_fd_bd_sram__openram_write_driver"
_WD_GDS: Path = (
    Path(__file__).parent.parent
    / f"peripherals/cells/{_WD_CELL_NAME}.gds"
)


class WriteDriverRow:
    """Row of write drivers pitched to the bitcell array's mux groups."""

    def __init__(self, bits: int, mux_ratio: int, name: str | None = None):
        if bits < 1:
            raise ValueError(f"bits must be >=1; got {bits}")
        if mux_ratio not in (2, 4, 8):
            raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
        pitch = mux_ratio * _BITCELL_WIDTH
        if _WD_WIDTH > pitch:
            raise ValueError(
                f"write_driver ({_WD_WIDTH} um) does not fit in "
                f"mux_ratio={mux_ratio} pitch ({pitch} um)."
            )
        self.bits = bits
        self.mux_ratio = mux_ratio
        self.pitch = pitch
        self.top_cell_name = name or f"write_driver_row_{bits}_mux{mux_ratio}"

    @property
    def width(self) -> float:
        return self.bits * self.pitch

    @property
    def height(self) -> float:
        return _WD_HEIGHT

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        wd_cell = self._import_cell(lib)
        for i in range(self.bits):
            origin = (i * self.pitch, 0.0)
            top.add(gdstk.Reference(wd_cell, origin=origin))

        lib.add(top)
        return lib

    def _import_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        src = gdstk.read_gds(str(_WD_GDS))
        imported: dict[str, gdstk.Cell] = {}
        for c in src.cells:
            copy = c.copy(c.name)
            imported[c.name] = copy
            lib.add(copy)
        return imported[_WD_CELL_NAME]
