"""Per-column column-mux row for v2 SRAM macros.

Uses rekolektion's native per-pair column_mux generator (see
`peripherals/column_mux.py`) at bitcell pitch 1.31 µm.  The generator
emits a cell whose width = num_pairs × 1.31 µm and whose height grows
with mux_ratio (each mux level adds ~1.2 µm of vertical stack).
Supports mux_ratio ∈ {2, 4, 8}.
"""
from __future__ import annotations

import gdstk

from rekolektion.peripherals.column_mux import generate_column_mux


_BITCELL_WIDTH: float = 1.31
# Height depends on mux_ratio (more mux levels -> more NMOS rows).
# Empirically measured from DRC-clean builds, kept as a lookup so the
# assembler's floorplan can query height without building the cell.
_COLMUX_HEIGHT_BY_MUX: dict[int, float] = {
    2: 4.14,
    4: 6.64,
    8: 11.64,
}
# Preserve the module-level constant other code imports as the "default".
_COLMUX_HEIGHT: float = _COLMUX_HEIGHT_BY_MUX[4]


class ColumnMuxRow:
    """Per-pair column-mux row covering every BL/BR pair."""

    def __init__(self, bits: int, mux_ratio: int, name: str | None = None):
        if bits < 1:
            raise ValueError(f"bits must be >=1; got {bits}")
        if mux_ratio not in (2, 4, 8):
            raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
        self.bits = bits
        self.mux_ratio = mux_ratio
        self.num_pairs = bits * mux_ratio
        self.top_cell_name = (
            name or f"column_mux_row_{bits}_mux{mux_ratio}"
        )

    @property
    def pitch(self) -> float:
        return _BITCELL_WIDTH

    @property
    def width(self) -> float:
        return self.num_pairs * _BITCELL_WIDTH

    @property
    def height(self) -> float:
        return _COLMUX_HEIGHT_BY_MUX[self.mux_ratio]

    def build(self) -> gdstk.Library:
        _, lib = generate_column_mux(
            num_pairs=self.num_pairs,
            mux_ratio=self.mux_ratio,
            pair_pitch=_BITCELL_WIDTH,
            cell_name=self.top_cell_name,
        )
        return lib
