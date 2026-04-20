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
    """Top cell has one poly.label + poly.pin + poly strip per row."""
    arr = BitcellArray(rows=4, cols=4)
    lib = arr.build()
    top = next(c for c in lib.cells if c.name == arr.top_cell_name)
    wl_labels = [
        l for l in top.labels
        if (l.layer, l.texttype) == (66, 5)
        and l.text.startswith("wl_0_")
    ]
    assert len(wl_labels) == 4
    names = {l.text for l in wl_labels}
    assert names == {"wl_0_0", "wl_0_1", "wl_0_2", "wl_0_3"}

    # poly.pin rects (66/16) — one per row
    poly_pins = [p for p in top.polygons if (p.layer, p.datatype) == (66, 16)]
    assert len(poly_pins) == 4


def test_wl_labels_y_within_row_bounds():
    """Each WL label y is inside its row's vertical span."""
    arr = BitcellArray(rows=4, cols=4)
    lib = arr.build()
    top = next(c for c in lib.cells if c.name == arr.top_cell_name)
    wl_labels = [l for l in top.labels if l.text.startswith("wl_0_")]
    cell_h = 1.58
    for lbl in wl_labels:
        row = int(lbl.text.split("_")[-1])
        y_base = row * cell_h
        y = lbl.origin[1]
        assert y_base <= y <= y_base + cell_h, (
            f"{lbl.text} y={y} outside row bounds [{y_base}, {y_base + cell_h}]"
        )


def test_bl_br_labels_one_per_col():
    """One bl_0_<col>/br_0_<col> label + pin per column."""
    arr = BitcellArray(rows=4, cols=4)
    lib = arr.build()
    top = next(c for c in lib.cells if c.name == arr.top_cell_name)
    bl_labels = [l for l in top.labels if l.text.startswith("bl_0_")]
    br_labels = [l for l in top.labels if l.text.startswith("br_0_")]
    assert len(bl_labels) == 4
    assert len(br_labels) == 4
    assert {l.text for l in bl_labels} == {f"bl_0_{c}" for c in range(4)}
    assert {l.text for l in br_labels} == {f"br_0_{c}" for c in range(4)}

    # met1.pin rects (68/16) — 2 per column (BL + BR) = 8
    met1_pins = [p for p in top.polygons if (p.layer, p.datatype) == (68, 16)]
    assert len(met1_pins) == 4 * 2


def test_bl_br_labels_x_within_col_bounds():
    """Each BL/BR label x is inside its column's horizontal span."""
    arr = BitcellArray(rows=4, cols=4)
    lib = arr.build()
    top = next(c for c in lib.cells if c.name == arr.top_cell_name)
    cell_w = 1.31
    for lbl in top.labels:
        if not (lbl.text.startswith("bl_0_") or lbl.text.startswith("br_0_")):
            continue
        col = int(lbl.text.split("_")[-1])
        x_base = col * cell_w
        assert x_base <= lbl.origin[0] <= x_base + cell_w, (
            f"{lbl.text} x={lbl.origin[0]} outside col bounds "
            f"[{x_base}, {x_base + cell_w}]"
        )


@pytest.mark.magic
def test_bitcell_array_4x4_drc_clean(tmp_path):
    """4x4 foundry bitcell array passes Magic DRC."""
    from rekolektion.verify.drc import run_drc

    arr = BitcellArray(rows=4, cols=4, name="sram_test_4x4")
    lib = arr.build()
    gds = tmp_path / "sram_test_4x4.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="sram_test_4x4", output_dir=tmp_path)
    assert result.clean, f"DRC errors: {result.errors}"


@pytest.mark.magic
def test_bitcell_array_extracts_unique_wl_and_bl_nets(tmp_path):
    """Phase 0's WL-merge problem is fixed in v2: each row/col extracts separately.

    This validates decision 7 end-to-end: per-row/col labels on the bitcell pin
    layer produce distinct net names at extraction time.
    """
    from rekolektion.verify.lvs import extract_netlist

    arr = BitcellArray(rows=4, cols=4, name="sram_test_extract")
    lib = arr.build()
    gds = tmp_path / "sram_test_extract.gds"
    lib.write_gds(str(gds))

    sp = extract_netlist(gds, cell_name="sram_test_extract", output_dir=tmp_path)
    txt = sp.read_text()

    # The .subckt header lists all external ports. Concatenate continuation
    # lines (start with '+') into one string.
    subckt_lines: list[str] = []
    in_header = False
    for line in txt.splitlines():
        if line.startswith(".subckt sram_test_extract"):
            in_header = True
            subckt_lines.append(line)
            continue
        if in_header:
            if line.startswith("+") or line.startswith("*"):
                subckt_lines.append(line)
                continue
            break
    ports_text = " ".join(subckt_lines)

    # Expect each of the 4 row WLs and 4×2 BL/BR nets to appear as ports.
    for row in range(4):
        assert f"wl_0_{row}" in ports_text, (
            f"wl_0_{row} missing from subckt ports:\n{ports_text}"
        )
    for col in range(4):
        assert f"bl_0_{col}" in ports_text, (
            f"bl_0_{col} missing from ports:\n{ports_text}"
        )
        assert f"br_0_{col}" in ports_text, (
            f"br_0_{col} missing from ports:\n{ports_text}"
        )
