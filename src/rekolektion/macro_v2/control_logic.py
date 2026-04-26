"""Control block for v2 SRAM macros.

Generates internal enable signals from external clk/we/cs:
    clk_buf   — buffered clock (DFF0.Q)
    p_en_bar  — precharge enable bar (DFF1.Q)
    s_en      — sense-amp enable (DFF2.Q)
    w_en      — write enable (DFF3.Q)

Topology (matches spice_generator `_write_control_logic_subckt`):
    DFF0.D = NAND2_0.Z = nand0_z   |  DFF0.CLK = clk    DFF0.Q = clk_buf
    DFF1.D = NAND2_0.Z = nand0_z   |  DFF1.CLK = clk    DFF1.Q = p_en_bar
    DFF2.D = NAND2_1.Z = nand1_z   |  DFF2.CLK = clk    DFF2.Q = s_en
    DFF3.D = NAND2_1.Z = nand1_z   |  DFF3.CLK = clk    DFF3.Q = w_en
    NAND2_{0,1}.A      = we
    NAND2_{0,1}.B      = cs

This cell is self-contained: internal routing lives INSIDE the cell so
Magic extracts a proper subckt with the 9 named ports (clk, we, cs,
clk_buf, p_en_bar, s_en, w_en, VPWR, VGND).  Without internal wiring
Magic would expose per-instance DFF pins as floating ports and LVS
would mismatch (22 nets vs 11 reference).
"""
from __future__ import annotations

from pathlib import Path

import gdstk

from rekolektion.macro_v2.routing import (
    draw_label, draw_via_stack, draw_wire,
)
from rekolektion.macro_v2.sky130_drc import GDS_LAYER


_DFF_CELL_NAME = "sky130_fd_bd_sram__openram_dff"
_NAND2_CELL_NAME = "sky130_fd_bd_sram__openram_sp_nand2_dec"

_CELLS_DIR: Path = Path(__file__).parent.parent / "peripherals/cells"
_DFF_GDS_PATH: Path = _CELLS_DIR / f"{_DFF_CELL_NAME}.gds"
_NAND2_GDS_PATH: Path = _CELLS_DIR / f"{_NAND2_CELL_NAME}.gds"

# One DFF per output-enable register (clk_buf, p_en_bar, s_en, w_en).
_NUM_OUTPUT_DFFS: int = 4
_NUM_CONTROL_NAND2S: int = 2

# East extension of clk/we/cs met2 rails past the cell body.  Required
# because the parent macro's clk/we/cs drops MUST land east of the
# row_decoder block to avoid crossing its internal met2 (Z→pred_out
# wires).  The row_decoder lives between the macro's left edge and the
# wl_driver/array column; its right edge maps to ctrl_logic-local x ≈
# 64.66 for the current weight_bank floorplan.  Extending rails into
# the row_decoder/wl_driver gap (x_local 64.66..66.66) puts the drop
# landing zone safely east of the row_decoder while remaining inside
# the floorplan-allocated ctrl_logic slot (slot width ≈ dec_w µm).
#
# Per-rail east extents (NOT a single shared extent): each met2 drop
# crosses every met2 horizontal rail in its y-traversal range.  If
# all three rails extended to the same x, every drop would merge
# with all three rails, recreating the cs/we/clk/p_en_bar/VPWR
# super-net.  Instead, each rail terminates JUST EAST of its drop x,
# and drops are ordered by the depth of their target rail (we → cs
# → clk, west → east, since clk_rail_y is the deepest absolute y in
# ctrl_logic and clk's drop must traverse past we/cs rail y's).
# Verified geometry: clk drop crosses no other rail because we/cs
# rails terminate west of clk's drop x; cs drop crosses no other
# rail because we rail terminates west of cs's drop x; we drop
# only reaches we_rail_y so terminates before any other rail's y.
_WE_RAIL_EAST: float = 65.20    # we drop lands at 65.10
_CS_RAIL_EAST: float = 65.76    # cs drop lands at 65.66, clear of we_rail
_CLK_RAIL_EAST: float = 66.32   # clk drop lands at 66.22, clear of we/cs rails

