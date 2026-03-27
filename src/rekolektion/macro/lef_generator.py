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

    # Left edge: address pins, evenly spaced vertically
    addr_start_y = h * 0.1
    addr_span = h * 0.8
    addr_step = addr_span / max(addr_bits, 1)
    for i in range(addr_bits):
        cy = addr_start_y + i * addr_step + addr_step / 2
        lines += _pin_block(f"addr[{i}]", "INPUT", cx=0.0, cy=cy)
        lines.append("")

    # Right edge: din and dout, evenly spaced vertically
    data_pins_total = data_bits * 2  # din + dout
    data_start_y = h * 0.05
    data_span = h * 0.9
    data_step = data_span / max(data_pins_total, 1)
    for i in range(data_bits):
        cy = data_start_y + i * data_step + data_step / 2
        lines += _pin_block(f"din[{i}]", "INPUT", cx=w, cy=cy)
        lines.append("")
    for i in range(data_bits):
        cy = data_start_y + (data_bits + i) * data_step + data_step / 2
        lines += _pin_block(f"dout[{i}]", "OUTPUT", cx=w, cy=cy)
        lines.append("")

    # Top edge: VPWR, centred
    lines += _pin_block("VPWR", "INOUT", cx=w / 2, cy=h, use="POWER")
    lines.append("")

    # Bottom edge: VGND, clk, we, cs — spread across width
    bottom_step = w / 5
    for idx, (pname, pdir, puse) in enumerate([
        ("VGND", "INOUT", "GROUND"),
        ("clk", "INPUT", None),
        ("we", "INPUT", None),
        ("cs", "INPUT", None),
    ]):
        cx = bottom_step * (idx + 1)
        lines += _pin_block(pname, pdir, cx=cx, cy=0.0, use=puse)
        lines.append("")

    # --- OBS (obstruction) ---
    lines += [
        "  OBS",
    ]
    for layer in ("met1", "met2", "met3"):
        lines += [
            f"    LAYER {layer} ;",
            f"      RECT 0.000 0.000 {w:.3f} {h:.3f} ;",
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
