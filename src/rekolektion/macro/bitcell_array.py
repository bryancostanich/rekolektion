"""Pitch-matched bitcell array generator for v2 SRAM macros.

Tiles the foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` bitcell in an
R×C grid with X/Y mirror pattern for power-rail sharing and diffusion
continuity. After tiling, emits per-row WL and per-col BL/BR labels
for clean extraction (fixes the WL-merge problem observed in v1).
"""
from __future__ import annotations

import gdstk

from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
from rekolektion.macro.routing import draw_label, draw_pin, draw_wire


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
        """Add per-row WL as TWO spanning poly strips in the top cell.

        The foundry sram_sp_cell_opt1 has TWO physically-isolated WL poly
        stripes per cell: wl_top (labeled "WL" inside the cell) and
        wl_bot (unlabeled). Both gate access transistors in the cell.
        Without bridging, only wl_top gets connected across the row via
        Magic's label-name merge, while wl_bot is left fragmented (per-cell
        anonymous gates) — the chip's BR-side access transistors would
        be floating.

        Fix: emit TWO per-row strips at BOTH stripe Y positions. Both
        labeled the same `wl_0_<row>` so they merge by name into one
        electrical WL net per row. Per FT6/FT8b validation in the foundry
        tiler tests.

        Pre-condition: the foundry cell's internal "WL" label was stripped
        in `_import_bitcell_into` so it does not override our per-row
        labels (Magic resolves duplicate labels by picking ONE; if the
        foundry "WL" remains, it dominates and merges all rows globally).

        For Y-mirrored rows (row % 2 == 1), wl_top and wl_bot Y positions
        are swapped relative to the cell origin. The strips need to be at
        BOTH cell-local Y positions regardless — we just emit at
        (row_origin + 1.385) AND (row_origin + 0.195) for every row,
        which catches both stripes whether or not the cell is Y-mirrored.
        """
        WL_STRIP_W = 0.18  # wider than min poly (0.15) to guarantee overlap
        # Foundry cell's two WL poly stripe Y positions in cell-local µm.
        # Top stripe (originally labeled "WL"): y = 1.385
        # Bottom stripe (unlabeled):           y = 0.195
        WL_STRIPE_YS = (_FOUNDRY_WL_LABEL_Y, self._cell_h - _FOUNDRY_WL_LABEL_Y)
        _WL_STRIP_WEST_EXT = 1.5
        pin_extent = 0.14
        for row in range(self.rows):
            row_y0 = row * self._cell_h
            for stripe_y_local in WL_STRIPE_YS:
                wl_y = row_y0 + stripe_y_local
                draw_wire(
                    top,
                    start=(-_WL_STRIP_WEST_EXT, wl_y),
                    end=(self.width, wl_y),
                    layer="poly",
                    width=WL_STRIP_W,
                )
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

        Strips the foundry cell's internal "WL", "BL", and "BR" labels
        so the parent's per-row `wl_0_<row>` and per-col
        `bl_0_<col>` / `br_0_<col>` labels (added by `_add_wl_labels`
        and `_add_bl_br_labels`) win Magic's label name resolution.

        Without stripping, the foundry's global "WL"/"BL"/"BR" labels
        dominate and merge ALL rows' WL nets and ALL columns' BL/BR
        nets into single electrical nets — chip-killer false-positive
        LVS pattern.  WL fix is F11; BL/BR fix is F13 (production-scale
        LVS surfaced sparse `bl_0_<c>` disconnects = 128 cols × 2 BL/BR
        = 256 disconnected nodes per side, every column affected).

        Returns the top bitcell `gdstk.Cell`.
        """
        src = gdstk.read_gds(str(self._bitcell_info.gds_path))
        imported: dict[str, gdstk.Cell] = {}
        _STRIP = {"WL", "BL", "BR"}
        for c in src.cells:
            copy = c.copy(c.name)
            for label in [l for l in copy.labels if l.text in _STRIP]:
                copy.remove(label)
            imported[c.name] = copy
            lib.add(copy)
        name = self._bitcell_info.cell_name
        return imported.get(name, next(iter(imported.values())))
