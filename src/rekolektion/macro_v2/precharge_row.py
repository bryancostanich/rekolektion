"""Per-column precharge row for v2 SRAM macros.

Uses rekolektion's native per-pair precharge generator (see
`peripherals/precharge.py`) at bitcell pitch 1.31 µm so every BL/BR
pair has its own precharge stage (D2 Option 3).  Total row width =
bits × mux_ratio × 1.31 µm.

Supports mux_ratio ∈ {2, 4, 8}.  No ValueError for mux=2 — the
generator fits in 1.31 µm per pair regardless of mux ratio.
"""
from __future__ import annotations

import gdstk

from rekolektion.peripherals.precharge import generate_precharge


_BITCELL_WIDTH: float = 1.31
# Height is determined by the precharge generator at bitcell pitch.
# Derived empirically from a DRC-clean build; kept as a module constant
# so the assembler's floorplan doesn't have to build the cell to get
# its height.
_PRECHARGE_HEIGHT: float = 4.56


class PrechargeRow:
    """One-big-cell precharge row covering every BL/BR pair in the array."""

    def __init__(self, bits: int, mux_ratio: int, name: str | None = None):
        if bits < 1:
            raise ValueError(f"bits must be >=1; got {bits}")
        if mux_ratio not in (2, 4, 8):
            raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
        self.bits = bits
        self.mux_ratio = mux_ratio
        self.num_pairs = bits * mux_ratio
        self.top_cell_name = (
            name or f"precharge_row_{bits}_mux{mux_ratio}"
        )

    @property
    def pitch(self) -> float:
        return _BITCELL_WIDTH

    @property
    def width(self) -> float:
        return self.num_pairs * _BITCELL_WIDTH

    @property
    def height(self) -> float:
        return _PRECHARGE_HEIGHT

    def build(self) -> gdstk.Library:
        """Emit the precharge cell directly into a library.

        The generator already produces a single cell spanning all
        pairs, so no top-level tiling is needed.
        """
        _, lib = generate_precharge(
            num_pairs=self.num_pairs,
            pair_pitch=_BITCELL_WIDTH,
            cell_name=self.top_cell_name,
        )
        return lib
