"""CIM supercell array builder — F8.

Tiles CIM supercells (foundry 6T + T7 + cap, foundry-cell-validated) in
an R×C grid with the F11-validated WL bridging mechanism:
  - Strip foundry cell's internal "WL" label so per-row labels win
  - Per-row dual WL POLY strips at BOTH wl_top and wl_bot Y positions

Replaces the LR-cell-based `CIMBitcellArray`.  Same interface so
`cim_assembler.py` can swap with minimal changes.
"""
from __future__ import annotations

from pathlib import Path
from typing import Optional

import gdstk

from rekolektion.bitcell.sky130_cim_supercell import (
    CIM_SUPERCELL_VARIANTS,
    create_cim_supercell,
)


# Foundry cell internal WL stripe Y positions (in foundry cell-local coords).
# These match _FOUNDRY_WL_LABEL_Y in macro/bitcell_array.py.
_FOUNDRY_WL_TOP_Y: float = 1.385
_FOUNDRY_WL_BOT_Y: float = 0.195
_FOUNDRY_LEF_H: float = 1.580

# Foundry cell BL/BR M1 X positions (cell-local µm) — matched against
# bitcell_array.py's _FOUNDRY_BL_X_IN_CELL/_FOUNDRY_BR_X_IN_CELL.
_FOUNDRY_BL_X: float = 0.420
_FOUNDRY_BR_X: float = 0.780


