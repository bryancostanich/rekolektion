import gdstk
import pytest

from rekolektion.macro_v2.routing import draw_wire
from rekolektion.macro_v2.sky130_drc import GDS_LAYER, MET1_MIN_WIDTH


def test_horizontal_wire_creates_rectangle_on_correct_layer():
    cell = gdstk.Cell("test_h")
    draw_wire(cell, start=(0.0, 5.0), end=(10.0, 5.0), layer="met1", width=0.14)
    polys = cell.polygons
    assert len(polys) == 1
    p = polys[0]
    assert (p.layer, p.datatype) == GDS_LAYER["met1"]
    bb = p.bounding_box()
    assert abs(bb[0][0] - 0.0) < 1e-9
    assert abs(bb[1][0] - 10.0) < 1e-9
    assert abs(bb[0][1] - 4.93) < 1e-9
    assert abs(bb[1][1] - 5.07) < 1e-9


def test_vertical_wire_creates_rectangle():
    cell = gdstk.Cell("test_v")
    draw_wire(cell, start=(3.0, 0.0), end=(3.0, 20.0), layer="met2", width=0.14)
    assert len(cell.polygons) == 1
    bb = cell.polygons[0].bounding_box()
    assert abs(bb[0][0] - 2.93) < 1e-9
    assert abs(bb[1][0] - 3.07) < 1e-9
    assert abs(bb[0][1] - 0.0) < 1e-9
    assert abs(bb[1][1] - 20.0) < 1e-9


def test_wire_rejects_non_axis_aligned():
    cell = gdstk.Cell("test_bad")
    with pytest.raises(ValueError, match="axis-aligned"):
        draw_wire(cell, start=(0.0, 0.0), end=(10.0, 5.0), layer="met1", width=0.14)


def test_wire_rejects_below_min_width():
    cell = gdstk.Cell("test_narrow")
    with pytest.raises(ValueError, match="min width"):
        draw_wire(cell, start=(0.0, 0.0), end=(10.0, 0.0), layer="met1", width=0.10)


def test_wire_default_width_is_layer_minimum():
    cell = gdstk.Cell("test_default")
    draw_wire(cell, start=(0.0, 0.0), end=(10.0, 0.0), layer="met1")
    bb = cell.polygons[0].bounding_box()
    assert abs((bb[1][1] - bb[0][1]) - MET1_MIN_WIDTH) < 1e-9


@pytest.mark.magic
def test_horizontal_wire_is_drc_clean(tmp_path):
    """Drawn wire passes Magic DRC against SKY130B deck."""
    from rekolektion.verify.drc import run_drc

    lib = gdstk.Library(name="test_wire_lib")
    cell = gdstk.Cell("test_wire")
    draw_wire(cell, start=(0.0, 0.0), end=(10.0, 0.0), layer="met1", width=0.14)
    lib.add(cell)
    gds = tmp_path / "test_wire.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="test_wire", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"
