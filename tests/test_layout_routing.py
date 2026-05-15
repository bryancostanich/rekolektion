"""Routing-helper tests."""

from __future__ import annotations

import tempfile
import warnings
from pathlib import Path

import pytest

from rekolektion.io import rkt
from rekolektion.layout import (
    gate_extension,
    inspect_primitive,
    pin_patch,
    pin_to_rail,
    place_via,
    place_wire,
)
from rekolektion.layout._rkt_bbox import _clear_primitive_cache
from rekolektion.primitives.sky130 import gen_nfet_hv, gen_pfet_hv


@pytest.fixture(scope="module")
def primitives_dir() -> Path:
    with tempfile.TemporaryDirectory(prefix="rkt_routing_test_") as d:
        yield Path(d) / "primitives"


@pytest.fixture(scope="module")
def nfet(primitives_dir: Path) -> str:
    return gen_nfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)


@pytest.fixture(scope="module")
def pfet(primitives_dir: Path) -> str:
    return gen_pfet_hv(w_um=1.0, l_um=0.5, primitives_dir=primitives_dir)


# ─── pin discovery via PrimitiveInfo ─────────────────────────────────


def test_inspect_primitive_returns_pin_labels(
    primitives_dir: Path, nfet: str
) -> None:
    info = inspect_primitive(nfet, primitives_dir=primitives_dir)
    terminals = {p.terminal for p in info.pins}
    # mos_draw with doports=1 emits D, S, G at minimum.
    assert {"D", "G", "S"}.issubset(terminals)


def test_primitive_info_pin_lookup(
    primitives_dir: Path, nfet: str
) -> None:
    info = inspect_primitive(nfet, primitives_dir=primitives_dir)
    drain = info.pin("D")
    assert drain is not None
    assert drain.terminal == "D"
    assert info.pin("nonexistent") is None


def test_primitive_info_cached_returns_same_object(
    primitives_dir: Path, nfet: str
) -> None:
    """Sanity-check: hitting the same path twice returns the cached
    summary (object identity)."""

    info1 = inspect_primitive(nfet, primitives_dir=primitives_dir)
    info2 = inspect_primitive(nfet, primitives_dir=primitives_dir)
    # Both reads should produce identical (and equal) PrimitiveInfo
    # instances since the underlying RktPrimitiveSummary is memoized.
    assert info1 == info2


# ─── pin_patch ───────────────────────────────────────────────────────


def test_pin_patch_paints_met1_and_mcon(
    primitives_dir: Path, nfet: str
) -> None:
    sref = rkt.SRef(cell=nfet, origin=(0, 0))
    patch = pin_patch(sref, "D", primitives_dir=primitives_dir)

    assert patch.cell == nfet
    assert patch.terminal == "D"
    # met1 rect on met1 layer.
    assert patch.met1_rect.layer.name == "met1"
    # Met1 patch is square, centered on the pin.
    width = patch.met1_rect.x2 - patch.met1_rect.x1
    height = patch.met1_rect.y2 - patch.met1_rect.y1
    assert width == height
    assert width >= 260  # SKY130 worst-axis via1 enclosure ≈ 0.32 µm
    # At least one mcon contact.
    assert len(patch.mcon_rects) >= 1
    for m in patch.mcon_rects:
        assert m.layer.name == "mcon"


def test_pin_patch_translates_by_sref_origin(
    primitives_dir: Path, nfet: str
) -> None:
    info = inspect_primitive(nfet, primitives_dir=primitives_dir)
    pin = info.pin("G")
    assert pin is not None

    sref_origin = (1000, 500)
    sref = rkt.SRef(cell=nfet, origin=sref_origin)
    patch = pin_patch(sref, "G", primitives_dir=primitives_dir)

    expected_x = sref_origin[0] + pin.origin[0]
    expected_y = sref_origin[1] + pin.origin[1]
    assert patch.center == (expected_x, expected_y)


def test_pin_patch_raises_for_missing_terminal(
    primitives_dir: Path, nfet: str
) -> None:
    sref = rkt.SRef(cell=nfet, origin=(0, 0))
    with pytest.raises(ValueError, match="no pin labeled"):
        pin_patch(sref, "Q", primitives_dir=primitives_dir)


def test_pin_patch_elements_property_combines_met1_and_mcons(
    primitives_dir: Path, pfet: str
) -> None:
    sref = rkt.SRef(cell=pfet, origin=(0, 0))
    patch = pin_patch(sref, "S", primitives_dir=primitives_dir)
    assert patch.elements[0] is patch.met1_rect
    assert patch.elements[1:] == list(patch.mcon_rects)


