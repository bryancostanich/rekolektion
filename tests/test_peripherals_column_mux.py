"""Standalone DRC + geometry tests for the column_mux cell generator."""
from pathlib import Path

import gdstk
import pytest

from rekolektion.peripherals.column_mux import generate_column_mux


@pytest.mark.parametrize(
    "num_pairs,mux_ratio", [
        (1, 2),
        (2, 2),
        (4, 2),
        (1, 4),
        (2, 4),
        (4, 4),
        (1, 8),
    ],
)
def test_column_mux_generates(tmp_path, num_pairs, mux_ratio):
    cell, _ = generate_column_mux(
        num_pairs=num_pairs, mux_ratio=mux_ratio, pair_pitch=1.31,
        cell_name=f"cm_{num_pairs}p_mux{mux_ratio}",
    )
    assert cell is not None


def test_column_mux_rejects_unsupported_mux():
    with pytest.raises(ValueError, match="mux_ratio"):
        generate_column_mux(num_pairs=1, mux_ratio=3, pair_pitch=1.31)


def test_column_mux_rejects_subminimum_pitch():
    with pytest.raises(ValueError, match="pair_pitch"):
        generate_column_mux(num_pairs=1, mux_ratio=2, pair_pitch=1.0)


def test_column_mux_emits_sel_and_out_labels():
    cell, _ = generate_column_mux(
        num_pairs=1, mux_ratio=4, pair_pitch=1.31, cell_name="cm4",
    )
    labels = [l.text for l in cell.labels]
    assert "BL[0]" in labels
    assert "BR[0]" in labels
    assert "BL_out[0]" in labels
    assert "BR_out[0]" in labels
    for k in range(4):
        assert f"sel[{k}]" in labels, f"missing sel[{k}]"
    assert "GND" in labels


def test_column_mux_height_scales_with_mux_ratio():
    """A mux=4 cell should be taller than a mux=2 cell (more stacked
    NMOS rows)."""
    cell2, _ = generate_column_mux(num_pairs=1, mux_ratio=2, pair_pitch=1.31,
                                    cell_name="cm_mux2")
    cell4, _ = generate_column_mux(num_pairs=1, mux_ratio=4, pair_pitch=1.31,
                                    cell_name="cm_mux4")
    h2 = cell2.bounding_box()[1][1] - cell2.bounding_box()[0][1]
    h4 = cell4.bounding_box()[1][1] - cell4.bounding_box()[0][1]
    assert h4 > h2


@pytest.mark.magic
@pytest.mark.parametrize(
    "num_pairs,mux_ratio", [(1, 2), (4, 2), (1, 4), (4, 4), (1, 8)],
)
def test_column_mux_drc_clean(tmp_path, num_pairs, mux_ratio):
    from rekolektion.verify.drc import run_drc
    name = f"cm_{num_pairs}p_mux{mux_ratio}"
    cell, lib = generate_column_mux(
        num_pairs=num_pairs, mux_ratio=mux_ratio, pair_pitch=1.31,
        cell_name=name,
    )
    gds = tmp_path / f"{name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=name, output_dir=tmp_path)
    assert r.clean, (
        f"real={r.real_error_count} (waivers={r.waiver_error_count}): "
        f"{r.real_errors[:5]}"
    )
