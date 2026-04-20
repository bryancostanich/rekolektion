import gdstk
import pytest

from rekolektion.macro_v2.precharge_row import PrechargeRow


def test_precharge_row_at_mux4_has_N_cells():
    """At bits=8 mux=4: 8 precharge cells (one per mux group)."""
    row = PrechargeRow(bits=8, mux_ratio=4)
    lib = row.build()
    top = next(c for c in lib.cells if c.name == row.top_cell_name)
    refs = [r for r in top.references if "precharge_0" in r.cell.name]
    assert len(refs) == 8


def test_precharge_row_pitch_matches_mux_group():
    """Adjacent precharge cells at mux_group_pitch = mux_ratio * 1.31."""
    row = PrechargeRow(bits=4, mux_ratio=4)
    lib = row.build()
    top = next(c for c in lib.cells if c.name == row.top_cell_name)
    refs = sorted(
        [r for r in top.references if "precharge_0" in r.cell.name],
        key=lambda r: r.origin[0],
    )
    pitches = [refs[i + 1].origin[0] - refs[i].origin[0] for i in range(len(refs) - 1)]
    assert all(abs(p - 5.24) < 1e-6 for p in pitches), pitches


def test_precharge_row_rejects_mux_ratio_2():
    """precharge_0 (3.12 um) does not fit in mux=2 pitch (2.62 um)."""
    with pytest.raises(ValueError, match="does not fit"):
        PrechargeRow(bits=8, mux_ratio=2)


def test_precharge_row_rejects_unsupported_mux():
    with pytest.raises(ValueError, match="mux_ratio"):
        PrechargeRow(bits=4, mux_ratio=3)


def test_precharge_row_dimensions():
    row = PrechargeRow(bits=8, mux_ratio=4)
    # 8 * 5.24 um = 41.92 um wide, precharge_0 height = 3.98 um
    assert abs(row.width - 8 * 5.24) < 1e-6
    assert abs(row.height - 3.98) < 1e-6


@pytest.mark.magic
def test_precharge_row_8x_mux4_drc_clean(tmp_path):
    from rekolektion.verify.drc import run_drc
    row = PrechargeRow(bits=8, mux_ratio=4, name="pre_row_8_mux4")
    lib = row.build()
    gds = tmp_path / "pre_row_8_mux4.gds"
    lib.write_gds(str(gds))
    result = run_drc(gds, cell_name="pre_row_8_mux4", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"


@pytest.mark.magic
def test_precharge_row_mux8_drc_clean(tmp_path):
    from rekolektion.verify.drc import run_drc
    row = PrechargeRow(bits=4, mux_ratio=8, name="pre_row_4_mux8")
    lib = row.build()
    gds = tmp_path / "pre_row_4_mux8.gds"
    lib.write_gds(str(gds))
    result = run_drc(gds, cell_name="pre_row_4_mux8", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"
