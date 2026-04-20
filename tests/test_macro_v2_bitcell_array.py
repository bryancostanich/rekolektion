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


def test_wl_labels_one_per_row():
    """After build(), top cell has a met1.label for each row."""
    arr = BitcellArray(rows=4, cols=4)
    lib = arr.build()
    top = next(c for c in lib.cells if c.name == arr.top_cell_name)
    wl_labels = [
        l for l in top.labels
        if (l.layer, l.texttype) == (68, 5)
        and l.text.startswith("wl_0_")
    ]
    assert len(wl_labels) == 4
    names = {l.text for l in wl_labels}
    assert names == {"wl_0_0", "wl_0_1", "wl_0_2", "wl_0_3"}


def test_wl_labels_y_within_row_bounds():
    """Each row's WL label y is inside the row's vertical span."""
    arr = BitcellArray(rows=4, cols=4)
    lib = arr.build()
    top = next(c for c in lib.cells if c.name == arr.top_cell_name)
    wl_labels = sorted(
        [l for l in top.labels if l.text.startswith("wl_0_")],
        key=lambda l: int(l.text.split("_")[-1])
    )
    cell_h = 1.58
    for i, lbl in enumerate(wl_labels):
        y_base = i * cell_h
        y = lbl.origin[1]
        assert y_base <= y <= y_base + cell_h, (
            f"wl_0_{i} y={y} outside row bounds [{y_base}, {y_base + cell_h}]"
        )
