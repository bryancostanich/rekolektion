import gdstk
import pytest

from rekolektion.macro_v2.sense_amp_row import SenseAmpRow


def test_sense_amp_row_has_bits_cells():
    row = SenseAmpRow(bits=8, mux_ratio=4)
    lib = row.build()
    top = next(c for c in lib.cells if c.name == row.top_cell_name)
    refs = [r for r in top.references if "sense_amp" in r.cell.name]
    assert len(refs) == 8


def test_sense_amp_row_pitch_matches_mux_group():
    row = SenseAmpRow(bits=4, mux_ratio=4)
    lib = row.build()
    top = next(c for c in lib.cells if c.name == row.top_cell_name)
    refs = sorted(
        [r for r in top.references if "sense_amp" in r.cell.name],
        key=lambda r: r.origin[0],
    )
    pitches = [refs[i + 1].origin[0] - refs[i].origin[0] for i in range(len(refs) - 1)]
    assert all(abs(p - 5.24) < 1e-6 for p in pitches), pitches


def test_sense_amp_row_supports_all_muxes():
    """SA fits at mux 2, 4, 8. mux=1 is rejected by spec decision 5."""
    for mux in (2, 4, 8):
        row = SenseAmpRow(bits=4, mux_ratio=mux)
        assert row.pitch == mux * 1.31


def test_sense_amp_row_dimensions():
    row = SenseAmpRow(bits=8, mux_ratio=4)
    assert abs(row.width - 8 * 5.24) < 1e-6
    assert abs(row.height - 11.28) < 1e-6


@pytest.mark.magic
@pytest.mark.parametrize("mux", [2, 4, 8])
def test_sense_amp_row_drc_clean_all_muxes(tmp_path, mux):
    from rekolektion.verify.drc import run_drc
    row = SenseAmpRow(bits=4, mux_ratio=mux, name=f"sa_{mux}")
    lib = row.build()
    gds = tmp_path / f"sa_{mux}.gds"
    lib.write_gds(str(gds))
    result = run_drc(gds, cell_name=f"sa_{mux}", output_dir=tmp_path)
    assert result.clean, f"mux={mux} DRC errors: {result.errors}"
