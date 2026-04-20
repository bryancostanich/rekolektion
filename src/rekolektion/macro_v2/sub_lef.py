"""Per-sub-block LEF generator for OpenROAD macro flow.

For Option Y (OpenROAD routes top-level signals), each sub-block in
the SRAM macro needs a LEF abstract that exposes its externally-
routed pins.  Pins that are already routed by the assembler (BL/BR
column strips, WL horizontal wires, PDN rails) do NOT get LEF pins —
they're electrically connected via physical overlap between adjacent
sub-blocks.

Pins that DO get LEF entries are those OpenROAD will drive at the
top level:
  - ctrl_logic : clk, we, cs + DFF Q outputs (clk_buf, p_en_bar, s_en,
                 w_en) + NAND2 A/B/Z pins (so the Z-to-D feedback
                 inside ctrl_logic also routes at the top)
  - row_decoder: addr[*] + wl_en (+ dec_out[*] is pre-routed to
                 wl_driver by the assembler, not exposed)
  - wl_driver  : (nothing — dec_out and wl are both pre-routed)
  - precharge  : p_en_bar
  - column_mux : col_sel[*]
  - sense_amp  : s_en + dout[*]
  - write_driver: w_en + din[*]
  - bitcell_array: (nothing — WL/BL/BR all pre-routed)

All pins are emitted at each cell instance's own pin location, NOT
pre-routed to a single "block-boundary" pin.  This avoids the cell-
interior collision issues we hit with Option Z hand-routing; OpenROAD
sees the unique per-instance pins and routes at its own discretion.

Power pins (VPWR/VGND) are emitted as large RECTs covering the block's
PDN contact footprint so OpenLane's PDN macro-hook logic can connect
them to the chip-level power grid.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import TextIO

from rekolektion.macro_v2.assembler import MacroV2Params
from rekolektion.macro_v2.row_decoder import _SPLIT_TABLE, _NAND_DEC_PITCH


# ---------------------------------------------------------------------------
# Pin descriptor + LEF emission core
# ---------------------------------------------------------------------------


def _snap(v: float, grid: float = 0.005) -> float:
    """Snap a coordinate to the 5-nm manufacturing grid (all LEF
    RECT coordinates must land on this grid or DRT-0416 rejects)."""
    return round(v / grid) * grid


@dataclass(frozen=True)
class LefPin:
    name: str
    layer: str                       # "met1", "met2", "li1", ...
    rect: tuple[float, float, float, float]  # (x1, y1, x2, y2) cell-local
    direction: str = "INPUT"         # INPUT / OUTPUT / INOUT
    use: str = "SIGNAL"              # SIGNAL / POWER / GROUND


def emit_lef(
    f: TextIO,
    macro_name: str,
    width: float,
    height: float,
    pins: list[LefPin],
    include_power: bool = True,
) -> None:
    f.write(f"MACRO {macro_name}\n")
    f.write(f"  CLASS BLOCK ;\n")
    f.write(f"  FOREIGN {macro_name} ;\n")
    f.write(f"  ORIGIN 0 0 ;\n")
    f.write(f"  SIZE {width:.3f} BY {height:.3f} ;\n")
    f.write(f"  SYMMETRY X Y ;\n\n")
    if include_power:
        # Emit VPWR + VGND as met2 horizontal strips covering the full
        # block width at the top + bottom edges.  OpenLane/OpenROAD
        # uses these to hook each block into the macro-level PDN.
        for name, y_frac, use_kw in (
            ("VPWR", 1.0, "POWER"),
            ("VGND", 0.0, "GROUND"),
        ):
            y_c = y_frac * height
            y1 = max(0.0, y_c - 0.5)
            y2 = min(height, y_c + 0.5)
            # snap to bounds when strip pushes outside the block
            if y_frac == 0.0:
                y1, y2 = 0.0, 1.0
            elif y_frac == 1.0:
                y1, y2 = max(0.0, height - 1.0), height
            f.write(f"  PIN {name}\n")
            f.write(f"    DIRECTION INOUT ;\n")
            f.write(f"    USE {use_kw} ;\n")
            f.write(f"    PORT\n")
            f.write(f"      LAYER met2 ;\n")
            f.write(f"        RECT 0.000 {y1:.3f} {width:.3f} {y2:.3f} ;\n")
            f.write(f"    END\n")
            f.write(f"  END {name}\n\n")
    for pin in pins:
        f.write(f"  PIN {pin.name}\n")
        f.write(f"    DIRECTION {pin.direction} ;\n")
        f.write(f"    USE {pin.use} ;\n")
        f.write(f"    PORT\n")
        f.write(f"      LAYER {pin.layer} ;\n")
        x1, y1, x2, y2 = pin.rect
        x1, y1, x2, y2 = _snap(x1), _snap(y1), _snap(x2), _snap(y2)
        f.write(f"        RECT {x1:.3f} {y1:.3f} {x2:.3f} {y2:.3f} ;\n")
        f.write(f"    END\n")
        f.write(f"  END {pin.name}\n\n")
    f.write(f"END {macro_name}\n\n")


def emit_lef_header(f: TextIO) -> None:
    f.write(
        'VERSION 5.7 ;\n'
        'BUSBITCHARS "[]" ;\n'
        'DIVIDERCHAR "/" ;\n'
        '\n'
        'UNITS\n'
        '  DATABASE MICRONS 1000 ;\n'
        'END UNITS\n\n'
    )


# ---------------------------------------------------------------------------
# Cell-local pin position tables (extracted from foundry LEFs + labels)
# ---------------------------------------------------------------------------

# DFF met2 pins (ORIGIN 0 0 so LEF RECT == GDS coord).
_DFF_PINS: dict[str, tuple[float, float, float, float, str]] = {
    # name: (x1, y1, x2, y2, layer)
    "CLK": (1.845, 3.460, 2.115, 3.780, "met2"),
    "D":   (0.685, 2.690, 1.015, 2.950, "met2"),
    "Q":   (5.410, 3.045, 5.740, 3.305, "met2"),
    # Q_N has a LEF RECT but NO label in the foundry GDS; Magic won't
    # promote it to a port in hierarchical extraction, so we also
    # leave it off the sub-block LEF.
}
_DFF_W: float = 6.200  # placement pitch (matches assembler _DFF_W)
_DFF_H: float = 7.545

# NAND2_dec li1 pins — from the foundry GDS label positions (label
# coords differ slightly from the LEF pin RECT because the RECT is a
# LEF abstract, not the actual li1 geometry).  Using label coords so
# Magic connects the LEF pin to the internal net.
_NAND2_PINS: dict[str, tuple[float, float, float, float, str]] = {
    "A": (0.320, 0.930, 0.490, 1.260, "li1"),  # actual li1 poly
    "B": (0.320, 0.390, 0.490, 0.720, "li1"),
    "Z": (0.940, 1.170, 4.330, 1.340, "li1"),
}
_NAND2_W: float = 4.770
_NAND2_H: float = 2.690

# NAND3_dec pin rects (li1 polygons covering each label).
_NAND3_PINS: dict[str, tuple[float, float, float, float, str]] = {
    "A": (1.100, 0.325, 1.430, 0.495, "li1"),
    "B": (0.550, 0.685, 0.880, 0.855, "li1"),
    "C": (0.000, 1.045, 0.330, 1.215, "li1"),
    "Z": (1.580, 0.200, 7.510, 0.370, "li1"),
}
_NAND3_PITCH: float = 1.580

# Sense amp pin RECTs (from LEF RECT, approximate — all on met1/met2).
_SENSE_AMP_PINS: dict[str, tuple[float, float, float, float, str]] = {
    # From foundry sense_amp LEF
    "BL":   (0.050, 10.755, 0.385, 10.985, "met1"),
    "BR":   (0.845, 10.755, 1.180, 10.985, "met1"),
    "EN":   (0.465, 10.805, 0.765, 11.135, "met1"),
    "DOUT": (0.500, 0.045, 0.830, 0.415, "met1"),
}

# Write driver pin RECTs
_WRITE_DRIVER_PINS: dict[str, tuple[float, float, float, float, str]] = {
    "BL":  (0.050, 9.745, 0.385, 9.975, "met1"),
    "BR":  (2.115, 9.745, 2.450, 9.975, "met1"),
    "DIN": (1.200, 0.045, 1.535, 0.415, "met1"),
    "EN":  (1.340, 0.445, 1.670, 0.815, "met1"),
}

# Pitch constants (bitcell-scaled)
_BITCELL_WIDTH: float = 1.31
_SENSE_AMP_WIDTH: float = 1.0  # per pair ~(actually 2.98 um, see sense_amp_row.py)
_WRITE_DRIVER_WIDTH: float = 2.5


# ---------------------------------------------------------------------------
# Per-block LEF generators
# ---------------------------------------------------------------------------


def _tag(p: MacroV2Params) -> str:
    return f"m{p.mux_ratio}_{p.words}x{p.bits}"


def lef_ctrl_logic(p: MacroV2Params, ctrl_size: tuple[float, float]) -> tuple[str, list[LefPin]]:
    """Expose every DFF + NAND2 signal pin individually on the
    ctrl_logic block.  Top-level Verilog wires multiple pins of the
    same net together (e.g. all 4 DFF CLK pins to `clk`)."""
    name = f"ctrl_logic_{_tag(p)}"
    pins: list[LefPin] = []
    dir_by_pin = {"CLK": "INPUT", "D": "INPUT", "Q": "OUTPUT"}
    for i in range(4):  # 4 DFFs placed at x=i*_DFF_W, y=0
        for pn, (x1, y1, x2, y2, layer) in _DFF_PINS.items():
            pins.append(LefPin(
                name=f"dff{i}_{pn.lower()}",
                layer=layer,
                rect=(i * _DFF_W + x1, y1, i * _DFF_W + x2, y2),
                direction=dir_by_pin[pn],
            ))
    # 2 NAND2s placed at x=0, _NAND2_W, y=y_nand (= _DFF_H + 2.0)
    y_nand = _DFF_H + 2.0
    dir_by_nand = {"A": "INPUT", "B": "INPUT", "Z": "OUTPUT"}
    for i in range(2):
        for pn, (x1, y1, x2, y2, layer) in _NAND2_PINS.items():
            pins.append(LefPin(
                name=f"nand{i}_{pn.lower()}",
                layer=layer,
                rect=(
                    i * _NAND2_W + x1, y_nand + y1,
                    i * _NAND2_W + x2, y_nand + y2,
                ),
                direction=dir_by_nand[pn],
            ))
    return name, pins


def lef_row_decoder(p: MacroV2Params, rd_size: tuple[float, float]) -> tuple[str, list[LefPin]]:
    """Single-predecoder case: N rows of NAND_k cells at pitch 1.58
    (X-mirrored on odd rows).  Expose each NAND's A/B/C signal pins
    plus Z outputs (Z is also pre-routed to wl_driver, so declaring
    it as an OpenROAD-reachable pin is redundant but harmless)."""
    name = f"row_decoder_{_tag(p)}"
    split = _SPLIT_TABLE[p.rows]
    if len(split) != 1:
        return name, []  # multi-predecoder case not yet handled
    k = split[0]
    if k not in (2, 3):
        return name, []
    pin_names = ["A", "B", "C"][:k] + ["Z"]
    pins: list[LefPin] = []
    dir_by_pin = {"A": "INPUT", "B": "INPUT", "C": "INPUT", "Z": "OUTPUT"}
    for r in range(p.rows):
        for pn in pin_names:
            x1, y1, x2, y2, layer = _NAND3_PINS[pn]
            if r % 2 == 0:
                ry1 = r * _NAND3_PITCH + y1
                ry2 = r * _NAND3_PITCH + y2
            else:
                ry1 = (r + 1) * _NAND3_PITCH - y2
                ry2 = (r + 1) * _NAND3_PITCH - y1
            pins.append(LefPin(
                name=f"nand{r}_{pn.lower()}",
                layer=layer,
                rect=(x1, ry1, x2, ry2),
                direction=dir_by_pin[pn],
            ))
    return name, pins


def lef_wl_driver(p: MacroV2Params, wld_size: tuple[float, float]) -> tuple[str, list[LefPin]]:
    """Expose each wl_driver NAND3's A (input from decoder) and Z
    (output to array WL poly).  Both are pre-routed by the assembler,
    so the LEF pins are documentation-only in the macro-flow case,
    but OpenROAD needs them declared if the Verilog references them."""
    name = f"wl_driver_{_tag(p)}"
    pins: list[LefPin] = []
    for r in range(p.rows):
        for pn in ("A", "Z"):
            x1, y1, x2, y2, layer = _NAND3_PINS[pn]
            if r % 2 == 0:
                ry1 = r * _NAND3_PITCH + y1
                ry2 = r * _NAND3_PITCH + y2
            else:
                ry1 = (r + 1) * _NAND3_PITCH - y2
                ry2 = (r + 1) * _NAND3_PITCH - y1
            pins.append(LefPin(
                name=f"nand{r}_{pn.lower()}",
                layer=layer,
                rect=(x1, ry1, x2, ry2),
                direction="INPUT" if pn == "A" else "OUTPUT",
            ))
    return name, pins


def lef_sense_amp_row(p: MacroV2Params, sa_size: tuple[float, float]) -> tuple[str, list[LefPin]]:
    """Expose each sense amp's EN (input) and DOUT (output) per bit.
    BL/BR are already connected to the array column strips via the
    assembler's _route_bl extension."""
    name = f"sa_{_tag(p)}"
    pins: list[LefPin] = []
    pitch = p.mux_ratio * _BITCELL_WIDTH
    for i in range(p.bits):
        x_offset = i * pitch
        for pn, dir_ in (("EN", "INPUT"), ("DOUT", "OUTPUT")):
            x1, y1, x2, y2, layer = _SENSE_AMP_PINS[pn]
            pins.append(LefPin(
                name=f"sa{i}_{pn.lower()}",
                layer=layer,
                rect=(x_offset + x1, y1, x_offset + x2, y2),
                direction=dir_,
            ))
    return name, pins


