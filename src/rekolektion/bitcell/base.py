"""Base dataclass for bitcell abstraction.

Both the foundry cell and custom cell implementations provide a BitcellInfo
so that downstream generators (array tiler, peripheral placement, etc.)
can work with either cell interchangeably.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Tuple

import gdstk


@dataclass
class PinInfo:
    """A single pin with one or more port rectangles."""

    name: str
    # List of (x_center, y_center, layer_name) for each port rectangle.
    # The first entry is the "primary" location used for routing.
    ports: list[Tuple[float, float, str]] = field(default_factory=list)

    @property
    def x(self) -> float:
        return self.ports[0][0]

    @property
    def y(self) -> float:
        return self.ports[0][1]

    @property
    def layer(self) -> str:
        return self.ports[0][2]

    @property
    def position(self) -> Tuple[float, float, str]:
        """Primary (x, y, layer) for this pin."""
        return self.ports[0]


@dataclass
class BitcellInfo:
    """Technology-independent description of a bitcell."""

    cell_name: str
    cell_width: float   # microns
    cell_height: float  # microns
    pins: Dict[str, PinInfo] = field(default_factory=dict)
    gds_path: Path = field(default_factory=lambda: Path())

    # --- convenience -------------------------------------------------------

    def pin_position(self, name: str) -> Tuple[float, float, str]:
        """Return (x, y, layer) of the named pin's primary port."""
        return self.pins[name].position

    def get_cell(self) -> gdstk.Cell:
        """Load and return the gdstk.Cell from the GDS file."""
        lib = gdstk.read_gds(str(self.gds_path))
        # Return the cell whose name matches cell_name, or the top cell.
        for cell in lib.cells:
            if cell.name == self.cell_name:
                return cell
        # Fallback: return first cell (shouldn't happen with well-formed GDS).
        return lib.cells[0]
