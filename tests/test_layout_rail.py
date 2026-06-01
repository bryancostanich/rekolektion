"""Tests for `rekolektion.layout.rail.place_rail`."""

from __future__ import annotations

import warnings

import pytest

from rekolektion.io import rkt
from rekolektion.layout import (
    place_rail,
    place_rail_from_strap,
    place_taps_around,
)


RAIL_BBOX = (0, -2200, 8000, -1700)


def _layer_rects(elements, name):
    return [
        e for e in elements
        if isinstance(e, rkt.Rect)
        and e.layer.kind == "named"
        and e.layer.name == name
    ]


def _label_of(elements, layer_name):
    matches = [
        e for e in elements
        if isinstance(e, rkt.Label)
        and e.layer.kind == "named"
        and e.layer.name == layer_name
    ]
    assert len(matches) == 1, f"expected exactly one {layer_name} label"
    return matches[0]


# ─── Basic rail ──────────────────────────────────────────────────────


def test_paints_rail_rect() -> None:
    elements = place_rail(RAIL_BBOX, layer="met1")
    rects = _layer_rects(elements, "met1")
    assert len(rects) == 1
    r = rects[0]
    assert (r.x1, r.y1, r.x2, r.y2) == RAIL_BBOX


def test_no_label_when_label_arg_omitted() -> None:
    elements = place_rail(RAIL_BBOX, layer="met1")
    labels = [e for e in elements if isinstance(e, rkt.Label)]
    assert labels == []


