"""WL driver row — inverts the row decoder's active-low output into
an active-high WL so the bitcell array actually gets accessed.

The foundry `sky130_fd_bd_sram__openram_sp_nand3_dec` cell is pitch-
matched to the bitcell row (1.58 µm SIZE height) and already DRC-
clean, so we repurpose it as an inverter: NAND3(A, VDD, VDD) = NOT A.
Inputs B and C are tied high via short li1/met1 stubs to a local
VDD rail drawn alongside the cell column.

This block sits between the row decoder and the bitcell array in the
macro floorplan; `assembler._route_wl` routes decoder Z → wl_driver A
(input) and wl_driver Z → array WL poly strip.
"""
from __future__ import annotations

from pathlib import Path

import gdstk

from rekolektion.macro_v2.routing import draw_label, draw_wire, draw_via_stack
from rekolektion.macro_v2.sky130_drc import GDS_LAYER


_NAND3_CELL_NAME: str = "sky130_fd_bd_sram__openram_sp_nand3_dec"
_NAND3_GDS: Path = (
    Path(__file__).parent.parent
    / f"peripherals/cells/{_NAND3_CELL_NAME}.gds"
)
_NAND_PITCH: float = 1.58

# NAND3 pin positions (cell-local, from LEF + GDS inspection):
#   A pin at (x=1.265, y=0.410) on li1
#   B pin at (x=0.715, y=0.770) on li1
#   C pin at (x=0.165, y=1.130) on li1
#   Z pin: wide li1 strip at y=0.200-0.370, extends x=1.610-7.510
_A_X: float = 1.265
_A_Y: float = 0.410
_B_X: float = 0.715
_B_Y: float = 0.770
_C_X: float = 0.165
_C_Y: float = 1.130
# Z pin approximate output point (near right edge of cell, well inside the
# wide li1 output strip).
_Z_X: float = 7.0
_Z_Y: float = 0.285
_NAND3_RIGHT_EDGE_X: float = 7.510

# NAND3 internal met1 VDD rail.  The cell declares two "VDD" m1 ports;
# the one at x=[4.26, 4.50] extends slightly past both cell edges in y
# (−0.035 to 1.735 µm) so abutted cells share it continuously.  This
# rail carries the PFET sources (the actual power pin, NOT the B/C
# gate ties), so it must be bonded to the wl_driver's external VPWR
# net to close LVS.  x_center = 4.38.
_NAND3_VDD_RAIL_X_CENTER: float = 4.38

# VDD rail runs vertically right of the NAND3 cell column; B + C pins
# tie to it via short horizontal stubs. Placed far enough right that
# poly / li1 spacing rules pass.
_VDD_RAIL_X_OFFSET: float = _NAND3_RIGHT_EDGE_X + 1.0
_VDD_RAIL_W: float = 0.30      # met1


