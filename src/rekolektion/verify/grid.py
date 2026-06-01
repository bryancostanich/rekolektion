"""Grid-snap validator.

Walks every coordinate-bearing element in a `.rkt` and reports any
that don't land on the PDK's manufacturing grid.  This is a
first-class verification gate alongside `verify_drc` and
`verify_lvs`: off-grid coords are foundry-rule violations in their
own right, and on SKY130 they cause Magic to emit phantom
14-nm-sliver `poly.2` errors under rotated SRefs.

Single-file usage:

    from rekolektion.verify import verify_grid

    r = verify_grid("cell_designs/bias_gen/bias_gen.rkt")
    if not r.clean:
        for v in r.off_grid:
            print(f"  {v.cell} {v.element_kind} {v.coord}")

The walker honours every coord-carrying element kind the reader
emits: `Rect` (4 corners), `Poly` / `Path` (point list), `SRef`
origin, `ARef` origin + col_pitch + row_pitch, `Label` origin.
`Port.shape` is not walked today (no `.rkt` in this repo carries
one — extend the walker when that changes).

The grid resolves from the `.rkt`'s `(pdk …)` line via
`rekolektion.tech.grid_nm`, which raises on unknown PDK so silent
fall-back to a default grid can't mask drift.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

from rekolektion.io import rkt
from rekolektion.tech import grid_nm


@dataclass(frozen=True)
class OffGridViolation:
    """One coordinate that doesn't land on the grid.

    `coord` is a 2-tuple even for `rect` violations: the helper
    walks all four corners independently and emits one violation
    per off-grid corner, naming each with the (x, y) of that
    corner.  `element_kind` is the lower-case class name (`rect`,
    `poly`, `path`, `sref`, `aref`, `label`).
    """

    cell: str
    element_kind: str
    coord: tuple[int, int]
    grid: int


@dataclass
class GridVerifyResult:
    rkt_path: str
    pdk: str
    grid: int
    total_coords: int
    off_grid: list[OffGridViolation] = field(default_factory=list)

    @property
    def clean(self) -> bool:
        return not self.off_grid

    def summary(self) -> str:
        if self.clean:
            return (
                f"grid CLEAN: {self.total_coords} coords on the "
                f"{self.grid}-nm grid ({self.pdk})"
            )
        return (
            f"grid FAIL: {len(self.off_grid)} off-grid coords "
            f"({self.total_coords} total checked) on the "
            f"{self.grid}-nm grid ({self.pdk}) — {self.rkt_path}"
        )


def _on_grid(v: int, grid: int) -> bool:
    return v % grid == 0


def _walk_cell(
    cell: rkt.Cell, grid: int, total: list[int]
) -> list[OffGridViolation]:
    violations: list[OffGridViolation] = []
    name = cell.name

    def check_point(kind: str, x: int, y: int) -> None:
        total[0] += 2
        if not _on_grid(x, grid) or not _on_grid(y, grid):
            violations.append(
                OffGridViolation(
                    cell=name, element_kind=kind, coord=(x, y), grid=grid
                )
            )

    for el in cell.elements:
        if isinstance(el, rkt.Rect):
            # Per-axis: each of (x1, x2, y1, y2) gets one violation
            # if off-grid. The coord packs the offending axis value
            # with the partner axis from the same corner, so the
            # report points at the actual rect edge to fix. A rect
            # with both x1 and y1 off emits two violations at the
            # lower-left corner — both axes need adjustment.
            total[0] += 4
            if not _on_grid(el.x1, grid):
                violations.append(OffGridViolation(
                    cell=name, element_kind="rect",
                    coord=(el.x1, el.y1), grid=grid,
                ))
            if not _on_grid(el.x2, grid):
                violations.append(OffGridViolation(
                    cell=name, element_kind="rect",
                    coord=(el.x2, el.y2), grid=grid,
                ))
            if not _on_grid(el.y1, grid):
                violations.append(OffGridViolation(
                    cell=name, element_kind="rect",
                    coord=(el.x1, el.y1), grid=grid,
                ))
            if not _on_grid(el.y2, grid):
                violations.append(OffGridViolation(
                    cell=name, element_kind="rect",
                    coord=(el.x2, el.y2), grid=grid,
                ))
        elif isinstance(el, rkt.Poly):
            for x, y in el.points:
                check_point("poly", x, y)
        elif isinstance(el, rkt.Path):
            for x, y in el.points:
                check_point("path", x, y)
        elif isinstance(el, rkt.SRef):
            check_point("sref", el.origin[0], el.origin[1])
        elif isinstance(el, rkt.ARef):
            check_point("aref", el.origin[0], el.origin[1])
            check_point("aref", el.col_pitch[0], el.col_pitch[1])
            check_point("aref", el.row_pitch[0], el.row_pitch[1])
        elif isinstance(el, rkt.Label):
            check_point("label", el.origin[0], el.origin[1])
        # Port / Props carry no coords (Port.shape walking is a future
        # extension when a .rkt in this repo uses it).

    return violations


def verify_grid(
    rkt_path: str | Path,
    *,
    grid: int | None = None,
) -> GridVerifyResult:
    """Walk every coord in `rkt_path` and return any off-grid violations.

    `grid` defaults to the manufacturing grid for the document's PDK
    (`Document.pdk` → `tech.grid_nm`).  Pass `grid=` explicitly to
    override (e.g., for ad-hoc 1-nm checks).

    The walker only inspects coords in the file at `rkt_path` itself.
    For composed cells with `(import …)`-referenced primitives,
    run `verify_grid` on each primitive separately; primitive `.rkt`s
    are PDK-generated and should never report violations, so any
    drift in them indicates a generator bug, not a build-script bug.
    """
    path = Path(rkt_path).resolve()
    doc = rkt.read_file(path)
    g = grid_nm(doc.pdk) if grid is None else grid

    total = [0]
    violations: list[OffGridViolation] = []
    for cell in doc.cells:
        violations.extend(_walk_cell(cell, g, total))

    return GridVerifyResult(
        rkt_path=str(path),
        pdk=doc.pdk,
        grid=g,
        total_coords=total[0],
        off_grid=violations,
    )
