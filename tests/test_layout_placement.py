"""Placement-helper tests.

Uses real generators (so `cell_designs/primitives/` gets populated as
a side effect — not a problem, the cache makes subsequent runs fast).
The bbox extractor is exercised against actual minted primitives.
"""

from __future__ import annotations

import tempfile
import warnings
from pathlib import Path

import pytest

from rekolektion.io import rkt
from rekolektion.layout import (
    inspect_primitive,
    place_row,
    place_tub,
    place_tub_row,
)
from rekolektion.primitives.sky130 import gen_nfet_hv, gen_pfet_hv


@pytest.fixture(scope="module")
def primitives_dir() -> Path:
    """Per-test-module primitives cache directory. Lives in /tmp so it
    doesn't pollute the repo, persists across tests in this file so
    the cache hits, gets cleaned up by tempfile."""

    with tempfile.TemporaryDirectory(prefix="rkt_layout_test_") as d:
        yield Path(d) / "primitives"


@pytest.fixture(scope="module")
def two_sized_nfets(primitives_dir: Path) -> tuple[str, str]:
    """Mint two nfets with different W so we can test mismatched-row
    warnings without re-minting."""

    small = gen_nfet_hv(w_um=0.6, l_um=0.15, primitives_dir=primitives_dir)
    big = gen_nfet_hv(w_um=2.4, l_um=1.0, primitives_dir=primitives_dir)
    return small, big


# ─── inspect_primitive ─────────────────────────────────────────────────


def test_inspect_primitive_returns_generator_and_bbox(
    primitives_dir: Path,
) -> None:
    name = gen_nfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    info = inspect_primitive(name, primitives_dir=primitives_dir)

    assert info.name == name
    assert info.generator == "sky130/nfet_hv"
    assert info.is_nmos is True
    assert info.is_pmos is False
    # bbox sanity: positive width & height, symmetric-ish around 0
    x_min, y_min, x_max, y_max = info.bbox
    assert x_max > x_min
    assert y_max > y_min
    assert info.width == x_max - x_min
    assert info.height == y_max - y_min


def test_inspect_primitive_classifies_pmos(primitives_dir: Path) -> None:
    name = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    info = inspect_primitive(name, primitives_dir=primitives_dir)
    assert info.is_pmos is True
    assert info.is_nmos is False


# ─── place_row ─────────────────────────────────────────────────────────


def test_place_row_abuts_identical_primitives(
    primitives_dir: Path,
) -> None:
    name = gen_nfet_hv(w_um=1.2, l_um=1.0, primitives_dir=primitives_dir)
    info = inspect_primitive(name, primitives_dir=primitives_dir)

    srefs = place_row([name] * 3, primitives_dir=primitives_dir)
    assert len(srefs) == 3
    # Each cell's left edge in parent coords = previous cell's right edge.
    width = info.width
    expected_origins = [
        (-info.bbox[0], 0),
        (width - info.bbox[0], 0),
        (2 * width - info.bbox[0], 0),
    ]
    actual_origins = [s.origin for s in srefs]
    assert actual_origins == expected_origins


def test_place_row_axis_y(primitives_dir: Path) -> None:
    name = gen_nfet_hv(w_um=1.2, l_um=1.0, primitives_dir=primitives_dir)
    info = inspect_primitive(name, primitives_dir=primitives_dir)
    srefs = place_row(
        [name, name], axis="y", primitives_dir=primitives_dir
    )
    # cursor_y starts at 0; second cell sits at info.height up.
    assert srefs[0].origin == (0, -info.bbox[1])
    assert srefs[1].origin == (0, info.height - info.bbox[1])


def test_place_row_respects_explicit_origin(
    primitives_dir: Path,
) -> None:
    name = gen_nfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    info = inspect_primitive(name, primitives_dir=primitives_dir)
    srefs = place_row(
        [name, name], origin=(1000, 500), primitives_dir=primitives_dir
    )
    assert srefs[0].origin == (1000 - info.bbox[0], 500)
    assert srefs[1].origin == (1000 + info.width - info.bbox[0], 500)


def test_place_row_rejects_mixed_well_types(
    primitives_dir: Path,
) -> None:
    nfet = gen_nfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    with pytest.raises(ValueError, match="nmos and pmos"):
        place_row([nfet, pfet], primitives_dir=primitives_dir)


def test_place_row_warns_on_height_mismatch(
    primitives_dir: Path,
    two_sized_nfets: tuple[str, str],
) -> None:
    small, big = two_sized_nfets
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        place_row([small, big], primitives_dir=primitives_dir)
    assert any("mismatched height" in str(w.message) for w in caught)


