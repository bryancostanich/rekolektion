import gdstk
import pytest

from rekolektion.macro_v2.column_mux_row import ColumnMuxRow


def test_column_mux_row_has_bits_cells():
    row = ColumnMuxRow(bits=8, mux_ratio=4)
    lib = row.build()
    top = next(c for c in lib.cells if c.name == row.top_cell_name)
    refs = [r for r in top.references if "column_mux" in r.cell.name]
    assert len(refs) == 8


def test_column_mux_row_pitch_matches_mux_group():
    row = ColumnMuxRow(bits=4, mux_ratio=4)
    lib = row.build()
    top = next(c for c in lib.cells if c.name == row.top_cell_name)
    refs = sorted(
        [r for r in top.references if "column_mux" in r.cell.name],
        key=lambda r: r.origin[0],
    )
    pitches = [refs[i + 1].origin[0] - refs[i].origin[0] for i in range(len(refs) - 1)]
    assert all(abs(p - 5.24) < 1e-6 for p in pitches), pitches


def test_column_mux_row_rejects_mux_ratio_2():
    with pytest.raises(ValueError, match="does not fit"):
        ColumnMuxRow(bits=8, mux_ratio=2)


def test_column_mux_row_dimensions():
    row = ColumnMuxRow(bits=8, mux_ratio=4)
    assert abs(row.width - 8 * 5.24) < 1e-6
    assert abs(row.height - 6.82) < 1e-6


@pytest.mark.magic
def test_column_mux_row_mux4_drc_clean(tmp_path):
    from rekolektion.verify.drc import run_drc
    row = ColumnMuxRow(bits=8, mux_ratio=4, name="cm_row_8_mux4")
    lib = row.build()
    gds = tmp_path / "cm_row_8_mux4.gds"
    lib.write_gds(str(gds))
    result = run_drc(gds, cell_name="cm_row_8_mux4", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"


@pytest.mark.magic
def test_column_mux_row_mux8_drc_clean(tmp_path):
    from rekolektion.verify.drc import run_drc
    row = ColumnMuxRow(bits=4, mux_ratio=8, name="cm_row_4_mux8")
    lib = row.build()
    gds = tmp_path / "cm_row_4_mux8.gds"
    lib.write_gds(str(gds))
    result = run_drc(gds, cell_name="cm_row_4_mux8", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"
