"""Standalone DRC + geometry tests for the precharge cell generator."""
from pathlib import Path

import gdstk
import pytest

from rekolektion.peripherals.precharge import generate_precharge


@pytest.mark.parametrize(
    "num_pairs,pair_pitch", [
        (1, 1.31),
        (2, 1.31),
        (4, 1.31),
        (8, 1.31),
        (1, 1.925),
        (1, 2.62),
    ],
)
def test_precharge_generates(tmp_path, num_pairs, pair_pitch):
    cell, lib = generate_precharge(
        num_pairs=num_pairs, pair_pitch=pair_pitch,
        cell_name=f"pc_{num_pairs}_{int(pair_pitch*1000)}",
    )
    bb = cell.bounding_box()
    w = bb[1][0] - bb[0][0]
    # Cell width matches num_pairs * pair_pitch within the nwell-
    # enclosure extent (nwell extends the bbox by ~0.36 um beyond the
    # drawn boundary rectangle).
    assert w >= num_pairs * pair_pitch - 0.01


def test_precharge_rejects_subminimum_pitch():
    with pytest.raises(ValueError, match="pair_pitch"):
        generate_precharge(num_pairs=1, pair_pitch=1.0)


def test_precharge_emits_BL_BR_labels():
    cell, _ = generate_precharge(num_pairs=2, pair_pitch=1.31, cell_name="pc2")
    labels = [l.text for l in cell.labels]
    assert "BL[0]" in labels
    assert "BR[0]" in labels
    assert "BL[1]" in labels
    assert "BR[1]" in labels
    assert "VDD" in labels
    assert "precharge_en" in labels


def test_precharge_bitline_x_matches_bitcell_convention():
    """BL / BR met1 stubs must land at x=0.0425 / 1.1575 per pair so
    the peripheral row taps the bitcell_array's spanning strips."""
    cell, _ = generate_precharge(num_pairs=1, pair_pitch=1.31, cell_name="pc1")
    label_x = {l.text: l.origin[0] for l in cell.labels}
    assert abs(label_x["BL[0]"] - 0.0425) < 0.01
    assert abs(label_x["BR[0]"] - 1.1575) < 0.01


@pytest.mark.magic
@pytest.mark.parametrize(
    "num_pairs,pair_pitch", [(1, 1.31), (4, 1.31), (8, 1.31)]
)
def test_precharge_drc_clean(tmp_path, num_pairs, pair_pitch):
    from rekolektion.verify.drc import run_drc
    name = f"pc_{num_pairs}_{int(pair_pitch*1000)}"
    cell, lib = generate_precharge(
        num_pairs=num_pairs, pair_pitch=pair_pitch, cell_name=name,
    )
    gds = tmp_path / f"{name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=name, output_dir=tmp_path)
    assert r.clean, (
        f"real={r.real_error_count} (waivers={r.waiver_error_count}): "
        f"{r.real_errors[:5]}"
    )