# ─── place_wire ──────────────────────────────────────────────────────


def test_place_wire_horizontal_on_met1_is_quiet() -> None:
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        elements = place_wire((0, 0), (1000, 0), layer="met1")
    assert len(elements) == 1
    assert elements[0].layer.name == "met1"
    assert not any("preferred" in str(w.message) for w in caught)


def test_place_wire_warns_on_non_preferred_axis() -> None:
    # Vertical wire on met1 — met1 prefers horizontal.
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        place_wire((0, 0), (0, 1000), layer="met1")
    assert any("preferred" in str(w.message) for w in caught)


def test_place_wire_vertical_on_met2_is_quiet() -> None:
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        elements = place_wire((0, 0), (0, 1000), layer="met2")
    assert len(elements) == 1
    assert elements[0].layer.name == "met2"
    assert not any("preferred" in str(w.message) for w in caught)


def test_place_wire_l_shape_emits_two_segments() -> None:
    # met1 prefers horizontal → first leg horizontal, then vertical.
    elements = place_wire((0, 0), (1000, 500), layer="met1")
    # Expect 2 rects (no via since via_to is None).
    rects = [e for e in elements if isinstance(e, rkt.Rect)]
    assert len(rects) == 2
    assert all(r.layer.name == "met1" for r in rects)


def test_place_wire_zero_length_returns_empty() -> None:
    assert place_wire((100, 100), (100, 100), layer="met1") == []


def test_place_wire_width_defaults_to_min_width() -> None:
    elements = place_wire((0, 0), (1000, 0), layer="met1")
    # MET1_MIN_WIDTH = 0.14 µm = 140 DBU. half_width = 70.
    rect = elements[0]
    assert rect.y2 - rect.y1 == 140


def test_place_wire_explicit_width_overrides_default() -> None:
    elements = place_wire((0, 0), (1000, 0), layer="met1", width_um=0.5)
    rect = elements[0]
    assert rect.y2 - rect.y1 == 500


def test_place_wire_via_to_adds_via_stack() -> None:
    elements = place_wire(
        (0, 0), (1000, 0), layer="met1", via_to="met2"
    )
    rects = [e for e in elements if isinstance(e, rkt.Rect)]
    layers = {r.layer.name for r in rects}
    assert "via" in layers  # met1↔met2 cut is 'via'
    assert "met2" in layers  # upper-layer enclosure rect


# ─── place_via ───────────────────────────────────────────────────────


def test_place_via_met1_to_met2_emits_via_cut_and_upper_rect() -> None:
    elements = place_via((0, 0), "met1", "met2")
    rects = [e for e in elements if isinstance(e, rkt.Rect)]
    layers = [r.layer.name for r in rects]
    assert "via" in layers
    assert "met2" in layers


def test_place_via_array_cuts() -> None:
    elements = place_via((0, 0), "met1", "met2", cuts=(2, 3))
    via_cuts = [
        e for e in elements
        if isinstance(e, rkt.Rect) and e.layer.name == "via"
    ]
    assert len(via_cuts) == 6


def test_place_via_rejects_unknown_layer_pair() -> None:
    with pytest.raises(ValueError, match="no via defined"):
        place_via((0, 0), "met1", "met5")


def test_place_via_args_are_order_independent() -> None:
    a = place_via((0, 0), "met1", "met2")
    b = place_via((0, 0), "met2", "met1")
    # Same cut count, same upper rect (geometry is symmetric).
    a_via_count = sum(
        1 for e in a if isinstance(e, rkt.Rect) and e.layer.name == "via"
    )
    b_via_count = sum(
        1 for e in b if isinstance(e, rkt.Rect) and e.layer.name == "via"
    )
    assert a_via_count == b_via_count


# ─── cache hygiene ───────────────────────────────────────────────────


# ─── pin_to_rail ─────────────────────────────────────────────────────


def _rect_layers(elements):
    return {
        r.layer.name for r in elements
        if isinstance(r, rkt.Rect)
        and r.layer.kind == "named"
    }


def _make_li1_strap(x1: int, y1: int, x2: int, y2: int) -> rkt.Rect:
    return rkt.Rect(
        layer=rkt.named("sky130", "li1"), x1=x1, y1=y1, x2=x2, y2=y2,
    )


def _make_met1_rail(x1: int, y1: int, x2: int, y2: int) -> rkt.Rect:
    return rkt.Rect(
        layer=rkt.named("sky130", "met1"), x1=x1, y1=y1, x2=x2, y2=y2,
    )


