"""Foundry peripheral cell loader for SRAM macro assembly.

Loads pre-exported GDS files and parses LEF pin data for peripheral cells:
- Sense amplifier (openram_sense_amp)
- Write driver (openram_write_driver)
- NAND2/3/4 decoder gates (openram_sp_nand{2,3,4}_dec)
- D flip-flop (openram_dff)

The GDS files in cells/ were copied from the foundry library at
/tmp/sky130_sram_cells/cells/.  If a cell's GDS is missing, a placeholder
cell with the correct pin interface (from the LEF) is generated instead.

Usage::

    from rekolektion.peripherals.foundry_cells import get_peripheral_cell
    sa = get_peripheral_cell("sense_amp")
    print(sa.cell_name, sa.width, sa.height)
"""

from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Tuple

import gdstk

from rekolektion.bitcell.base import PinInfo

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

_CELLS_DIR = Path(__file__).parent / "cells"

# short_name -> (full cell name, directory name in foundry repo)
_CELL_REGISTRY: Dict[str, str] = {
    "sense_amp":  "sky130_fd_bd_sram__openram_sense_amp",
    "write_driver": "sky130_fd_bd_sram__openram_write_driver",
    "nand2_dec":  "sky130_fd_bd_sram__openram_sp_nand2_dec",
    "nand3_dec":  "sky130_fd_bd_sram__openram_sp_nand3_dec",
    "nand4_dec":  "sky130_fd_bd_sram__openram_sp_nand4_dec",
    "dff":        "sky130_fd_bd_sram__openram_dff",
    "precharge":  "precharge_0",
    "column_mux": "single_level_column_mux",
}


# ---------------------------------------------------------------------------
# LEF parser (reuses approach from foundry_sp.py / support_cells.py)
# ---------------------------------------------------------------------------

def _parse_lef(lef_path: Path) -> Tuple[float, float, Dict[str, PinInfo]]:
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

        m = re.match(r"SIZE\s+([\d.]+)\s+BY\s+([\d.]+)", stripped)
        if m:
            width = float(m.group(1))
            height = float(m.group(2))
            continue

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

        m = re.match(r"LAYER\s+(\S+)\s*;", stripped)
        if m:
            current_layer = m.group(1)
            continue

        m = re.match(r"RECT\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)", stripped)
        if m and current_layer:
            x1, y1, x2, y2 = (float(m.group(i)) for i in range(1, 5))
            cx = (x1 + x2) / 2.0
            cy = (y1 + y2) / 2.0
            pins[current_pin].ports.append((cx, cy, current_layer))
            continue

    return width, height, pins


def _pick_primary_port(pin: PinInfo) -> PinInfo:
    """Reorder ports so the best routing layer is first."""
    layer_order = {"met2": 0, "met1": 1, "li1": 2}

    def sort_key(port: Tuple[float, float, str]) -> int:
        return layer_order.get(port[2], 99)

    pin.ports.sort(key=sort_key)
    return pin


# ---------------------------------------------------------------------------
# Peripheral cell info
# ---------------------------------------------------------------------------

@dataclass
class PeripheralCellInfo:
    """Metadata for a peripheral cell."""
    short_name: str
    cell_name: str
    width: float
    height: float
    pins: Dict[str, PinInfo] = field(default_factory=dict)
    gds_path: Path = field(default_factory=lambda: Path())
    is_placeholder: bool = False

    def get_cell(self) -> gdstk.Cell:
        """Load and return the gdstk.Cell from the GDS file."""
        lib = gdstk.read_gds(str(self.gds_path))
        for cell in lib.cells:
            if cell.name == self.cell_name:
                return cell
        return lib.cells[0]


# ---------------------------------------------------------------------------
# Placeholder generator
# ---------------------------------------------------------------------------

def _make_placeholder_gds(
    cell_name: str,
    width: float,
    height: float,
    output_path: Path,
) -> None:
    """Create a minimal placeholder GDS with a boundary rectangle."""
    lib = gdstk.Library(name=f"{cell_name}_lib")
    cell = gdstk.Cell(cell_name)
    # Boundary on layer 235 (text/boundary — won't conflict with routing)
    cell.add(gdstk.rectangle((0, 0), (width, height), layer=235, datatype=0))
    lib.add(cell)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    lib.write_gds(str(output_path))


# ---------------------------------------------------------------------------
# Cache & public API
# ---------------------------------------------------------------------------

_cache: Dict[str, PeripheralCellInfo] = {}


def get_peripheral_cell(name: str) -> PeripheralCellInfo:
    """Get a peripheral cell by short name.

    Valid names: sense_amp, write_driver, nand2_dec, nand3_dec,
                 nand4_dec, dff
    """
    if name in _cache:
        return _cache[name]

    if name not in _CELL_REGISTRY:
        raise KeyError(
            f"Unknown peripheral cell '{name}'. "
            f"Valid names: {sorted(_CELL_REGISTRY.keys())}"
        )

    cell_name = _CELL_REGISTRY[name]
    gds_path = _CELLS_DIR / f"{cell_name}.gds"
    lef_path = _CELLS_DIR / f"{cell_name}.magic.lef"

    # Parse LEF for dimensions and pins
    width, height, pins = 0.0, 0.0, {}
    if lef_path.exists():
        try:
            width, height, pins = _parse_lef(lef_path)
            for pin in pins.values():
                _pick_primary_port(pin)
        except Exception as e:
            logger.warning("Failed to parse LEF for %s: %s", name, e)
    else:
        logger.warning("LEF not found for %s at %s", name, lef_path)

    is_placeholder = False
    if not gds_path.exists():
        logger.warning(
            "GDS not found for %s at %s — generating placeholder",
            name, gds_path,
        )
        if width == 0.0:
            # Fallback dimensions for placeholder
            width = 5.0
            height = 5.0
        _make_placeholder_gds(cell_name, width, height, gds_path)
        is_placeholder = True

    info = PeripheralCellInfo(
        short_name=name,
        cell_name=cell_name,
        width=width,
        height=height,
        pins=pins,
        gds_path=gds_path,
        is_placeholder=is_placeholder,
    )
    _cache[name] = info
    return info


def list_peripheral_cells() -> List[str]:
    """Return list of available peripheral cell short names."""
    return sorted(_CELL_REGISTRY.keys())
