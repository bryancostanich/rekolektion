"""Grid-snap validator tests.

`verify_grid` walks every coordinate-bearing element in a `.rkt` (and
its transitive imports) and reports any that don't land on the PDK's
manufacturing grid. SKY130 = 5 nm.
"""
from __future__ import annotations

import tempfile
from pathlib import Path

import pytest

from rekolektion.io import rkt
from rekolektion.verify.grid import (
    GridVerifyResult,
    OffGridViolation,
    verify_grid,
)


# ─── Helpers ─────────────────────────────────────────────────────────


def _write_doc(tmpdir: Path, doc: rkt.Document, name: str = "test") -> Path:
    p = tmpdir / f"{name}.rkt"
    p.write_text(rkt.write(doc))
    return p


def _clean_doc(elements: list[rkt.Element]) -> rkt.Document:
    return rkt.Document(
        cells=[rkt.Cell(name="t", elements=elements)],
        top_cell="t",
        pdk="sky130",
    )


# ─── Clean case ──────────────────────────────────────────────────────


def test_clean_rect_is_clean(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=0, y1=0, x2=1000, y2=500),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    assert r.clean
    assert r.off_grid == []
    assert r.grid == 5
    assert r.pdk == "sky130"
    assert r.total_coords > 0


def test_clean_sref_label_poly_are_clean(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=0, y1=0, x2=1000, y2=500),
        rkt.Label(layer=rkt.named("sky130", "met1_label"),
                  text="VSS", origin=(500, 250)),
        rkt.Poly(layer=rkt.named("sky130", "li1"),
                 points=[(0, 0), (100, 0), (100, 100), (0, 100)]),
        rkt.Path(layer=rkt.named("sky130", "li1"),
                 width=170, points=[(0, 0), (1000, 0), (1000, 500)]),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    assert r.clean


# ─── Off-grid detection ──────────────────────────────────────────────


def test_offgrid_rect_corner_detected(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=173, y1=0, x2=1000, y2=500),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    assert not r.clean
    assert len(r.off_grid) == 1
    v = r.off_grid[0]
    assert v.element_kind == "rect"
    assert v.coord == (173, 0)
    assert v.cell == "t"
    assert v.grid == 5


def test_offgrid_label_origin_detected(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Label(layer=rkt.named("sky130", "met1_label"),
                  text="VSS", origin=(173, 7)),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    assert len(r.off_grid) == 1
    assert r.off_grid[0].element_kind == "label"
    assert r.off_grid[0].coord == (173, 7)


def test_offgrid_sref_origin_detected(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.SRef(cell="some_prim", origin=(173, 7)),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    # `some_prim` import not resolvable but walker still checks origin
    assert len(r.off_grid) >= 1
    sref_violations = [v for v in r.off_grid if v.element_kind == "sref"]
    assert len(sref_violations) == 1
    assert sref_violations[0].coord == (173, 7)


def test_offgrid_poly_point_detected(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Poly(layer=rkt.named("sky130", "li1"),
                 points=[(0, 0), (173, 0), (100, 100)]),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    assert len(r.off_grid) == 1
    assert r.off_grid[0].element_kind == "poly"
    assert r.off_grid[0].coord == (173, 0)


def test_offgrid_path_point_detected(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Path(layer=rkt.named("sky130", "li1"),
                 width=170, points=[(0, 0), (173, 0)]),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    assert len(r.off_grid) == 1
    assert r.off_grid[0].element_kind == "path"


def test_multiple_violations_all_reported(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=173, y1=0, x2=1001, y2=500),
        rkt.Label(layer=rkt.named("sky130", "met1_label"),
                  text="VSS", origin=(7, 11)),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    # Rect: 2 corners off (x1=173, x2=1001), Label: 1 origin off
    assert len(r.off_grid) == 3


# ─── PDK / grid resolution ───────────────────────────────────────────


def test_grid_kwarg_overrides_pdk(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=10, y1=0, x2=20, y2=500),
    ])
    p = _write_doc(tmp_path, doc)
    # On a 1-nm grid this is clean; on 5-nm grid it is also clean
    # (10, 20 % 5 == 0). Pick coords that fail at 5 but pass at 1:
    doc2 = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=11, y1=0, x2=21, y2=500),
    ])
    p2 = _write_doc(tmp_path, doc2, name="t2")
    assert not verify_grid(p2).clean
    assert verify_grid(p2, grid=1).clean


