"""DRC-clean-by-construction routing primitives for the v2 SRAM generator."""
from __future__ import annotations

from typing import Tuple

import gdstk

from rekolektion.macro_v2.sky130_drc import (
    GDS_LAYER,
    layer_min_width,
    snap,
)


Point = Tuple[float, float]


def draw_wire(
    cell: gdstk.Cell,
    *,
    start: Point,
    end: Point,
    layer: str,
    width: float | None = None,
) -> gdstk.Polygon:
    """Draw a horizontal or vertical wire from `start` to `end` on `layer`.

    Parameters
    ----------
    cell : gdstk.Cell
        Target cell the wire is added to.
    start, end : (x, y) points in um
        Wire endpoints. Must share either x-coordinate (vertical wire) or
        y-coordinate (horizontal wire). Diagonal wires raise ValueError.
    layer : str
        Drawing layer name ("met1", "met2", etc.); looked up in GDS_LAYER.
    width : float, optional
        Wire width in um. Defaults to the layer's minimum width. Raising
        ValueError below that minimum keeps DRC clean by construction.

    Returns
    -------
    gdstk.Polygon
        The rectangle added to the cell.
    """
    if width is None:
        width = layer_min_width(layer)
    min_w = layer_min_width(layer)
    if width < min_w:
        raise ValueError(
            f"width {width} for {layer} is below min width {min_w}"
        )

    x1, y1 = snap(start[0]), snap(start[1])
    x2, y2 = snap(end[0]), snap(end[1])

    if x1 == x2 and y1 != y2:
        half = width / 2
        lo, hi = (y1, y2) if y1 < y2 else (y2, y1)
        rect = gdstk.rectangle(
            (snap(x1 - half), lo),
            (snap(x1 + half), hi),
            layer=GDS_LAYER[layer][0],
            datatype=GDS_LAYER[layer][1],
        )
    elif y1 == y2 and x1 != x2:
        half = width / 2
        lo, hi = (x1, x2) if x1 < x2 else (x2, x1)
        rect = gdstk.rectangle(
            (lo, snap(y1 - half)),
            (hi, snap(y1 + half)),
            layer=GDS_LAYER[layer][0],
            datatype=GDS_LAYER[layer][1],
        )
    else:
        raise ValueError(
            f"draw_wire requires axis-aligned path; got start={start} end={end}"
        )

    cell.add(rect)
    return rect
