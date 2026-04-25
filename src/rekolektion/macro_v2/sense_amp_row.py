"""Sense amp row for v2 SRAM macros.

Tiles the foundry sense amp cell at `mux_ratio × bitcell_width` pitch —
one SA per bit (`bits` cells across). At mux_ratio >= 2, the foundry SA's
2.5 µm width fits in the mux group pitch.
"""
from __future__ import annotations

from pathlib import Path

import gdstk


_BITCELL_WIDTH: float = 1.31
_SA_WIDTH: float = 2.5
_SA_HEIGHT: float = 11.28
_SA_CELL_NAME: str = "sky130_fd_bd_sram__openram_sense_amp"
_SA_GDS: Path = (
    Path(__file__).parent.parent
    / f"peripherals/cells/{_SA_CELL_NAME}.gds"
)


class SenseAmpRow:
    """Row of sense amps pitched to the bitcell array's mux groups."""

    def __init__(self, bits: int, mux_ratio: int, name: str | None = None):
        if bits < 1:
            raise ValueError(f"bits must be >=1; got {bits}")
        if mux_ratio not in (2, 4, 8):
            raise ValueError(f"mux_ratio must be 2/4/8; got {mux_ratio}")
        pitch = mux_ratio * _BITCELL_WIDTH
        if _SA_WIDTH > pitch:
            raise ValueError(
                f"sense_amp ({_SA_WIDTH} um) does not fit in "
                f"mux_ratio={mux_ratio} pitch ({pitch} um)."
            )
        self.bits = bits
        self.mux_ratio = mux_ratio
        self.pitch = pitch
        self.top_cell_name = name or f"sense_amp_row_{bits}_mux{mux_ratio}"

    @property
    def width(self) -> float:
        return self.bits * self.pitch

    @property
    def height(self) -> float:
        return _SA_HEIGHT

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        sa_cell = self._import_cell(lib)
        for i in range(self.bits):
            origin = (i * self.pitch, 0.0)
            top.add(gdstk.Reference(sa_cell, origin=origin))

        # Consolidate the per-cell EN pins (met1 at local y=[10.820,
        # 11.120]) into one row-wide `s_en` bus so Magic extracts a
        # single EN port instead of `bits` separate ones.
        self._add_s_en_rail(top)

        # Per-cell VPWR / VGND .pin shapes so the sense_amp_row cell
        # exposes both supply rails as ports (same pattern as
        # write_driver_row / wl_driver_row / row_decoder).  Foundry SA
        # met1 power label positions: VDD at cell-local (1.89, 2.00),
        # (1.97, 6.34); GND at (1.90, 0.385), (2.25, 10.055).
        from rekolektion.macro_v2.routing import draw_pin_with_label
        _half = 0.07
        _VDD_X = 1.89
        _VDD_Y = 2.00
        _GND_X = 1.90
        _GND_Y = 0.385
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

        lib.add(top)
        return lib

    def _add_s_en_rail(self, top: gdstk.Cell) -> None:
        """Add a met2 horizontal `s_en` rail above all SA EN pins.

        Met1 is not safe here: SA's BL and BR pins extend as full-
        height met1 strips (y=0..11.28) at x=0.98–1.15 and 1.36–1.50
        respectively, so a horizontal met1 at the EN-pin y would short
        BL/BR into s_en.  SA has zero internal met2, so a horizontal
        met2 above the EN pins, with a per-cell via2 down to each EN
        met1 pad, fans out cleanly.
        """
        from rekolektion.macro_v2.routing import draw_label, draw_via_stack
        from rekolektion.macro_v2.sky130_drc import GDS_LAYER

        met2_l, met2_d = GDS_LAYER["met2"]
        # SA EN met1 pad: RECT (0.470, 10.820)-(0.760, 11.120), centre
        # (0.615, 10.970).  Route met2 rail at the same y range, then
        # drop met1-met2 via at each cell's EN centre.
        en_cx_local = 0.615
        en_cy = 10.970
        rail_y_lo = 10.820
        rail_y_hi = 11.120
        rail_x_lo = -0.20                                 # a hair west of cell 0
        rail_x_hi = (self.bits - 1) * self.pitch + 2.700  # a hair east of cell N-1
        top.add(gdstk.rectangle(
            (rail_x_lo, rail_y_lo),
            (rail_x_hi, rail_y_hi),
            layer=met2_l, datatype=met2_d,
        ))
        for i in range(self.bits):
            draw_via_stack(
                top, from_layer="met1", to_layer="met2",
                position=(i * self.pitch + en_cx_local, en_cy),
            )
        draw_label(
            top, text="s_en", layer="met2",
            position=((rail_x_lo + rail_x_hi) / 2, en_cy),
        )

    def _import_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        src = gdstk.read_gds(str(_SA_GDS))
        imported: dict[str, gdstk.Cell] = {}
        for c in src.cells:
            copy = c.copy(c.name)
            imported[c.name] = copy
            lib.add(copy)
        return imported[_SA_CELL_NAME]