class CIMSupercellArray:
    """Tiled CIM supercell array with F11-style WL bridging.

    Each cell is a "supercell" wrapping the foundry 6T core + T7 + MIM cap.
    Tiling: same orientation per row (no column X-mirror — production
    macro convention), Y-mirror odd rows (for shared power rails).
    Per-row dual WL POLY strips bridge wl_top + wl_bot of every cell in
    the row.
    """

    def __init__(
        self,
        variant: str,
        rows: int,
        cols: int,
        name: Optional[str] = None,
    ):
        if variant not in CIM_SUPERCELL_VARIANTS:
            raise ValueError(
                f"Unknown CIM variant {variant!r}. "
                f"Valid: {sorted(CIM_SUPERCELL_VARIANTS)}"
            )
        self.variant = variant
        self.rows = rows
        self.cols = cols
        self._cfg = CIM_SUPERCELL_VARIANTS[variant]
        self.top_cell_name = (
            name
            or f"cim_array_{variant.lower().replace('-', '_')}_{rows}x{cols}"
        )

    @property
    def cell_pitch_x(self) -> float:
        return self._cfg.supercell_w

    @property
    def cell_pitch_y(self) -> float:
        return self._cfg.supercell_h

    @property
    def width(self) -> float:
        return self.cols * self._cfg.supercell_w

    @property
    def height(self) -> float:
        return self.rows * self._cfg.supercell_h

    def build(self) -> gdstk.Library:
        """Build the supercell-based array library."""
        # Build the supercell once and copy its cells into the output lib
        super_lib, _ = create_cim_supercell(self.variant)
        out_lib = gdstk.Library(
            name=f"{self.top_cell_name}_lib", unit=1e-6, precision=5e-9
        )
        # Find the supercell top cell
        super_top_name = (
            f"sky130_cim_supercell_"
            f"{self.variant.lower().replace('-', '_')}"
        )
        super_top = next(c for c in super_lib.cells if c.name == super_top_name)
        # Strip from array-context copies of cells:
        # - "MBL"/"MWL" on every cell (replaced by per-row/per-col array labels)
        # - "BL"/"BR"/"Q" on the foundry qtap sub-cell (replaced by per-column
        #   bl_0_<c>/br_0_<c> array labels; Q is supercell-internal and must
        #   not merge across instances).  Same chip-killer pattern as F11's WL
        #   bug — without stripping, all 4096 foundry BL labels would collapse
        #   into one global BL net post-flatten, and likewise for Q (every
        #   T7 source would tie to every other T7 source).
        _GENERAL_STRIP = {"MBL", "MWL"}
        _FOUNDRY_QTAP_STRIP = {"BL", "BR", "Q"}
        foundry_qtap_name = "sky130_fd_bd_sram__sram_sp_cell_opt1_qtap"
        # Copy all cells from super_lib into out_lib (including foundry sub-cell)
        cell_map: dict[str, gdstk.Cell] = {}
        for c in super_lib.cells:
            copy = c.copy(c.name)
            strip_set = set(_GENERAL_STRIP)
            if c.name == foundry_qtap_name:
                strip_set |= _FOUNDRY_QTAP_STRIP
            for label in [l for l in copy.labels if l.text in strip_set]:
                copy.remove(label)
            cell_map[c.name] = copy
            out_lib.add(copy)
        super_local = cell_map[super_top_name]

        # Top array cell
        top = gdstk.Cell(self.top_cell_name)

        # Place supercells in R×C grid
        # No X-mirror (production macro convention — avoids tile_array's
        # catastrophic short pattern).  Y-mirror odd rows for shared rails.
        cw = self._cfg.supercell_w
        ch = self._cfg.supercell_h
        for row in range(self.rows):
            for col in range(self.cols):
                x = col * cw
                y = row * ch
                if row % 2 == 0:
                    top.add(gdstk.Reference(super_local, origin=(x, y)))
                else:
                    # Y-mirror about cell midline → origin shifts up by ch
                    top.add(gdstk.Reference(
                        super_local,
                        origin=(x, y + ch),
                        x_reflection=True,
                    ))

        # Per-row dual WL POLY strips (F11/FT8b mechanism)
        self._add_wl_strips(top)

        # Per-row MWL POLY strip (T7 gate signal)
        self._add_mwl_strips(top)

        # Per-column BL/BR M1 strips
        self._add_bl_br_strips(top)

        # Per-column MBL M4 strips (cap top plate signal)
        self._add_mbl_strips(top)

        out_lib.add(top)
        return out_lib

    def _add_wl_strips(self, top: gdstk.Cell) -> None:
        """Per-row dual WL POLY strips at wl_top and wl_bot Y.

        Stripped foundry cell's "WL" label is handled inside the supercell
        already; here we just add the parent-level spanning strips and
        per-row labels.

        Y-mirror handling: for odd rows, the supercell is Y-mirrored about
        its midline.  Foundry's WL polys (cell-local y=WL_TOP_Y, WL_BOT_Y)
        end up at array_y = row_y0 + (supercell_h - foundry_local_Y).
        """
        WL_STRIP_W = 0.18  # > poly min 0.15 to ensure overlap
        WL_W_HALF = WL_STRIP_W / 2
        x_left = -0.20
        x_right = self.width + 0.20
        ch = self._cfg.supercell_h
        # POLY layer (66, 20), POLY label (66, 5), POLY pin (66, 16)
        POLY = 66
        POLY_DT = 20
        POLY_LBL_DT = 5
        POLY_PIN_DT = 16

        for row in range(self.rows):
            row_y0 = row * ch
            if row % 2 == 0:
                wl_top_y = row_y0 + _FOUNDRY_WL_TOP_Y
                wl_bot_y = row_y0 + _FOUNDRY_WL_BOT_Y
            else:
                # Y-mirrored row: foundry polys end up at
                # row_y0 + (ch - foundry_local_Y)
                wl_top_y = row_y0 + (ch - _FOUNDRY_WL_TOP_Y)
                wl_bot_y = row_y0 + (ch - _FOUNDRY_WL_BOT_Y)
            for wl_y in (wl_bot_y, wl_top_y):
                # Spanning poly strip
                top.add(gdstk.rectangle(
                    (x_left, wl_y - WL_W_HALF),
                    (x_right, wl_y + WL_W_HALF),
                    layer=POLY, datatype=POLY_DT
                ))
                # Per-row label "wl_0_<row>"
                top.add(gdstk.Label(
                    f"wl_0_{row}", (0.0, wl_y),
                    layer=POLY, texttype=POLY_LBL_DT
                ))
                # Pin shape near label
                top.add(gdstk.rectangle(
                    (x_left, wl_y - WL_W_HALF),
                    (x_left + 0.10, wl_y + WL_W_HALF),
                    layer=POLY, datatype=POLY_PIN_DT
                ))

    def _add_mwl_strips(self, top: gdstk.Cell) -> None:
        """Per-row MWL POLY strip — bridges T7 gates across all cells in row.

        T7 gate is a poly stripe in the annex above foundry cell.  T7's
        gate Y in supercell-local coords: foundry_h + 0.10 + overhang
        (~ 1.68 + 0.36 = 2.04).  For Y-mirrored rows, gate Y flips.
        """
        T7_GATE_Y_LOCAL = 2.04  # T7 gate Y in supercell-local coords
        STRIP_W = 0.18
        STRIP_HALF = STRIP_W / 2
        x_left = -0.20
        x_right = self.width + 0.20
        ch = self._cfg.supercell_h
        POLY = 66
        POLY_DT = 20
        POLY_LBL_DT = 5
        POLY_PIN_DT = 16

        for row in range(self.rows):
            row_y0 = row * ch
            if row % 2 == 0:
                mwl_y = row_y0 + T7_GATE_Y_LOCAL
            else:
                mwl_y = row_y0 + (ch - T7_GATE_Y_LOCAL)
            top.add(gdstk.rectangle(
                (x_left, mwl_y - STRIP_HALF),
                (x_right, mwl_y + STRIP_HALF),
                layer=POLY, datatype=POLY_DT
            ))
            top.add(gdstk.Label(
                f"mwl_{row}", (0.0, mwl_y),
                layer=POLY, texttype=POLY_LBL_DT
            ))
            top.add(gdstk.rectangle(
                (x_left, mwl_y - STRIP_HALF),
                (x_left + 0.10, mwl_y + STRIP_HALF),
                layer=POLY, datatype=POLY_PIN_DT
            ))

    def _add_bl_br_strips(self, top: gdstk.Cell) -> None:
        """Per-column BL/BR M1 strips spanning full array height."""
        STRIP_W = 0.18
        STRIP_HALF = STRIP_W / 2
        cw = self._cfg.supercell_w
        ch = self._cfg.supercell_h
        y_lo = -0.20
        y_hi = self.height + 0.20
        MET1 = 68
        MET1_DT = 20
        MET1_LBL_DT = 5
        MET1_PIN_DT = 16

        for col in range(self.cols):
            x_base = col * cw
            for x_local, prefix in ((_FOUNDRY_BL_X, "bl"), (_FOUNDRY_BR_X, "br")):
                strip_x = x_base + x_local
                top.add(gdstk.rectangle(
                    (strip_x - STRIP_HALF, y_lo),
                    (strip_x + STRIP_HALF, y_hi),
                    layer=MET1, datatype=MET1_DT
                ))
                top.add(gdstk.Label(
                    f"{prefix}_0_{col}", (strip_x, y_lo + 0.05),
                    layer=MET1, texttype=MET1_LBL_DT
                ))
                top.add(gdstk.rectangle(
                    (strip_x - STRIP_HALF, y_lo),
                    (strip_x + STRIP_HALF, y_lo + 0.10),
                    layer=MET1, datatype=MET1_PIN_DT
                ))

    def _add_mbl_strips(self, top: gdstk.Cell) -> None:
        """Per-column MBL M4 strips spanning full array height."""
        STRIP_W = 0.40
        STRIP_HALF = STRIP_W / 2
        cw = self._cfg.supercell_w
        y_lo = -0.20
        y_hi = self.height + 0.20
        MET4 = 71
        MET4_DT = 20
        MET4_LBL_DT = 5
        MET4_PIN_DT = 16
        # MBL position in supercell: cap center X (centered in supercell)
        mbl_x_in_super = self._cfg.supercell_w / 2

        for col in range(self.cols):
            strip_x = col * cw + mbl_x_in_super
            top.add(gdstk.rectangle(
                (strip_x - STRIP_HALF, y_lo),
                (strip_x + STRIP_HALF, y_hi),
                layer=MET4, datatype=MET4_DT
            ))
            top.add(gdstk.Label(
                f"mbl_{col}", (strip_x, y_lo + 0.05),
                layer=MET4, texttype=MET4_LBL_DT
            ))
            top.add(gdstk.rectangle(
                (strip_x - STRIP_HALF, y_lo),
                (strip_x + STRIP_HALF, y_lo + 0.20),
                layer=MET4, datatype=MET4_PIN_DT
            ))

    def array_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        for c in lib.cells:
            if c.name == self.top_cell_name:
                return c
        return lib.cells[0]
