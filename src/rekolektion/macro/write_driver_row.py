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

        # Per-cell VPWR / VGND .pin shapes so the write_driver_row
        # cell exposes both supply rails as ports.  Foundry write_driver
        # met1 power label positions: VDD at cell-local (1.48, 1.05),
        # (1.10, 5.70); GND at (1.31, 3.13), (1.965, 4.135), (1.10,
        # 7.885).  We anchor a single VPWR + single VGND .pin per cell
        # over one rail position; identical labels merge all cells into
        # one VPWR / VGND net at the write_driver_row boundary.  Same
        # pattern as `row_decoder._label_power_rails` and `wl_driver_row`.
        # Per-cell muxed_bl_X / muxed_br_X / din{X} .pin shapes so the
        # write_driver_row exposes one named port per bit instead of
        # anonymous foundry-instance pin paths.  Same fix as
        # `sense_amp_row` (see comment there for why this is needed
        # given the spice_generator's hard-coded port order in Xwd).
        # WD pin label positions (verified against foundry GDS
        # labels on layer 68/5):
        #   BL  at (0.700, 9.905)
        #   BR  at (1.700, 9.865)
        #   DIN at (1.425, 0.060)
        from rekolektion.macro.routing import draw_pin_with_label
        _half = 0.07
        _VDD_X = 1.48
        _VDD_Y = 1.05
        _GND_X = 1.31
        _GND_Y = 3.13
        _BL_X, _BL_Y = 0.700, 9.905
        _BR_X, _BR_Y = 1.700, 9.865
        _DIN_X, _DIN_Y = 1.425, 0.060
        for i in range(self.bits):
            cx = i * self.pitch
            draw_pin_with_label(
                top, text="VPWR", layer="met1",
                rect=(cx + _VDD_X - _half, _VDD_Y - _half,
                      cx + _VDD_X + _half, _VDD_Y + _half),
            )
            draw_pin_with_label(
                top, text="VGND", layer="met1",
                rect=(cx + _GND_X - _half, _GND_Y - _half,
                      cx + _GND_X + _half, _GND_Y + _half),
            )
            draw_pin_with_label(
                top, text=f"muxed_bl_{i}", layer="met1",
                rect=(cx + _BL_X - _half, _BL_Y - _half,
                      cx + _BL_X + _half, _BL_Y + _half),
            )
            draw_pin_with_label(
                top, text=f"muxed_br_{i}", layer="met1",
                rect=(cx + _BR_X - _half, _BR_Y - _half,
                      cx + _BR_X + _half, _BR_Y + _half),
            )
            # NOTE: din{i} labels intentionally OMITTED.  At mux=2 pitch
            # they were creating top-level equiv directives shorting
            # din[10]↔VPWR and din[63]↔VGND.  The wd cell's DIN ports
            # remain anonymous (named after foundry instance/DIN);
            # netgen's topological matching still finds the correct
            # connectivity without explicit labels.

        lib.add(top)
        return lib

    def _add_w_en_rail(self, top: gdstk.Cell) -> None:
        """Extend each WD's EN met1 strip into one continuous row-wide
        bus and label it `w_en`.  The bus sits at the WD cell's native
        EN y-range (0.470–0.640) so it overlays the existing EN pin
        strips and adds no new DRC risk — all we're doing is filling
        the gaps between instances."""
        from rekolektion.macro.routing import draw_label
        from rekolektion.macro.sky130_drc import GDS_LAYER

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
