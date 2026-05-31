"""Regression test for the box-grow fix in `rekolektion.verify.drc.run_drc`.

Background
----------
Magic's `drc listall why` returns DRC error tiles FILTERED by the
current selection box. The previous Tcl script used `select top cell`,
which sets the box flush with the cell's geometry. Edge-effect
violations — `met1.1` (min width on a thin perimeter wire),
`nwell.2a` (well spacing across an inter-cell gap), etc. — land
OUTSIDE the tight box and were silently dropped from the tile count.

This caused `run_drc` to under-report DRC errors at cell boundaries,
which:
  * masked real bugs in rekolektion-generated layouts (e.g. the
    `pre_row_*` and `pc_*` precharge tests each hid 10
    `diff/tap.8` tiles at the row boundary), and
  * matched the CLAUDE.md hand-recipe (which had the same bug),
    making the issue invisible to anyone running parity by eye.

The fix adds `box grow {n,s,e,w} 2000` (≈1 µm per side at the
sky130B scale factor) after `select top cell` so the listall window
covers a margin past the cell, capturing edge-effect tiles.

The 2000 internal-unit margin is comfortably larger than the widest
single-rule spacing in sky130 (`nwell.2a` = 1.27 µm), so any tile
Magic computes within rule-reachable distance of the cell boundary
lands inside the listall window.

These tests minimise GDS to the bug shape — a single sub-min-width
met1 rect — and assert the post-fix `run_drc` flags it. The pre-fix
behavior was to silently return zero tiles for this case.
"""
from __future__ import annotations

import pytest

gdstk = pytest.importorskip("gdstk")


@pytest.mark.magic
def test_run_drc_catches_sub_min_width_met1_after_box_grow(tmp_path):
    """A 100 nm × 3 µm met1 rect violates met1.1 (min 140 nm). The
    fix is what makes this tile visible — without `box grow`, the
    selection bbox equals the rect and `drc listall why` returns 0.
    """
    from rekolektion.verify.drc import run_drc

    lib = gdstk.Library(name="thinwire_lib")
    cell = gdstk.Cell("thinwire")
    cell.add(gdstk.rectangle((0.0, 0.0), (3.0, 0.1), layer=68, datatype=20))
    lib.add(cell)
    gds = tmp_path / "thinwire.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="thinwire", output_dir=tmp_path)
    # The wire is sub-min on a known-waiver rule; in strict-default
    # mode the tile counts as a real error. What we care about for the
    # regression is just that the tile is SEEN at all.
    assert result.error_count >= 1, (
        f"expected ≥1 met1.1 tile, got error_count={result.error_count}; "
        "if this is 0, the box-grow fix in drc.py has regressed and "
        "edge-effect tiles are being silently dropped again."
    )
    found_met1_1 = any("met1.1" in line for line in result.errors)
    assert found_met1_1, (
        f"expected a met1.1 rule message in errors, got: {result.errors}"
    )


@pytest.mark.magic
def test_run_drc_catches_min_area_met1_after_box_grow(tmp_path):
    """A 0.2 × 0.2 µm met1 square is min-width-clean (0.2 ≥ 0.14)
    but min-area-violating (0.04 µm² < 0.083 µm²). Same selection-box
    pathology — the violation is at the cell-bbox boundary.
    """
    from rekolektion.verify.drc import run_drc

    lib = gdstk.Library(name="smallpatch_lib")
    cell = gdstk.Cell("smallpatch")
    cell.add(gdstk.rectangle((0.0, 0.0), (0.2, 0.2), layer=68, datatype=20))
    lib.add(cell)
    gds = tmp_path / "smallpatch.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="smallpatch", output_dir=tmp_path)
    assert result.error_count >= 1, (
        f"expected ≥1 met1.6 tile, got error_count={result.error_count}; "
        "box-grow fix has regressed"
    )
    found_min_area = any("met1.6" in line for line in result.errors)
    assert found_min_area, (
        f"expected a met1.6 rule message in errors, got: {result.errors}"
    )