def lef_write_driver_row(p: MacroV2Params, wd_size: tuple[float, float]) -> tuple[str, list[LefPin]]:
    name = f"wd_{_tag(p)}"
    pins: list[LefPin] = []
    pitch = p.mux_ratio * _BITCELL_WIDTH
    for i in range(p.bits):
        x_offset = i * pitch
        for pn, dir_ in (("EN", "INPUT"), ("DIN", "INPUT")):
            x1, y1, x2, y2, layer = _WRITE_DRIVER_PINS[pn]
            pins.append(LefPin(
                name=f"wd{i}_{pn.lower()}",
                layer=layer,
                rect=(x_offset + x1, y1, x_offset + x2, y2),
                direction=dir_,
            ))
    return name, pins


def lef_precharge_row(p: MacroV2Params, pre_size: tuple[float, float]) -> tuple[str, list[LefPin]]:
    """Python-generated precharge row.  Single p_en_bar signal that
    drives every pair's precharge+equalizer PMOS gates.  The signal
    runs on met1 horizontally across the block (pre-drawn by
    peripherals/precharge.py).  Expose it as a single LEF pin."""
    name = f"pre_{_tag(p)}"
    # p_en_bar signal is the horizontal met1 strip at cell-local y
    # matching precharge.py's design.  For now expose a small RECT
    # at the center — OpenROAD will find it.
    w, h = pre_size
    pins = [LefPin(
        name="p_en_bar",
        layer="met1",
        rect=(w / 2 - 0.15, h / 2 - 0.10, w / 2 + 0.15, h / 2 + 0.10),
        direction="INPUT",
    )]
    return name, pins


