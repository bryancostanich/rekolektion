"""Pitch-matched bitcell array generator for v2 SRAM macros.

Tiles the foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` bitcell in an
R×C grid with X/Y mirror pattern for power-rail sharing and diffusion
continuity. After tiling, emits per-row WL and per-col BL/BR labels
for clean extraction (fixes the WL-merge problem observed in v1).
"""
from __future__ import annotations

import gdstk

from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
from rekolektion.macro_v2.routing import draw_label, draw_pin, draw_wire


# Foundry bitcell's internal WL label position (cell-local, µm). The opt1
# cell's WL is the poly gate of the access transistors, not a metal wire.
# The bitcell's own "WL" text label is on poly.label (66/5) at (0.605, 1.385).
# We place our per-row override label at the same coordinate so Magic's
# extractor uses our `wl_0_<row>` name instead of the inherited "WL".
_FOUNDRY_WL_LABEL_X: float = 0.605
_FOUNDRY_WL_LABEL_Y: float = 1.385

# Foundry bitcell's BL/BR x-coordinates (cell-local, µm). Per the full
# foundry LEF `sky130_fd_bd_sram__sram_sp_cell_opt1.magic.lef`:
#   BL  met1 rail  RECT 0.350 0.000 0.490 1.435  -> x-centre 0.420
#   BR  met1 rail  RECT 0.710 0.145 0.850 1.580  -> x-centre 0.780
# (Earlier versions of this file misread the LEF and put these at
# 0.0425 / 1.1575 — those are VGND's pin rect x-centre, not BL's. The
# bug caused our BL strip to physically overlap VGND inside every
# bitcell, shorting BL to VGND across the array.)
_FOUNDRY_BL_X_IN_CELL: float = 0.420
_FOUNDRY_BR_X_IN_CELL: float = 0.780
_FOUNDRY_BL_LABEL_X: float = 0.420
_FOUNDRY_BR_LABEL_X: float = 0.780
_FOUNDRY_BLBR_LABEL_Y: float = 1.130


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
        self._add_bl_br_labels(top)

        lib.add(top)
        return lib

    def _add_wl_labels(self, top: gdstk.Cell) -> None:
        """Add per-row WL as spanning poly strips in the top cell.

        For each row, draw a horizontal poly strip at the row's WL y-coord
        spanning the full array width. The strip overlaps every bitcell's
        WL poly gate in that row, electrically joining them into one net.
        Add a poly.pin rect and poly.label at one end so Magic extracts it
        as a named external port `wl_0_<row>`.

        For X-mirrored rows (row % 2 == 1), the cell flips so the internal
        WL position moves to (cell_height - _FOUNDRY_WL_LABEL_Y). The
        poly-strip y-coord follows.
        """
        from rekolektion.macro_v2.sky130_drc import MFG_GRID
        WL_STRIP_W = 0.18  # wider than min poly (0.15) to guarantee overlap
        for row in range(self.rows):
            row_y0 = row * self._cell_h
            if row % 2 == 0:
                wl_y = row_y0 + _FOUNDRY_WL_LABEL_Y
            else:
                wl_y = row_y0 + self._cell_h - _FOUNDRY_WL_LABEL_Y

            # Spanning horizontal poly strip — drawn on poly (66/20).
            # Extend westward of the array bbox by `_WL_STRIP_WEST_EXT`
            # so the parent's `_route_wl` poly→met1 via stack at
            # x = array_left − _WL_VIA_ARRAY_GAP lands on this strip
            # (the pad is centred 0.3 µm west of array_left and is
            # 0.43 µm wide, so its east edge is 0.085 µm short of the
            # array's nominal left edge — without extension, the
            # pad overlaps NOTHING and Magic emits no merge between
            # wl_driver_X/wl_X and array/wl_0_X).  With the staggered
            # odd-row via at array_left − 1.0, we need the strip to
            # extend at least 1.215 µm west of x=0 cell-local.
            _WL_STRIP_WEST_EXT = 1.5
            draw_wire(
                top,
                start=(-_WL_STRIP_WEST_EXT, wl_y),
                end=(self.width, wl_y),
                layer="poly",
                width=WL_STRIP_W,
            )
            # Pin + label at the (extended) left end
            pin_extent = 0.14
            draw_pin(
                top,
                layer="poly",
                rect=(
                    -_WL_STRIP_WEST_EXT,
                    wl_y - WL_STRIP_W / 2,
                    -_WL_STRIP_WEST_EXT + pin_extent,
                    wl_y + WL_STRIP_W / 2,
                ),
            )
            draw_label(
                top,
                text=f"wl_0_{row}",
                layer="poly",
                position=(-_WL_STRIP_WEST_EXT + pin_extent / 2, wl_y),
            )

    def _add_bl_br_labels(self, top: gdstk.Cell) -> None:
        """Add per-col BL/BR as spanning met1 strips in the top cell.

        For each column, draw vertical met1 strips at the BL and BR
        x-coordinates spanning the full array height. The strips overlap
        every bitcell's BL/BR met1 pin in that column. Pin+label at the
        bottom of each strip exposes as a named external port.

        Foundry bitcell's BL pin is on met1 at cell-local x=[0.000, 0.085];
        BR is on met1 at cell-local x=[1.115, 1.200]. Our strip is wider
        than min met1 to guarantee overlap.
        """
        BL_STRIP_W = 0.14
        for col in range(self.cols):
            col_x0 = col * self._cell_w
            # All columns placed un-mirrored — no Y-mirror complexity.
            bl_x = col_x0 + _FOUNDRY_BL_X_IN_CELL
            br_x = col_x0 + _FOUNDRY_BR_X_IN_CELL

            for xc, prefix in ((bl_x, "bl"), (br_x, "br")):
                # Spanning vertical met1 strip
                draw_wire(
                    top,
                    start=(xc, 0.0),
                    end=(xc, self.height),
                    layer="met1",
                    width=BL_STRIP_W,
                )
                # Pin + label at bottom
                pin_extent = 0.14
                draw_pin(
                    top,
                    layer="met1",
                    rect=(
                        xc - BL_STRIP_W / 2,
                        0.0,
                        xc + BL_STRIP_W / 2,
                        pin_extent,
                    ),
                )
                draw_label(
                    top,
                    text=f"{prefix}_0_{col}",
                    layer="met1",
                    position=(xc, pin_extent / 2),
                )

    def _place_bitcell(
        self, bc_cell: gdstk.Cell, row: int, col: int
    ) -> gdstk.Reference:
        """Place one bitcell instance with X-mirror for odd rows.

        Only X-mirror (vertical flip) is used to share power rails between
        adjacent rows. We do NOT Y-mirror columns because the foundry cell
        has BL only on the left side — Y-mirroring would cause adjacent
        cols' BL rails to nearly abut (0.085 µm gap), which is less than
        the min met1 width we use for spanning strips, leading to merged
        nets at extraction time.

        All columns share the same orientation per row.
        """
        cw, ch = self._cell_w, self._cell_h
        x = col * cw
        y = row * ch
        if row % 2 == 0:
            return gdstk.Reference(bc_cell, origin=(x, y))
        else:
            # X-mirror (flip vertically around the cell's bottom edge), so
            # the placement origin shifts up by cell height.
            return gdstk.Reference(
                bc_cell, origin=(x, y + ch), x_reflection=True
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
