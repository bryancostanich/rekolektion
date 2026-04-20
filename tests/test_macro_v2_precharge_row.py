import gdstk
import pytest

from rekolektion.macro_v2.precharge_row import PrechargeRow


def test_precharge_row_at_mux4_width_matches_all_pairs():
    """At bits=8 mux=4: one big cell covering 8 * 4 = 32 BL/BR pairs
    at bitcell pitch (1.31 um)."""
    row = PrechargeRow(bits=8, mux_ratio=4)
    assert row.num_pairs == 32
    assert abs(row.width - 32 * 1.31) < 1e-6


def test_precharge_row_cell_name_follows_convention():
    row = PrechargeRow(bits=8, mux_ratio=4, name="custom_name")
    assert row.top_cell_name == "custom_name"
    row_default = PrechargeRow(bits=8, mux_ratio=4)
    assert "precharge_row" in row_default.top_cell_name


def test_precharge_row_accepts_mux_ratio_2():
    """The new per-pair generator fits in 1.31 um pitch regardless of
    mux_ratio, so mux=2 is now supported (D4 Option B)."""
    row = PrechargeRow(bits=8, mux_ratio=2)
    assert row.num_pairs == 16
    assert abs(row.width - 16 * 1.31) < 1e-6


def test_precharge_row_rejects_unsupported_mux():
    with pytest.raises(ValueError, match="mux_ratio"):
        PrechargeRow(bits=4, mux_ratio=3)


def test_precharge_row_height_matches_generator():
    row = PrechargeRow(bits=8, mux_ratio=4)
    assert abs(row.height - 4.56) < 0.1


@pytest.mark.magic
@pytest.mark.parametrize("bits,mux_ratio", [
    (8, 2), (8, 4), (4, 8),
])
def test_precharge_row_drc_clean(tmp_path, bits, mux_ratio):
    from rekolektion.verify.drc import run_drc
    name = f"pre_row_{bits}_mux{mux_ratio}"
    row = PrechargeRow(bits=bits, mux_ratio=mux_ratio, name=name)
    lib = row.build()
    gds = tmp_path / f"{name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=name, output_dir=tmp_path)
    assert r.clean, (
        f"real={r.real_error_count} (waivers={r.waiver_error_count}): "
        f"{r.real_errors[:5]}"
    )