def lef_column_mux_row(p: MacroV2Params, mux_size: tuple[float, float]) -> tuple[str, list[LefPin]]:
    """Python-generated column mux.  One col_sel line per mux position
    (so mux_ratio col_sel pins total, each driving the NMOS gates of
    every pair in that mux position)."""
    name = f"mux_{_tag(p)}"
    w, h = mux_size
    pins: list[LefPin] = []
    # Space col_sel pins evenly across the block height (col_sel[0]
    # lowest, col_sel[M-1] highest) on met1.  Actual cell-internal
    # layout is what matters for Magic extraction; these LEF pins are
    # the abstract entry points.
    for s in range(p.mux_ratio):
        y = (s + 1) * h / (p.mux_ratio + 1)
        pins.append(LefPin(
            name=f"col_sel_{s}",
            layer="met1",
            rect=(0.0, y - 0.10, 0.30, y + 0.10),
            direction="INPUT",
        ))
    return name, pins


def lef_bitcell_array(p: MacroV2Params, arr_size: tuple[float, float]) -> tuple[str, list[LefPin]]:
    """Bitcell array.  WL/BL/BR are all pre-routed by the assembler
    via physical overlap with the peripheral rows; no LEF pins for
    signals.  Power pins only (required by OpenLane for PDN hook-up).
    """
    name = f"sram_array_{_tag(p)}"
    return name, []  # no signal pins