def test_label_lands_on_layer_label_purpose() -> None:
    elements = place_rail(RAIL_BBOX, label="VSS")
    label = _label_of(elements, "met1_label")
    assert label.text == "VSS"
    # Default origin = rail centroid.
    assert label.origin == ((RAIL_BBOX[0] + RAIL_BBOX[2]) // 2,
                            (RAIL_BBOX[1] + RAIL_BBOX[3]) // 2)


def test_explicit_label_origin_respected() -> None:
    elements = place_rail(
        RAIL_BBOX, label="VDD", label_origin=(100, -1900)
    )
    label = _label_of(elements, "met1_label")
    assert label.origin == (100, -1900)


def test_rejects_inverted_bbox() -> None:
    with pytest.raises(ValueError, match="empty or inverted"):
        place_rail((100, 100, 50, 50))


# ─── Stitch from tap straps ──────────────────────────────────────────


def _make_tap_band() -> tuple:
    """Build a real pwell tap band below an SMALL active bbox so we
    have a concrete li1 strap to stitch."""

    inner = (0, 0, 5000, 3000)
    return place_taps_around(inner, "pwell", sides=("bottom",))


def test_stitch_produces_mcon_array() -> None:
    tap = _make_tap_band()
    # tap band's li1 strap is around y = -1735..-1405 with default
    # 0.3 µm clearance and 0.42 µm tap width. Rail must overlap that.
    strap = tap.li1_straps[0]
    # Build a rail that overlaps the strap top-half.
    rail_bbox = (strap.x1, strap.y1 + 50, strap.x2, strap.y2 + 200)
    elements = place_rail(
        rail_bbox, label="VSS", stitch_li1_straps=tap.li1_straps
    )
    mcons = _layer_rects(elements, "mcon")
    # The overlap is ~280 DBU wide × full strap width: should fit
    # several mcons.
    assert len(mcons) >= 5
    # Each mcon is 0.17 µm square.
    for m in mcons:
        assert m.x2 - m.x1 == 170
        assert m.y2 - m.y1 == 170


def test_mcon_array_uses_correct_pitch() -> None:
    tap = _make_tap_band()
    strap = tap.li1_straps[0]
    rail_bbox = (strap.x1, strap.y1 + 50, strap.x2, strap.y2 + 200)
    elements = place_rail(rail_bbox, stitch_li1_straps=tap.li1_straps)
    mcons = _layer_rects(elements, "mcon")
    xs = sorted({m.x1 for m in mcons})
    if len(xs) > 1:
        diffs = [xs[i + 1] - xs[i] for i in range(len(xs) - 1)]
        # mcon_pitch = MCON_SIZE + MCON_SPACING = 0.17 + 0.19 = 0.36 µm
        for d in diffs:
            assert abs(d - 360) <= 2


def test_warns_on_strap_without_rail_overlap() -> None:
    # Rail at the TOP of the block, strap from a BOTTOM tap → no overlap.
    tap = _make_tap_band()
    rail_bbox = (0, 5000, 8000, 5500)
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        elements = place_rail(
            rail_bbox, stitch_li1_straps=tap.li1_straps
        )
    assert any("overlap" in str(w.message) for w in caught)
    # No mcons emitted.
    assert _layer_rects(elements, "mcon") == []


def test_no_straps_no_stitch() -> None:
    elements = place_rail(RAIL_BBOX, label="VSS")
    assert _layer_rects(elements, "mcon") == []


def test_empty_stitch_list_no_warning() -> None:
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        place_rail(RAIL_BBOX, label="VSS", stitch_li1_straps=[])
    assert not any("overlap" in str(w.message) for w in caught)


def test_tap_band_li1_straps_property_returns_all() -> None:
    tap = place_taps_around(
        (0, 0, 5000, 3000), "pwell", sides=("top", "bottom")
    )
    assert len(tap.li1_straps) == 2


# ─── place_rail_from_strap ───────────────────────────────────────────


def test_place_rail_from_strap_covering_default() -> None:
    tap = _make_tap_band()
    strap = tap.li1_straps[0]
    elements = place_rail_from_strap(strap, label="VSS")
    rail_rects = _layer_rects(elements, "met1")
    assert len(rail_rects) == 1
    rail = rail_rects[0]
    # Covering: rail x-extent == strap x-extent; rail y-extent
    # encloses strap plus 0.5 µm = 500 DBU on each side.
    assert rail.x1 == strap.x1
    assert rail.x2 == strap.x2
    assert rail.y1 == strap.y1 - 500
    assert rail.y2 == strap.y2 + 500
    # mcons get auto-placed because rail overlaps strap.
    assert len(_layer_rects(elements, "mcon")) > 0
    # Label landed.
    assert any(isinstance(e, rkt.Label) for e in elements)


def test_place_rail_from_strap_custom_extend() -> None:
    tap = _make_tap_band()
    strap = tap.li1_straps[0]
    elements = place_rail_from_strap(strap, label="VSS", extend_um=1.0)
    rail = _layer_rects(elements, "met1")[0]
    assert rail.y1 == strap.y1 - 1000
    assert rail.y2 == strap.y2 + 1000


def test_place_rail_from_strap_rejects_invalid_side() -> None:
    tap = _make_tap_band()
    strap = tap.li1_straps[0]
    with pytest.raises(ValueError, match="side must be"):
        place_rail_from_strap(strap, label="VSS", side="sideways")  # type: ignore[arg-type]


# ─── Grid snap (Track 01) ────────────────────────────────────────────


GRID_NM = 5


def _assert_all_on_grid(elements, grid=GRID_NM):
    """Every Rect corner and Label origin sits on the manufacturing grid."""
    for el in elements:
        if isinstance(el, rkt.Rect):
            for axis, v in (
                ("x1", el.x1), ("y1", el.y1), ("x2", el.x2), ("y2", el.y2)
            ):
                assert v % grid == 0, (
                    f"Rect {el.layer.name} {axis}={v} off grid {grid}"
                )
        elif isinstance(el, rkt.Label):
            x, y = el.origin
            assert x % grid == 0 and y % grid == 0, (
                f"Label {el.text!r} origin ({x},{y}) off grid {grid}"
            )


def test_offgrid_rail_bbox_snaps_corners() -> None:
    """An off-grid rail_bbox is silently snapped at the entry."""
    bbox = (173, -2917, 12444, -1701)   # all four corners off by 1-4 nm
    elements = place_rail(bbox, label="VSS")
    _assert_all_on_grid(elements)
    rail = _layer_rects(elements, "met1")[0]
    # 173 → 175, -2917 → -2915, 12444 → 12445, -1701 → -1700
    assert (rail.x1, rail.y1, rail.x2, rail.y2) == (175, -2915, 12445, -1700)


def test_offgrid_label_centroid_snaps() -> None:
    """Default centroid (x1+x2)//2 can land off-grid; helper snaps it."""
    # x1=0, x2=15990 → centroid 7995 = on-grid (multiple of 5).
    # Pick a width where (x1+x2)//2 is off-grid: x1=0, x2=15994 → 7997.
    elements = place_rail((0, -2200, 15994, -1700), label="VSS")
    _assert_all_on_grid(elements)


def test_offgrid_strap_snaps_before_overlap() -> None:
    """An off-grid li1 strap input still produces on-grid mcons."""
    bad_strap = rkt.Rect(
        layer=rkt.named("sky130", "li1"),
        x1=173, y1=-2917, x2=2173, y2=-1917,
    )
    rail = (0, -2200, 4000, -1700)
    elements = place_rail(rail, label="VSS", stitch_li1_straps=[bad_strap])
    mcons = _layer_rects(elements, "mcon")
    assert len(mcons) > 0
    _assert_all_on_grid(elements)


def test_pdk_kwarg_threads_through() -> None:
    """An explicit pdk= kwarg routes via tech.grid_nm; sky130=5."""
    elements = place_rail((173, -2917, 12444, -1701), label="VSS", pdk="sky130")
    _assert_all_on_grid(elements, grid=5)


def test_unknown_pdk_raises() -> None:
    with pytest.raises(ValueError, match="Unknown PDK"):
        place_rail(RAIL_BBOX, label="VSS", pdk="not_a_real_pdk")
