"""CIM bitcell array builder for v2-style macro assembly.

Wraps the existing `array.tiler.tile_array` call with a builder-class
interface that mirrors the v2 SRAM block builders (BitcellArray,
PrechargeRow, etc.).

Each variant uses the corresponding CIM bitcell (sky130_6t_lr_cim_<v>),
generated on-demand if its GDS doesn't exist.
"""
from __future__ import annotations

from pathlib import Path
from typing import Optional

import gdstk

from rekolektion.bitcell.sky130_6t_lr_cim import (
    CIM_VARIANTS,
    load_cim_bitcell,
    generate_cim_bitcell,
)
from rekolektion.array.tiler import tile_array


class CIMBitcellArray:
    """Tiled CIM bitcell array, parameterised by variant + dimensions."""

    def __init__(
        self,
        variant: str,
        rows: int,
        cols: int,
        name: Optional[str] = None,
        *,
        gds_dir: Optional[Path] = None,
    ):
        if variant not in CIM_VARIANTS:
            raise ValueError(
                f"Unknown CIM variant {variant!r}. "
                f"Valid: {sorted(CIM_VARIANTS)}"
            )
        self.variant = variant
        self.rows = rows
        self.cols = cols
        self.top_cell_name = (
            name or f"cim_array_{variant.lower().replace('-', '_')}_{rows}x{cols}"
        )
        self._gds_dir = gds_dir or Path("output/cim_variants")

    def _ensure_bitcell_gds(self):
        v = CIM_VARIANTS[self.variant]
        slug = self.variant.lower().replace("-", "_")
        self._gds_dir.mkdir(parents=True, exist_ok=True)
        cell_gds = self._gds_dir / f"sky130_6t_cim_lr_{slug}.gds"
        if not cell_gds.exists():
            generate_cim_bitcell(
                str(cell_gds), mim_w=v["mim_w"], mim_l=v["mim_l"],
            )
        return cell_gds

    @property
    def width(self) -> float:
        bc = load_cim_bitcell(str(self._ensure_bitcell_gds()), variant=self.variant)
        return self.cols * bc.cell_width

    @property
    def height(self) -> float:
        bc = load_cim_bitcell(str(self._ensure_bitcell_gds()), variant=self.variant)
        return self.rows * bc.cell_height

    @property
    def cell_pitch_x(self) -> float:
        bc = load_cim_bitcell(str(self._ensure_bitcell_gds()), variant=self.variant)
        return bc.cell_width

    @property
    def cell_pitch_y(self) -> float:
        bc = load_cim_bitcell(str(self._ensure_bitcell_gds()), variant=self.variant)
        return bc.cell_height

    def build(self) -> gdstk.Library:
        """Build the array library.  Returns a `gdstk.Library` whose
        first cell containing 'array' (or the only cell) is the array
        top.  Caller embeds it via `gdstk.Reference(array_cell, ...)`.
        """
        cell_gds = self._ensure_bitcell_gds()
        bitcell = load_cim_bitcell(str(cell_gds), variant=self.variant)
        return tile_array(
            bitcell,
            num_rows=self.rows,
            num_cols=self.cols,
            with_routing=False,  # CIM routing is handled by the assembler
        )

    def array_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        """Return the top array cell from the library produced by build()."""
        for c in lib.cells:
            if "array" in c.name:
                return c
        return lib.cells[0]