# OpenRAM std-cell-style cells (DFF / NAND_dec) are designed to abut edge-to-
# edge — their N-wells extend to the cell boundary and merge into one when
# tiled.  Leaving even a small gap creates an N-well spacing violation
# (sky130 nwell.2a, min 1.27 µm).
_INTER_CELL_GAP: float = 0.0
# Vertical gap between the DFF row and the NAND row above.  Large enough
# to host per-signal met2 trunks in the gap without shorting adjacent
# cell power rails.
_INTER_ROW_GAP: float = 2.0

# Cell pin coordinates (LEF origin = 0,0 for gdstk; values are local
# to each placed cell origin).
# DFF GDS bbox is 6.200 × 7.545 (wider/taller than the LEF SIZE
# 5.840 × 7.070 because the GDS geometry includes well/metal
# overhang).  Use the GDS extent for tiling so placed cells don't
# physically overlap — and so our pin-x arithmetic matches the
# assembler's `_DFF_W = 6.2`.
_DFF_W: float = 6.200
_DFF_H: float = 7.545
_DFF_D_LOCAL: tuple[float, float] = (0.850, 2.820)      # met2
_DFF_CLK_LOCAL: tuple[float, float] = (1.980, 3.620)    # met2
_DFF_Q_LOCAL: tuple[float, float] = (5.575, 3.175)      # met2
# DFF top li1 rail (VDD): y=[6.98, 7.16], full cell width.
_DFF_VDD_LI1_Y_LO: float = 6.980
_DFF_VDD_LI1_Y_HI: float = 7.160
# DFF bottom li1 rail (GND/VSUBS connection): y=[-0.1, 0.1], full width.
_DFF_VGND_LI1_Y_LO: float = -0.100
_DFF_VGND_LI1_Y_HI: float = 0.100

# LEF-declared SIZE; actual GDS extent is larger (GDS bbox 4.77 x 2.69,
# y=-0.7..1.99) due to power-rail overhang into abutting cells.  For
# placement+wiring we use GDS coords; LEF SIZE isn't relevant here.
_NAND_W: float = 4.770  # GDS bbox width
_NAND_H: float = 2.690  # GDS bbox height (y=-0.7..1.99)
# Pin positions from GDS (NOT LEF — LEF ORIGIN makes LEF coords differ).
_NAND_A_LOCAL: tuple[float, float] = (0.405, 1.095)     # li1 pin rect center
_NAND_B_LOCAL: tuple[float, float] = (0.405, 0.555)     # li1
_NAND_Z_LOCAL: tuple[float, float] = (2.000, 1.255)     # li1 (wide top strip)
_NAND_VDD_X_LOCAL: float = 3.365                        # met1 vertical rail x-center
_NAND_GND_X_LOCAL: float = 1.240                        # met1 vertical rail x-center
_NAND_RAIL_Y_LO: float = 0.450                          # met1 rail y range
_NAND_RAIL_Y_HI: float = 1.610


