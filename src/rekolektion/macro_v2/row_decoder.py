"""Hierarchical row decoder for v2 SRAM macros.

Parametric over `num_rows`. Splits the address bits into 2-3 predecoder
groups (each 2 or 3 bits wide) producing one-hot outputs, then a final
stage of NAND gates ANDs one predecoder output per stage to pick exactly
one WL per address.

Split table — chosen to minimise final-stage fan-in while keeping
predecoders at 2- or 3-input (no NAND4 predecoders; reserved for the
final stage at very large N).

All cells are foundry NAND_k from `sky130_fd_bd_sram__openram_sp_nand*_dec`.
Inverters (for NOT-addr lines) are constructed at routing time by tying
both inputs of a NAND2 together — no dedicated inverter cell in the
sky130_fd_bd_sram library.
"""
from __future__ import annotations

from pathlib import Path

import gdstk


# num_rows → (widths of each predecoder, in bits)
# Constraint: sum(widths) = log2(num_rows); each width in {2, 3};
#             len(widths) = final-stage fan-in (pick the lowest feasible)
_SPLIT_TABLE: dict[int, tuple[int, ...]] = {
    4: (2,),
    8: (3,),
    16: (2, 2),
    32: (2, 3),
    64: (3, 3),
    128: (2, 2, 3),
    256: (2, 3, 3),
    512: (3, 3, 3),
    1024: (2, 2, 3, 3),
}


# Foundry NAND cells (key = fan-in, value = cell name)
_NAND_CELL_NAMES: dict[int, str] = {
    2: "sky130_fd_bd_sram__openram_sp_nand2_dec",
    3: "sky130_fd_bd_sram__openram_sp_nand3_dec",
    4: "sky130_fd_bd_sram__openram_sp_nand4_dec",
}

_CELLS_DIR: Path = Path(__file__).parent.parent / "peripherals/cells"

_NAND_GDS_PATHS: dict[int, Path] = {
    k: _CELLS_DIR / f"{name}.gds" for k, name in _NAND_CELL_NAMES.items()
}


def num_addr_bits_for_rows(num_rows: int) -> int:
    """Return the number of address bits required to select `num_rows`."""
    if num_rows not in _SPLIT_TABLE:
        raise ValueError(
            f"num_rows {num_rows} not in split table; valid values: "
            f"{sorted(_SPLIT_TABLE.keys())}"
        )
    return sum(_SPLIT_TABLE[num_rows])


# Horizontal gap between predecoder block and final-stage NAND column.
_PREDECODER_TO_NAND_GAP: float = 2.0
# Vertical gap between stacked predecoder blocks.
_INTER_PREDECODER_GAP: float = 2.0

# Foundry NAND_dec cells are LEF-pitch-matched to the SRAM bitcell
# (SIZE 1.580 um per the LEF, even though the GDS extent is ~2.69 um
# due to shared-boundary overhang into adjacent cells' power rails).
# Tiling at this pitch — not the raw GDS bbox height — makes the NAND
# column's rows align 1:1 with bitcell-array rows.
_NAND_DEC_PITCH: float = 1.58


