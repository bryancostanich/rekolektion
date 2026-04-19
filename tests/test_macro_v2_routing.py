import gdstk
import pytest

from rekolektion.macro_v2.routing import (
    draw_pdn_strap,
    draw_via_array,
    draw_via_stack,
    draw_wire,
)
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


def test_via_stack_met1_to_met2_creates_three_shape_types():
    """A met1->met2 via emits: met1 landing + via cut + met2 landing."""
    cell = gdstk.Cell("test_via12")
    draw_via_stack(cell, from_layer="met1", to_layer="met2", position=(5.0, 5.0))
    layers = [(p.layer, p.datatype) for p in cell.polygons]
    assert GDS_LAYER["met1"] in layers
    assert GDS_LAYER["via"] in layers
    assert GDS_LAYER["met2"] in layers


def test_via_stack_met1_to_met3_is_stacked():
    """met1 -> met3 requires via + via2 cuts with intermediate met2 landing."""
    cell = gdstk.Cell("test_via13")
    draw_via_stack(cell, from_layer="met1", to_layer="met3", position=(5.0, 5.0))
    layers = [(p.layer, p.datatype) for p in cell.polygons]
    assert GDS_LAYER["met1"] in layers
    assert GDS_LAYER["via"] in layers
    assert GDS_LAYER["met2"] in layers
    assert GDS_LAYER["via2"] in layers
    assert GDS_LAYER["met3"] in layers


def test_via_stack_rejects_invalid_direction():
    cell = gdstk.Cell("test_bad_dir")
    with pytest.raises(ValueError):
        draw_via_stack(cell, from_layer="met3", to_layer="met1", position=(0, 0))


def test_horizontal_pdn_strap_on_met4():
    cell = gdstk.Cell("test_pdn_h")
    draw_pdn_strap(
        cell, orientation="horizontal",
        center_coord=10.0, span_start=0.0, span_end=100.0,
        layer="met4", width=1.6,
    )
    assert len(cell.polygons) == 1
    p = cell.polygons[0]
    assert (p.layer, p.datatype) == GDS_LAYER["met4"]
    bb = p.bounding_box()
    assert abs(bb[0][0] - 0.0) < 1e-9
    assert abs(bb[1][0] - 100.0) < 1e-9
    assert abs(bb[0][1] - 9.2) < 1e-9
    assert abs(bb[1][1] - 10.8) < 1e-9


def test_vertical_pdn_strap_on_met3():
    cell = gdstk.Cell("test_pdn_v")
    draw_pdn_strap(
        cell, orientation="vertical",
        center_coord=5.0, span_start=0.0, span_end=50.0,
        layer="met3", width=1.6,
    )
    bb = cell.polygons[0].bounding_box()
    assert abs(bb[0][0] - 4.2) < 1e-9
    assert abs(bb[1][0] - 5.8) < 1e-9
    assert abs(bb[0][1] - 0.0) < 1e-9
    assert abs(bb[1][1] - 50.0) < 1e-9


def test_via_array_creates_N_cuts():
    """A 3x3 via_array between met1 and met2 produces 9 via cuts."""
    cell = gdstk.Cell("test_via_array")
    draw_via_array(
        cell, from_layer="met1", to_layer="met2",
        position=(5.0, 5.0), rows=3, cols=3,
    )
    via_cuts = [p for p in cell.polygons if (p.layer, p.datatype) == GDS_LAYER["via"]]
    assert len(via_cuts) == 9


def test_via_array_landing_encloses_all_cuts():
    """The metal landing pad must enclose the full array of cuts."""
    cell = gdstk.Cell("test_encl")
    draw_via_array(
        cell, from_layer="met1", to_layer="met2",
        position=(0.0, 0.0), rows=4, cols=4,
    )
    met1_shapes = [p for p in cell.polygons if (p.layer, p.datatype) == GDS_LAYER["met1"]]
    assert len(met1_shapes) == 1
    met1_bb = met1_shapes[0].bounding_box()
    via_cuts = [p for p in cell.polygons if (p.layer, p.datatype) == GDS_LAYER["via"]]
    for cut in via_cuts:
        cbb = cut.bounding_box()
        assert met1_bb[0][0] <= cbb[0][0] + 1e-9
        assert met1_bb[1][0] >= cbb[1][0] - 1e-9
        assert met1_bb[0][1] <= cbb[0][1] + 1e-9
        assert met1_bb[1][1] >= cbb[1][1] - 1e-9


