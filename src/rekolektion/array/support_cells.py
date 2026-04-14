"""Support cell loader for SRAM array edge termination and well straps.

Loads pre-exported GDS files and LEF pin data for foundry support cells:
- Dummy bitcell (array border fill)
- Column end cells (top/bottom termination)
- Row end cells (left/right termination)
- Corner cells
- WL strap cells (VDD and GND well taps)

The GDS files in cells/ were exported from the foundry MAG files via Magic:
    magic -dnull -noconsole -rcfile $PDK_ROOT/sky130B/libs.tech/magic/sky130B.magicrc
    load <cell>.mag
    gds write <cell>.gds
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Tuple

import gdstk

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

_CELLS_DIR = Path(__file__).parent / "cells"

# Cell name mapping: short name -> (directory in foundry repo, full cell name)
_CELL_REGISTRY: Dict[str, str] = {
    "dummy":        "sky130_fd_bd_sram__openram_sp_cell_opt1_dummy",
    "colend":       "sky130_fd_bd_sram__sram_sp_colend",
    "colend_cent":  "sky130_fd_bd_sram__sram_sp_colend_cent",
    "rowend":       "sky130_fd_bd_sram__sram_sp_rowend",
    "rowenda":      "sky130_fd_bd_sram__sram_sp_rowenda",
    "corner":       "sky130_fd_bd_sram__sram_sp_corner",
    "wlstrap":      "sky130_fd_bd_sram__sram_sp_wlstrap",
    "wlstrap_p":    "sky130_fd_bd_sram__sram_sp_wlstrap_p",
}


# ---------------------------------------------------------------------------
# Minimal LEF parser (reuses approach from foundry_sp.py)
# ---------------------------------------------------------------------------

def _parse_lef_size(lef_path: Path) -> Tuple[float, float]:
    """Extract SIZE width BY height from a LEF file."""
    text = lef_path.read_text()
    m = re.search(r"SIZE\s+([\d.]+)\s+BY\s+([\d.]+)", text)
    if not m:
        raise ValueError(f"No SIZE found in {lef_path}")
    return float(m.group(1)), float(m.group(2))


# ---------------------------------------------------------------------------
# Support cell info
# ---------------------------------------------------------------------------

@dataclass
class SupportCellInfo:
    """Metadata for a support cell."""
    short_name: str
    cell_name: str
    width: float
    height: float
    gds_path: Path

    def get_cell(self) -> gdstk.Cell:
        """Load and return the gdstk.Cell from the GDS file."""
        lib = gdstk.read_gds(str(self.gds_path))
        for cell in lib.cells:
            if cell.name == self.cell_name:
                return cell
        return lib.cells[0]


# Cache loaded cells
_cache: Dict[str, SupportCellInfo] = {}


def get_support_cell(name: str) -> SupportCellInfo:
    """Get a support cell by short name.

    Valid names: dummy, colend, colend_cent, rowend, rowenda,
                 corner, wlstrap, wlstrap_p
    """
    if name in _cache:
        return _cache[name]

    if name not in _CELL_REGISTRY:
        raise KeyError(
            f"Unknown support cell '{name}'. "
            f"Valid names: {sorted(_CELL_REGISTRY.keys())}"
        )

    cell_name = _CELL_REGISTRY[name]
    gds_path = _CELLS_DIR / f"{cell_name}.gds"
    lef_path = _CELLS_DIR / f"{cell_name}.magic.lef"

    if not gds_path.exists():
        raise FileNotFoundError(
            f"Support cell GDS not found: {gds_path}. "
            "Run the Magic export step first."
        )

    width, height = _parse_lef_size(lef_path) if lef_path.exists() else (0.0, 0.0)

    info = SupportCellInfo(
        short_name=name,
        cell_name=cell_name,
        width=width,
        height=height,
        gds_path=gds_path,
    )
    _cache[name] = info
    return info


def list_support_cells() -> list[str]:
    """Return list of available support cell short names."""
    return sorted(_CELL_REGISTRY.keys())
