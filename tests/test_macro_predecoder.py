import gdstk
import pytest

from rekolektion.macro.predecoder import Predecoder


def test_predecoder_2_to_4():
    pd = Predecoder(num_inputs=2)
    assert pd.num_outputs == 4


def test_predecoder_3_to_8():
    pd = Predecoder(num_inputs=3)
    assert pd.num_outputs == 8


def test_predecoder_rejects_invalid_width():
    for bad in (1, 4, 5, 0):
        with pytest.raises(ValueError):
            Predecoder(num_inputs=bad)


def test_predecoder_2_to_4_has_4_nand_refs():
    pd = Predecoder(num_inputs=2)
    lib = pd.build()
    top = next(c for c in lib.cells if c.name == pd.top_cell_name)
    nand_refs = [r for r in top.references if "nand" in r.cell.name]
    assert len(nand_refs) == 4


def test_predecoder_3_to_8_has_8_nand3_refs():
    pd = Predecoder(num_inputs=3)
    lib = pd.build()
    top = next(c for c in lib.cells if c.name == pd.top_cell_name)
    nand3_refs = [r for r in top.references if "nand3_dec" in r.cell.name]
    assert len(nand3_refs) == 8


@pytest.mark.magic
@pytest.mark.parametrize("k", [2, 3])
def test_predecoder_drc_clean(tmp_path, k):
    from rekolektion.verify.drc import run_drc
    pd = Predecoder(num_inputs=k, name=f"pd_{k}")
    lib = pd.build()
    gds = tmp_path / f"pd_{k}.gds"
    lib.write_gds(str(gds))
    result = run_drc(gds, cell_name=f"pd_{k}", output_dir=tmp_path)
    assert result.clean, f"k={k}: {result.errors}"
