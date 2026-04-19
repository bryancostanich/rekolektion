from rekolektion.macro_v2 import sky130_drc as drc


def test_metal_min_widths():
    assert drc.MET1_MIN_WIDTH == 0.14
    assert drc.MET2_MIN_WIDTH == 0.14
    assert drc.MET3_MIN_WIDTH == 0.30
    assert drc.MET4_MIN_WIDTH == 0.30
    assert drc.MET5_MIN_WIDTH == 1.60


def test_metal_min_spacings():
    assert drc.MET1_MIN_SPACE == 0.14
    assert drc.MET2_MIN_SPACE == 0.14
    assert drc.MET3_MIN_SPACE == 0.30
    assert drc.MET4_MIN_SPACE == 0.30
    assert drc.MET5_MIN_SPACE == 1.60


def test_mfg_grid():
    assert drc.MFG_GRID == 0.005  # 5 nm


def test_gds_layer_map_has_all_metals():
    assert drc.GDS_LAYER["met1"] == (68, 20)
    assert drc.GDS_LAYER["met2"] == (69, 20)
    assert drc.GDS_LAYER["met3"] == (70, 20)
    assert drc.GDS_LAYER["met4"] == (71, 20)
    assert drc.GDS_LAYER["met5"] == (72, 20)
    assert drc.GDS_LAYER["li1"] == (67, 20)


def test_gds_layer_map_has_via_layers():
    assert drc.GDS_LAYER["mcon"] == (67, 44)
    assert drc.GDS_LAYER["via"] == (68, 44)
    assert drc.GDS_LAYER["via2"] == (69, 44)
    assert drc.GDS_LAYER["via3"] == (70, 44)
    assert drc.GDS_LAYER["via4"] == (71, 44)


def test_gds_layer_map_has_pin_and_label():
    assert drc.GDS_LAYER["met1.pin"] == (68, 16)
    assert drc.GDS_LAYER["met1.label"] == (68, 5)
    assert drc.GDS_LAYER["met2.pin"] == (69, 16)
    assert drc.GDS_LAYER["met2.label"] == (69, 5)
    assert drc.GDS_LAYER["met3.pin"] == (70, 16)
    assert drc.GDS_LAYER["met3.label"] == (70, 5)
    assert drc.GDS_LAYER["met4.pin"] == (71, 16)
    assert drc.GDS_LAYER["met4.label"] == (71, 5)


def test_layer_min_width_helper():
    assert drc.layer_min_width("met1") == 0.14
    assert drc.layer_min_width("met5") == 1.60


def test_layer_min_space_helper():
    assert drc.layer_min_space("met3") == 0.30


def test_snap_to_mfg_grid():
    assert drc.snap(0.137) == 0.135  # rounds down to nearest 0.005
    assert drc.snap(0.138) == 0.14   # rounds up
    assert drc.snap(0.0) == 0.0
