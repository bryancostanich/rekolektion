"""Pitch-matched bitcell array generator for v2 SRAM macros.

Tiles the foundry `sky130_fd_bd_sram__sram_sp_cell_opt1` bitcell in an
R×C grid with Y-mirror per row for power-rail sharing and diffusion
continuity. After tiling, emits per-row WL and per-col BL/BR labels
for clean extraction (fixes the WL-merge problem observed in v1).

Body-bias path (audit T4.4-A / issue #9): inserts foundry
`sky130_fd_bd_sram__sram_sp_wlstrap` cells as periodic strap columns
to provide N-tap (NWELL→VPWR via real metal), AND adds parent-level
NWELL fill rectangles at every row boundary to bridge the column
gaps in bitcell NWELL geometry. Combined, every bitcell NWELL is in
the same physical cluster as a strap-anchored N-tap.
"""
from __future__ import annotations

from pathlib import Path

import gdstk

from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
from rekolektion.bitcell.sky130_cim_drain_bridge import BRIDGE_H
from rekolektion.bitcell.sky130_sp_bridged import (
    create_sp_bridged_cell,
    WRAPPER_NAME as _BRIDGED_CELL_NAME,
    WRAPPER_W as _BRIDGED_CELL_W,
    WRAPPER_H as _BRIDGED_CELL_H,
)
from rekolektion.bitcell.sky130_sp_wlstrap_bridged import (
    create_sp_wlstrap_bridged_cell,
    WRAPPER_NAME as _BRIDGED_STRAP_NAME,
    WRAPPER_W as _BRIDGED_STRAP_W,
    WRAPPER_H as _BRIDGED_STRAP_H,
)
from rekolektion.macro.routing import draw_label, draw_pin, draw_wire


# Bridged wlstrap wrapper (sky130_sp_wlstrap_bridged.py) — same width
# as foundry wlstrap, but Y-pitch matches the bridged bitcell wrapper.
# Foundry wlstrap is shifted up by BRIDGE_H inside the wrapper, with
# NWELL filler at top and bottom.  This restores strap-row alignment
# after the bitcell pitch grew from 1.58 → 2.22 µm with the drain-
# bridge fix.
_WLSTRAP_NAME: str = _BRIDGED_STRAP_NAME
_WLSTRAP_W: float = _BRIDGED_STRAP_W   # 1.410
_WLSTRAP_H: float = _BRIDGED_STRAP_H   # 2.220


# Public — single source of truth for strap-aware column X position.
# Periphery row generators (precharge, column_mux, sense_amp,
# write_driver) and assembler routing functions both use this so a
# bitcell column at index `col` in the array sits at the same physical
# X as a periphery slot at the same `col`.
def strap_aware_col_x(
    col: int,
    cell_pitch: float = _BRIDGED_CELL_W,
    strap_interval: int = 0,
    strap_width: float = _WLSTRAP_W,
) -> float:
    """X-coordinate of bitcell column ``col`` accounting for strap insertions.

    With ``strap_interval=0`` (no straps) this is just ``col * cell_pitch``.
    With ``strap_interval=N>0``, a strap of width ``strap_width`` is inserted
    after every ``N`` bitcell columns; the X for column ``col`` shifts east
    by ``(col // N) * strap_width``.
    """
    if strap_interval <= 0:
        return col * cell_pitch
    n_straps_before = col // strap_interval
    return col * cell_pitch + n_straps_before * strap_width


def strap_aware_total_width(
    n_cols: int,
    cell_pitch: float = _BRIDGED_CELL_W,
    strap_interval: int = 0,
    strap_width: float = _WLSTRAP_W,
) -> float:
    """Total width of an n-column row accounting for inserted straps.

    Strap is inserted after every `strap_interval` columns up to but not
    including the last column (no trailing strap), matching
    `BitcellArray.n_strap_cols`'s `(cols - 1) // strap_interval` formula.
    """
    if strap_interval <= 0 or n_cols <= 1:
        return n_cols * cell_pitch
    n_straps = (n_cols - 1) // strap_interval
    return n_cols * cell_pitch + n_straps * strap_width


