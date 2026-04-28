"""C7 / C9 regression tests — production macros via v2 pipeline."""
from pathlib import Path

import pytest

from rekolektion.macro.assembler import MacroParams, assemble
from rekolektion.macro.lef_generator import generate_lef
from rekolektion.macro.spice_generator import generate_reference_spice


PRODUCTION = [
    ("sram_weight_bank_small", 512, 32, 4, 128, 128),
    ("sram_activation_bank",   256, 64, 4,  64, 256),
]


@pytest.mark.parametrize(
    "macro,words,bits,mux,rows,cols", PRODUCTION
)
def test_production_params_match_expected_shape(
    macro, words, bits, mux, rows, cols
):
    p = MacroParams(words=words, bits=bits, mux_ratio=mux)
    assert p.rows == rows
    assert p.cols == cols


@pytest.mark.parametrize(
    "macro,words,bits,mux,rows,cols", PRODUCTION
)
def test_production_macros_assemble(
    tmp_path, macro, words, bits, mux, rows, cols
):
    p = MacroParams(words=words, bits=bits, mux_ratio=mux)
    lib = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    ref_names = {r.cell.name for r in top.references}
    assert any("sram_array" in n for n in ref_names)
    gds = tmp_path / f"{macro}.gds"
    lib.write_gds(str(gds))
    assert gds.stat().st_size > 1000


@pytest.mark.parametrize(
    "macro,words,bits,mux,rows,cols", PRODUCTION
)
def test_production_spice_emitted(
    tmp_path, macro, words, bits, mux, rows, cols
):
    p = MacroParams(words=words, bits=bits, mux_ratio=mux)
    sp = generate_reference_spice(p, tmp_path / f"{macro}.sp")
    text = sp.read_text()
    assert f".subckt sram_array_{rows}x{cols}" in text
    assert f".subckt precharge_row_{bits}_mux{mux}" in text


@pytest.mark.parametrize(
    "macro,words,bits,mux,rows,cols", PRODUCTION
)
def test_production_lef_emitted(
    tmp_path, macro, words, bits, mux, rows, cols
):
    p = MacroParams(words=words, bits=bits, mux_ratio=mux)
    lef = generate_lef(p, tmp_path / f"{macro}.lef", macro_name=macro)
    text = lef.read_text()
    assert f"MACRO {macro}" in text
    # Spot-check pin counts
    addr_bits = p.num_addr_bits
    for i in range(addr_bits):
        assert f"PIN addr[{i}]" in text
    for i in range(bits):
        assert f"PIN din[{i}]" in text
        assert f"PIN dout[{i}]" in text
