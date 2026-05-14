"""Tests for `rekolektion.layout.rail.place_rail`."""

from __future__ import annotations

import warnings

import pytest

from rekolektion.io import rkt
from rekolektion.layout import place_rail, place_taps_around


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