class ControlLogic:
    """SRAM control block with internal signal + power routing."""

    def __init__(
        self,
        use_replica: bool = True,
        name: str | None = None,
    ):
        self.use_replica = use_replica
        self.top_cell_name = name or (
            "ctrl_logic_rbl" if use_replica else "ctrl_logic_delay"
        )

    @property
    def height(self) -> float:
        """Total stack height: DFF row + inter-row gap + NAND2 row."""
        return _DFF_H + _INTER_ROW_GAP + _NAND_H

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)
        seen: set[str] = set()

        dff_cell = self._import_cell(lib, _DFF_GDS_PATH, _DFF_CELL_NAME, seen)
        nand2_cell = self._import_cell(
            lib, _NAND2_GDS_PATH, _NAND2_CELL_NAME, seen
        )

        # ------------------------------------------------------------------
        # Placement
        # ------------------------------------------------------------------
        # Row 0: 4 DFFs abutted horizontally, starting at x=0.
        dff_origins = [(i * _DFF_W, 0.0) for i in range(_NUM_OUTPUT_DFFS)]
        for ox, oy in dff_origins:
            top.add(gdstk.Reference(dff_cell, origin=(ox, oy)))

        # Row 1: 2 NAND2s, each centered over the DFF pair it drives.
        # NAND2_0 drives DFF0, DFF1; NAND2_1 drives DFF2, DFF3.
        y_nand = _DFF_H + _INTER_ROW_GAP
        pair0_center_x = (dff_origins[0][0] + dff_origins[1][0] + _DFF_W) / 2
        pair1_center_x = (dff_origins[2][0] + dff_origins[3][0] + _DFF_W) / 2
        nand2_origins = [
            (pair0_center_x - _NAND_W / 2, y_nand),
            (pair1_center_x - _NAND_W / 2, y_nand),
        ]
        for ox, oy in nand2_origins:
            top.add(gdstk.Reference(nand2_cell, origin=(ox, oy)))

        # ------------------------------------------------------------------
        # Pin-position helpers
        # ------------------------------------------------------------------
        def _dff_d(i): return (dff_origins[i][0] + _DFF_D_LOCAL[0],
                               dff_origins[i][1] + _DFF_D_LOCAL[1])
        def _dff_clk(i): return (dff_origins[i][0] + _DFF_CLK_LOCAL[0],
                                 dff_origins[i][1] + _DFF_CLK_LOCAL[1])
        def _dff_q(i): return (dff_origins[i][0] + _DFF_Q_LOCAL[0],
                               dff_origins[i][1] + _DFF_Q_LOCAL[1])

        def _nand_a(i): return (nand2_origins[i][0] + _NAND_A_LOCAL[0],
                                nand2_origins[i][1] + _NAND_A_LOCAL[1])
        def _nand_b(i): return (nand2_origins[i][0] + _NAND_B_LOCAL[0],
                                nand2_origins[i][1] + _NAND_B_LOCAL[1])
        def _nand_z(i): return (nand2_origins[i][0] + _NAND_Z_LOCAL[0],
                                nand2_origins[i][1] + _NAND_Z_LOCAL[1])

        # ------------------------------------------------------------------
        # Internal signal routing
        # ------------------------------------------------------------------
        cell_w = _NUM_OUTPUT_DFFS * _DFF_W
        cell_h = y_nand + _NAND_H

        # clk: horizontal met2 rail at the DFF CLK pin y, spanning all 4 DFFs.
        # The DFF's CLK pin is met2 (port rect y=[3.46, 3.78]); a met2 rail
        # at y=3.62 overlaps every CLK pin and merges them into one net.
        # East extension to _CLK_RAIL_EAST lets the parent's drop land
        # east of the row_decoder block (see header comment).  clk's
        # rail extent is the LONGEST of the three because clk_rail_y is
        # the deepest absolute y in ctrl_logic — clk drop must traverse
        # past the we/cs rail y's, so we/cs rails MUST terminate west
        # of the clk drop x to avoid merging clk with we/cs.
        clk_y = _DFF_CLK_LOCAL[1]
        draw_wire(
            top, start=(-0.5, clk_y), end=(_CLK_RAIL_EAST, clk_y),
            layer="met2",
        )
        draw_label(top, text="clk", layer="met2",
                   position=(-0.3, clk_y))

        # nand0_z: NAND2_0.Z (li1) → DFF0.D + DFF1.D (met2).
        # Use met2 for the horizontal run at the DFF.D pin y; vertical
        # met2 drop from NAND2.Z position down to the D row.  li1↔met2
        # via stack at the NAND2 Z landing.
        _draw_nand_z_to_d_pair(
            top, nand_z=_nand_z(0), d0=_dff_d(0), d1=_dff_d(1),
            net_name="nand0_z",
        )
        _draw_nand_z_to_d_pair(
            top, nand_z=_nand_z(1), d0=_dff_d(2), d1=_dff_d(3),
            net_name="nand1_z",
        )

        # we: horizontal met2 rail ABOVE the NAND2 cell body.  At the A
        # pin y (10.64) the met2 rail's width-0.14 y range would
        # overlap the nand_z met2 via stack's pad (y=[10.64, 10.96] at
        # Z_y=10.8) — shorting we with nand_z.  Route above the pad
        # instead (y=11.3).  Met2 (not met3) is important: the parent
        # cell's clk/cs drops descend on met3 verticals at some x in
        # the we rail's x range — same-layer crossings would short
        # them.  Connect A pins to the rail via li1→met2 stubs.
        we_rail_y = 11.3
        for i in range(_NUM_CONTROL_NAND2S):
            draw_via_stack(top, from_layer="li1", to_layer="met2",
                           position=_nand_a(i))
            # Vertical met2 riser from A pin (li1→met2 via) up to rail.
            draw_wire(
                top, start=_nand_a(i), end=(_nand_a(i)[0], we_rail_y),
                layer="met2",
            )
        # Extend rail east past the cell body so the parent's `we` drop
        # can land at a safe x outside the row_decoder block.  we's
        # rail terminates at the SHORTEST east extent (_WE_RAIL_EAST)
        # because we_rail_y is the highest of the three in absolute y
        # (least-deep drop target).  Terminating short keeps clk and
        # cs drops (which land deeper, traversing past we_rail_y)
        # from crossing the we rail at their own drop x.
        draw_wire(
            top,
            start=(_nand_a(0)[0], we_rail_y),
            end=(_WE_RAIL_EAST, we_rail_y),
            layer="met2",
        )
        draw_label(top, text="we", layer="met2",
                   position=(_nand_a(0)[0], we_rail_y))

        # cs: same pattern for NAND2_{0,1}.B pins.  cs rail's east end
        # (_CS_RAIL_EAST) sits BETWEEN we's and clk's so cs's drop
        # crosses no longer rail and clk's drop (further east) doesn't
        # cross cs.
        b_y = nand2_origins[0][1] + _NAND_B_LOCAL[1]
        for i in range(_NUM_CONTROL_NAND2S):
            draw_via_stack(top, from_layer="li1", to_layer="met2",
                           position=_nand_b(i))
        draw_wire(
            top, start=(_nand_b(0)[0], b_y), end=(_CS_RAIL_EAST, b_y),
            layer="met2",
        )
        draw_label(top, text="cs", layer="met2", position=_nand_b(0))

        # Output ports: label each DFF's Q pin with the output net name.
        # The Q pin is already met2; the label is enough for Magic to
        # expose it as a port when the parent wires to it.
        q_labels = ["clk_buf", "p_en_bar", "s_en", "w_en"]
        for i, label in enumerate(q_labels):
            draw_label(top, text=label, layer="met2", position=_dff_q(i))

        # ------------------------------------------------------------------
        # Power routing (VPWR + VGND rails)
        # ------------------------------------------------------------------
        # VPWR rail: horizontal met1 at y = cell_h + 0.3 (above NAND2 row).
        # Connects to every NAND2 VDD met1 vertical rail (drop down)
        # and every DFF top li1 VDD rail (via stack).
        vpwr_rail_y = cell_h + 0.3
        vpwr_rail_w = 0.4
        _rect(top, "met1",
              -0.5, vpwr_rail_y - vpwr_rail_w / 2,
              cell_w + 0.5, vpwr_rail_y + vpwr_rail_w / 2)
        # Pin shape promotes VPWR to a port of ctrl_logic (was just a
        # named net before, leaving the parent macro's VPWR port
        # disconnected at the LVS comparison).
        from rekolektion.macro_v2.routing import draw_pin_with_label
        draw_pin_with_label(
            top, text="VPWR", layer="met1",
            rect=(-0.5, vpwr_rail_y - vpwr_rail_w / 2,
                  cell_w + 0.5, vpwr_rail_y + vpwr_rail_w / 2),
        )
        # Drop to each NAND2 VDD met1 rail.  NAND2 VDD rail is met1
        # at local x=3.35..3.60, y=0.15..2.01.  Extend a vertical met1
        # from the NAND rail top up to the VPWR rail.
        for ox, oy in nand2_origins:
            vdd_x = ox + _NAND_VDD_X_LOCAL
            vdd_top_y = oy + _NAND_RAIL_Y_HI
            _rect(top, "met1",
                  vdd_x - 0.125, vdd_top_y,
                  vdd_x + 0.125, vpwr_rail_y + vpwr_rail_w / 2)
        # Bridge to each DFF top li1 rail via li1↔met1 via stack.
        for ox, oy in dff_origins:
            # Put the via stack at the center of the DFF's top li1 rail.
            vdd_land_x = ox + _DFF_W / 2
            vdd_land_y = oy + (_DFF_VDD_LI1_Y_LO + _DFF_VDD_LI1_Y_HI) / 2
            draw_via_stack(top, from_layer="li1", to_layer="met1",
                           position=(vdd_land_x, vdd_land_y))
            # Vertical met1 from the via stack up to the VPWR rail.
            _rect(top, "met1",
                  vdd_land_x - 0.125, vdd_land_y,
                  vdd_land_x + 0.125, vpwr_rail_y - vpwr_rail_w / 2)

        # VGND rail: horizontal met1 at y = -0.5 (below DFFs).
        vgnd_rail_y = -0.5
        _rect(top, "met1",
              -0.5, vgnd_rail_y - vpwr_rail_w / 2,
              cell_w + 0.5, vgnd_rail_y + vpwr_rail_w / 2)
        draw_pin_with_label(
            top, text="VGND", layer="met1",
            rect=(-0.5, vgnd_rail_y - vpwr_rail_w / 2,
                  cell_w + 0.5, vgnd_rail_y + vpwr_rail_w / 2),
        )
        # Bridge to each DFF bottom li1 rail (li1 at y=-0.1..0.1)
        # via li1↔met1 via stack + vertical met1 down to VGND rail.
        for ox, oy in dff_origins:
            vgnd_land_x = ox + _DFF_W / 2
            vgnd_land_y = oy + (_DFF_VGND_LI1_Y_LO + _DFF_VGND_LI1_Y_HI) / 2
            draw_via_stack(top, from_layer="li1", to_layer="met1",
                           position=(vgnd_land_x, vgnd_land_y))
            _rect(top, "met1",
                  vgnd_land_x - 0.125, vgnd_rail_y + vpwr_rail_w / 2,
                  vgnd_land_x + 0.125, vgnd_land_y)
        # Bridge to each NAND2 GND met1 rail (met1 at local x=1.23..1.47,
        # y=0.15..2.01).  We want a wire from VGND rail (below) UP to
        # the NAND2 GND rail.  But the NAND2 is ABOVE the DFFs, so
        # drawing a long met1 vertical from y=-0.5 up to the NAND2
        # would cross through the DFF body and likely short DFF metal.
        # Instead, rely on the substrate-tap merge: Magic connects all
        # GND rails through VSUBS at extract time (seen in the prior
        # ctrl_logic.ext merges: DFF.GND, NAND2.GND → VSUBS).
        # The VGND label above still names this net.

        lib.add(top)
        return lib

    def _import_cell(
        self,
        lib: gdstk.Library,
        gds_path: Path,
        cell_name: str,
        seen: set[str],
    ) -> gdstk.Cell:
        src = gdstk.read_gds(str(gds_path))
        for c in src.cells:
            if c.name in seen:
                continue
            lib.add(c.copy(c.name))
            seen.add(c.name)
        return next(c for c in lib.cells if c.name == cell_name)


