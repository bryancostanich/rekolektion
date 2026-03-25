"""Foundry single-port 6T SRAM bitcell abstraction.

Wraps the SkyWater ``sram_sp_cell_opt1`` cell, parsing pin geometry from
the LEF shipped with the foundry library and loading the full-layout GDS
that was exported from the MAG file via Magic.

Typical usage::

    from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
    info = load_foundry_sp_bitcell()
    print(info.cell_width, info.cell_height)
    print(info.pin_position("BL"))
"""

from __future__ import annotations

import re
from pathlib import Path
from typing import Dict, List, Tuple

from .base import BitcellInfo, PinInfo

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

_CELLS_DIR = Path(__file__).parent / "cells"
_CELL_NAME = "sky130_fd_bd_sram__sram_sp_cell_opt1"
_GDS_FILE = _CELLS_DIR / f"{_CELL_NAME}.gds"

# The LEF lives in the cloned foundry repo.  We also bundle a copy in the
# project so that the tool works stand-alone after the initial setup.
_FOUNDRY_LEF = Path(
    "/tmp/sky130_sram_cells/cells/sram_sp_cell_opt1"
) / f"{_CELL_NAME}.magic.lef"
_LOCAL_LEF = _CELLS_DIR / f"{_CELL_NAME}.magic.lef"


# ---------------------------------------------------------------------------
# LEF parser (minimal, only what we need for pin extraction)
# ---------------------------------------------------------------------------

def _parse_lef_pins(lef_path: Path) -> Tuple[float, float, Dict[str, PinInfo]]:
    """Parse SIZE and PIN sections from a LEF macro definition.

    Returns (width, height, pins_dict).
    """
    text = lef_path.read_text()
    lines = text.splitlines()

    width = 0.0
    height = 0.0
    pins: Dict[str, PinInfo] = {}

    current_pin: str | None = None
    current_layer: str | None = None
    in_port = False

    for line in lines:
        stripped = line.strip()

        # SIZE w BY h ;
        m = re.match(r"SIZE\s+([\d.]+)\s+BY\s+([\d.]+)", stripped)
        if m:
            width = float(m.group(1))
            height = float(m.group(2))
            continue

        # PIN <name>
        m = re.match(r"PIN\s+(\S+)", stripped)
        if m:
            current_pin = m.group(1)
            pins[current_pin] = PinInfo(name=current_pin)
            in_port = False
            current_layer = None
            continue

        if stripped == "PORT":
            in_port = True
            continue

        if stripped.startswith("END") and current_pin and stripped == f"END {current_pin}":
            current_pin = None
            in_port = False
            current_layer = None
            continue

        if not in_port or current_pin is None:
            continue

        # LAYER <name> ;
        m = re.match(r"LAYER\s+(\S+)\s*;", stripped)
        if m:
            current_layer = m.group(1)
            continue

        # RECT x1 y1 x2 y2 ;
        m = re.match(r"RECT\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)", stripped)
        if m and current_layer:
            x1, y1, x2, y2 = (float(m.group(i)) for i in range(1, 5))
            cx = (x1 + x2) / 2.0
            cy = (y1 + y2) / 2.0
            pins[current_pin].ports.append((cx, cy, current_layer))
            continue

    return width, height, pins


def _pick_primary_port(pin: PinInfo) -> PinInfo:
    """Reorder ports so the best routing layer is first.

    Priority: met2 > met1 > li1 > everything else.
    """
    layer_order = {"met2": 0, "met1": 1, "li1": 2}

    def sort_key(port: Tuple[float, float, str]) -> int:
        return layer_order.get(port[2], 99)

    pin.ports.sort(key=sort_key)
    return pin


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def load_foundry_sp_bitcell(
    gds_path: Path | None = None,
    lef_path: Path | None = None,
) -> BitcellInfo:
    """Load the SkyWater foundry 6T single-port bitcell.

    Parameters
    ----------
    gds_path : Path, optional
        Override path to the GDS file (default: bundled copy in cells/).
    lef_path : Path, optional
        Override path to the LEF file (default: foundry repo, then local copy).
    """
    gds = gds_path or _GDS_FILE
    if not gds.exists():
        raise FileNotFoundError(
            f"Foundry cell GDS not found at {gds}.  "
            "Run Magic export or copy output/foundry_sp_cell.gds into "
            "src/rekolektion/bitcell/cells/."
        )

    lef = lef_path or (_FOUNDRY_LEF if _FOUNDRY_LEF.exists() else _LOCAL_LEF)
    if not lef.exists():
        raise FileNotFoundError(
            f"LEF file not found at {lef}.  "
            "Clone the foundry cell repo or copy the LEF into cells/."
        )

    width, height, pins = _parse_lef_pins(lef)

    # Reorder each pin's ports so the highest metal layer is primary.
    for pin in pins.values():
        _pick_primary_port(pin)

    return BitcellInfo(
        cell_name=_CELL_NAME,
        cell_width=width,
        cell_height=height,
        pins=pins,
        gds_path=gds,
    )
