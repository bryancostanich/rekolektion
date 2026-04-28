import gdstk
import pytest

from rekolektion.macro.row_decoder import RowDecoder


def test_row_decoder_32_rows_uses_2_predecoders():
    """32 rows (5 bits) → split 2+3 → 2 predecoders, 32 NAND2 final-stage gates."""
    dec = RowDecoder(num_rows=32)
    lib = dec.build()
    top = next(c for c in lib.cells if c.name == dec.top_cell_name)
    preds = [r for r in top.references if "predecoder" in r.cell.name]
    nand2s = [r for r in top.references if "nand2_dec" in r.cell.name]
    assert len(preds) == 2
    assert len(nand2s) == 32


def test_row_decoder_128_rows_uses_3_predecoders_and_nand3():
    """128 rows (7 bits) → split 2+2+3 → 3 predecoders, 128 NAND3 final-stage gates."""
    dec = RowDecoder(num_rows=128)
    lib = dec.build()
    top = next(c for c in lib.cells if c.name == dec.top_cell_name)
    preds = [r for r in top.references if "predecoder" in r.cell.name]
    nand3s = [r for r in top.references if "nand3_dec" in r.cell.name]
    assert len(preds) == 3
    assert len(nand3s) == 128


def test_row_decoder_64_rows_uses_2_predecoders_and_nand2():
    """64 rows (6 bits) → split 3+3 → 2 predecoders, 64 NAND2 final-stage gates."""
    dec = RowDecoder(num_rows=64)
    lib = dec.build()
    top = next(c for c in lib.cells if c.name == dec.top_cell_name)
    preds = [r for r in top.references if "predecoder" in r.cell.name]
    nand2s = [r for r in top.references if "nand2_dec" in r.cell.name]
    assert len(preds) == 2
    assert len(nand2s) == 64


def test_row_decoder_num_addr_bits():
    assert RowDecoder(num_rows=32).num_addr_bits == 5
    assert RowDecoder(num_rows=128).num_addr_bits == 7
    assert RowDecoder(num_rows=1024).num_addr_bits == 10


def test_row_decoder_rejects_non_power_of_2():
    with pytest.raises(ValueError):
        RowDecoder(num_rows=100)


def test_row_decoder_rejects_unsupported_size():
    with pytest.raises(ValueError):
        RowDecoder(num_rows=2048)


@pytest.mark.magic
@pytest.mark.parametrize("num_rows", [32, 128])
def test_row_decoder_drc_clean(tmp_path, num_rows):
    from rekolektion.verify.drc import run_drc
    dec = RowDecoder(num_rows=num_rows, name=f"dec{num_rows}")
    lib = dec.build()
    gds = tmp_path / f"dec{num_rows}.gds"
    lib.write_gds(str(gds))
    result = run_drc(gds, cell_name=f"dec{num_rows}", output_dir=tmp_path)
    assert result.clean, f"N={num_rows}: {result.errors}"