def test_pin_to_rail_li1_mode_paints_strap_no_mcons(
    primitives_dir: Path, nfet: str
) -> None:
    """Destination is an existing li1 tap strap: paint li1 extension
    merging into it. No new mcons (strap has its own stitch)."""

    sref = rkt.SRef(cell=nfet, origin=(0, 0))
    strap_dest = _make_li1_strap(-2000, 2000, 2000, 2170)  # below or above
    elements = pin_to_rail(sref, "S", strap_dest, primitives_dir=primitives_dir)
    layers = _rect_layers(elements)
    assert "li1" in layers
    assert "mcon" not in layers
    assert "met1" not in layers


def test_pin_to_rail_li1_mode_strap_extends_to_dest(
    primitives_dir: Path, nfet: str
) -> None:
    """Strap should reach into the destination's y-extent so the li1
    rects merge."""

    sref = rkt.SRef(cell=nfet, origin=(0, 0))
    strap_dest = _make_li1_strap(-2000, 2000, 2000, 2170)
    elements = pin_to_rail(sref, "S", strap_dest, primitives_dir=primitives_dir)
    info = inspect_primitive(nfet, primitives_dir=primitives_dir)
    pin = info.pin("S")
    pin_y = sref.origin[1] + pin.origin[1]
    strap = [r for r in elements if isinstance(r, rkt.Rect) and r.layer.name == "li1"][0]
    assert strap.y1 == pin_y
    assert strap.y2 == strap_dest.y2  # extends into the dest


def test_pin_to_rail_met1_mode_adds_mcon_stitch(
    primitives_dir: Path, nfet: str
) -> None:
    """Destination is a met1 rail (no intermediate strap): paint li1
    extension AND mcon array in the overlap."""

    sref = rkt.SRef(cell=nfet, origin=(0, 0))
    rail = _make_met1_rail(-2000, 2000, 2000, 2500)
    elements = pin_to_rail(sref, "D", rail, primitives_dir=primitives_dir)
    layers = _rect_layers(elements)
    assert "li1" in layers
    assert "mcon" in layers
    # Each mcon is 0.17 µm square.
    mcons = [
        r for r in elements
        if isinstance(r, rkt.Rect) and r.layer.name == "mcon"
    ]
    assert len(mcons) >= 1
    for m in mcons:
        assert m.x2 - m.x1 == 170
        assert m.y2 - m.y1 == 170


def test_pin_to_rail_bbox_tuple_defaults_to_li1_mode(
    primitives_dir: Path, nfet: str
) -> None:
    """Without a layer cue, treat as li1 (the more conservative
    interpretation — no spurious mcons)."""

    sref = rkt.SRef(cell=nfet, origin=(0, 0))
    elements = pin_to_rail(
        sref, "S", (-2000, 2000, 2000, 2170),
        primitives_dir=primitives_dir,
    )
    layers = _rect_layers(elements)
    assert "li1" in layers
    assert "mcon" not in layers


def test_pin_to_rail_rejects_pin_outside_dest_x_extent(
    primitives_dir: Path, nfet: str
) -> None:
    sref = rkt.SRef(cell=nfet, origin=(50_000, 0))
    rail = _make_met1_rail(-2000, 2000, 2000, 2500)
    with pytest.raises(ValueError, match="outside destination"):
        pin_to_rail(sref, "S", rail, primitives_dir=primitives_dir)


def test_pin_to_rail_rejects_missing_terminal(
    primitives_dir: Path, nfet: str
) -> None:
    sref = rkt.SRef(cell=nfet, origin=(0, 0))
    rail = _make_met1_rail(-2000, 2000, 2000, 2500)
    with pytest.raises(ValueError, match="no pin labeled"):
        pin_to_rail(sref, "Q", rail, primitives_dir=primitives_dir)


# ─── cache hygiene ───────────────────────────────────────────────────


def test_clear_primitive_cache_drops_entries(
    primitives_dir: Path, nfet: str
) -> None:
    # Prime the cache.
    inspect_primitive(nfet, primitives_dir=primitives_dir)
    from rekolektion.layout._rkt_bbox import _primitive_cache
    assert len(_primitive_cache) >= 1

    _clear_primitive_cache()
    assert len(_primitive_cache) == 0

    # Re-read works fine after clearing.
    info = inspect_primitive(nfet, primitives_dir=primitives_dir)
    assert info.pins


# ─── gate_extension ──────────────────────────────────────────────────