class RowDecoder:
    """Parameterized hierarchical row decoder.

    Composes 2–4 `Predecoder` blocks (one per address split) with a
    final-stage column of `num_rows` NAND_k gates, where k = number
    of predecoders.

    Structural placement only; internal wiring happens in the C6
    assembler alongside the bitcell array.
    """

    def __init__(self, num_rows: int, name: str | None = None):
        if num_rows not in _SPLIT_TABLE:
            raise ValueError(
                f"num_rows {num_rows} not supported; must be a power of 2 "
                f"in {sorted(_SPLIT_TABLE.keys())}"
            )
        self.num_rows = num_rows
        self.split = _SPLIT_TABLE[num_rows]
        self.num_addr_bits = sum(self.split)
        self.final_fanin = len(self.split)
        self.top_cell_name = name or f"row_decoder_{num_rows}"

    def build(self) -> gdstk.Library:
        lib = gdstk.Library(name=f"{self.top_cell_name}_lib")
        top = gdstk.Cell(self.top_cell_name)
        seen: set[str] = set()

        # Single-predecoder case (num_rows in {4, 8}): skip the intermediate
        # Predecoder block entirely — we just need a vertical column of
        # num_rows NAND_k cells tiled at array-row pitch, where k = the
        # single predecoder's input width. Each NAND_k takes the k address
        # bits (or their inversions) as inputs; the NAND output IS the WL.
        if len(self.split) == 1:
            k = self.split[0]
            self._emit_vertical_nand_column(lib, top, seen, k_fanin=k, x=0.0)
            self._add_addr_rails(top, k_fanin=k, nand_x=0.0)
            lib.add(top)
            return lib

        # Multi-predecoder case: flatten predecoder NAND placements
        # directly into row_decoder (no separate Predecoder sub-cell)
        # so Magic extracts a flat topology matching the flat reference
        # SPICE (spice_generator._write_row_decoder_subckt multi-branch).
        self._build_multi_predecoder(lib, top, seen)

        lib.add(top)
        return lib

    def _build_multi_predecoder(
        self, lib: gdstk.Library, top: gdstk.Cell, seen: set[str],
    ) -> None:
        """Place + wire predecoder stages and final NAND column inline.

        Layout (cell-local):
            x=0..pred_w: predecoder stages stacked vertically
                stage 0 (k=split[0]): 2^k NAND_k cells in a horizontal row
                stage 1, stage 2, ... above
            x=pred_w+GAP: final NAND_m column, m=len(split), pitched 1.58

        Internal wiring:
            N = sum(split) vertical met3 addr rails labeled addr0..addrN-1,
                spanning the predecoder block height.  Each predecoder
                stage taps its k addr rails horizontally.
            m vertical met3 "pred{stage}_out_0" rails running down the
                final NAND column, each connected to stage N's first
                NAND Z output; per-row met2 spurs + li1 spurs drop from
                each rail into every final NAND's corresponding input.
        """
        from rekolektion.macro_v2.routing import (
            draw_label, draw_via_stack, draw_wire,
        )

        # 1. Place predecoder stages (flattened NAND cells).
        total_addr = sum(self.split)
        stage_y = 0.0
        stage_geom: list[dict] = []
        # Predecoders sit at x >= addr_rail_area_w so the N addr rails to
        # their west don't collide with NAND cell bodies.
        addr_rail_pitch = 0.5
        addr_rail_x0 = 0.3
        pred_area_x0 = addr_rail_x0 + total_addr * addr_rail_pitch + 0.5

        for stage_idx, k in enumerate(self.split):
            if k not in _NAND_CELL_NAMES:
                raise ValueError(f"stage fan-in {k} has no foundry NAND")
            nand_name = _NAND_CELL_NAMES[k]
            nand_src = gdstk.read_gds(str(_NAND_GDS_PATHS[k]))
            for c in nand_src.cells:
                if c.name in seen:
                    continue
                lib.add(c.copy(c.name))
                seen.add(c.name)
            nand_cell = next(c for c in lib.cells if c.name == nand_name)
            bb = nand_cell.bounding_box()
            nand_w = bb[1][0] - bb[0][0]
            nand_h = bb[1][1] - bb[0][1]

            nand_origins: list[tuple[float, float]] = []
            for j in range(2 ** k):
                ox = pred_area_x0 + j * nand_w
                top.add(gdstk.Reference(nand_cell, origin=(ox, stage_y)))
                nand_origins.append((ox, stage_y))
            stage_geom.append(dict(
                stage=stage_idx, k=k, nand_w=nand_w, nand_h=nand_h,
                y_origin=stage_y, nand_origins=nand_origins,
            ))
            stage_y += nand_h + _INTER_PREDECODER_GAP

        pred_block_top_y = stage_y
        pred_block_right_x = pred_area_x0 + max(
            (2 ** k) * g["nand_w"]
            for k, g in zip(self.split, stage_geom)
        )

        # 2. Place final NAND column east of the predecoder block.
        nand_x = pred_block_right_x + _PREDECODER_TO_NAND_GAP
        self._emit_vertical_nand_column(
            lib, top, seen, k_fanin=self.final_fanin, x=nand_x,
        )

        # 3. Draw N vertical addr rails on met3, labeled addr0..addrN-1.
        addr_rail_xs: list[float] = []
        for i in range(total_addr):
            rail_x = addr_rail_x0 + i * addr_rail_pitch
            addr_rail_xs.append(rail_x)
            draw_wire(
                top, start=(rail_x, -0.2), end=(rail_x, pred_block_top_y + 0.2),
                layer="met3",
            )
            draw_label(
                top, text=f"addr{i}", layer="met3",
                position=(rail_x, pred_block_top_y / 2),
            )

        # 4. Wire each predecoder NAND's k inputs to its k addr rails.
        addr_offset = 0
        for g in stage_geom:
            k = g["k"]
            if k not in self._NAND_INPUT_PIN_POS:
                raise ValueError(f"NAND fan-in {k} pin map missing")
            pin_pos = self._NAND_INPUT_PIN_POS[k]
            pin_names = ["A", "B", "C"][:k]
            stage_addr_xs = addr_rail_xs[addr_offset : addr_offset + k]
            addr_offset += k

            for (nand_ox, nand_oy) in g["nand_origins"]:
                for pin_name, rail_x in zip(pin_names, stage_addr_xs):
                    pin_lx, pin_ly = pin_pos[pin_name]
                    pin_ax, pin_ay = nand_ox + pin_lx, nand_oy + pin_ly
                    # met2 horizontal from pin x to rail x at pin y.
                    draw_wire(
                        top, start=(rail_x, pin_ay), end=(pin_ax, pin_ay),
                        layer="met2",
                    )
                    draw_via_stack(
                        top, from_layer="met2", to_layer="met3",
                        position=(rail_x, pin_ay),
                    )
                    draw_via_stack(
                        top, from_layer="li1", to_layer="met2",
                        position=(pin_ax, pin_ay),
                    )

        # 5. Wire each stage's first-NAND output to a pred_out rail
        #    spanning the final NAND column, then drop per-row spurs
        #    into the final NAND input pins.
        #
        # The final NAND_m column uses k_fanin=m; its pin_pos gives the
        # cell-local (x,y) of pins A..C.  We route one rail per stage to
        # the corresponding pin (stage 0→A, stage 1→B, stage 2→C).
        if self.final_fanin not in self._NAND_INPUT_PIN_POS:
            return  # NAND4+ not supported for fanout; skip
        final_pin_pos = self._NAND_INPUT_PIN_POS[self.final_fanin]
        final_pin_names = ["A", "B", "C"][: self.final_fanin]

        # The pred_out rails live WEST of the final NAND column at
        # rail_offsets (matches _add_addr_rails in single-predecoder).
        # Must NOT be at nand_x + pin_lx — via-stack pads at adjacent
        # pin positions (A at 0.41, B at 0.77, C at 1.13 — only 0.36
        # µm apart) overlap and short all 3 inputs to one net.
        rail_offsets = [-0.4, -2.3, -3.8][: self.final_fanin]

        for stage_idx, g in enumerate(stage_geom):
            k = g["k"]
            # First NAND in stage: its Z output is on li1, typically at
            # the top of the cell.  For NAND2: li1 Z rect x=[1.05, 4.44]
            # y=[1.57, 1.74].  For NAND3: similar topology.  We land
            # mid-strip for robust via drop.
            first_ox, first_oy = g["nand_origins"][0]
            z_local_x, z_local_y = self._NAND_Z_PIN_POS[k]
            z_ax = first_ox + z_local_x
            z_ay = first_oy + z_local_y

            # Target vertical rail on met3 down the final column,
            # positioned WEST of the column to give via-stack pads a
            # clean y-column that doesn't overlap adjacent pins.
            pin_name = final_pin_names[stage_idx]
            pin_lx, _ = final_pin_pos[pin_name]
            rail_x = nand_x + rail_offsets[stage_idx]

            # (a) li1→met3 via stack at the Z output.
            draw_via_stack(
                top, from_layer="li1", to_layer="met3",
                position=(z_ax, z_ay),
            )
            # (b) met3 horizontal from Z to rail_x at z_ay.
            draw_wire(
                top, start=(z_ax, z_ay), end=(rail_x, z_ay),
                layer="met3",
            )
            # (c) met3 vertical rail spanning the FULL final-NAND
            # column height (all 128 rows at pitch 1.58), plus the
            # predecoder block height above.  An earlier version
            # clipped at pred_block_top_y (~14 µm) which cut the
            # rail short of rows >9 — per-row via stacks landed on
            # floating met3 pads instead of the rail, leaving every
            # final NAND's A/B/C pin disconnected from its
            # predecoder output.
            rail_bot_y = -0.2
            rail_top_y = max(
                self.num_rows * _NAND_DEC_PITCH + 0.2,
                z_ay + 0.2,
                pred_block_top_y + 0.2,
            )
            draw_wire(
                top, start=(rail_x, rail_bot_y), end=(rail_x, rail_top_y),
                layer="met3",
            )
            # (d) per-row: drop met2 spur + li1 via into each final NAND
            #     A/B/C pin.
            for r in range(self.num_rows):
                if r % 2 == 0:
                    pin_ay = r * _NAND_DEC_PITCH + final_pin_pos[pin_name][1]
                else:
                    pin_ay = (r + 1) * _NAND_DEC_PITCH - final_pin_pos[pin_name][1]
                pin_ax = nand_x + pin_lx
                draw_via_stack(
                    top, from_layer="met2", to_layer="met3",
                    position=(rail_x, pin_ay),
                )
                draw_wire(
                    top, start=(rail_x, pin_ay), end=(pin_ax, pin_ay),
                    layer="met2",
                )
                draw_via_stack(
                    top, from_layer="li1", to_layer="met2",
                    position=(pin_ax, pin_ay),
                )

    # NAND input-pin cell-local coords (met1 label / li1 pin).  These
    # need to match what Magic extracts for A/B/C on each NAND variant.
    #   NAND2: A=(0.405, 1.095), B=(0.405, 0.555)  (li1, same x)
    #   NAND3: A=(1.265, 0.410), B=(0.715, 0.770), C=(0.165, 1.130)
    _NAND_INPUT_PIN_POS: dict[int, dict[str, tuple[float, float]]] = {
        2: {"A": (0.405, 1.095), "B": (0.405, 0.555)},
        3: {"A": (1.265, 0.410), "B": (0.715, 0.770), "C": (0.165, 1.130)},
    }

    # NAND Z-output cell-local (x, y) — center of a safe-to-via landing
    # on the li1 output strip.  The strip is wide on both variants, so
    # any x inside the strip + a central y works.
    #   NAND2: li1 Z strip x=[1.05, 4.44], y=[1.57, 1.74]
    #   NAND3: li1 Z strip spans the cell width from x≈1.6; y=[0.20, 0.37]
    _NAND_Z_PIN_POS: dict[int, tuple[float, float]] = {
        2: (2.75, 1.65),
        3: (3.5, 0.285),
    }

    def _add_addr_rails(
        self,
        top: "gdstk.Cell",
        *,
        k_fanin: int,
        nand_x: float,
    ) -> None:
        """Add internal addr-input rails to consolidate per-row NAND
        input pins.  Without these, Magic extracts every NAND's A/B/C
        pin as its own port of the row_decoder cell; the LVS reference
        (which ties all rows' A pins to addr0, B to addr1, C to addr2)
        then mismatches structurally.

        Each addr_i gets a vertical met3 rail west of the NAND column,
        plus per-row met3 horizontal spurs that cross the NAND body at
        the pin y, with met1↔met3 via stacks where the spur reaches
        the pin's cell-local (x, y) — met1 is available at those
        positions because the NAND cell places a met1.label on each
        input pin's li1 via (so there's met1 metal co-located).
        """
        from rekolektion.macro_v2.routing import (
            draw_label, draw_via_stack, draw_wire,
        )

        if k_fanin not in self._NAND_INPUT_PIN_POS:
            # Unsupported fan-in (e.g. NAND4) — skip; LVS won't match
            # but the layout still works functionally.
            return
        pin_pos = self._NAND_INPUT_PIN_POS[k_fanin]

        # Unique rail x per addr signal, placed WEST of the NAND
        # column in the space between it and the row_decoder's
        # default width.  The offsets are chosen to clear the top-
        # level _route_addr sidebars at dec_x-1.5, -3.0, -4.5 (also
        # met3) by at least 0.5 µm on either side.
        rail_offsets = [-0.4, -2.3, -3.8][:k_fanin]  # addr0, addr1, addr2
        pin_names = ["A", "B", "C"][:k_fanin]

        rail_ylo = -0.1
        rail_yhi = self.num_rows * _NAND_DEC_PITCH + 0.1

        for i, (pin_name, rail_dx) in enumerate(zip(pin_names, rail_offsets)):
            rail_x = nand_x + rail_dx
            # Vertical met3 rail spanning all rows.
            draw_wire(
                top,
                start=(rail_x, rail_ylo),
                end=(rail_x, rail_yhi),
                layer="met3",
            )
            x_local, y_local = pin_pos[pin_name]
            for row in range(self.num_rows):
                pin_x = nand_x + x_local
                if row % 2 == 0:
                    pin_y = row * _NAND_DEC_PITCH + y_local
                else:
                    pin_y = (row + 1) * _NAND_DEC_PITCH - y_local
                # Horizontal spur on met2 (different layer from the
                # rails and the top-level addr sidebars) so we don't
                # short adjacent addr rails when one spur crosses
                # another rail's x.  NAND cells have zero internal
                # met2.
                draw_wire(
                    top,
                    start=(rail_x, pin_y),
                    end=(pin_x, pin_y),
                    layer="met2",
                )
                # Via stack at the rail end: bridge met3 rail to the
                # met2 spur.
                draw_via_stack(
                    top,
                    from_layer="met2", to_layer="met3",
                    position=(rail_x, pin_y),
                )
                # Li1↔met2 via stack at the pin: NAND A/B/C pins live
                # on li1 (layer 67), so the chain must reach li1.
                draw_via_stack(
                    top,
                    from_layer="li1", to_layer="met2",
                    position=(pin_x, pin_y),
                )

            # Label the rail at its midpoint.
            draw_label(
                top,
                text=f"addr{i}",
                layer="met3",
                position=(rail_x, (rail_ylo + rail_yhi) / 2),
            )

    def _emit_vertical_nand_column(
        self,
        lib: gdstk.Library,
        top: gdstk.Cell,
        seen: set[str],
        *,
        k_fanin: int,
        x: float,
    ) -> None:
        """Import NAND_k and tile num_rows of them vertically at nand_h pitch."""
        if k_fanin not in _NAND_CELL_NAMES:
            raise ValueError(
                f"fan-in {k_fanin} has no foundry NAND cell"
            )
        nand_name = _NAND_CELL_NAMES[k_fanin]
        nand_src = gdstk.read_gds(str(_NAND_GDS_PATHS[k_fanin]))
        for c in nand_src.cells:
            if c.name in seen:
                continue
            lib.add(c.copy(c.name))
            seen.add(c.name)
        nand_cell = next(c for c in lib.cells if c.name == nand_name)
        # X-mirror odd rows so adjacent cells share power rails — this
        # is the standard dec-family tiling pattern (same as the bitcell
        # array) and is what keeps the layout DRC-clean at pitch 1.58.
        for row in range(self.num_rows):
            if row % 2 == 0:
                top.add(gdstk.Reference(
                    nand_cell, origin=(x, row * _NAND_DEC_PITCH),
                ))
            else:
                top.add(gdstk.Reference(
                    nand_cell,
                    origin=(x, (row + 1) * _NAND_DEC_PITCH),
                    x_reflection=True,
                ))
