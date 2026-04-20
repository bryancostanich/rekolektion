import pytest

from rekolektion.macro_v2.assembler import MacroV2Params, build_floorplan


def test_params_rejects_mux_1():
    """Mux=1 can't pitch-match the foundry sense amp (2.5µm > 1.31µm bitcell)."""
    with pytest.raises(ValueError, match="mux"):
        MacroV2Params(words=32, bits=8, mux_ratio=1)


def test_params_rejects_non_power_of_2_mux():
    with pytest.raises(ValueError, match="mux"):
        MacroV2Params(words=32, bits=8, mux_ratio=3)


def test_params_rejects_words_not_divisible_by_mux():
    with pytest.raises(ValueError, match="mux"):
        MacroV2Params(words=30, bits=8, mux_ratio=4)


def test_params_rejects_unsupported_row_count():
    # 128 words / mux=4 = 32 rows → in _SPLIT_TABLE ✓
    # 64 words / mux=4 = 16 rows → in _SPLIT_TABLE ✓
    # 24 words / mux=4 = 6 rows → NOT in _SPLIT_TABLE
    with pytest.raises(ValueError, match="rows"):
        MacroV2Params(words=24, bits=8, mux_ratio=4)


def test_params_computes_rows_cols_and_addr_bits():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    assert p.rows == 8
    assert p.cols == 32
    assert p.num_addr_bits == 5   # log2(32)


def test_params_names():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    assert p.top_cell_name == "sram_32x8_mux4"


def test_floorplan_returns_positions_for_every_block():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    fp = build_floorplan(p)
    required = {
        "array", "precharge", "col_mux", "sense_amp",
        "write_driver", "row_decoder", "control_logic",
    }
    assert required.issubset(fp.positions.keys())
    # Every positioned block has a matching size entry
    for name in fp.positions:
        assert name in fp.sizes


def test_floorplan_array_at_origin():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    fp = build_floorplan(p)
    assert fp.positions["array"] == (0.0, 0.0)


def test_floorplan_decoder_left_of_array_same_y():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    fp = build_floorplan(p)
    ax, ay = fp.positions["array"]
    dx, dy = fp.positions["row_decoder"]
    assert dx < ax, "decoder must sit left of array"
    assert abs(dy - ay) < 0.01, "decoder y must align with array y=0"


def test_floorplan_precharge_above_array():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    fp = build_floorplan(p)
    _, ay = fp.positions["array"]
    ah = fp.sizes["array"][1]
    _, py = fp.positions["precharge"]
    assert py > ay + ah, "precharge must sit above the array"


def test_floorplan_sense_amp_below_array():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    fp = build_floorplan(p)
    _, ay = fp.positions["array"]
    _, sy = fp.positions["sense_amp"]
    assert sy < ay, "sense amp must sit below the array"


def test_floorplan_macro_size_is_bounding_box():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    fp = build_floorplan(p)
    mw, mh = fp.macro_size
    assert mw > 0 and mh > 0
    # Array alone is 32 * 1.31 = 41.92 wide; macro must be wider (has decoder)
    assert mw > 41.92
