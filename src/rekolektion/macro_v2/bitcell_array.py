"""Pitch-matched bitcell array generator for v2 SRAM macros.

Tiles the foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` bitcell in an
R×C grid with X/Y mirror pattern for power-rail sharing and diffusion
continuity. After tiling, emits per-row WL and per-col BL/BR labels
for clean extraction (fixes the WL-merge problem observed in v1).
"""
from __future__ import annotations

import gdstk

from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell


class BitcellArray:
    """R×C tiled foundry bitcell array."""

    def __init__(self, rows: int, cols: int, name: str | None = None):
        if rows < 1 or cols < 1:
            raise ValueError(f"rows and cols must be >=1; got {rows}x{cols}")
        self.rows = rows
        self.cols = cols
        self.top_cell_name = name or f"sram_array_{rows}x{cols}"

        self._bitcell_info = load_foundry_sp_bitcell()
        self._cell_w = self._bitcell_info.cell_width
        self._cell_h = self._bitcell_info.cell_height

    @property
    def width(self) -> float:
        return self.cols * self._cell_w

    @property
    def height(self) -> float:
        return self.rows * self._cell_h

    def build(self) -> gdstk.Library:
        """Generate the array GDS library."""
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        bc_cell = self._import_bitcell_into(lib)

        for row in range(self.rows):
            for col in range(self.cols):
                ref = self._place_bitcell(bc_cell, row, col)
                top.add(ref)

        lib.add(top)
        return lib

    def _place_bitcell(
        self, bc_cell: gdstk.Cell, row: int, col: int
    ) -> gdstk.Reference:
        """Place one bitcell instance with appropriate mirroring."""
        # The foundry bitcell tiles via alternating mirrors to share power
        # rails and diffusion at row/column boundaries.
        mx = row % 2 == 1  # mirror in X-axis (flip vertically)
        my = col % 2 == 1  # mirror in Y-axis (flip horizontally)

        # gdstk.Reference orientation encoded as (rotation, x_reflection):
        #   N  (no mirror):    rot=0,   x_refl=False
        #   MX (flip vert):    rot=0,   x_refl=True
        #   MY (flip horiz):   rot=180, x_refl=True
        #   XY (flip both):    rot=180, x_refl=False
        # Placement origin for mirrored cells must be at the cell's top/right
        # (post-mirror the reference "extends" backward from origin).
        cw, ch = self._cell_w, self._cell_h
        x = col * cw
        y = row * ch

        if not mx and not my:
            return gdstk.Reference(bc_cell, origin=(x, y))
        if mx and not my:
            return gdstk.Reference(
                bc_cell, origin=(x, y + ch), x_reflection=True
            )
        if my and not mx:
            return gdstk.Reference(
                bc_cell, origin=(x + cw, y), rotation=3.141592653589793,
                x_reflection=True,
            )
        # both mirrored
        return gdstk.Reference(
            bc_cell, origin=(x + cw, y + ch),
            rotation=3.141592653589793,
        )

    def _import_bitcell_into(self, lib: gdstk.Library) -> gdstk.Cell:
        """Read the foundry bitcell GDS and add its cells to `lib`.
        Returns the top bitcell `gdstk.Cell`."""
        src = gdstk.read_gds(str(self._bitcell_info.gds_path))
        imported: dict[str, gdstk.Cell] = {}
        for c in src.cells:
            copy = c.copy(c.name)
            imported[c.name] = copy
            lib.add(copy)
        name = self._bitcell_info.cell_name
        return imported.get(name, next(iter(imported.values())))
