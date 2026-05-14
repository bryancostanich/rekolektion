"""GDS → `rkt.Document` translation for primitive output.

Magic writes the primitive (and any subcells it chose to keep
hierarchical, e.g. contact arrays) to GDS. We read that with gdstk
and assemble an `rkt.Document` that contains every structure as
a `rkt.Cell`. Layer references resolve to `Named("sky130", ...)`
via the layer map; unknowns pass through as `Unknown(n, d)`.

Magic's GDS uses a 1 nm DBU by default (matches our `(units (dbu_nm
1))`). If a future generator changes that, the unit is read from
the GDS library header and propagated.
"""

from __future__ import annotations

from pathlib import Path

import gdstk

from rekolektion.io import rkt
from rekolektion.primitives.sky130._layer_map import layer_for_pair


def _polygon_to_element(p: gdstk.Polygon, dbu_per_uu: float) -> rkt.Element:
    """Convert a gdstk Polygon to a `rkt.Poly` or `rkt.Rect`.

    gdstk reports coordinates in GDS *user units* (typically µm).
    `.rkt` stores integer DBUs. `dbu_per_uu` = lib.unit / lib.precision
    is the multiplier — for the standard 1 µm user-unit / 1 nm
    precision pairing, it's 1000.

    Axis-aligned 4-point polygons collapse to `Rect` for cleaner
    `.rkt` output and easier downstream edits. Everything else stays
    as `Poly`.
    """

    layer = layer_for_pair(p.layer, p.datatype)
    pts = [
        (int(round(x * dbu_per_uu)), int(round(y * dbu_per_uu)))
        for (x, y) in p.points
    ]
    if len(pts) == 4:
        xs = {x for x, _ in pts}
        ys = {y for _, y in pts}
        if len(xs) == 2 and len(ys) == 2:
            x1, x2 = sorted(xs)
            y1, y2 = sorted(ys)
            return rkt.Rect(layer=layer, x1=x1, y1=y1, x2=x2, y2=y2)
    return rkt.Poly(layer=layer, points=pts)


def _label_to_element(lbl: gdstk.Label, dbu_per_uu: float) -> rkt.Label:
    return rkt.Label(
        layer=layer_for_pair(lbl.layer, lbl.texttype),
        text=lbl.text,
        origin=(
            int(round(lbl.origin[0] * dbu_per_uu)),
            int(round(lbl.origin[1] * dbu_per_uu)),
        ),
    )


def _reference_to_element(
    ref: gdstk.Reference, dbu_per_uu: float
) -> rkt.Element:
    """gdstk Reference → SRef (single) or ARef (repeated). Coordinate
    conversion matches `_polygon_to_element`."""

    origin = (
        int(round(ref.origin[0] * dbu_per_uu)),
        int(round(ref.origin[1] * dbu_per_uu)),
    )
    rot = float(ref.rotation) if ref.rotation else 0.0
    mag = float(ref.magnification) if ref.magnification else 1.0
    reflect = bool(ref.x_reflection)
    cols, rows = (
        (ref.repetition.columns, ref.repetition.rows)
        if ref.repetition
        else (1, 1)
    )
    if cols > 1 or rows > 1:
        rep = ref.repetition
        v1 = rep.v1 if rep else (0, 0)
        v2 = rep.v2 if rep else (0, 0)
        return rkt.ARef(
            cell=ref.cell.name,
            origin=origin,
            cols=cols,
            rows=rows,
            col_pitch=(
                int(round(v1[0] * dbu_per_uu)),
                int(round(v1[1] * dbu_per_uu)),
            ),
            row_pitch=(
                int(round(v2[0] * dbu_per_uu)),
                int(round(v2[1] * dbu_per_uu)),
            ),
            rot=rot,
            mag=mag,
            reflect=reflect,
        )
    return rkt.SRef(
        cell=ref.cell.name,
        origin=origin,
        rot=rot,
        mag=mag,
        reflect=reflect,
    )


def read_gds(path: Path) -> rkt.Document:
    """Read a GDS file (produced by Magic) into an `rkt.Document`.

    Every GDS structure becomes an `rkt.Cell`; the document's top is
    set from gdstk's automatic top-cell detection. Units come from
    the GDS header (`unit` in meters and `precision` in meters);
    we encode them as `dbu_nm = precision * 1e9`.
    """

    lib = gdstk.read_gds(str(path))
    # `precision` is the database unit in meters; 1e-9 = 1 nm DBU.
    # `unit` is the user unit in meters; 1e-6 = 1 µm (the gdstk-reported
    # coordinate). dbu_per_uu converts gdstk's µm coords back to integer
    # DBU. For sky130's standard 1 µm / 1 nm pairing it's 1000.
    dbu_nm = max(1, int(round(lib.precision * 1e9)))
    dbu_per_uu = lib.unit / lib.precision

    cells: list[rkt.Cell] = []
    for cell in lib.cells:
        elements: list[rkt.Element] = []
        for poly in cell.polygons:
            elements.append(_polygon_to_element(poly, dbu_per_uu))
        for lbl in cell.labels:
            elements.append(_label_to_element(lbl, dbu_per_uu))
        for ref in cell.references:
            elements.append(_reference_to_element(ref, dbu_per_uu))
        cells.append(rkt.Cell(name=cell.name, elements=elements))

    top_cell = None
    tops = lib.top_level()
    if tops:
        top_cell = tops[0].name

    return rkt.Document(
        cells=cells,
        pdk="sky130",
        units=rkt.Units(dbu_nm=dbu_nm, uu_um=1),
        top_cell=top_cell,
    )