class WlDriverRow:
    """Column of `num_rows` NAND3_dec inverters, one per array row."""

    def __init__(self, num_rows: int, name: str | None = None):
        if num_rows < 1:
            raise ValueError(f"num_rows must be >= 1; got {num_rows}")
        self.num_rows = num_rows
        self.top_cell_name = name or f"wl_driver_row_{num_rows}"

    @property
    def pitch(self) -> float:
        return _NAND_PITCH

    @property
    def width(self) -> float:
        # NAND3 width + VDD rail clearance
        return _VDD_RAIL_X_OFFSET + _VDD_RAIL_W + 0.5

    @property
    def height(self) -> float:
        return self.num_rows * _NAND_PITCH

    def a_pin_absolute(self, row: int) -> tuple[float, float]:
        """Absolute (x, y) of the A (input) pin for a given row,
        accounting for X-mirror on odd rows."""
        if row % 2 == 0:
            return (_A_X, row * _NAND_PITCH + _A_Y)
        return (_A_X, (row + 1) * _NAND_PITCH - _A_Y)

    def z_pin_absolute(self, row: int) -> tuple[float, float]:
        """Absolute (x, y) of the Z (output) pin for a given row."""
        if row % 2 == 0:
            return (_Z_X, row * _NAND_PITCH + _Z_Y)
        return (_Z_X, (row + 1) * _NAND_PITCH - _Z_Y)

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)
        seen: set[str] = set()

        # Import NAND3 cell library
        nand_src = gdstk.read_gds(str(_NAND3_GDS))
        for c in nand_src.cells:
            if c.name in seen:
                continue
            lib.add(c.copy(c.name))
            seen.add(c.name)
        nand_cell = next(c for c in lib.cells if c.name == _NAND3_CELL_NAME)

        # Tile NAND3 cells vertically, X-mirror on odd rows (matches
        # row_decoder and bitcell_array convention so power rails abut).
        for row in range(self.num_rows):
            if row % 2 == 0:
                top.add(gdstk.Reference(
                    nand_cell, origin=(0.0, row * _NAND_PITCH),
                ))
            else:
                top.add(gdstk.Reference(
                    nand_cell,
                    origin=(0.0, (row + 1) * _NAND_PITCH),
                    x_reflection=True,
                ))

        # VDD rail on met2 (vertical) on the right side of the column.
        # Met1 would collide with the top-level _route_wl met1
        # horizontal that crosses this x on its way from each wl_driver
        # NAND3 Z output eastward to the array WL — same-layer
        # crossings short every row's WL to VDD.  Met2 sidesteps the
        # collision (wl_driver has zero internal met2, and met1-only
        # top-level WL routing doesn't touch met2 at this x).
        vdd_x = _VDD_RAIL_X_OFFSET
        _rect(top, "met2",
              vdd_x - _VDD_RAIL_W / 2, 0.0,
              vdd_x + _VDD_RAIL_W / 2, self.num_rows * _NAND_PITCH)
        draw_label(
            top, text="VPWR", layer="met2",
            position=(vdd_x, self.num_rows * _NAND_PITCH / 2),
        )

        # Tie B + C to VDD for every row.
        # NAND3_dec has internal met1 VDD rails at x=[4.26,4.50] and
        # x=[6.42,6.66], and a met1 GND rail at x=[1.79,2.02]. A met1
        # horizontal tie stub at the B/C pin y would CUT THROUGH both
        # the GND and VDD rails on met1, shorting them together (and
        # chaining through adjacent NAND3s via abutted rails).
        # NAND3 has no internal met2, so use met2 for the horizontal
        # tie stub and via stack up from li1 at the pin.
        for row in range(self.num_rows):
            for pin_local_x, pin_local_y in ((_B_X, _B_Y), (_C_X, _C_Y)):
                if row % 2 == 0:
                    pin_y = row * _NAND_PITCH + pin_local_y
                else:
                    pin_y = (row + 1) * _NAND_PITCH - pin_local_y
                # li1 -> met2 via stack at the pin
                draw_via_stack(
                    top, from_layer="li1", to_layer="met2",
                    position=(pin_local_x, pin_y),
                )
                # met2 horizontal from pin to VDD rail x — rail is
                # also on met2 now, so no via needed at the rail end.
                draw_wire(
                    top,
                    start=(pin_local_x, pin_y),
                    end=(vdd_x, pin_y),
                    layer="met2",
                )

        # Bond NAND3 PFET-source rail to external VPWR.
        # The B/C stubs above only tie the NAND3 *input gates* high;
        # they do not connect the cell's VDD port (the PFET sources).
        # Without this bridge, hierarchical LVS sees a 1-net gap —
        # every NAND3 instance's merged VDD rail ("VDD" port, met1 at
        # x=[4.26,4.50]) is electrically floating from the wl_driver's
        # "VPWR" rail at x=vdd_x.  Since port 1's y-extent [-0.035,
        # 1.735] overlaps between abutted cells, all 128 rails fuse
        # into ONE net — so a single via + bridge is sufficient.
        #
        # Placed in row 0 at local y=1.45 (between C stub at 1.130 and
        # cell top at 1.580).  The horizontal met2 crosses the second
        # internal VDD rail at x=[6.42,6.66] on a different layer — no
        # via, and even accidental via-ing is same-net, so safe.
        bridge_y = 1.45
        draw_via_stack(
            top, from_layer="met1", to_layer="met2",
            position=(_NAND3_VDD_RAIL_X_CENTER, bridge_y),
        )
        draw_wire(
            top,
            start=(_NAND3_VDD_RAIL_X_CENTER, bridge_y),
            end=(vdd_x, bridge_y),
            layer="met2",
        )

        lib.add(top)
        return lib


def _rect(cell: gdstk.Cell, layer: str,
          x0: float, y0: float, x1: float, y1: float) -> None:
    """Draw a rectangle on the named sky130 layer."""
    layer_id, datatype = GDS_LAYER[layer]
    cell.add(gdstk.rectangle((x0, y0), (x1, y1),
                             layer=layer_id, datatype=datatype))
