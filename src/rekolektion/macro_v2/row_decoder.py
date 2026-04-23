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
        # Deferred import avoids circular import at module load
        from rekolektion.macro_v2.predecoder import Predecoder

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

        # Multi-predecoder case: 2-4 predecoder blocks stacked vertically
        # at the left, followed by a column of num_rows NAND_k cells
        # where k = number of predecoders (final-stage fan-in).
        pred_block_width = 0.0
        y = 0.0
        for idx, k in enumerate(self.split):
            pd = Predecoder(
                num_inputs=k,
                name=f"{self.top_cell_name}_predecoder{idx}_{k}to{2**k}",
            )
            pd_lib = pd.build()
            for c in pd_lib.cells:
                if c.name in seen:
                    continue
                lib.add(c.copy(c.name))
                seen.add(c.name)
            pd_cell = next(c for c in lib.cells if c.name == pd.top_cell_name)
            top.add(gdstk.Reference(pd_cell, origin=(0.0, y)))
            bb = pd_cell.bounding_box()
            pd_w = bb[1][0] - bb[0][0]
            pd_h = bb[1][1] - bb[0][1]
            pred_block_width = max(pred_block_width, pd_w)
            y += pd_h + _INTER_PREDECODER_GAP

        nand_x = pred_block_width + _PREDECODER_TO_NAND_GAP
        self._emit_vertical_nand_column(
            lib, top, seen, k_fanin=self.final_fanin, x=nand_x,
        )

        lib.add(top)
        return lib

    # NAND input-pin cell-local coords (met1 label / li1 pin).  These
    # need to match what Magic extracts for A/B/C on each NAND variant.
    #   NAND2: A=(0.405, 1.095), B=(0.405, 0.555)  (li1, same x)
    #   NAND3: A=(1.265, 0.410), B=(0.715, 0.770), C=(0.165, 1.130)
    _NAND_INPUT_PIN_POS: dict[int, dict[str, tuple[float, float]]] = {
        2: {"A": (0.405, 1.095), "B": (0.405, 0.555)},
        3: {"A": (1.265, 0.410), "B": (0.715, 0.770), "C": (0.165, 1.130)},
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
