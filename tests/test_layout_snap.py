"""Grid-snap helper tests.

The snap module is the foundation of Track 01 (Grid-Snap Conclusive Fix).
Every coord rekolektion emits must land on the PDK manufacturing grid;
these tests pin down the rounding behaviour so downstream helpers can
rely on it without re-asserting per-call.
"""
from __future__ import annotations

import pytest

from rekolektion.io import rkt
from rekolektion.layout import snap
from rekolektion.tech import grid_nm


# ─── snap_dbu ───────────────────────────────────────────────────────


class TestSnapDbu:
    def test_zero_stays_zero(self) -> None:
        assert snap.snap_dbu(0, pdk="sky130") == 0

    def test_on_grid_positive_unchanged(self) -> None:
        assert snap.snap_dbu(5, pdk="sky130") == 5
        assert snap.snap_dbu(100, pdk="sky130") == 100
        assert snap.snap_dbu(12345, pdk="sky130") == 12345

    def test_on_grid_negative_unchanged(self) -> None:
        assert snap.snap_dbu(-5, pdk="sky130") == -5
        assert snap.snap_dbu(-100, pdk="sky130") == -100
        assert snap.snap_dbu(-12345, pdk="sky130") == -12345

    def test_round_up_positive(self) -> None:
        # 3 nm → 5 nm (closer to 5 than to 0)
        assert snap.snap_dbu(3, pdk="sky130") == 5
        # 4 nm → 5 nm
        assert snap.snap_dbu(4, pdk="sky130") == 5
        # 173 nm → 175 nm
        assert snap.snap_dbu(173, pdk="sky130") == 175

    def test_round_down_positive(self) -> None:
        # 1 nm → 0 nm
        assert snap.snap_dbu(1, pdk="sky130") == 0
        # 2 nm → 0 nm (half rounds toward +inf, so 2.5 → 5; 2 < 2.5 → 0)
        assert snap.snap_dbu(2, pdk="sky130") == 0
        # 172 nm → 170 nm
        assert snap.snap_dbu(172, pdk="sky130") == 170

    def test_half_step_positive_rounds_up(self) -> None:
        # The half-step (2 → ?) lives at 2.5 nm on a 5-nm grid; with
        # integer DBU coords we can only test 2 → 0 and 3 → 5. The
        # "half rounds up" property is exercised at 2.5 only when grids
        # are even-integer DBU like sky130. Document the chosen rule
        # with the boundary value the routing arithmetic actually hits.
        assert snap.snap_dbu(2, pdk="sky130") == 0
        assert snap.snap_dbu(3, pdk="sky130") == 5

    def test_negative_symmetric_under_sign_flip(self) -> None:
        # Symmetric under sign flip: snap(-v) == -snap(v).
        # Critical for rotation: a coord at +173 nm snapping to +175
        # and its 180°-rotated twin at -173 snapping to -175 keeps the
        # cell's centre of symmetry on-grid.
        for v in (1, 2, 3, 4, 7, 172, 173, 174, 12345):
            assert snap.snap_dbu(-v, pdk="sky130") == -snap.snap_dbu(v, pdk="sky130")

    def test_grid_kwarg_overrides_pdk(self) -> None:
        assert snap.snap_dbu(173, grid=10) == 170
        assert snap.snap_dbu(173, grid=1) == 173
        assert snap.snap_dbu(173, grid=100) == 200

    def test_grid_one_is_identity(self) -> None:
        for v in (-12345, -1, 0, 1, 12345):
            assert snap.snap_dbu(v, grid=1) == v

    def test_requires_pdk_or_grid(self) -> None:
        with pytest.raises(ValueError):
            snap.snap_dbu(173)

    def test_unknown_pdk_raises(self) -> None:
        with pytest.raises(ValueError):
            snap.snap_dbu(173, pdk="not_a_real_pdk")


# ─── snap_point / snap_rect ─────────────────────────────────────────


class TestSnapPoint:
    def test_basic(self) -> None:
        assert snap.snap_point((173, -7000), pdk="sky130") == (175, -7000)

    def test_both_off_grid(self) -> None:
        assert snap.snap_point((173, -2917), pdk="sky130") == (175, -2915)

    def test_grid_kwarg(self) -> None:
        assert snap.snap_point((173, -7000), grid=10) == (170, -7000)


class TestSnapRect:
    def test_all_corners_snapped(self) -> None:
        assert snap.snap_rect((173, -7000, 12444, -2917), pdk="sky130") == (
            175,
            -7000,
            12445,
            -2915,
        )

    def test_on_grid_unchanged(self) -> None:
        assert snap.snap_rect((0, 0, 1000, 1000), pdk="sky130") == (0, 0, 1000, 1000)


# ─── grid_for(layer) ────────────────────────────────────────────────


class TestGridFor:
    def test_named_layer_uses_pdk(self) -> None:
        layer = rkt.named("sky130", "met1")
        assert snap.grid_for(layer) == grid_nm("sky130")

    def test_unknown_layer_raises(self) -> None:
        layer = rkt.unknown(68, 20)
        with pytest.raises(ValueError):
            snap.grid_for(layer)

    def test_named_layer_unknown_pdk_raises(self) -> None:
        layer = rkt.named("not_a_real_pdk", "met1")
        with pytest.raises(ValueError):
            snap.grid_for(layer)
