"""DRC-clean-by-construction routing primitives for the v2 SRAM generator."""
from __future__ import annotations

from typing import Tuple

import gdstk

from rekolektion.macro_v2.sky130_drc import (
    GDS_LAYER,
    layer_min_space,
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


# Via ladder steps: (lower_metal, via_layer, upper_metal, via_size_um,
#                    lower_enclosure_um, upper_enclosure_um)
_VIA_LADDER = [
    ("li1",  "mcon", "met1", 0.17, 0.0,   0.030),
    ("met1", "via",  "met2", 0.15, 0.055, 0.055),
    ("met2", "via2", "met3", 0.20, 0.040, 0.065),
    ("met3", "via3", "met4", 0.20, 0.060, 0.060),
    ("met4", "via4", "met5", 0.80, 0.210, 0.310),
]


_METAL_ORDER = ["li1", "met1", "met2", "met3", "met4", "met5"]


def draw_via_stack(
    cell: gdstk.Cell,
    *,
    from_layer: str,
    to_layer: str,
    position: Point,
) -> None:
    """Draw a centered via stack from `from_layer` up to `to_layer` at `position`.

    Emits, for each step up the ladder:
      - lower metal landing pad (square, sized by via + enclosure)
      - via cut on the appropriate via layer
      - upper metal landing pad
    Adjacent stack steps share landing pads (no duplicates emitted).
    """
    if from_layer not in _METAL_ORDER or to_layer not in _METAL_ORDER:
        raise ValueError(f"unknown layer; got from={from_layer} to={to_layer}")
    from_idx = _METAL_ORDER.index(from_layer)
    to_idx = _METAL_ORDER.index(to_layer)
    if to_idx <= from_idx:
        raise ValueError(
            f"draw_via_stack requires to_layer above from_layer; "
            f"got {from_layer} -> {to_layer}"
        )

    cx, cy = snap(position[0]), snap(position[1])

    landed_metals: set[str] = set()
    for m_lower, via_name, m_upper, via_size, enc_lower, enc_upper in _VIA_LADDER:
        lower_idx = _METAL_ORDER.index(m_lower)
        upper_idx = _METAL_ORDER.index(m_upper)
        if upper_idx <= from_idx or lower_idx >= to_idx:
            continue

        if m_lower not in landed_metals:
            lower_size = via_size + 2 * enc_lower
            _emit_square(cell, cx, cy, lower_size, m_lower)
            landed_metals.add(m_lower)

        _emit_square(cell, cx, cy, via_size, via_name)

        if m_upper not in landed_metals:
            upper_size = via_size + 2 * enc_upper
            _emit_square(cell, cx, cy, upper_size, m_upper)
            landed_metals.add(m_upper)


def _emit_square(cell: gdstk.Cell, cx: float, cy: float, size: float, layer: str) -> None:
    """Emit a centered square of `size` um on `layer` at (cx, cy)."""
    half = size / 2
    rect = gdstk.rectangle(
        (snap(cx - half), snap(cy - half)),
        (snap(cx + half), snap(cy + half)),
        layer=GDS_LAYER[layer][0],
        datatype=GDS_LAYER[layer][1],
    )
    cell.add(rect)


def draw_via_array(
    cell: gdstk.Cell,
    *,
    from_layer: str,
    to_layer: str,
    position: Point,
    rows: int,
    cols: int,
) -> None:
    """Draw an R×C array of via cuts between from_layer and to_layer.

    Cuts are centered on `position`. Cut-to-cut pitch is the greater of
    the via's square size and the lower/upper metal min space (conservative).
    Landing pads on each metal layer are sized to enclose the entire cut
    array plus per-metal enclosure on each side.

    Multi-layer stacks emit parallel via arrays at each ladder step
    (e.g. met1->met3 emits 4 via cuts + 4 via2 cuts for a 2x2).
    """
    if from_layer not in _METAL_ORDER or to_layer not in _METAL_ORDER:
        raise ValueError(
            f"unknown layer; got from={from_layer} to={to_layer}"
        )
    from_idx = _METAL_ORDER.index(from_layer)
    to_idx = _METAL_ORDER.index(to_layer)
    if to_idx <= from_idx:
        raise ValueError(
            f"requires to_layer above from_layer; got {from_layer} -> {to_layer}"
        )
    if rows < 1 or cols < 1:
        raise ValueError(
            f"rows and cols must be >=1; got rows={rows} cols={cols}"
        )

    cx, cy = snap(position[0]), snap(position[1])

    landed: set[str] = set()
    for m_lower, via_name, m_upper, via_size, enc_lower, enc_upper in _VIA_LADDER:
        lower_idx = _METAL_ORDER.index(m_lower)
        upper_idx = _METAL_ORDER.index(m_upper)
        if upper_idx <= from_idx or lower_idx >= to_idx:
            continue

        cut_spacing = max(
            via_size,
            layer_min_space(m_lower),
            layer_min_space(m_upper),
        )
        array_w = cols * via_size + (cols - 1) * cut_spacing
        array_h = rows * via_size + (rows - 1) * cut_spacing

        # Emit cuts — origin is lower-left corner of the array's bounding box
        x0 = cx - array_w / 2
        y0 = cy - array_h / 2
        for r in range(rows):
            for c in range(cols):
                cx_cut = x0 + c * (via_size + cut_spacing) + via_size / 2
                cy_cut = y0 + r * (via_size + cut_spacing) + via_size / 2
                _emit_square(cell, cx_cut, cy_cut, via_size, via_name)

        # Emit lower landing pad
        if m_lower not in landed:
            lower_w = array_w + 2 * enc_lower
            lower_h = array_h + 2 * enc_lower
            _emit_rect(cell, cx, cy, lower_w, lower_h, m_lower)
            landed.add(m_lower)

        # Emit upper landing pad
        if m_upper not in landed:
            upper_w = array_w + 2 * enc_upper
            upper_h = array_h + 2 * enc_upper
            _emit_rect(cell, cx, cy, upper_w, upper_h, m_upper)
            landed.add(m_upper)


def _emit_rect(
    cell: gdstk.Cell, cx: float, cy: float, w: float, h: float, layer: str
) -> None:
    """Emit a centered W×H rectangle on `layer` at (cx, cy)."""
    rect = gdstk.rectangle(
        (snap(cx - w / 2), snap(cy - h / 2)),
        (snap(cx + w / 2), snap(cy + h / 2)),
        layer=GDS_LAYER[layer][0],
        datatype=GDS_LAYER[layer][1],
    )
    cell.add(rect)


def draw_pdn_strap(
    cell: gdstk.Cell,
    *,
    orientation: str,
    center_coord: float,
    span_start: float,
    span_end: float,
    layer: str,
    width: float,
) -> gdstk.Polygon:
    """Draw a power-distribution strap on `layer`.

    Parameters
    ----------
    orientation : {"horizontal", "vertical"}
        Direction the strap runs.
    center_coord : float
        Perpendicular position of the strap centreline (y for horizontal,
        x for vertical).
    span_start, span_end : float
        Along-axis extent of the strap.
    layer : str
        Metal layer name (typically "met3" or "met4").
    width : float
        Strap width in um. Must be >= layer minimum width.
    """
    if orientation not in ("horizontal", "vertical"):
        raise ValueError(
            f"orientation must be horizontal or vertical, got {orientation}"
        )
    min_w = layer_min_width(layer)
    if width < min_w:
        raise ValueError(
            f"width {width} for {layer} below min width {min_w}"
        )

    half = width / 2
    if orientation == "horizontal":
        rect = gdstk.rectangle(
            (snap(span_start), snap(center_coord - half)),
            (snap(span_end), snap(center_coord + half)),
            layer=GDS_LAYER[layer][0],
            datatype=GDS_LAYER[layer][1],
        )
    else:
        rect = gdstk.rectangle(
            (snap(center_coord - half), snap(span_start)),
            (snap(center_coord + half), snap(span_end)),
            layer=GDS_LAYER[layer][0],
            datatype=GDS_LAYER[layer][1],
        )
    cell.add(rect)
    return rect
