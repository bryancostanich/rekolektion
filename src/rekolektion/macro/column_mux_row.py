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
_STRAP_WIDTH: float = 1.41   # foundry sky130_fd_bd_sram__sram_sp_wlstrap LEF SIZE
# Height depends on mux_ratio. Values come from column_mux.py's cell_h
# formula: sel_first_y + (M-1)*sel_pitch + RAIL_W/2 + 0.14, where
# sel_first_y=2.425, sel_pitch=0.80, RAIL_W=0.40.
#   M=2: 2.425 + 1*0.80 + 0.20 + 0.14 = 3.565
#   M=4: 2.425 + 3*0.80 + 0.20 + 0.14 = 5.165
#   M=8: 2.425 + 7*0.80 + 0.20 + 0.14 = 8.365
_COLMUX_HEIGHT_BY_MUX: dict[int, float] = {
    2: 3.565,
    4: 5.165,
    8: 8.365,
}
# Preserve the module-level constant other code imports as the "default".
_COLMUX_HEIGHT: float = _COLMUX_HEIGHT_BY_MUX[4]


class ColumnMuxRow:
    """Per-pair column-mux row covering every BL/BR pair."""

    def __init__(
        self,
        bits: int,
        mux_ratio: int,
        name: str | None = None,
        strap_interval: int = 0,
    ):
        if bits < 1:
            raise ValueError(f"bits must be >=1; got {bits}")
        if mux_ratio not in (2, 4, 8):
            raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
        self.bits = bits
        self.mux_ratio = mux_ratio
        self.num_pairs = bits * mux_ratio
        self.strap_interval = max(0, int(strap_interval))
        self.top_cell_name = (
            name or f"column_mux_row_{bits}_mux{mux_ratio}"
        )

    @property
    def pitch(self) -> float:
        return _BITCELL_WIDTH

    @property
    def width(self) -> float:
        if self.strap_interval > 0:
            n_straps = (self.num_pairs - 1) // self.strap_interval
            return self.num_pairs * _BITCELL_WIDTH + n_straps * _STRAP_WIDTH
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
            strap_interval=self.strap_interval,
            strap_width=_STRAP_WIDTH if self.strap_interval > 0 else 0.0,
        )
        return lib
