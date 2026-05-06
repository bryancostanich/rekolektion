"""Unit tests for verify.abutment_2x2 placement math.

End-to-end DRC tests live in scripts/ runs (need Magic + sky130B).
These tests cover the per-slot transform/origin computation in
isolation so the placement math is locked even if the surrounding
flow changes.
"""

from rekolektion.verify.abutment_2x2 import _placement


# Reference cell with bbox (1, 2) → (5, 6): cw=4, ch=4. Asymmetric origin
# so off-by-x0/y0 errors don't hide.
_BBOX = dict(x0=1.0, y0=2.0, x1=5.0, y1=6.0, cw=4.0, ch=4.0)


def _round(rot: int, xrefl: bool, origin: tuple[float, float]) -> tuple[int, bool, tuple[float, float]]:
    return (rot, xrefl, (round(origin[0], 6), round(origin[1], 6)))


def test_xy_pattern_four_distinct_transforms() -> None:
    """xy: identity at (0,0); X at (0,1); Y at (1,0); XY at (1,1)."""
    cases = [
        ((0, 0), (0, False, (-1.0, -2.0))),       # identity, origin = (-x0, -y0)
        ((0, 1), (180, True, (4.0 + 5.0, -2.0))), # X-mirror, origin x = cw + x1
        ((1, 0), (0, True, (-1.0, 4.0 + 6.0))),   # Y-mirror, origin y = ch + y1
        ((1, 1), (180, False, (4.0 + 5.0, 4.0 + 6.0))),  # XY-mirror
    ]
    for (row, col), expected in cases:
        rot, xrefl, origin = _placement(row, col, "xy", **_BBOX)
        assert _round(rot, xrefl, origin) == _round(*expected), \
            f"xy pattern mismatch at ({row},{col})"


def test_y_only_pattern_mirrors_rows_not_cols() -> None:
    """y: rows 0 identity, row 1 Y-mirror; cols always identity."""
    # Row 0 — both cols identity
    rot, xrefl, origin = _placement(0, 0, "y", **_BBOX)
    assert _round(rot, xrefl, origin) == _round(0, False, (-1.0, -2.0))
    rot, xrefl, origin = _placement(0, 1, "y", **_BBOX)
    assert _round(rot, xrefl, origin) == _round(0, False, (4.0 - 1.0, -2.0))
    # Row 1 — both cols Y-mirror
    rot, xrefl, origin = _placement(1, 0, "y", **_BBOX)
    assert _round(rot, xrefl, origin) == _round(0, True, (-1.0, 4.0 + 6.0))
    rot, xrefl, origin = _placement(1, 1, "y", **_BBOX)
    assert _round(rot, xrefl, origin) == _round(0, True, (4.0 - 1.0, 4.0 + 6.0))


def test_x_only_pattern_mirrors_cols_not_rows() -> None:
    """x: cols 0 identity, col 1 X-mirror; rows always identity."""
    # Col 0 — both rows identity
    rot, xrefl, origin = _placement(0, 0, "x", **_BBOX)
    assert _round(rot, xrefl, origin) == _round(0, False, (-1.0, -2.0))
    rot, xrefl, origin = _placement(1, 0, "x", **_BBOX)
    assert _round(rot, xrefl, origin) == _round(0, False, (-1.0, 4.0 - 2.0))
    # Col 1 — both rows X-mirror
    rot, xrefl, origin = _placement(0, 1, "x", **_BBOX)
    assert _round(rot, xrefl, origin) == _round(180, True, (4.0 + 5.0, -2.0))
    rot, xrefl, origin = _placement(1, 1, "x", **_BBOX)
    assert _round(rot, xrefl, origin) == _round(180, True, (4.0 + 5.0, 4.0 - 2.0))


def test_none_pattern_is_all_identity() -> None:
    """none: all four slots use identity transform; origins still slot per (row, col)."""
    for row in range(2):
        for col in range(2):
            rot, xrefl, origin = _placement(row, col, "none", **_BBOX)
            assert rot == 0
            assert xrefl is False
            # Origin = (col*cw - x0, row*ch - y0) for plain identity placement
            assert _round(rot, xrefl, origin) == _round(
                0, False, (col * 4.0 - 1.0, row * 4.0 - 2.0)
            )


def test_tile_corners_abut_for_xy_pattern() -> None:
    """The transformed bbox of each cell must land in its assigned slot.

    Assigned slot for (row, col) is [col*cw, (col+1)*cw] × [row*ch, (row+1)*ch].
    Verifies the placement math by computing the post-transform bbox of
    each cell and comparing against the slot.
    """
    for row in range(2):
        for col in range(2):
            rot, xrefl, origin = _placement(row, col, "xy", **_BBOX)
            x0, y0, x1, y1 = _BBOX["x0"], _BBOX["y0"], _BBOX["x1"], _BBOX["y1"]
            cw, ch = _BBOX["cw"], _BBOX["ch"]
            # Apply x_reflection (Y-flip) then rotation, then translate by origin.
            corners = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
            if xrefl:
                corners = [(x, -y) for x, y in corners]
            if rot == 180:
                corners = [(-x, -y) for x, y in corners]
            corners = [(x + origin[0], y + origin[1]) for x, y in corners]
            xs = [c[0] for c in corners]
            ys = [c[1] for c in corners]
            slot_x0, slot_x1 = col * cw, (col + 1) * cw
            slot_y0, slot_y1 = row * ch, (row + 1) * ch
            assert round(min(xs), 6) == round(slot_x0, 6), f"({row},{col}) x_min"
            assert round(max(xs), 6) == round(slot_x1, 6), f"({row},{col}) x_max"
            assert round(min(ys), 6) == round(slot_y0, 6), f"({row},{col}) y_min"
            assert round(max(ys), 6) == round(slot_y1, 6), f"({row},{col}) y_max"
