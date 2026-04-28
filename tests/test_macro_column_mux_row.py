import gdstk
import pytest

from rekolektion.macro.column_mux_row import ColumnMuxRow


def test_column_mux_row_width_matches_all_pairs():
    """One big cell covering bits * mux_ratio BL/BR pairs at bitcell pitch."""
    row = ColumnMuxRow(bits=8, mux_ratio=4)
    assert row.num_pairs == 32
    assert abs(row.width - 32 * 1.31) < 1e-6


def test_column_mux_row_accepts_mux_ratio_2():
    row = ColumnMuxRow(bits=8, mux_ratio=2)
    assert row.num_pairs == 16


def test_column_mux_row_rejects_unsupported_mux():
    with pytest.raises(ValueError, match="mux_ratio"):
        ColumnMuxRow(bits=4, mux_ratio=3)


def test_column_mux_row_height_scales_with_mux_ratio():
    row2 = ColumnMuxRow(bits=4, mux_ratio=2)
    row4 = ColumnMuxRow(bits=4, mux_ratio=4)
    row8 = ColumnMuxRow(bits=4, mux_ratio=8)
    assert row2.height < row4.height < row8.height


@pytest.mark.magic
@pytest.mark.parametrize("bits,mux_ratio", [
    (8, 2), (8, 4), (4, 8),
])
def test_column_mux_row_drc_clean(tmp_path, bits, mux_ratio):
    from rekolektion.verify.drc import run_drc
    name = f"cm_row_{bits}_mux{mux_ratio}"
    row = ColumnMuxRow(bits=bits, mux_ratio=mux_ratio, name=name)
    lib = row.build()
    gds = tmp_path / f"{name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=name, output_dir=tmp_path)
    assert r.clean, (
        f"real={r.real_error_count} (waivers={r.waiver_error_count}): "
        f"{r.real_errors[:5]}"
    )
