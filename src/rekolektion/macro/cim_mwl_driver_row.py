"""MWL driver column for CIM macros — one foundry buf_2 per bitcell row.

Stacks `rows` MWL driver cells (`sky130_fd_sc_hd__buf_2`) on the LEFT
side of the bitcell array, pitch-matched to the bitcell row pitch.
Each driver's input (A) is exposed as `MWL_EN[r]` and its output (X)
is exposed as `MWL[r]` for routing to the array.

The foundry cell is 2.72 µm tall rail-to-rail; bitcell row pitch is
3.915–5.155 µm depending on variant, so each driver fits inside its
row span with margin for power straps on both sides.
"""
from __future__ import annotations

from typing import Optional

import gdstk

from rekolektion.peripherals.cim_mwl_driver import (
    generate_mwl_driver, get_cell_dimensions,
)


_DRIVER_CELL: str = "sky130_fd_sc_hd__buf_2"

# Foundry buf_2 internal label positions (from inspection of cells/sky130_fd_sc_hd__buf_2.gds).
# Coordinates are CELL-LOCAL; placement adds row offset.
#   A    @ (0.23, 1.19)  on li1 — input
#   X    @ (1.145, 0.51), (1.145, 1.87), (1.145, 2.21)  on li1 — output
#   VPWR @ (0.23, 2.72)  on met1 — top rail
#   VGND @ (0.23, 0.0)   on met1 — bottom rail
_A_LOCAL_X: float = 0.23
_A_LOCAL_Y: float = 1.19
_X_LOCAL_X: float = 1.145
_X_LOCAL_Y: float = 1.87       # mid-output position (used as canonical)
_VPWR_LOCAL_Y: float = 2.72
_VGND_LOCAL_Y: float = 0.0

# Cell width used for rail spans (rail-to-rail, NOT GDS bbox which includes
# N-well overhang).
_CELL_W, _CELL_H = get_cell_dimensions()      # 1.84 × 2.72


