"""DRC-clean-by-construction routing primitives for the v2 SRAM generator."""
from __future__ import annotations

from typing import TYPE_CHECKING, Tuple

import gdstk

from rekolektion.macro_v2.sky130_drc import (
    GDS_LAYER,
    layer_min_space,
    layer_min_width,
    snap,
)

if TYPE_CHECKING:
    from rekolektion.macro_v2.nets_tracker import NetClass, NetsTracker


Point = Tuple[float, float]


def draw_wire(
    cell: gdstk.Cell,
    *,
    start: Point,
    end: Point,
    layer: str,
    width: float | None = None,
    tracker: "NetsTracker | None" = None,
    net: str | None = None,
    cls: "NetClass" = "signal",
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
    if tracker is not None and net is not None:
        tracker.record(
            cell=cell,
            layer=GDS_LAYER[layer][0],
            datatype=GDS_LAYER[layer][1],
            net=net,
            cls=cls,
        )
    return rect


from rekolektion.macro_v2.sky130_drc import (
    MET1_ENCLOSURE_MCON,
    MET1_ENCLOSURE_VIA,
    MET2_ENCLOSURE_VIA,
    MET2_ENCLOSURE_VIA2,
    MET3_ENCLOSURE_VIA2,
    MET3_ENCLOSURE_VIA3,
    MET4_ENCLOSURE_VIA3,
    MET4_ENCLOSURE_VIA4,
    MET5_ENCLOSURE_VIA4,
    POLY_ENCLOSURE_PC,
    LI1_ENCLOSURE_PC,
    MCON_SIZE,
    PC_SIZE,
    VIA_SIZE,
    VIA2_SIZE,
    VIA3_SIZE,
    VIA4_SIZE,
    MCON_MIN_SPACE,
    PC_MIN_SPACE,
    VIA_MIN_SPACE,
    VIA2_MIN_SPACE,
    VIA3_MIN_SPACE,
    VIA4_MIN_SPACE,
)

# Via ladder steps: (lower_metal, via_layer, upper_metal, via_size_um,
#                    lower_enclosure_um, upper_enclosure_um, via_min_space_um)
# Enclosures are SYMMETRIC (all-around) and chosen to satisfy both the
# SKY130 base-enclosure (width X/M) rule AND the directional "surround
# ... directional" rule, which requires extra overlap in at least one
# direction. See src/rekolektion/macro_v2/sky130_drc.py for derivations.
#
# pc (poly contact) connects poly to li1. mcon connects li1 to met1.
# Stacking poly → li1 (via pc) → met1 (via mcon) gives a full met1→poly
# via stack for row-decoder WL drive and similar.
_VIA_LADDER = [
    ("poly", "pc",   "li1",  PC_SIZE,   POLY_ENCLOSURE_PC,   LI1_ENCLOSURE_PC,    PC_MIN_SPACE),
    ("li1",  "mcon", "met1", MCON_SIZE, 0.0,                 MET1_ENCLOSURE_MCON, MCON_MIN_SPACE),
    ("met1", "via",  "met2", VIA_SIZE,  MET1_ENCLOSURE_VIA,  MET2_ENCLOSURE_VIA,  VIA_MIN_SPACE),
    ("met2", "via2", "met3", VIA2_SIZE, MET2_ENCLOSURE_VIA2, MET3_ENCLOSURE_VIA2, VIA2_MIN_SPACE),
    ("met3", "via3", "met4", VIA3_SIZE, MET3_ENCLOSURE_VIA3, MET4_ENCLOSURE_VIA3, VIA3_MIN_SPACE),
    ("met4", "via4", "met5", VIA4_SIZE, MET4_ENCLOSURE_VIA4, MET5_ENCLOSURE_VIA4, VIA4_MIN_SPACE),
]


_METAL_ORDER = ["poly", "li1", "met1", "met2", "met3", "met4", "met5"]


def draw_via_stack(
    cell: gdstk.Cell,
    *,
    from_layer: str,
    to_layer: str,
    position: Point,
    tracker: "NetsTracker | None" = None,
    net: str | None = None,
    cls: "NetClass" = "signal",
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

    # First pass: compute the landing-pad size needed on each metal layer
    # (max across all via steps that touch it). A metal between two via
    # steps (e.g. met2 between via1 and via2) must enclose BOTH vias.
    pad_size: dict[str, float] = {}
    for m_lower, via_name, m_upper, via_size, enc_lower, enc_upper, _ in _VIA_LADDER:
        lower_idx = _METAL_ORDER.index(m_lower)
        upper_idx = _METAL_ORDER.index(m_upper)
        if upper_idx <= from_idx or lower_idx >= to_idx:
            continue
        pad_size[m_lower] = max(pad_size.get(m_lower, 0.0), via_size + 2 * enc_lower)
        pad_size[m_upper] = max(pad_size.get(m_upper, 0.0), via_size + 2 * enc_upper)

    # Second pass: emit cuts. Landing pads are emitted ONCE per metal
    # using the max size computed above.
    for metal, size in pad_size.items():
        _emit_square(cell, cx, cy, size, metal,
                     tracker=tracker, net=net, cls=cls)
    for m_lower, via_name, m_upper, via_size, _, _, _ in _VIA_LADDER:
        lower_idx = _METAL_ORDER.index(m_lower)
        upper_idx = _METAL_ORDER.index(m_upper)
        if upper_idx <= from_idx or lower_idx >= to_idx:
            continue
        _emit_square(cell, cx, cy, via_size, via_name,
                     tracker=tracker, net=net, cls=cls)


def _emit_square(
    cell: gdstk.Cell, cx: float, cy: float, size: float, layer: str,
    *,
    tracker: "NetsTracker | None" = None,
    net: str | None = None,
    cls: "NetClass" = "signal",
) -> None:
    """Emit a centered square of `size` um on `layer` at (cx, cy)."""
    half = size / 2
    rect = gdstk.rectangle(
        (snap(cx - half), snap(cy - half)),
        (snap(cx + half), snap(cy + half)),
        layer=GDS_LAYER[layer][0],
        datatype=GDS_LAYER[layer][1],
    )
    cell.add(rect)
    if tracker is not None and net is not None:
        tracker.record(
            cell=cell,
            layer=GDS_LAYER[layer][0],
            datatype=GDS_LAYER[layer][1],
            net=net,
            cls=cls,
        )


def draw_via_array(
    cell: gdstk.Cell,
    *,
    from_layer: str,
    to_layer: str,
    position: Point,
    rows: int,
    cols: int,
    tracker: "NetsTracker | None" = None,
    net: str | None = None,
    cls: "NetClass" = "signal",
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

    # First pass: per ladder step, compute array extent AND the landing-pad
    # extent needed on each adjacent metal. Different vias produce different
    # array sizes (via1 size 0.15 packs tighter than via2 size 0.20), so the
    # metal between two steps must enclose the LARGER of the two requirements.
    pad_w: dict[str, float] = {}
    pad_h: dict[str, float] = {}
    steps: list[tuple[str, str, str, float, float, float, float, float, float]] = []
    for m_lower, via_name, m_upper, via_size, enc_lower, enc_upper, via_space in _VIA_LADDER:
        lower_idx = _METAL_ORDER.index(m_lower)
        upper_idx = _METAL_ORDER.index(m_upper)
        if upper_idx <= from_idx or lower_idx >= to_idx:
            continue
        # Cut pitch: max(via_size + via_min_space, metal_min_pitch_at_via).
        # The metal min-pitch constraint applies to the landing pad strip
        # BETWEEN cuts (there's no metal there, so it's really about the
        # cuts themselves). Use just via_min_space.
        cut_gap = via_space
        array_w = cols * via_size + (cols - 1) * cut_gap
        array_h = rows * via_size + (rows - 1) * cut_gap
        steps.append((
            m_lower, via_name, m_upper, via_size, cut_gap, array_w, array_h,
            enc_lower, enc_upper,
        ))
        pad_w[m_lower] = max(pad_w.get(m_lower, 0.0), array_w + 2 * enc_lower)
        pad_h[m_lower] = max(pad_h.get(m_lower, 0.0), array_h + 2 * enc_lower)
        pad_w[m_upper] = max(pad_w.get(m_upper, 0.0), array_w + 2 * enc_upper)
        pad_h[m_upper] = max(pad_h.get(m_upper, 0.0), array_h + 2 * enc_upper)

    # Second pass: emit one landing pad per metal (sized to the max).
    for metal in pad_w:
        _emit_rect(cell, cx, cy, pad_w[metal], pad_h[metal], metal,
                   tracker=tracker, net=net, cls=cls)

    # Third pass: emit cuts at each step's own pitch.
    for m_lower, via_name, m_upper, via_size, cut_gap, array_w, array_h, _, _ in steps:
        x0 = cx - array_w / 2
        y0 = cy - array_h / 2
        for r in range(rows):
            for c in range(cols):
                cx_cut = x0 + c * (via_size + cut_gap) + via_size / 2
                cy_cut = y0 + r * (via_size + cut_gap) + via_size / 2
                _emit_square(cell, cx_cut, cy_cut, via_size, via_name,
                             tracker=tracker, net=net, cls=cls)


def _emit_rect(
    cell: gdstk.Cell, cx: float, cy: float, w: float, h: float, layer: str,
    *,
    tracker: "NetsTracker | None" = None,
    net: str | None = None,
    cls: "NetClass" = "signal",
) -> None:
    """Emit a centered W×H rectangle on `layer` at (cx, cy)."""
    rect = gdstk.rectangle(
        (snap(cx - w / 2), snap(cy - h / 2)),
        (snap(cx + w / 2), snap(cy + h / 2)),
        layer=GDS_LAYER[layer][0],
        datatype=GDS_LAYER[layer][1],
    )
    cell.add(rect)
    if tracker is not None and net is not None:
        tracker.record(
            cell=cell,
            layer=GDS_LAYER[layer][0],
            datatype=GDS_LAYER[layer][1],
            net=net,
            cls=cls,
        )


def draw_pin(
    cell: gdstk.Cell,
    *,
    layer: str,
    rect: Tuple[float, float, float, float],
    tracker: "NetsTracker | None" = None,
    net: str | None = None,
    cls: "NetClass" = "signal",
) -> gdstk.Polygon:
    """Emit a metal rectangle on the layer's .pin purpose (dtype 16).

    Used to declare a LEF pin at the top level. The pin rect must overlap
    drawn metal on the same layer for Magic to tie it to an electrical net.

    Parameters
    ----------
    rect : (x1, y1, x2, y2) in um — the pin extent.
    """
    pin_layer_key = f"{layer}.pin"
    if pin_layer_key not in GDS_LAYER:
        raise ValueError(f"no .pin purpose defined for layer {layer}")
    x1, y1, x2, y2 = rect
    r = gdstk.rectangle(
        (snap(x1), snap(y1)),
        (snap(x2), snap(y2)),
        layer=GDS_LAYER[pin_layer_key][0],
        datatype=GDS_LAYER[pin_layer_key][1],
    )
    cell.add(r)
    if tracker is not None and net is not None:
        tracker.record(
            cell=cell,
            layer=GDS_LAYER[pin_layer_key][0],
            datatype=GDS_LAYER[pin_layer_key][1],
            net=net,
            cls=cls,
        )
    return r


def draw_label(
    cell: gdstk.Cell,
    *,
    text: str,
    layer: str,
    position: Point,
) -> gdstk.Label:
    """Emit a text label on the layer's .label purpose (dtype 5).

    Magic's extractor ties the label to whatever drawn net overlaps at
    this coordinate, naming the net after `text`. Used for per-row/col
    WL/BL naming (decision 7) and top-level port identification.
    """
    label_layer_key = f"{layer}.label"
    if label_layer_key not in GDS_LAYER:
        raise ValueError(f"no .label purpose defined for layer {layer}")
    lbl = gdstk.Label(
        text,
        (snap(position[0]), snap(position[1])),
        layer=GDS_LAYER[label_layer_key][0],
        texttype=GDS_LAYER[label_layer_key][1],
    )
    cell.add(lbl)
    return lbl


def draw_pin_with_label(
    cell: gdstk.Cell,
    *,
    text: str,
    layer: str,
    rect: Tuple[float, float, float, float],
    tracker: "NetsTracker | None" = None,
    net: str | None = None,
    cls: "NetClass" = "signal",
) -> None:
    """Convenience: emit a pin rect and a label at the rect's center.

    If ``tracker`` is provided but ``net`` is not, the pin's ``text`` is
    used as the net name (the common case for top-level pin labels).
    """
    effective_net = net if net is not None else (text if tracker is not None else None)
    draw_pin(cell, layer=layer, rect=rect,
             tracker=tracker, net=effective_net, cls=cls)
    x1, y1, x2, y2 = rect
    cx = (x1 + x2) / 2
    cy = (y1 + y2) / 2
    draw_label(cell, text=text, layer=layer, position=(cx, cy))


def draw_pdn_strap(
    cell: gdstk.Cell,
    *,
    orientation: str,
    center_coord: float,
    span_start: float,
    span_end: float,
    layer: str,
    width: float,
    tracker: "NetsTracker | None" = None,
    net: str | None = None,
    cls: "NetClass" = "signal",
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
    if tracker is not None and net is not None:
        tracker.record(
            cell=cell,
            layer=GDS_LAYER[layer][0],
            datatype=GDS_LAYER[layer][1],
            net=net,
            cls=cls,
        )
    return rect