def _draw_nand_z_to_d_pair(
    top: gdstk.Cell, *,
    nand_z: tuple[float, float],
    d0: tuple[float, float],
    d1: tuple[float, float],
    net_name: str,
) -> None:
    """Wire a NAND2 Z output (li1) to two DFF D inputs (met2).

    Route: li1→met3 via stack at NAND_Z; vertical met3 down from
    NAND_Z to the D pin y; met3→met2 via stack at the landing; met2
    horizontal rail spanning both D pin x's.

    Uses met3 for the vertical drop because the NAND_Z x sits deep
    inside a DFF body at y positions that are otherwise occupied by
    met2 clk/we/cs rails — same-layer crossings would short the nets.
    """
    # li1 -> met3 via stack at the NAND Z output.
    draw_via_stack(top, from_layer="li1", to_layer="met3", position=nand_z)

    # Horizontal rail at DFF D pin y, spanning both D pins.
    d_y = d0[1]  # d0 and d1 share y
    lo_x = min(d0[0], d1[0], nand_z[0])
    hi_x = max(d0[0], d1[0], nand_z[0])
    draw_wire(
        top, start=(lo_x, d_y), end=(hi_x, d_y), layer="met2",
    )

    # Vertical met3 from NAND Z down to the D row y.
    draw_wire(
        top, start=(nand_z[0], d_y), end=nand_z, layer="met3",
    )

    # met3→met2 via stack at the drop landing.
    draw_via_stack(
        top, from_layer="met2", to_layer="met3",
        position=(nand_z[0], d_y),
    )

    # Label the net.
    draw_label(top, text=net_name, layer="met2",
               position=((lo_x + hi_x) / 2, d_y))


def _rect(cell: gdstk.Cell, layer: str,
          x0: float, y0: float, x1: float, y1: float) -> None:
    """Draw a rectangle on the named sky130 layer."""
    layer_id, datatype = GDS_LAYER[layer]
    cell.add(gdstk.rectangle((x0, y0), (x1, y1),
                             layer=layer_id, datatype=datatype))