class MWLDriverRow:
    """Stack of MWL drivers (one foundry buf_2 per row) on LEFT of array."""

    def __init__(
        self,
        rows: int,
        row_pitch: float,
        name: Optional[str] = None,
    ):
        if rows < 1:
            raise ValueError(f"rows must be >= 1; got {rows}")
        if row_pitch <= 0:
            raise ValueError(f"row_pitch must be positive; got {row_pitch}")
        if row_pitch < _CELL_H:
            raise ValueError(
                f"row_pitch {row_pitch:.3f} < buf_2 cell height "
                f"{_CELL_H:.3f}; foundry cell does not fit"
            )
        self.rows = rows
        self.row_pitch = row_pitch
        self.top_cell_name = name or f"cim_mwl_driver_col_{rows}"

    @property
    def driver_w(self) -> float:
        return _CELL_W

    @property
    def driver_h(self) -> float:
        return _CELL_H

    @property
    def width(self) -> float:
        return self.driver_w

    @property
    def height(self) -> float:
        return self.rows * self.row_pitch

    def _row_y_offset(self, row: int) -> float:
        """Y origin of the buf_2 cell for `row`.

        The cell is centered vertically within the row's pitch span so
        that drivers don't collide and the array's MWL input lands close
        to the driver's X output Y.
        """
        slack = self.row_pitch - _CELL_H
        return row * self.row_pitch + slack / 2.0

    def build(self) -> gdstk.Library:
        from rekolektion.macro.routing import (
            draw_pin_with_label, draw_via_stack,
        )
        from rekolektion.macro.sky130_drc import GDS_LAYER

        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)

        # Import foundry cell + dependencies.
        drv_cell, drv_lib = generate_mwl_driver()
        seen: set[str] = set()
        for c in drv_lib.cells:
            if c.name in seen:
                continue
            lib.add(c.copy(c.name))
            seen.add(c.name)
        local_drv = next(c for c in lib.cells if c.name == _DRIVER_CELL)

        # Place one driver per row, vertically centred in the row's pitch.
        for row in range(self.rows):
            origin = (0.0, self._row_y_offset(row))
            top.add(gdstk.Reference(local_drv, origin=origin))

        # Per-row pin shapes — A and X pins on the foundry cell are on
        # li1 (not met1), so the parent .pin shape must also be on li1
        # to electrically connect.  The foundry rail labels are on met1,
        # but for VPWR/VGND we use li1 too because the rail also has li1
        # underneath the met1.  (Same pattern V2 wl_driver_row uses.)
        _PIN_HALF = 0.07
        li1_id, li1_dt = GDS_LAYER["li1"]
        for row in range(self.rows):
            y_off = self._row_y_offset(row)

            # MWL_EN[row] — the buf_2 A pin is on li1 at cell-local
            # (0.23, 1.19).  Draw an li1 stub from x=0 (row's left edge,
            # which becomes the macro's left edge when this row builder
            # is placed at the LEFT of the macro) to the A pin.  Place
            # the .pin shape at the boundary so Magic promotes
            # MWL_EN[row] as a top-level macro port.
            a_cy = y_off + _A_LOCAL_Y
            top.add(gdstk.rectangle(
                (0.0, a_cy - _PIN_HALF),
                (_A_LOCAL_X + _PIN_HALF, a_cy + _PIN_HALF),
                layer=li1_id, datatype=li1_dt,
            ))
            draw_pin_with_label(
                top, text=f"MWL_EN[{row}]", layer="li1",
                rect=(0.0, a_cy - _PIN_HALF,
                      0.07, a_cy + _PIN_HALF),
            )

            # MWL[row] — buf_2 X pin on li1 at (1.145, 1.87).  Extend
            # an li1 stub EAST out to the row builder's right edge so
            # the parent macro can connect MWL[row] to the bitcell
            # array's MWL[row] poly stripe.
            x_cy = y_off + _X_LOCAL_Y
            top.add(gdstk.rectangle(
                (_X_LOCAL_X - _PIN_HALF, x_cy - _PIN_HALF),
                (_CELL_W, x_cy + _PIN_HALF),
                layer=li1_id, datatype=li1_dt,
            ))
            draw_pin_with_label(
                top, text=f"MWL[{row}]", layer="li1",
                rect=(_CELL_W - 0.07, x_cy - _PIN_HALF,
                      _CELL_W, x_cy + _PIN_HALF),
            )

        # ---- PDN: bridge per-row VPWR/VGND rails ----
        # Each foundry buf_2 has its own met1 VPWR rail at cell-local
        # y=cell_h and VGND rail at y=0.  With row_pitch > cell_h, rows
        # don't naturally abut on supply rails, so we add vertical met2
        # straps that via-stack down to each rail.
        m1_id, m1_dt = GDS_LAYER["met1"]
        m2_id, m2_dt = GDS_LAYER["met2"]

        # VPWR strap on met2, vertical, positioned at the cell's VPWR
        # label X (0.23) — slightly to the LEFT of cell so it doesn't
        # interfere with internal cell routing.
        strap_w = 0.30
        vpwr_strap_x = _VPWR_LOCAL_Y * 0.0 + 0.23   # use VPWR label x-coord
        vgnd_strap_x = vpwr_strap_x + 0.50           # offset for parallel strap
        col_top_y = self.rows * self.row_pitch
        # met2 vertical straps spanning the full driver column height
        top.add(gdstk.rectangle(
            (vpwr_strap_x - strap_w / 2, 0.0),
            (vpwr_strap_x + strap_w / 2, col_top_y),
            layer=m2_id, datatype=m2_dt,
        ))
        top.add(gdstk.rectangle(
            (vgnd_strap_x - strap_w / 2, 0.0),
            (vgnd_strap_x + strap_w / 2, col_top_y),
            layer=m2_id, datatype=m2_dt,
        ))

        # Per-row via stacks: drop met2 strap into met1 rails of each
        # foundry cell.
        for row in range(self.rows):
            y_off = self._row_y_offset(row)
            vpwr_cy = y_off + _VPWR_LOCAL_Y
            vgnd_cy = y_off + _VGND_LOCAL_Y
            # VPWR via from met2 strap to cell's met1 VPWR rail
            draw_via_stack(
                top, from_layer="met1", to_layer="met2",
                position=(vpwr_strap_x, vpwr_cy),
            )
            # VGND via — same x offset on the GND strap
            draw_via_stack(
                top, from_layer="met1", to_layer="met2",
                position=(vgnd_strap_x, vgnd_cy),
            )

        # Single VPWR / VGND .pin shape on the strap (one per net is
        # enough since the strap is one continuous net).
        col_mid_y = col_top_y / 2.0
        _SUPPLY_HALF = 0.10
        draw_pin_with_label(
            top, text="VPWR", layer="met2",
            rect=(vpwr_strap_x - _SUPPLY_HALF, col_mid_y - _SUPPLY_HALF,
                  vpwr_strap_x + _SUPPLY_HALF, col_mid_y + _SUPPLY_HALF),
        )
        draw_pin_with_label(
            top, text="VGND", layer="met2",
            rect=(vgnd_strap_x - _SUPPLY_HALF, col_mid_y - _SUPPLY_HALF,
                  vgnd_strap_x + _SUPPLY_HALF, col_mid_y + _SUPPLY_HALF),
        )

        lib.add(top)
        return lib