def test_unknown_pdk_raises(tmp_path: Path) -> None:
    doc = rkt.Document(
        cells=[rkt.Cell(name="t", elements=[])],
        top_cell="t",
        pdk="not_a_real_pdk",
    )
    p = _write_doc(tmp_path, doc)
    with pytest.raises(ValueError, match="Unknown PDK"):
        verify_grid(p)


# ─── Result surface ──────────────────────────────────────────────────


def test_summary_reports_violation_count(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=173, y1=0, x2=1000, y2=500),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    summary = r.summary()
    assert "off-grid" in summary.lower() or "violation" in summary.lower()
    assert "1" in summary


def test_clean_summary_says_clean(tmp_path: Path) -> None:
    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=0, y1=0, x2=1000, y2=500),
    ])
    p = _write_doc(tmp_path, doc)
    r = verify_grid(p)
    assert "clean" in r.summary().lower() or "on-grid" in r.summary().lower()


# ─── verify_drc integration ──────────────────────────────────────────


def test_verify_drc_strict_grid_escalates(tmp_path, monkeypatch):
    """An off-grid coord trips clean=False on the combined DRCResult."""
    from rekolektion.verify import rkt_drc
    from rekolektion.verify.drc import DRCResult

    # Off-grid rect in a stub .rkt.
    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=173, y1=0, x2=1000, y2=500),
    ])
    rkt_file = _write_doc(tmp_path, doc, name="bad")

    def fake_convert(rkt_path, gds_path):
        gds_path.write_bytes(b"")

    def fake_run_drc(*_a, **_k):
        return DRCResult(
            clean=True, error_count=0, real_error_count=0,
            waiver_error_count=0, errors=[], real_errors=[],
            log_path=Path("/dev/null"), cell_name="t",
        )

    monkeypatch.setattr(rkt_drc, "_convert_rkt_to_gds", fake_convert)
    monkeypatch.setattr(rkt_drc, "run_drc", fake_run_drc)
    monkeypatch.setattr(
        rkt_drc, "compute_primitive_footprints", lambda *_a, **_k: []
    )

    result = rkt_drc.verify_drc(rkt_file)
    assert not result.clean
    assert result.real_error_count >= 1
    assert any("grid" in e.lower() for e in result.real_errors)
    # Attached detailed report still available.
    assert hasattr(result, "grid")
    assert len(result.grid.off_grid) == 1


def test_verify_drc_strict_grid_false_observes_only(tmp_path, monkeypatch):
    """With strict_grid=False, grid violations report but don't fail."""
    from rekolektion.verify import rkt_drc
    from rekolektion.verify.drc import DRCResult

    doc = _clean_doc([
        rkt.Rect(layer=rkt.named("sky130", "met1"),
                 x1=173, y1=0, x2=1000, y2=500),
    ])
    rkt_file = _write_doc(tmp_path, doc, name="bad")

    def fake_convert(rkt_path, gds_path):
        gds_path.write_bytes(b"")

    def fake_run_drc(*_a, **_k):
        return DRCResult(
            clean=True, error_count=0, real_error_count=0,
            waiver_error_count=0, errors=[], real_errors=[],
            log_path=Path("/dev/null"), cell_name="t",
        )

    monkeypatch.setattr(rkt_drc, "_convert_rkt_to_gds", fake_convert)
    monkeypatch.setattr(rkt_drc, "run_drc", fake_run_drc)
    monkeypatch.setattr(
        rkt_drc, "compute_primitive_footprints", lambda *_a, **_k: []
    )

    result = rkt_drc.verify_drc(rkt_file, strict_grid=False)
    # Clean stays True; violations appear in errors but not real_errors.
    assert result.clean
    assert result.real_error_count == 0
    assert any("grid" in e.lower() for e in result.errors)
    assert not any("grid" in e.lower() for e in result.real_errors)
