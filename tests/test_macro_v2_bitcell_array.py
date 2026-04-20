import gdstk
import pytest

from rekolektion.macro_v2.bitcell_array import BitcellArray


def test_bitcell_array_dimensions_8x8():
    """An 8x8 array of foundry bitcells is 10.48 x 12.64 um."""
    arr = BitcellArray(rows=8, cols=8)
    assert arr.rows == 8
    assert arr.cols == 8
    assert abs(arr.width - 8 * 1.31) < 1e-6
    assert abs(arr.height - 8 * 1.58) < 1e-6


def test_bitcell_array_generates_gds():
    """Array generates a GDS with one top-level cell."""
    arr = BitcellArray(rows=4, cols=4)
    lib = arr.build()
    top = next((c for c in lib.cells if c.name == arr.top_cell_name), None)
    assert top is not None, f"top cell {arr.top_cell_name} not in lib"


def test_bitcell_array_uses_foundry_cell():
    """Array references the foundry opt1 bitcell."""
    arr = BitcellArray(rows=2, cols=2)
    lib = arr.build()
    cell_names = {c.name for c in lib.cells}
    assert any("opt1" in n for n in cell_names), (
        f"foundry cell not found in {cell_names}"
    )


def test_bitcell_array_has_NxM_references():
    """An RxC array should have R*C bitcell references in the top cell."""
    arr = BitcellArray(rows=3, cols=4)
    lib = arr.build()
    top = next(c for c in lib.cells if c.name == arr.top_cell_name)
    assert len(top.references) == 3 * 4
