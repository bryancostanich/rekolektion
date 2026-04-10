"""LEF abstract generator for SRAM macros.

Generates LEF (Library Exchange Format) files for use in OpenLane
place-and-route.  The LEF describes macro dimensions, pin locations
and directions, and obstruction layers.

Usage::

    from rekolektion.macro.assembler import compute_macro_params
    from rekolektion.macro.lef_generator import generate_lef

    params = compute_macro_params(words=1024, bits=32, mux_ratio=8)
    params.macro_width = 500.0
    params.macro_height = 400.0
    generate_lef(params, "output/sram_1024x32.lef")
"""

from __future__ import annotations

import math
from pathlib import Path

from rekolektion.macro.assembler import MacroParams
from rekolektion.macro.outputs import _pn


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_PIN_WIDTH = 0.14    # met2 minimum width (um)
_PIN_HEIGHT = 0.28   # pin rect height (um)
_PIN_PITCH = 0.28    # met2 pitch (um)
_PIN_LAYER = "met2"


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _snap(v: float, grid: float = 0.005) -> float:
    """Snap coordinate to manufacturing grid (5nm for SKY130)."""
    return round(v / grid) * grid


def _pin_rect(cx: float, cy: float) -> str:
    """Return a RECT string centred on (cx, cy), snapped to mfg grid."""
    x1 = _snap(cx - _PIN_WIDTH / 2)
    y1 = _snap(cy - _PIN_HEIGHT / 2)
    x2 = _snap(cx + _PIN_WIDTH / 2)
    y2 = _snap(cy + _PIN_HEIGHT / 2)
    return f"        RECT {x1:.3f} {y1:.3f} {x2:.3f} {y2:.3f} ;"


def _pin_block(
    name: str,
    direction: str,
    cx: float,
    cy: float,
    *,
    use: str | None = None,
) -> list[str]:
    """Generate LEF lines for a single pin."""
    lines = [
        f"  PIN {name}",
        f"    DIRECTION {direction} ;",
    ]
    if use:
        lines.append(f"    USE {use} ;")
    lines += [
        "    PORT",
        f"      LAYER {_PIN_LAYER} ;",
        _pin_rect(cx, cy),
        "    END",
        f"  END {name}",
    ]
    return lines


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def generate_lef(
    params: MacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    uppercase_ports: bool = False,
) -> Path:
    """Generate a LEF abstract for the SRAM macro.

    Parameters
    ----------
    params : MacroParams
        Macro parameters (words, bits, dimensions, etc.).
    output_path : path
        Write LEF to this file.

    Returns
    -------
    Path
        The output file path.
    """
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    w = params.macro_width
    h = params.macro_height
    if not macro_name:
        macro_name = f"sram_{params.words}x{params.bits}_mux{params.mux_ratio}"
    addr_bits = params.num_addr_bits
    data_bits = params.bits
    ben_bits = params.num_ben_bits
    scan = params.scan_chain

    lines: list[str] = []

    # Header
    lines += [
        "VERSION 5.7 ;",
        "BUSBITCHARS \"[]\" ;",
        "DIVIDERCHAR \"/\" ;",
        "",
        "UNITS",
        "  DATABASE MICRONS 1000 ;",
        "END UNITS",
        "",
        f"MACRO {macro_name}",
        "  CLASS BLOCK ;",
        f"  SIZE {w:.3f} BY {h:.3f} ;",
        "  SYMMETRY X Y ;",
        "",
    ]

    # --- Pin placement ---
    up = uppercase_ports

    # Left edge: address pins, evenly spaced vertically
    addr_start_y = h * 0.1
    addr_span = h * 0.8
    addr_step = addr_span / max(addr_bits, 1)
    for i in range(addr_bits):
        cy = addr_start_y + i * addr_step + addr_step / 2
        lines += _pin_block(_pn(f"addr[{i}]", up), "INPUT", cx=0.0, cy=cy)
        lines.append("")

    # Right edge: din and dout, evenly spaced vertically
    data_pins_total = data_bits * 2  # din + dout
    data_start_y = h * 0.05
    data_span = h * 0.9
    data_step = data_span / max(data_pins_total, 1)
    for i in range(data_bits):
        cy = data_start_y + i * data_step + data_step / 2
        lines += _pin_block(_pn(f"din[{i}]", up), "INPUT", cx=w, cy=cy)
        lines.append("")
    for i in range(data_bits):
        cy = data_start_y + (data_bits + i) * data_step + data_step / 2
        lines += _pin_block(_pn(f"dout[{i}]", up), "OUTPUT", cx=w, cy=cy)
        lines.append("")

    # Top edge: VPWR, centred
    lines += _pin_block("VPWR", "INOUT", cx=w / 2, cy=h, use="POWER")
    lines.append("")

    # Bottom edge: VGND, clk, we, cs, [ben] — spread across width
    bottom_pins = [
        ("VGND", "INOUT", "GROUND"),
        (_pn("clk", up), "INPUT", None),
        (_pn("we", up), "INPUT", None),
        (_pn("cs", up), "INPUT", None),
    ]
    if ben_bits:
        for i in range(ben_bits):
            bottom_pins.append((_pn(f"ben[{i}]", up), "INPUT", None))
    if scan:
        bottom_pins += [
            (_pn("scan_in", up), "INPUT", None),
            (_pn("scan_out", up), "OUTPUT", None),
            (_pn("scan_en", up), "INPUT", None),
        ]
    if params.clock_gating:
        bottom_pins.append((_pn("cen", up), "INPUT", None))
    if params.power_gating:
        bottom_pins.append((_pn("sleep", up), "INPUT", None))
    if params.wl_switchoff:
        bottom_pins.append((_pn("wl_off", up), "INPUT", None))
    if params.burn_in:
        bottom_pins.append((_pn("tm", up), "INPUT", None))
    bottom_step = w / (len(bottom_pins) + 1)
    for idx, (pname, pdir, puse) in enumerate(bottom_pins):
        cx = bottom_step * (idx + 1)
        lines += _pin_block(pname, pdir, cx=cx, cy=0.0, use=puse)
        lines.append("")

    # --- OBS (obstruction) ---
    # met1/met2: full-area obstruction (dense internal routing)
    # met3: narrow strips only at power rail via locations (top/bottom edges)
    #   The SRAM uses met3 only for via2 landing pads connecting met2 power
    #   straps to met3 power rails. These are at the first and last bitcell
    #   rows (~15 um from each edge). Obstructing only these strips frees
    #   93% of the met3 area for over-the-macro signal routing.
    # met4/met5: not obstructed (SRAM doesn't use them)
    met3_rail_margin = 18.0  # um from each edge to cover power rail vias
    lines += [
        "  OBS",
    ]
    for layer in ("met1", "met2"):
        lines += [
            f"    LAYER {layer} ;",
            f"      RECT 0.000 0.000 {w:.3f} {h:.3f} ;",
        ]
    lines += [
        f"    LAYER met3 ;",
        f"      RECT 0.000 0.000 {w:.3f} {met3_rail_margin:.3f} ;",
        f"      RECT 0.000 {_snap(h - met3_rail_margin):.3f} {w:.3f} {h:.3f} ;",
    ]
    lines += [
        "  END",
        "",
        f"END {macro_name}",
        "",
        "END LIBRARY",
        "",
    ]

    out.write_text("\n".join(lines))
    return out
