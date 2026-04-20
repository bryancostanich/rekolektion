"""Pitch-matched bitcell array generator for v2 SRAM macros.

Tiles the foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` bitcell in an
R×C grid with X/Y mirror pattern for power-rail sharing and diffusion
continuity. After tiling, emits per-row WL and per-col BL/BR labels
for clean extraction (fixes the WL-merge problem observed in v1).
"""
from __future__ import annotations

import gdstk

from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
from rekolektion.macro_v2.routing import draw_label


# Foundry bitcell's internal WL y-coordinate (cell-local, µm). The opt1 cell
# does not declare WL as a LEF pin; the WL runs horizontally on met1 near
# the cell's vertical center. Value derived from v1 tiler's analysis of the
# LEF met2 OBS midpoint (~0.38 µm from cell bottom).
_FOUNDRY_WL_Y_IN_CELL: float = 0.38


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

        self._add_wl_labels(top)

        lib.add(top)
        return lib

    def _add_wl_labels(self, top: gdstk.Cell) -> None:
        """Draw a met1.label `wl_0_<row>` at each row's WL y-coordinate.

        For un-mirrored rows (row % 2 == 0), the bitcell's internal WL is at
        y = row_origin + _FOUNDRY_WL_Y_IN_CELL. For X-mirrored rows (odd),
        the cell flips so the WL ends up at
            row_origin + cell_height - _FOUNDRY_WL_Y_IN_CELL.
        """
        for row in range(self.rows):
            row_y0 = row * self._cell_h
            if row % 2 == 0:
                wl_y = row_y0 + _FOUNDRY_WL_Y_IN_CELL
            else:
                wl_y = row_y0 + self._cell_h - _FOUNDRY_WL_Y_IN_CELL
            # Place label inside column 0 of the row (small x offset)
            draw_label(
                top,
                text=f"wl_0_{row}",
                layer="met1",
                position=(self._cell_w * 0.5, wl_y),
            )

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
