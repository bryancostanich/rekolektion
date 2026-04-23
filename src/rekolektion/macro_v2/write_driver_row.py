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

        # Consolidate the per-cell EN pins (met1 strip at local y=[0.470,
        # 0.640], x extending from 0.495 to 2.500) into a single row-wide
        # `w_en` rail.  Without this, Magic extracts the row as a cell
        # with `bits` separate EN ports (one per WD instance), which
        # doesn't match a reference SPICE that ties them all to one
        # `w_en` net.
        self._add_w_en_rail(top)

        lib.add(top)
        return lib

    def _add_w_en_rail(self, top: gdstk.Cell) -> None:
        """Extend each WD's EN met1 strip into one continuous row-wide
        bus and label it `w_en`.  The bus sits at the WD cell's native
        EN y-range (0.470–0.640) so it overlays the existing EN pin
        strips and adds no new DRC risk — all we're doing is filling
        the gaps between instances."""
        from rekolektion.macro_v2.routing import draw_label
        from rekolektion.macro_v2.sky130_drc import GDS_LAYER

        met1_l, met1_d = GDS_LAYER["met1"]
        en_y_lo = 0.470
        en_y_hi = 0.640
        # The EN pin on each WD starts at x=0.495 in cell-local coords.
        # Extend from the first WD's EN start to the last WD's EN end
        # so the rail overlays every EN strip and merges them.
        rail_x_lo = 0.495
        rail_x_hi = (self.bits - 1) * self.pitch + 2.500
        top.add(gdstk.rectangle(
            (rail_x_lo, en_y_lo),
            (rail_x_hi, en_y_hi),
            layer=met1_l, datatype=met1_d,
        ))
        # Label the rail so Magic names its net `w_en` when extracted.
        draw_label(
            top, text="w_en", layer="met1",
            position=((rail_x_lo + rail_x_hi) / 2, (en_y_lo + en_y_hi) / 2),
        )

    def _import_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        src = gdstk.read_gds(str(_WD_GDS))
        # Foundry write_driver GDS ships with duplicate cell-name
        # entries (an empty placeholder + the real populated cell).
        # Keep the populated one per name so we don't end up with a
        # 0-polygon placeholder in the assembled macro.
        by_name: dict[str, gdstk.Cell] = {}
        for c in src.cells:
            existing = by_name.get(c.name)
            if existing is None:
                by_name[c.name] = c
                continue
            if existing.bounding_box() is None and c.bounding_box() is not None:
                by_name[c.name] = c
        imported: dict[str, gdstk.Cell] = {}
        for c in by_name.values():
            copy = c.copy(c.name)
            imported[c.name] = copy
            lib.add(copy)
        return imported[_WD_CELL_NAME]