def test_via_array_stacks_multiple_layers():
    """A met1->met3 via_array creates via + via2 arrays with met1/met2/met3 pads."""
    cell = gdstk.Cell("test_stack_array")
    draw_via_array(
        cell, from_layer="met1", to_layer="met3",
        position=(0, 0), rows=2, cols=2,
    )
    via_cuts = [p for p in cell.polygons if (p.layer, p.datatype) == GDS_LAYER["via"]]
    via2_cuts = [p for p in cell.polygons if (p.layer, p.datatype) == GDS_LAYER["via2"]]
    assert len(via_cuts) == 4
    assert len(via2_cuts) == 4
    layers = [(p.layer, p.datatype) for p in cell.polygons]
    assert GDS_LAYER["met1"] in layers
    assert GDS_LAYER["met2"] in layers
    assert GDS_LAYER["met3"] in layers


def test_pdn_strap_rejects_below_min_width():
    cell = gdstk.Cell("test_pdn_bad")
    with pytest.raises(ValueError, match="min width"):
        draw_pdn_strap(
            cell, orientation="horizontal",
            center_coord=0.0, span_start=0.0, span_end=10.0,
            layer="met4", width=0.10,
        )


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


@pytest.mark.magic
def test_via_stack_met1_to_met3_drc_clean(tmp_path):
    """Via stack with wire stubs on each end passes DRC."""
    from rekolektion.verify.drc import run_drc

    lib = gdstk.Library(name="test_via13_lib")
    cell = gdstk.Cell("test_via13")
    draw_wire(cell, start=(0, 5), end=(10, 5), layer="met1", width=0.14)
    draw_wire(cell, start=(5, 0), end=(5, 10), layer="met3", width=0.30)
    draw_via_stack(cell, from_layer="met1", to_layer="met3", position=(5, 5))
    lib.add(cell)
    gds = tmp_path / "test_via13.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="test_via13", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"


@pytest.mark.magic
def test_pdn_strap_drc_clean(tmp_path):
    from rekolektion.verify.drc import run_drc

    lib = gdstk.Library(name="test_pdn_lib")
    cell = gdstk.Cell("test_pdn")
    draw_pdn_strap(
        cell, orientation="horizontal",
        center_coord=10.0, span_start=0.0, span_end=100.0,
        layer="met4", width=1.6,
    )
    lib.add(cell)
    gds = tmp_path / "test_pdn.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="test_pdn", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"


@pytest.mark.magic
def test_via_array_drc_clean(tmp_path):
    """A 4×4 via array connecting met1 up to met4 is DRC clean."""
    from rekolektion.verify.drc import run_drc

    lib = gdstk.Library(name="test_via_array_lib")
    cell = gdstk.Cell("test_via_array")
    # PDN-style drop: met4 strap + met1 landing + 4x4 via array to connect them
    draw_pdn_strap(
        cell, orientation="horizontal",
        center_coord=0.0, span_start=-10.0, span_end=10.0,
        layer="met4", width=1.6,
    )
    draw_wire(cell, start=(-10, 0), end=(10, 0), layer="met1", width=1.0)
    draw_via_array(
        cell, from_layer="met1", to_layer="met4",
        position=(0, 0), rows=4, cols=4,
    )
    lib.add(cell)
    gds = tmp_path / "test_via_array.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="test_via_array", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"


@pytest.mark.magic
def test_compound_cell_drc_clean(tmp_path):
    """A cell using draw_wire + draw_via_stack + draw_pdn_strap is DRC clean."""
    from rekolektion.verify.drc import run_drc

    lib = gdstk.Library(name="test_compound_lib")
    cell = gdstk.Cell("test_compound")

    # Two parallel met1 wires spaced at min-spacing (0.14)
    draw_wire(cell, start=(0.0, 0.0), end=(20.0, 0.0), layer="met1", width=0.14)
    draw_wire(cell, start=(0.0, 0.28), end=(20.0, 0.28), layer="met1", width=0.14)

    # Orthogonal met2 crossing both, via stack to bridge met1 to met2
    draw_wire(cell, start=(10.0, -2.0), end=(10.0, 2.0), layer="met2", width=0.14)
    draw_via_stack(cell, from_layer="met1", to_layer="met2", position=(10.0, 0.0))

    # PDN strap running above the wires
    draw_pdn_strap(
        cell, orientation="horizontal",
        center_coord=5.0, span_start=0.0, span_end=20.0,
        layer="met4", width=1.6,
    )

    lib.add(cell)
    gds = tmp_path / "test_compound.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="test_compound", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"