@pytest.fixture(scope="module")
def nfet_topgate(primitives_dir: Path) -> str:
    return gen_nfet_hv(
        w_um=1.0, l_um=0.5, botc=False, primitives_dir=primitives_dir,
    )


@pytest.fixture(scope="module")
def pfet_botgate(primitives_dir: Path) -> str:
    return gen_pfet_hv(
        w_um=1.0, l_um=0.5, topc=False, primitives_dir=primitives_dir,
    )


def test_gate_extension_topgate_paints_full_stack(
    primitives_dir: Path, nfet_topgate: str,
) -> None:
    sref = rkt.SRef(cell=nfet_topgate, origin=(0, 0))
    info = inspect_primitive(nfet_topgate, primitives_dir=primitives_dir)
    g_pin = info.pin("G")
    # Poly edge for topgate = G_y + 165. Pick contact 600 nm past
    # that so the polycont's 80 nm poly encl + 85 nm half all fit.
    contact_y = g_pin.origin[1] + 165 + 600

    ext = gate_extension(
        sref, contact_y=contact_y, primitives_dir=primitives_dir,
    )

    layers = [e.layer.name for e in ext.elements]
    # Includes the in-cell-met1 bridge as a second met1 rect.
    assert layers == ["poly", "licon1", "li1", "mcon", "met1", "met1"]
    # Center matches gate-pin X and chosen contact_y.
    assert ext.center == (g_pin.origin[0], contact_y)
    # met1 patch is 320 nm square at default patch_half_um=0.16.
    m = ext.met1_rect
    assert (m.x2 - m.x1, m.y2 - m.y1) == (320, 320)


def test_gate_extension_botgate_extends_downward(
    primitives_dir: Path, pfet_botgate: str,
) -> None:
    # Place pfet at a positive sref Y so the extension lands at a
    # parent-coord Y we can reason about cleanly.
    sref = rkt.SRef(cell=pfet_botgate, origin=(0, 5000))
    info = inspect_primitive(pfet_botgate, primitives_dir=primitives_dir)
    g_pin = info.pin("G")
    # Botgate poly bottom in primitive = G_y - 165; in parent =
    # sref.y + G_y - 165. Place contact 600 nm below that.
    poly_bot_parent = 5000 + g_pin.origin[1] - 165
    contact_y = poly_bot_parent - 600

    ext = gate_extension(
        sref, contact_y=contact_y, primitives_dir=primitives_dir,
    )

    poly_rect = ext.elements[0]
    assert poly_rect.layer.name == "poly"
    # Extension poly runs from contact_y - 165 (poly encl below the
    # licon) up to the primitive's poly bottom edge.
    assert poly_rect.y1 == contact_y - 165
    assert poly_rect.y2 == poly_bot_parent


def test_gate_extension_refuses_both_contact_primitive(
    primitives_dir: Path, nfet: str,
) -> None:
    sref = rkt.SRef(cell=nfet, origin=(0, 0))
    with pytest.raises(ValueError, match="_topgate.*_botgate"):
        gate_extension(
            sref, contact_y=5000, primitives_dir=primitives_dir,
        )


def test_gate_extension_refuses_contact_too_close(
    primitives_dir: Path, nfet_topgate: str,
) -> None:
    sref = rkt.SRef(cell=nfet_topgate, origin=(0, 0))
    info = inspect_primitive(nfet_topgate, primitives_dir=primitives_dir)
    g_pin = info.pin("G")
    poly_top = g_pin.origin[1] + 165
    # 100 nm above poly top — short of the 165 nm needed.
    with pytest.raises(ValueError, match="too close"):
        gate_extension(
            sref, contact_y=poly_top + 100,
            primitives_dir=primitives_dir,
        )


def test_gate_extension_translates_by_sref_origin(
    primitives_dir: Path, nfet_topgate: str,
) -> None:
    sref = rkt.SRef(cell=nfet_topgate, origin=(2000, 3000))
    info = inspect_primitive(nfet_topgate, primitives_dir=primitives_dir)
    g_pin = info.pin("G")
    poly_top_parent = 3000 + g_pin.origin[1] + 165
    contact_y = poly_top_parent + 500

    ext = gate_extension(
        sref, contact_y=contact_y, primitives_dir=primitives_dir,
    )

    # Gate-pin X is translated by sref.x.
    assert ext.center[0] == 2000 + g_pin.origin[0]
    # All elements sit at the translated X column.
    for e in ext.elements:
        cx = (e.x1 + e.x2) // 2
        assert cx == 2000 + g_pin.origin[0]
