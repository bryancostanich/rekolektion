"""Manufacturing-grid snap utilities — PDK-configurable.

Every coord rekolektion emits (rect corner, sref origin, label origin,
poly point) must land on the PDK manufacturing grid. SKY130 = 5 nm.
Off-grid coords trigger foundry sign-off DRC failures and cause Magic to
emit phantom 14-nm-sliver poly.2 violations under rotated SRefs.

The single source of truth for grid pitch is `rekolektion.tech.grid_nm`;
this module's job is to apply that grid to ints (DBU) and tuples
(points, rects). All `place_*` helpers and the F# `to-gds` emitter call
through these primitives so callers don't have to.

Rounding rule: half-away-from-zero. snap(-v) == -snap(v) for every v.
This symmetry matters because rkt cells are routinely SRef'd at rot=180
or with reflect; if half-step rounding were biased (e.g. half-to-even),
a coord at +2.5 nm and its rotated twin at -2.5 nm would land at
different grid points and the cell's centre of symmetry would drift.

Usage:
    from rekolektion.layout import snap
    x  = snap.snap_dbu(173, pdk="sky130")        # → 175
    pt = snap.snap_point((173, -7000), pdk="sky130")   # → (175, -7000)
    bx = snap.snap_rect((173, -7000, 12444, -2917), pdk="sky130")

    # When the PDK is known via a Layer record:
    g  = snap.grid_for(rkt.named("sky130", "met1"))
    pt = snap.snap_point((173, -7000), grid=g)
"""
from __future__ import annotations

from rekolektion.io import rkt
from rekolektion.tech import grid_nm


def _resolve_grid(pdk: str | None, grid: int | None) -> int:
    if grid is not None:
        return grid
    if pdk is not None:
        return grid_nm(pdk)
    raise ValueError("snap helpers require pdk= or grid=")


def snap_dbu(v: int, *, pdk: str | None = None, grid: int | None = None) -> int:
    """Round v (DBU = nanometers when Units.dbu_nm=1) to the PDK grid.

    Half-away-from-zero: snap(-v) == -snap(v). See module docstring.
    """
    g = _resolve_grid(pdk, grid)
    if g <= 1:
        return int(v)
    half = g // 2
    if v >= 0:
        return ((v + half) // g) * g
    return -(((-v + half) // g) * g)


def snap_point(
    pt: tuple[int, int], *, pdk: str | None = None, grid: int | None = None
) -> tuple[int, int]:
    """Snap both axes of a 2-tuple."""
    g = _resolve_grid(pdk, grid)
    return (snap_dbu(pt[0], grid=g), snap_dbu(pt[1], grid=g))


def snap_rect(
    bbox: tuple[int, int, int, int],
    *,
    pdk: str | None = None,
    grid: int | None = None,
) -> tuple[int, int, int, int]:
    """Snap all four corners of an (x1, y1, x2, y2) bbox."""
    g = _resolve_grid(pdk, grid)
    x1, y1, x2, y2 = bbox
    return (
        snap_dbu(x1, grid=g),
        snap_dbu(y1, grid=g),
        snap_dbu(x2, grid=g),
        snap_dbu(y2, grid=g),
    )


def grid_for(layer: rkt.Layer) -> int:
    """Resolve the manufacturing grid from a Layer record.

    Named layers carry their PDK identity (`Layer(kind="named", pdk=...)`);
    unknown layers don't, so the caller must specify a grid explicitly.
    Raises ValueError otherwise — silent default is exactly how off-grid
    coords slipped in before.
    """
    if layer.kind == "named":
        return grid_nm(layer.pdk)
    raise ValueError(
        f"cannot resolve grid for {layer!r} (unknown layer carries no PDK); "
        "pass grid= explicitly"
    )
