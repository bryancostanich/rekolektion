"""Tests for `rekolektion.layout.taps.place_taps_around`."""

from __future__ import annotations

import warnings

import pytest

from rekolektion.io import rkt
from rekolektion.layout import place_taps_around
from rekolektion.layout.taps import _DEFAULT_TAP_WIDTH_UM, _um_to_dbu


# Inner bbox: a 5 × 3 µm rectangle of active geometry to surround.
SMALL_BBOX = (0, 0, 5000, 3000)


def _rects_on_layer(elements, layer_name):
    return [
        e for e in elements
        if isinstance(e, rkt.Rect)
        and e.layer.kind == "named"
        and e.layer.name == layer_name
    ]


# ─── Basic structure ─────────────────────────────────────────────────


def test_top_and_bottom_bands_default() -> None:
    result = place_taps_around(SMALL_BBOX, "nwell")
    assert "top" in result.bands
    assert "bottom" in result.bands
    assert "left" not in result.bands
    assert "right" not in result.bands
    # Every band has at least: 1 tap + 1 implant + 1+ licons + 1 li1.
    for band in result.bands.values():
        layers = {r.layer.name for r in band if isinstance(r, rkt.Rect)}
        assert "tap" in layers
        assert "nsdm" in layers  # nwell ⇒ n+ ⇒ nsdm
        assert "licon1" in layers
        assert "li1" in layers


def test_pwell_uses_psdm_implant() -> None:
    result = place_taps_around(SMALL_BBOX, "pwell")
    for band in result.bands.values():
        names = {r.layer.name for r in band if isinstance(r, rkt.Rect)}
        assert "psdm" in names
        assert "nsdm" not in names


def test_all_sides() -> None:
    result = place_taps_around(
        SMALL_BBOX, "nwell", sides=("top", "bottom", "left", "right")
    )
    assert set(result.bands.keys()) == {"top", "bottom", "left", "right"}


# ─── Geometry: bands sit outside the inner bbox ──────────────────────


def test_bottom_band_below_inner_bbox() -> None:
    result = place_taps_around(SMALL_BBOX, "nwell", clearance_um=0.3)
    tap = _rects_on_layer(result.bands["bottom"], "tap")[0]
    inner_y_min = SMALL_BBOX[1]
    # Tap's top edge must be at least `clearance_um` below the inner bbox.
    assert tap.y2 < inner_y_min
    assert (inner_y_min - tap.y2) >= _um_to_dbu(0.3) - 1  # rounding slack


def test_top_band_above_inner_bbox() -> None:
    result = place_taps_around(SMALL_BBOX, "nwell")
    tap = _rects_on_layer(result.bands["top"], "tap")[0]
    inner_y_max = SMALL_BBOX[3]
    assert tap.y1 > inner_y_max


def test_left_band_left_of_inner_bbox() -> None:
    result = place_taps_around(SMALL_BBOX, "nwell", sides=("left",))
    tap = _rects_on_layer(result.bands["left"], "tap")[0]
    inner_x_min = SMALL_BBOX[0]
    assert tap.x2 < inner_x_min


# ─── Geometry: implant encloses tap by NSDM_ENCLOSURE_OF_DIFF ────────


def test_implant_encloses_tap_with_margin() -> None:
    result = place_taps_around(SMALL_BBOX, "nwell")
    band = result.bands["bottom"]
    tap = _rects_on_layer(band, "tap")[0]
    nsdm = _rects_on_layer(band, "nsdm")[0]
    # 0.125 µm = 125 DBU margin on each side.
    assert nsdm.x1 <= tap.x1 - 124
    assert nsdm.y1 <= tap.y1 - 124
    assert nsdm.x2 >= tap.x2 + 124
    assert nsdm.y2 >= tap.y2 + 124


# ─── Contacts: present, sized, periodic ──────────────────────────────


def test_horizontal_band_has_licon_array() -> None:
    result = place_taps_around(SMALL_BBOX, "nwell")
    licons = _rects_on_layer(result.bands["bottom"], "licon1")
    # 5 µm long band with 0.34 µm pitch ⇒ 13-14 contacts (depending on
    # licon enclosure offsets). Just confirm it's "several".
    assert len(licons) >= 8
    # Each licon is 0.17 µm square (170 DBU).
    for licon in licons:
        assert licon.x2 - licon.x1 == 170
        assert licon.y2 - licon.y1 == 170
    # Spacing: x-pitch ≈ 0.34 µm = 340 DBU. Allow ±1 DBU rounding.
    xs = sorted(l.x1 for l in licons)
    diffs = [xs[i + 1] - xs[i] for i in range(len(xs) - 1)]
    assert all(abs(d - 340) <= 2 for d in diffs)


def test_vertical_band_has_licon_array() -> None:
    result = place_taps_around(SMALL_BBOX, "nwell", sides=("left",))
    licons = _rects_on_layer(result.bands["left"], "licon1")
    assert len(licons) >= 4   # 3 µm tall band, ~6-7 contacts


# ─── Bad input ───────────────────────────────────────────────────────


def test_rejects_invalid_well_type() -> None:
    with pytest.raises(ValueError, match="well_type"):
        place_taps_around(SMALL_BBOX, "bogus_well")  # type: ignore[arg-type]


def test_rejects_invalid_side() -> None:
    with pytest.raises(ValueError, match="side"):
        place_taps_around(SMALL_BBOX, "nwell", sides=("northwest",))  # type: ignore[arg-type]


# ─── Latch-up warning ────────────────────────────────────────────────


def test_warns_on_huge_inner_bbox() -> None:
    big_bbox = (0, 0, 20_000, 3000)  # 20 µm wide — exceeds 14.85 µm
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        place_taps_around(big_bbox, "nwell")
    assert any("latch-up" in str(w.message) for w in caught)


def test_no_warning_for_normal_size() -> None:
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        place_taps_around(SMALL_BBOX, "nwell")
    assert not any("latch-up" in str(w.message) for w in caught)


# ─── Elements property is the flat splat ─────────────────────────────


def test_elements_aggregates_all_bands() -> None:
    result = place_taps_around(SMALL_BBOX, "nwell", sides=("top", "bottom"))
    total = sum(len(b) for b in result.bands.values())
    assert len(result.elements) == total


def test_li1_straps_by_side_separates_by_side() -> None:
    result = place_taps_around(
        SMALL_BBOX, "nwell", sides=("top", "bottom")
    )
    by_side = result.li1_straps_by_side
    assert set(by_side.keys()) == {"top", "bottom"}
    assert len(by_side["top"]) == 1
    assert len(by_side["bottom"]) == 1
    # Each side's strap matches an entry in the flat .li1_straps list.
    assert by_side["top"][0] in result.li1_straps
    assert by_side["bottom"][0] in result.li1_straps