# ---------------------------------------------------------------------------
# Aggregated LEF emission for the whole sub-block family
# ---------------------------------------------------------------------------


def generate_sub_block_lefs(
    p: MacroV2Params,
    output_dir: str | Path,
) -> dict[str, Path]:
    """Write one LEF file per sub-block into output_dir.

    Returns a dict: {block_logical_name -> lef_path}.  The logical
    names match the assembler's naming (sram_array, pre, mux, sa, wd,
    row_decoder, wl_driver, ctrl_logic).
    """
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    # Import lazily to avoid circularity with the assembler.
    from rekolektion.macro_v2.assembler import build_floorplan
    from rekolektion.macro_v2.bitcell_array import BitcellArray
    from rekolektion.macro_v2.control_logic import ControlLogic
    from rekolektion.macro_v2.column_mux_row import ColumnMuxRow
    from rekolektion.macro_v2.precharge_row import PrechargeRow
    from rekolektion.macro_v2.row_decoder import RowDecoder
    from rekolektion.macro_v2.sense_amp_row import SenseAmpRow
    from rekolektion.macro_v2.wl_driver_row import WlDriverRow
    from rekolektion.macro_v2.write_driver_row import WriteDriverRow

    fp = build_floorplan(p)

    generators: dict[str, tuple[callable, tuple[float, float]]] = {
        "sram_array":     (lef_bitcell_array,    fp.sizes["array"]),
        "pre":            (lef_precharge_row,    fp.sizes["precharge"]),
        "mux":            (lef_column_mux_row,   fp.sizes["col_mux"]),
        "sa":             (lef_sense_amp_row,    fp.sizes["sense_amp"]),
        "wd":             (lef_write_driver_row, fp.sizes["write_driver"]),
        "row_decoder":    (lef_row_decoder,      fp.sizes["row_decoder"]),
        "wl_driver":      (lef_wl_driver,        fp.sizes["wl_driver"]),
        "ctrl_logic":     (lef_ctrl_logic,       fp.sizes["control_logic"]),
    }

    results: dict[str, Path] = {}
    for block, (gen, size) in generators.items():
        macro_name, pins = gen(p, size)
        lef_path = out / f"{macro_name}.lef"
        with lef_path.open("w") as f:
            emit_lef_header(f)
            emit_lef(f, macro_name, size[0], size[1], pins)
        results[block] = lef_path
    return results
