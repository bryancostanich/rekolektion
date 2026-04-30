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
            self._label_power_rails(top)
            lib.add(top)
            return lib

        # Multi-predecoder case: flatten predecoder NAND placements
        # directly into row_decoder (no separate Predecoder sub-cell)
        # so Magic extracts a flat topology matching the flat reference
        # SPICE (spice_generator._write_row_decoder_subckt multi-branch).
        self._build_multi_predecoder(lib, top, seen)
        self._label_power_rails(top)

        lib.add(top)
        return lib

    # Cell-local centres of foundry NAND VDD / GND met1 rails.  Used to
    # plant row_decoder-owned met1.pin shapes that promote each cell's
    # supply rails into VPWR/VGND ports of the row_decoder cell.
    _NAND_POWER_PIN_POS: dict[int, dict[str, tuple[float, float]]] = {
        2: {"VPWR": (3.365, 1.030), "VGND": (1.240, 0.782)},
        3: {"VPWR": (4.380, 0.850), "VGND": (1.905, 0.715)},
    }

    def _label_power_rails(self, top: "gdstk.Cell") -> None:
        """Drop VPWR/VGND .pin shapes over each placed NAND cell's supply
        rails so Magic exposes them as ports of the row_decoder cell.

        Without these, every isolated VDD group in the cell (the 8
        stage-2 NAND3 columns, 4×2 NAND2 stages, and the y-abutted
        final NAND column) becomes its own stray
        `nand3_dec_X/VDD` port at the row_decoder boundary — the
        extracted subckt then carries 6+ unmatched VDD ports while
        the reference SPICE has a single VPWR.  Adding a labeled
        met1.pin per rail gives every cell's supply pin a row_decoder
        net name; identical labels ensure all groups merge."""
        from rekolektion.macro.routing import draw_pin_with_label

        for ref in list(top.references):
            cell_name = ref.cell.name
            if "nand3_dec" in cell_name:
                k = 3
            elif "nand2_dec" in cell_name:
                k = 2
            else:
                continue
            ox, oy = ref.origin
            mirrored = bool(getattr(ref, "x_reflection", False))
            pin_pos = self._NAND_POWER_PIN_POS[k]
            for net_name, (lx, ly) in pin_pos.items():
                # x_reflection negates Y in cell-local before translation.
                pin_ay = oy - ly if mirrored else oy + ly
                pin_ax = ox + lx
                # Tiny .pin so we don't introduce new geometry that
                # could collide with adjacent routing — just enough to
                # carry the label.  The foundry rail extends past cell
                # boundaries (e.g. NAND3 VDD y_local [-0.035, 1.735])
                # so the rail itself is what carries current; the .pin
                # shape only anchors the label to the row_decoder cell.
                half = 0.07
                draw_pin_with_label(
                    top, text=net_name, layer="met1",
                    rect=(pin_ax - half, pin_ay - half,
                          pin_ax + half, pin_ay + half),
                )

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
        from rekolektion.macro.routing import (
            draw_label, draw_via_stack, draw_wire,
        )

        # 1. Place predecoder stages (flattened NAND cells).
        total_addr = sum(self.split)
        stage_y = 0.0
        stage_geom: list[dict] = []
        # Predecoders sit at x >= addr_rail_area_w so the N addr rails to
        # their west don't collide with NAND cell bodies.
        # Pitch 0.7: at stage 2 (NAND3), the three via2 pads at rail
        # ends stack diagonally (pin Δx=0.55, Δy=0.36).  With pad=0.37
        # and pitch=0.5, adjacent pads end up 0.13 µm apart in x with
        # 0.01 µm y overlap — Magic merges them into one net.  Pitch
        # 0.7 gives 0.33 µm x clearance between stacked pads.
        addr_rail_pitch = 0.7
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

        # 3. Draw N vertical addr rails on met3, labeled
        #    addr[0]..addr[N-1] — matches the top-level pin label
        #    so Magic merges the two labels onto one net (the
        #    top-level subckt port) rather than naming the net by
        #    the alphabetically-first of {addr0, addr[0]} = `addr0`
        #    and dropping `addr[0]` from the ext2spice output.
        # Addr rail .pin shapes: a label-only addr[i] is detected by
        # Magic as a NET name but is not exposed as a ROW_DECODER PORT
        # unless a .pin purpose polygon (datatype 16) sits on the same
        # net.  Without the .pin, the parent macro's met3 feeder lands
        # on a parent-level addr[i] net that Magic does NOT merge with
        # the cell's internal addr[i] rail (hierarchical extraction
        # treats child INTERNAL labeled metal as private when no port
        # shape exists).  Result observed at activation_bank top-level:
        # addr[2..5] missing as macro ports while their feeders dangled
        # on top-level-only nets, contributing 4 of the 6 net delta.
        # Same fix as `dec_out_X`: draw_pin_with_label which adds both
        # the .pin shape and the label.
        from rekolektion.macro.routing import draw_pin_with_label
        _ADDR_PIN_HALF: float = 0.075   # .pin rect 0.15 × 0.15 µm
        # F12: extend the addr rail SOUTH so its terminus reaches the
        # row_decoder cell's bbox boundary (y=-0.5).  Magic's hierarchical
        # port-promotion only treats `.pin` shapes as true sub-cell ports
        # when they touch the cell bbox; an interior `.pin` at the
        # predecoder block midpoint creates a port marker but parent
        # met3 feeders land on `instance/addr[i]` (per-instance qualified
        # name), not the parent's `addr[i]` net.  Placing the .pin at
        # y=-0.4 (just inside the cell's southern bbox edge, on the now-
        # extended rail) gives Magic a boundary port that merges with
        # the parent's addr[i] feeder.
        _RAIL_S_END_Y: float = -0.5
        _ADDR_PIN_S_Y: float = -0.4
        addr_rail_xs: list[float] = []
        for i in range(total_addr):
            rail_x = addr_rail_x0 + i * addr_rail_pitch
            addr_rail_xs.append(rail_x)
            draw_wire(
                top, start=(rail_x, _RAIL_S_END_Y),
                end=(rail_x, pred_block_top_y + 0.2),
                layer="met3",
            )
            # Boundary .pin + label at the south end of the rail.
            draw_pin_with_label(
                top, text=f"addr[{i}]", layer="met3",
                rect=(rail_x - _ADDR_PIN_HALF, _ADDR_PIN_S_Y - _ADDR_PIN_HALF,
                      rail_x + _ADDR_PIN_HALF, _ADDR_PIN_S_Y + _ADDR_PIN_HALF),
            )

        # 4. Wire each predecoder NAND's k inputs to its k addr rails.
        #
        # Two routing modes, chosen per stage:
        #
        #   DIRECT (NAND2, k=2): met2 spur at pin_y from rail_x to
        #     pin_x.  Safe because NAND2 pins (y_local 0.555/1.095)
        #     are well clear of the cell's internal met2 (y_local
        #     0.795-1.335), and the spur doesn't cross cell bodies.
        #
        #   DETOUR (NAND3, k=3): route UNDER the stage's cell row via
        #     the inter-stage gap.  NAND3 has substantial internal
        #     met2 at y_local 0.795-1.335 (between B and C pins).
        #     Every spur that would extend PAST its target cell (i.e.,
        #     any cell east of the westernmost pin in that addr's
        #     stage) passes through neighbouring cells' bodies and
        #     merges with their internal met2 — shorting addr5↔addr6
        #     via the internal met2 net.  Detour routes at y below the
        #     cell row (in the stage's y-gap), then jogs up at the
        #     pin's x (inside the target cell, where no met2 exists
        #     at that x because pin_x is never inside the cell's
        #     internal met2 x range).
        addr_offset = 0
        for g in stage_geom:
            k = g["k"]
            if k not in self._NAND_INPUT_PIN_POS:
                raise ValueError(f"NAND fan-in {k} pin map missing")
            pin_pos = self._NAND_INPUT_PIN_POS[k]
            pin_names = ["A", "B", "C"][:k]
            stage_addr_xs = addr_rail_xs[addr_offset : addr_offset + k]
            addr_offset += k

            stage_y_origin = g["y_origin"]

            if k == 3:
                # Detour y: below the stage's cell bottom (y_origin -
                # 0.395 for NAND3 bbox).  Need a UNIQUE y per pin so
                # that A/B/C detour horizontals (all met2) don't merge
                # into one wire where their x-ranges overlap.
                #
                # Crucially, assign C (leftmost pin, smallest pin_x)
                # the HIGHEST detour_y — closest to the cell.  Pin x
                # ordering is A > B > C (A rightmost).  Each pin's
                # vertical jog runs from detour_y up to pin_y at
                # pin_x.  If we assigned C the LOWEST detour, pin A's
                # horizontal at a higher y would cross C's vertical
                # (and B's) within C_v's y range — shorting all three.
                # Putting C's detour HIGHEST keeps each pin's
                # horizontal below the other pins' verticals (their
                # verticals start at higher y).
                #
                # Stagger at 0.5 µm steps: A sits 1.5 below cell, B
                # sits 1.0 below, C sits 0.5 below.  Must stay above
                # the previous stage's cell top.
                detour_ys = {
                    pin_names[i]: stage_y_origin - 1.5 + 0.5 * i
                    for i in range(k)
                }
                assert min(detour_ys.values()) > 0.0, (
                    f"NAND3 stage at y={stage_y_origin} has no gap below; "
                    f"detour routing requires a lower-stage gap"
                )
            else:
                detour_ys = None  # use direct routing

            # Draw the rail-end via stack ONCE per (stage, pin).  An
            # earlier version emitted it inside the cell loop, which
            # produced N (= 2^k) identical via2 cuts + met2 + met3 pads
            # stacked at the same (rail_x, y).  Magic's extract treated
            # the stack of 8 overlapping cuts as a single cut whose
            # connectivity bonded the rail to ONLY the first cell's
            # spur — leaving the other 7 cells' input pins isolated
            # nets at the row_decoder level.  Drawing once here gives
            # one clean cut per pin, and the per-cell met2 spur drawn
            # below merges through it onto the rail for every cell.
            if detour_ys is not None:
                for pin_name, rail_x in zip(pin_names, stage_addr_xs):
                    draw_via_stack(
                        top, from_layer="met2", to_layer="met3",
                        position=(rail_x, detour_ys[pin_name]),
                    )
            else:
                ref_oy = g["nand_origins"][0][1]
                for pin_name, rail_x in zip(pin_names, stage_addr_xs):
                    pin_y_ref = ref_oy + pin_pos[pin_name][1]
                    draw_via_stack(
                        top, from_layer="met2", to_layer="met3",
                        position=(rail_x, pin_y_ref),
                    )

            for (nand_ox, nand_oy) in g["nand_origins"]:
                for pin_name, rail_x in zip(pin_names, stage_addr_xs):
                    pin_lx, pin_ly = pin_pos[pin_name]
                    pin_ax, pin_ay = nand_ox + pin_lx, nand_oy + pin_ly

                    if detour_ys is not None:
                        detour_y = detour_ys[pin_name]
                        # Detour: rail -> [met3 rail] -> via2 ->
                        # [met2 horizontal at detour_y] -> via1 ->
                        # [met1 vertical at pin_x] -> mcon ->
                        # [li1 pin pad].  The rail-end via2 is drawn
                        # once outside this loop (see comment above);
                        # here we draw the per-cell spur and pin-end
                        # via1 + met1 vertical + mcon.
                        #
                        # Why split layers: the horizontals (one per
                        # pin at its unique detour_y) span x from rail
                        # to target cell's pin_x, passing THROUGH
                        # other cells' x.  The verticals (one per pin
                        # at pin_x) span y from detour_y up to pin_y.
                        # If both were on the same layer, cell M's
                        # horizontal for pin P would cross cell K's
                        # vertical for another pin Q at (pin_Q_x_K,
                        # detour_y_P) — shorting addr(P) to addr(Q).
                        # Putting horizontals on met2 and verticals on
                        # met1 keeps crossings harmless.
                        # (1) met2 horizontal at detour_y.
                        draw_wire(
                            top,
                            start=(rail_x, detour_y), end=(pin_ax, detour_y),
                            layer="met2",
                        )
                        # (2) via1 at pin_x @ detour_y: met2→met1.
                        draw_via_stack(
                            top, from_layer="met1", to_layer="met2",
                            position=(pin_ax, detour_y),
                        )
                        # (3) met1 vertical from detour_y up to pin_y.
                        #     Inside the target NAND3 at pin_x (pin
                        #     x_local ≤ 1.265) — all stage-2 NAND3
                        #     internal met1 lives at x_local ≥ 1.79.
                        draw_wire(
                            top,
                            start=(pin_ax, detour_y), end=(pin_ax, pin_ay),
                            layer="met1",
                        )
                        # (4) mcon at pin: li1→met1 stack connects to
                        #     the cell's li1 pin pad.
                        draw_via_stack(
                            top, from_layer="li1", to_layer="met1",
                            position=(pin_ax, pin_ay),
                        )
                    else:
                        # Direct: met2 horizontal at pin_y from rail_x
                        # to pin_x.  Rail-end via stack drawn once
                        # outside this loop (see comment above).
                        draw_wire(
                            top,
                            start=(rail_x, pin_ay), end=(pin_ax, pin_ay),
                            layer="met2",
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
        #
        # Rails MUST sit east of the predecoder block's rightmost
        # internal met2.  Stage-2 NAND3 cells have an internal met2
        # rail at cell-local x=[2.530, 5.945], y=[0.795, 1.335]
        # (likely the cell's VDD anchor).  The rightmost stage-2 cell
        # at x_origin=58.41 puts that internal met2 at abs x_max=64.355,
        # y=[10.175, 10.715].  Per-row spurs to the final NAND column
        # emit a via2 stack at (rail_x, pin_y).  For row 6 (pin_y=10.61
        # from pin C y_local=1.130, r=6 even: 6*1.58+1.130) the via2
        # met2 pad lands at y=[10.425, 10.795] — y_min=10.425 OVERLAPS
        # the stage-2 cell's internal met2 y_max=10.435 by 10 nm.  If
        # the rail x is also inside the cell's met2 x range (≤64.355),
        # the spur pad shorts pred*_out_0 to that cell's internal net,
        # which the cell ties to its pin C — so addr[6] (pin C of
        # cell 135 in the chain) bridges to pred2_out_0.  Original
        # offsets [-0.4, -2.3, -3.8] put rails at x=64.14 / 65.64 —
        # BOTH inside the predecoder block (which ends at x=65.94).
        # Shift rails into the (pred_block_right_x, nand_x) gap so
        # every via2 pad sits east of stage-2 cell internal met2.
        rail_offsets = [-0.4, -1.4, -2.4][: self.final_fanin]

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

            # Routing from the first-NAND Z output to its pred_out rail.
            # Two cases based on stage fan-in:
            #
            # NAND2 (k=2): Z cell-local y (1.65) is ABOVE both input
            #   pins (A=1.095, B=0.555).  A direct met2 horizontal at
            #   z_ay clears the A/B pin via-stack pads (y_local_top
            #   ≤1.255).  But that horizontal crosses the pred_out
            #   rails of stages whose pred_out_x is east of this stage's
            #   rail_x (on met3) — met2 over met3 does not short.
            #
            # NAND3 (k=3): Z cell-local y (0.285) is BELOW all input
            #   pins (A=0.41, B=0.77, C=1.13).  A met2 horizontal at
            #   z_ay passes through every other NAND3's A-pin via-stack
            #   met2 pad AND a met2 vertical jog crosses the A/B/C
            #   input spurs (also met2) — both cause rail merges.
            #   Solution: route the ENTIRE Z→rail path on met3.  The
            #   vertical jog at x=z_ax is inside the first cell body
            #   (no existing met3).  The horizontal at jog_y hits
            #   rail_x (merge intended — same layer).  Stage 2 is the
            #   westernmost pred_out rail by construction so the
            #   horizontal never crosses the other pred_out rails.
            # NAND2 and NAND3 both use a li1→met1→met2 detour with a
            # final met2→met3 hop AT the destination rail.  The horizontal
            # leg MUST be on met2 (not met3): for stages 0/1 the rail is
            # east of other stages' rails, so a met3 horizontal would
            # cross those other met3 rails on its way east and merge
            # pred0_out_0 ≡ pred1_out_0 ≡ pred2_out_0 at the parent level.
            # Routing on met2 keeps the horizontal one layer below all
            # the met3 rails (different layers don't merge without a via).
            #
            # NAND3 (k=3): Z y_local=0.285 (below pins 0.41/0.77/1.13).
            #   Direct met2 at z_ay would touch every downstream row's
            #   A-pin spur at z_ay+row*pitch, so detour up to a jog y
            #   above the cell.
            #
            # NAND2 (k=2): Z y_local=1.255 inside the Z li1 strip
            #   y∈[1.170, 1.340].  Addr feeders to A (y_local=1.095)
            #   and B (y_local=0.555) run on parent met2.  A's spur is
            #   only 0.16 µm below z_ay — no z_ay inside the Z strip
            #   gives met2 clearance.  Detour up to jog_y above the
            #   cell so the met2 horizontal sits well above every
            #   addr feeder.
            #
            # Step-by-step:
            #   1. li1→met1 stack at Z (small, contained, lands inside
            #      the foundry Z li1 strip).
            #   2. met1 vertical from z_ay up to jog_y (above cell top).
            #   3. met1→met2 stack at jog_y (intermediate pad).
            #   4. met2 horizontal east to rail_x — crosses other
            #      stages' met3 rails harmlessly (different layer).
            #   5. met2→met3 stack at rail_x lands on the target rail.
            if k == 3:
                jog_y = g["y_origin"] + 1.7
            else:  # k == 2
                # NAND2 cell height ≈ 1.99 µm; jog above cell top.
                jog_y = g["y_origin"] + 2.2
            draw_via_stack(
                top, from_layer="li1", to_layer="met1",
                position=(z_ax, z_ay),
            )
            draw_wire(
                top, start=(z_ax, z_ay), end=(z_ax, jog_y),
                layer="met1",
            )
            # met1→met4 stack at z_ax (predecoder block, x≪65 µm
            # rails area).  Has intermediate met2 and met3 pads, but
            # they sit at z_ax which is well west of every per-row
            # spur (those run from x=rail_x≈65.5–67.5 east to the
            # final-NAND pin), so no conflict.
            draw_via_stack(
                top, from_layer="met1", to_layer="met4",
                position=(z_ax, jog_y),
            )
            # met4 horizontal from (z_ax, jog_y) east to rail_x.
            # met4 has no other consumers in the row_decoder cell —
            # rails are met3, per-row spurs are met2 — so this layer
            # is uncluttered for the long east-bound run.
            draw_wire(
                top, start=(z_ax, jog_y), end=(rail_x, jog_y),
                layer="met4",
            )
            # met3↔met4 stack at rail_x.  CRUCIAL: from="met3"
            # to="met4" means the via stack only emits met3 and
            # met4 pads — NO met2 pad — so the rail-end pad cannot
            # bridge any of the per-row met2 spurs running through
            # this x at nearby y values.  The met3 pad lands on the
            # target pred_out rail (same net).
            draw_via_stack(
                top, from_layer="met3", to_layer="met4",
                position=(rail_x, jog_y),
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
                jog_y + 0.2,
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
    # on the li1 output strip.  Coordinates verified against the foundry
    # GDS layout AND clearance-checked against all internal li1/met1
    # polygons that the via stack's lower-metal pads might bridge.
    #
    #   NAND2: li1 Z strip x=[0.940, 4.330], y=[1.170, 1.340].  Z label
    #          sits at (2.635, 1.255), but x=2.635 is inside an internal
    #          li1 strip at x=[2.435, 2.605] y=[0.450, 1.170] (drain
    #          interconnect for the cell's nmos stack).  Landing the
    #          via stack at the label's x bridged Z's li1 to that
    #          internal drain through the parent-level li1 pad,
    #          shorting pred_out_X to addr[Y] (e.g.
    #          addr[0]↔pred0_out_0).  Clearance map for y=1.255 ± 0.18:
    #            internal li1 strip [4]    x∈[2.435, 2.605] → avoid (2.275, 2.765)
    #            internal met1 strip       x∈[1.120, 1.360] → avoid (0.945, 1.535)
    #            internal met1 strip       x∈[3.240, 3.490] → avoid (3.065, 3.665)
    #          Picking x=2.0 sits cleanly inside Z's x range and clears
    #          every internal poly halo by ≥0.27 µm.
    #
    #          Earliest (2.75, 1.65) value was OUTSIDE the Z strip
    #          (y=1.65 vs strip top y=1.340), so the li1→met2 via stack
    #          landed on EMPTY li1 — the parent-drawn li1 pad created
    #          an isolated li1 island, and the NAND2's Z output never
    #          reached the met3 pred_out rail.  Effect: every final
    #          NAND3 A/B input was driven by a rail whose source was
    #          floating, dropping each stage-0/stage-1 pred_out's
    #          fanout from 129 to 128 and adding 2 orphan source-only
    #          nets to the decoder (Δ_decoder = +2).
    #
    #   NAND3: li1 Z strip x=[1.610, 7.510], y=[0.200, 0.370];
    #          label @ (7.370, 0.285).  (3.5, 0.285) is also inside
    #          this strip and clears all internal polys, so kept.
    _NAND_Z_PIN_POS: dict[int, tuple[float, float]] = {
        2: (2.0, 1.255),
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
        from rekolektion.macro.routing import (
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
                text=f"addr[{i}]",
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
        from rekolektion.macro.routing import draw_label

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
        z_lx, z_ly = self._NAND_Z_PIN_POS[k_fanin]
        # X-mirror odd rows so adjacent cells share power rails — this
        # is the standard dec-family tiling pattern (same as the bitcell
        # array) and is what keeps the layout DRC-clean at pitch 1.58.
        for row in range(self.num_rows):
            if row % 2 == 0:
                top.add(gdstk.Reference(
                    nand_cell, origin=(x, row * _NAND_DEC_PITCH),
                ))
                z_ay = row * _NAND_DEC_PITCH + z_ly
            else:
                top.add(gdstk.Reference(
                    nand_cell,
                    origin=(x, (row + 1) * _NAND_DEC_PITCH),
                    x_reflection=True,
                ))
                z_ay = (row + 1) * _NAND_DEC_PITCH - z_ly
            # Expose each row's Z output as port `dec_out_{row}` of
            # the row_decoder cell so the parent macro's X-instance
            # can connect it to the wl_driver's A input.  Magic only
            # promotes a label to a port when (a) the label sits on a
            # drawn polygon owned by THIS cell (not a sub-cell
            # reference) and (b) a `.pin` purpose polygon (datatype
            # 16) exists on that same net.  An earlier attempt with
            # only a li1.label landed the label on the foundry NAND3's
            # internal Z li1 strip; Magic registered the net but did
            # not export it as a row_decoder port, so the row_decoder
            # subckt was emitted with only 17 ports (addr[0..6] +
            # stray subcell paths) instead of 137, and the parent
            # X-instance line collapsed to all `we` placeholders.
            #
            # Fix: draw a li1.pin shape OVER the foundry NAND3's full
            # Z strip (cell-local x=[1.61, 7.51], y=[0.20, 0.37]) plus
            # a label.  The wide pin guarantees any later parent-level
            # drop on this strip merges with the named port — assembler
            # `_route_wl` lands its li1→met1 stack at cell-local x=7.01
            # (right_edge − _NAND_OUTPUT_X_OFFSET_FROM_RIGHT = 0.5),
            # which is far from z_lx = 3.5.  A narrow stub at z_lx
            # gave the row_decoder.ext a dec_out_X port but the parent
            # drop missed it, so the X-instance still emitted
            # `instance/dec_out_X` (subcell-internal) instead of the
            # top-level dec_out_X net that wl_driver expects.
            from rekolektion.macro.routing import draw_pin_with_label
            # Z-strip absolute extent (cell-local x [1.61, 7.51],
            # y_local [0.20, 0.37], width 0.17 µm).  For odd rows the
            # cell is x_reflected so y mirrors around the cell centre;
            # the strip's y in absolute terms is [z_ay-0.085, z_ay+0.085].
            draw_pin_with_label(
                top, text=f"dec_out_{row}", layer="li1",
                rect=(x + 1.61, z_ay - 0.085,
                      x + 7.51, z_ay + 0.085),
            )