def test_place_row_rejects_invalid_axis(primitives_dir: Path) -> None:
    name = gen_nfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    with pytest.raises(ValueError, match="axis"):
        place_row([name], axis="diagonal", primitives_dir=primitives_dir)


def test_place_row_empty_input_returns_empty(
    primitives_dir: Path,
) -> None:
    assert place_row([], primitives_dir=primitives_dir) == []


# ─── place_tub ─────────────────────────────────────────────────────────


def test_place_tub_paints_nwell_for_pmos(primitives_dir: Path) -> None:
    pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    result = place_tub(
        [(pfet, (0, 0)), (pfet, (3000, 0))],
        primitives_dir=primitives_dir,
    )
    # 1 nwell + 1 hvi (auto-added for HV devices) = 2 rects
    layers = [r.layer.name for r in result.well_rects]
    assert "nwell" in layers
    assert "hvi" in layers
    assert len(result.srefs) == 2


def test_place_tub_pwell_for_nmos(primitives_dir: Path) -> None:
    nfet = gen_nfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    result = place_tub(
        [(nfet, (0, 0))],
        primitives_dir=primitives_dir,
    )
    layers = [r.layer.name for r in result.well_rects]
    assert "pwell" in layers


def test_place_tub_explicit_well_layer_overrides_default(
    primitives_dir: Path,
) -> None:
    pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    result = place_tub(
        [(pfet, (0, 0))],
        well_layer="dnwell",
        primitives_dir=primitives_dir,
    )
    assert result.well_rects[0].layer.name == "dnwell"


def test_place_tub_covers_union_bbox_with_margin(
    primitives_dir: Path,
) -> None:
    pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    info = inspect_primitive(pfet, primitives_dir=primitives_dir)
    margin_dbu = 400  # 0.4 µm default

    result = place_tub(
        [(pfet, (0, 0)), (pfet, (5000, 0))],
        primitives_dir=primitives_dir,
    )
    well = result.well_rects[0]
    # Leftmost primitive's left edge = 0 + bbox.x_min
    assert well.x1 == info.bbox[0] - margin_dbu
    # Rightmost primitive's right edge = 5000 + bbox.x_max
    assert well.x2 == 5000 + info.bbox[2] + margin_dbu


def test_place_tub_rejects_empty(primitives_dir: Path) -> None:
    with pytest.raises(ValueError, match="at least one"):
        place_tub([], primitives_dir=primitives_dir)


def test_place_tub_rejects_mixed_well_types(
    primitives_dir: Path,
) -> None:
    nfet = gen_nfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    with pytest.raises(ValueError, match="nmos and pmos"):
        place_tub(
            [(nfet, (0, 0)), (pfet, (3000, 0))],
            primitives_dir=primitives_dir,
        )


def test_place_tub_elements_property_orders_well_first(
    primitives_dir: Path,
) -> None:
    pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    result = place_tub([(pfet, (0, 0))], primitives_dir=primitives_dir)
    elements = result.elements
    # well_rects appear before srefs in the elements list
    last_rect_idx = max(
        i for i, e in enumerate(elements) if isinstance(e, rkt.Rect)
    )
    first_sref_idx = min(
        i for i, e in enumerate(elements) if isinstance(e, rkt.SRef)
    )
    assert last_rect_idx < first_sref_idx


# ─── place_tub_row ─────────────────────────────────────────────────────


def test_place_tub_row_abuts_and_tubs(primitives_dir: Path) -> None:
    pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    result = place_tub_row(
        [pfet, pfet, pfet],
        origin=(0, 5000),
        primitives_dir=primitives_dir,
    )
    info = inspect_primitive(pfet, primitives_dir=primitives_dir)

    # Same number of SRefs as the row, each abutting.
    assert len(result.srefs) == 3
    width = info.width
    expected = [
        (-info.bbox[0], 5000),
        (width - info.bbox[0], 5000),
        (2 * width - info.bbox[0], 5000),
    ]
    actual = [s.origin for s in result.srefs]
    assert actual == expected

    # And a tub painted around them.
    well_layers = {r.layer.name for r in result.well_rects}
    assert "nwell" in well_layers
    assert "hvi" in well_layers   # HV auto-add


def test_place_tub_row_rejects_mixed_well_types(
    primitives_dir: Path,
) -> None:
    nfet = gen_nfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)
    with pytest.raises(ValueError, match="nmos and pmos"):
        place_tub_row([nfet, pfet], primitives_dir=primitives_dir)
