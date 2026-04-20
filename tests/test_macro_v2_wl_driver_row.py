import gdstk
import pytest

from rekolektion.macro_v2.wl_driver_row import WlDriverRow


def test_wl_driver_row_height_scales_with_rows():
    r4 = WlDriverRow(num_rows=4)
    r16 = WlDriverRow(num_rows=16)
    assert r4.height < r16.height
    assert abs(r16.height - 16 * 1.58) < 1e-6


def test_wl_driver_row_has_one_nand3_per_row():
    row = WlDriverRow(num_rows=8)
    lib = row.build()
    top = next(c for c in lib.cells if c.name == row.top_cell_name)
    refs = [
        r for r in top.references
        if "nand3_dec" in r.cell.name
    ]
    assert len(refs) == 8


def test_wl_driver_row_rejects_zero_rows():
    with pytest.raises(ValueError):
        WlDriverRow(num_rows=0)


def test_wl_driver_row_exposes_a_and_z_pin_positions():
    row = WlDriverRow(num_rows=4)
    # Even row 0: unmirrored; pin at cell-local coords
    ax0, ay0 = row.a_pin_absolute(0)
    zx0, zy0 = row.z_pin_absolute(0)
    assert abs(ax0 - 1.265) < 1e-6
    assert abs(ay0 - 0.410) < 1e-6
    assert abs(zy0 - 0.285) < 1e-6
    # Odd row 1: X-mirrored, Y flipped around pitch
    ax1, ay1 = row.a_pin_absolute(1)
    assert abs(ay1 - (2 * 1.58 - 0.410)) < 1e-6


@pytest.mark.magic
@pytest.mark.parametrize("num_rows", [4, 8, 16])
def test_wl_driver_row_drc_clean(tmp_path, num_rows):
    from rekolektion.verify.drc import run_drc
    name = f"wld_{num_rows}"
    row = WlDriverRow(num_rows=num_rows, name=name)
    lib = row.build()
    gds = tmp_path / f"{name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=name, output_dir=tmp_path)
    assert r.clean, (
        f"real={r.real_error_count} (waivers={r.waiver_error_count}): "
        f"{r.real_errors[:5]}"
    )