# Foundry bitcell's internal WL label position, expressed in
# bridged-wrapper-local coords (foundry cell is shifted up by BRIDGE_H
# inside the wrapper, so all foundry-internal Y positions add BRIDGE_H).
# The foundry's WL text label is on poly.label (66/5) at foundry-local
# (0.605, 1.385) → wrapper-local (0.605, BRIDGE_H + 1.385).
_FOUNDRY_WL_LABEL_X: float = 0.605
_FOUNDRY_WL_LABEL_Y: float = BRIDGE_H + 1.385

# Foundry bitcell's BL/BR x-coordinates (unchanged by Y shift).
#   BL  met1 rail  foundry-local x-centre 0.420
#   BR  met1 rail  foundry-local x-centre 0.780
_FOUNDRY_BL_X_IN_CELL: float = 0.420
_FOUNDRY_BR_X_IN_CELL: float = 0.780
_FOUNDRY_BL_LABEL_X: float = 0.420
_FOUNDRY_BR_LABEL_X: float = 0.780
# BL/BR label Y in wrapper-local coords (foundry-local 1.130 + BRIDGE_H).
_FOUNDRY_BLBR_LABEL_Y: float = BRIDGE_H + 1.130


class BitcellArray:
    """R×C tiled foundry bitcell array with body-bias body-tap fix."""

    def __init__(
        self,
        rows: int,
        cols: int,
        name: str | None = None,
        strap_interval: int = 8,
    ):
        if rows < 1 or cols < 1:
            raise ValueError(f"rows and cols must be >=1; got {rows}x{cols}")
        self.rows = rows
        self.cols = cols
        self.top_cell_name = name or f"sram_array_{rows}x{cols}"
        # Number of bitcell cols between strap inserts.  0 disables strap
        # insertion (legacy floating-NWELL behavior — only for tests).
        # Default 8 follows industry sky130 SRAM convention.  Each strap
        # adds an N+ tap to VPWR; combined with parent-level NWELL fill
        # at row boundaries, every bitcell NWELL physically reaches a tap.
        self.strap_interval = max(0, int(strap_interval))

        # Use the bridged wrapper (foundry + drain bridge cells) instead
        # of the unmodified foundry cell.  The wrapper closes the
        # T1.1-A drain-floating defect by adding LICON1+LI1+MCON+M1
        # contact stacks for both BL and BR drain rails.  Pitch grows
        # from 1.58 → 2.22 µm (+40%) but the bitcell becomes silicon-
        # functional.  See `sky130_sp_bridged.py` for the architecture.
        self._bitcell_info = load_foundry_sp_bitcell()
        self._cell_w = _BRIDGED_CELL_W
        self._cell_h = _BRIDGED_CELL_H

    @property
    def n_strap_cols(self) -> int:
        """Number of strap columns inserted between bitcell columns."""
        if self.strap_interval <= 0:
            return 0
        # Strap inserted after every `strap_interval` bitcells; no trailing strap.
        return (self.cols - 1) // self.strap_interval

    def _bitcell_x(self, col: int) -> float:
        """Array X for the leftmost edge of bitcell column `col`,
        accounting for strap columns inserted to its left."""
        if self.strap_interval <= 0:
            return col * self._cell_w
        n_straps_before = col // self.strap_interval
        return col * self._cell_w + n_straps_before * _WLSTRAP_W

    def _strap_x(self, strap_idx: int) -> float:
        """Array X for the leftmost edge of strap column `strap_idx`."""
        bitcells_before = (strap_idx + 1) * self.strap_interval
        return bitcells_before * self._cell_w + strap_idx * _WLSTRAP_W

    @property
    def width(self) -> float:
        return self.cols * self._cell_w + self.n_strap_cols * _WLSTRAP_W

    @property
    def height(self) -> float:
        return self.rows * self._cell_h

    def build(self) -> gdstk.Library:
        """Generate the array GDS library."""
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        bc_cell = self._import_bitcell_into(lib)
        strap_cell = (
            self._import_strap_into(lib) if self.strap_interval > 0 else None
        )

        for row in range(self.rows):
            for col in range(self.cols):
                ref = self._place_bitcell(bc_cell, row, col)
                top.add(ref)

        if strap_cell is not None:
            self._place_strap_columns(top, strap_cell)
            # Parent-level NWELL fill at row boundaries bridges the
            # bitcell NWELL fragments across columns.  Without this,
            # adjacent columns' NWELLs (right-half-only at x=[0.72, 1.20])
            # gap by 0.83 µm and stay isolated even with strap cells.
            # Foundry-cell PSDM (p-tap) at cell-local y=[0.71, 0.87] is
            # well clear of row boundaries (y=N*ch), so a thin NWELL
            # strip at each row boundary is DRC-safe.
            self._add_nwell_row_bridges(top)

        self._add_wl_labels(top)
        self._add_bl_br_labels(top)

        lib.add(top)
        return lib

    def _import_strap_into(self, lib: gdstk.Library) -> gdstk.Cell:
        """Build the bridged wlstrap wrapper and add its cells to `lib`.

        The wrapper is `sky130_fd_bd_sram__sram_sp_wlstrap_bridged`, which
        contains the unmodified foundry wlstrap as a sub-cell shifted up
        by BRIDGE_H, with NWELL filler at the BL-bridge and BR-Phase2
        zones to maintain NWELL continuity through the wrapper.
        """
        bridged_lib, bridged_top = create_sp_wlstrap_bridged_cell()
        existing = {c.name for c in lib.cells}
        result = None
        for c in bridged_lib.cells:
            if c.name in existing:
                if c.name == _WLSTRAP_NAME:
                    result = next(x for x in lib.cells if x.name == c.name)
                continue
            copy = c.copy(c.name)
            lib.add(copy)
            if c.name == _WLSTRAP_NAME:
                result = copy
        if result is None:
            raise RuntimeError(f"Failed to import {_WLSTRAP_NAME}")
        return result

    def _place_strap_columns(
        self, top: gdstk.Cell, strap_cell: gdstk.Cell
    ) -> None:
        """Place wlstrap cell at each strap-column X for every row.

        Same Y-mirror per-odd-row pattern as bitcells so VPWR/VGND M1
        rails abut and NWELL extends symmetrically.
        """
        for strap_idx in range(self.n_strap_cols):
            x = self._strap_x(strap_idx)
            for row in range(self.rows):
                y = row * self._cell_h
                if row % 2 == 0:
                    top.add(gdstk.Reference(strap_cell, origin=(x, y)))
                else:
                    top.add(gdstk.Reference(
                        strap_cell,
                        origin=(x, y + self._cell_h),
                        x_reflection=True,
                    ))

    def _add_nwell_row_bridges(self, top: gdstk.Cell) -> None:
        """Add NWELL strips at row boundaries to bridge column NWELLs.

        Important: bridge cells (`sky130_cim_drain_bridge_v1`) sit at
        the bottom of every wrapper, with NMOS NSDM-marked DIFF at
        wrapper-local x=[0.065, 0.455] y=[0, BRIDGE_H].  A full-width
        horizontal NWELL strip at certain row boundaries would
        partially cover this bridge DIFF — making it look like an
        N-tap with insufficient NWELL enclosure (diff/tap.10) AND
        creating a real silicon short between the bridge BL net and
        the NWELL VPB rail.

        Same architecture as `cim_supercell_array._add_nwell_row_bridges`:
          - Strips at EVEN K (row boundaries between mirrored-row top
            and unmirrored-row bottom, both with bridge cells abutting)
            are SEGMENTED with gaps at every column's bridge NSDM x-range.
          - Strips at ODD K (row boundaries between unmirrored-row top
            and mirrored-row bottom — where bridges are at OPPOSITE
            ends of their rows) can be FULL WIDTH, providing the
            cross-column NWELL bridging that segmented strips cannot.
        """
        nwell_id, nwell_dt = 64, 20
        T = 0.10
        ch = self._cell_h
        cw = self._cell_w
        x0 = -0.10
        x1 = self.width + 0.10
        BRIDGE_NSDM_W = 0.065
        BRIDGE_NSDM_E = 0.455

        def _emit_full(y_lo: float, y_hi: float) -> None:
            top.add(gdstk.rectangle(
                (x0, y_lo), (x1, y_hi),
                layer=nwell_id, datatype=nwell_dt,
            ))

        def _emit_segmented(y_lo: float, y_hi: float) -> None:
            seg_x_lo = x0
            for col in range(self.cols + self.n_strap_cols):
                # Bridge NSDM exists in BITCELL columns; strap columns
                # don't have bridges, so we skip the gap there.
                # We approximate by computing per-column bridge NSDM
                # x-position from the bitcell pitch.
                gap_lo = col * cw + BRIDGE_NSDM_W
                gap_hi = col * cw + BRIDGE_NSDM_E
                if gap_lo > seg_x_lo:
                    top.add(gdstk.rectangle(
                        (seg_x_lo, y_lo), (gap_lo, y_hi),
                        layer=nwell_id, datatype=nwell_dt,
                    ))
                seg_x_lo = gap_hi
            if x1 > seg_x_lo:
                top.add(gdstk.rectangle(
                    (seg_x_lo, y_lo), (x1, y_hi),
                    layer=nwell_id, datatype=nwell_dt,
                ))

        # Inter-row strip K: even K overlaps bridges, odd K is clear.
        for row in range(1, self.rows):
            by = row * ch
            if row % 2 == 0:
                _emit_segmented(by - T / 2, by + T / 2)
            else:
                _emit_full(by - T / 2, by + T / 2)
        # Bottom edge overlaps row 0's bridge (at the bottom of row 0);
        # top edge overlaps row (rows-1)'s mirrored bridge (at the top
        # of the last row).  Both must be segmented.
        _emit_segmented(-T, T / 2)
        _emit_segmented(self.height - T / 2, self.height + T)

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
            col_x0 = self._bitcell_x(col)
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
        x = self._bitcell_x(col)
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
        """Build the bridged bitcell wrapper and add its cells to `lib`.

        The wrapper is `sky130_fd_bd_sram__sram_sp_cell_bridged`, which
        contains the unmodified foundry sram_sp_cell_opt1 sub-cell, a
        sky130_cim_drain_bridge_v1 sub-cell at the bottom, and Phase 2
        BR contact-stack polygons in the annex above the foundry top.
        Pitch is `_BRIDGED_CELL_W × _BRIDGED_CELL_H` (1.31 × 2.22 µm).

        Strips foundry-internal "WL"/"BL"/"BR" labels from the foundry
        sub-cell so the parent's per-row/col labels win Magic's name
        resolution (F11/F13 mechanism — without this, all 128 rows'
        WL nets and all 128 cols' BL/BR nets collapse globally).

        Returns the wrapper top cell (sky130_fd_bd_sram__sram_sp_cell_bridged).
        """
        bridged_lib, bridged_top = create_sp_bridged_cell()
        _STRIP = {"WL", "BL", "BR"}
        # Copy every cell in the bridged-lib into the array's library,
        # stripping foundry-internal merge labels from the foundry copy.
        copied: dict[str, gdstk.Cell] = {}
        for c in bridged_lib.cells:
            copy = c.copy(c.name)
            if c.name == self._bitcell_info.cell_name:
                for label in [l for l in copy.labels if l.text in _STRIP]:
                    copy.remove(label)
            copied[c.name] = copy
            lib.add(copy)
        return copied[_BRIDGED_CELL_NAME]
