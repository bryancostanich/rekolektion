import gdstk
import pytest

from rekolektion.macro_v2.assembler import (
    MacroV2Params,
    assemble,
    build_floorplan,
)


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


def test_assemble_returns_library_with_top_cell():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    names = {c.name for c in lib.cells}
    assert p.top_cell_name in names


def test_assemble_top_cell_references_every_block():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    ref_names = {r.cell.name for r in top.references}
    for needle in (
        "sram_array", "pre", "mux", "sa", "wd",
        "row_decoder", "ctrl_logic",
    ):
        assert any(needle in n for n in ref_names), (
            f"expected a reference with '{needle}' in its cell name; got {ref_names}"
        )


def test_assemble_block_references_at_floorplan_positions():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    fp = build_floorplan(p)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)

    def ref_with(substring: str) -> gdstk.Reference:
        return next(r for r in top.references if substring in r.cell.name)

    # Array origin at floorplan position
    ax, ay = fp.positions["array"]
    array_ref = ref_with("sram_array")
    assert abs(array_ref.origin[0] - ax) < 0.01
    assert abs(array_ref.origin[1] - ay) < 0.01


@pytest.mark.magic
def test_assemble_tiny_macro_drc_clean(tmp_path):
    from rekolektion.verify.drc import run_drc
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    gds = tmp_path / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=p.top_cell_name, output_dir=tmp_path)
    assert r.clean, (
        f"real DRC errors ({r.real_error_count}): {r.real_errors[:5]}"
    )


# ---------------------------------------------------------------------------
# C6.2 — WL fanout from decoder to array
# ---------------------------------------------------------------------------

def test_wl_routing_adds_one_met1_wire_per_row():
    """Each row gets a top-level met1 wire between decoder column and array."""
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    # Top-level met1 polygons between the decoder right edge and the
    # array left edge live in x < 0 (array is at x=0).
    # Filter to just the horizontal routing wires (aspect ratio > 2).
    wl_wires = []
    for poly in top.polygons:
        if (poly.layer, poly.datatype) != (68, 20):
            continue
        bb = poly.bounding_box()
        if bb is None:
            continue
        # Must lie in the decoder-to-array channel (x < 0)
        if bb[1][0] > 0.0:
            continue
        w = bb[1][0] - bb[0][0]
        h = bb[1][1] - bb[0][1]
        if w > 2 * h and w > 1.0:   # horizontal, long
            wl_wires.append(poly)
    assert len(wl_wires) >= p.rows, (
        f"expected >= {p.rows} WL fanout wires in decoder-array channel; "
        f"got {len(wl_wires)}"
    )


def test_wl_routing_met1_to_poly_via_stacks_at_array_edge():
    """Each WL wire ends with a met1->poly via stack landing on the array's
    WL poly strip.

    We don't try to measure the exact position; instead count poly.pin
    polygons at the array's left edge (one per row)."""
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    # Via stacks drop poly + mcon via + met1 landing pads. Count the
    # met1 landing pads directly at the array's left edge (x ~ 0).
    array_x = 0.0
    via_pads = []
    for poly in top.polygons:
        if (poly.layer, poly.datatype) != (68, 20):
            continue
        bb = poly.bounding_box()
        if bb is None:
            continue
        w = bb[1][0] - bb[0][0]
        h = bb[1][1] - bb[0][1]
        # A via landing pad is square-ish and small (<0.5 um each side).
        if 0.1 < w < 0.5 and 0.1 < h < 0.5 and abs(w - h) < 0.1:
            # And close to array left edge
            if -2.0 < (bb[0][0] + bb[1][0]) / 2 < array_x + 0.5:
                via_pads.append(poly)
    assert len(via_pads) >= p.rows, (
        f"expected >= {p.rows} via-stack landing pads at array edge; "
        f"got {len(via_pads)}"
    )


@pytest.mark.magic
def test_assemble_tiny_macro_with_wl_routing_drc_clean(tmp_path):
    """Same DRC check as above but guards against the WL routing
    introducing new real errors (all existing waivers still apply)."""
    from rekolektion.verify.drc import run_drc
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    gds = tmp_path / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=p.top_cell_name, output_dir=tmp_path)
    assert r.clean, (
        f"real DRC errors ({r.real_error_count}): {r.real_errors[:5]}"
    )


# ---------------------------------------------------------------------------
# C6.3 — BL/BR fanout: array strip extension into peripheral rows
# ---------------------------------------------------------------------------

def test_bl_extends_strips_above_array_to_precharge():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    fp = build_floorplan(p)
    array_top_y = fp.positions["array"][1] + fp.sizes["array"][1]
    prec_top_y = fp.positions["precharge"][1] + fp.sizes["precharge"][1]
    # Count top-level met1 polygons whose bbox spans the array-top to
    # precharge-top channel (vertical strips).
    count = 0
    for poly in top.polygons:
        if (poly.layer, poly.datatype) != (68, 20):
            continue
        bb = poly.bounding_box()
        if bb is None:
            continue
        if (bb[0][1] <= array_top_y + 0.05
                and bb[1][1] >= prec_top_y - 0.05
                and (bb[1][0] - bb[0][0]) < 0.3):
            count += 1
    # 2 strips (BL + BR) per column, 32 cols -> 64 strips minimum
    assert count >= 2 * p.cols, (
        f"expected >= {2 * p.cols} BL/BR up-extension strips; got {count}"
    )


def test_bl_extends_strips_below_array_through_peripherals():
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    fp = build_floorplan(p)
    array_bot_y = fp.positions["array"][1]
    wd_bot_y = fp.positions["write_driver"][1]
    count = 0
    for poly in top.polygons:
        if (poly.layer, poly.datatype) != (68, 20):
            continue
        bb = poly.bounding_box()
        if bb is None:
            continue
        if (bb[0][1] <= wd_bot_y + 0.05
                and bb[1][1] >= array_bot_y - 0.05
                and (bb[1][0] - bb[0][0]) < 0.3):
            count += 1
    assert count >= 2 * p.cols, (
        f"expected >= {2 * p.cols} BL/BR down-extension strips; got {count}"
    )


@pytest.mark.magic
def test_assemble_tiny_macro_with_bl_routing_drc_clean(tmp_path):
    from rekolektion.verify.drc import run_drc
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    gds = tmp_path / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=p.top_cell_name, output_dir=tmp_path)
    assert r.clean, (
        f"real DRC errors ({r.real_error_count}): {r.real_errors[:5]}"
    )


# ---------------------------------------------------------------------------
# C6.4 — control signal fanout
# ---------------------------------------------------------------------------

def test_control_routing_adds_met2_rails():
    """Three long horizontal met2 rails (p_en_bar, s_en, w_en) crossing
    the macro at peripheral y-positions."""
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    fp = build_floorplan(p)
    array_w = fp.sizes["array"][0]
    long_met2_wires = []
    for poly in top.polygons:
        if (poly.layer, poly.datatype) != (69, 20):  # met2 drawing
            continue
        bb = poly.bounding_box()
        if bb is None:
            continue
        w = bb[1][0] - bb[0][0]
        h = bb[1][1] - bb[0][1]
        # Horizontal rail: wide (~array_w) and thin
        if w > array_w * 0.8 and h < 0.3:
            long_met2_wires.append(poly)
    assert len(long_met2_wires) >= 3, (
        f"expected >= 3 control rails; got {len(long_met2_wires)}"
    )


@pytest.mark.magic
def test_assemble_tiny_macro_with_control_routing_drc_clean(tmp_path):
    from rekolektion.verify.drc import run_drc
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    gds = tmp_path / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=p.top_cell_name, output_dir=tmp_path)
    assert r.clean, (
        f"real DRC errors ({r.real_error_count}): {r.real_errors[:5]}"
    )


# ---------------------------------------------------------------------------
# C6.5 — Top-level pins + power grid
# ---------------------------------------------------------------------------

def test_top_level_has_pin_labels_for_every_signal():
    """Signal pins carry top-level GDS labels so Magic extraction
    identifies the nets. Power pins (VPWR/VGND) are declared only in
    the LEF as met2 edge stubs; the GDS has met2 rails but no label."""
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    pin_labels = {lbl.text for lbl in top.labels}
    required = {"clk", "we", "cs"}
    for i in range(p.num_addr_bits):
        required.add(f"addr[{i}]")
    for i in range(p.bits):
        required.add(f"din[{i}]")
        required.add(f"dout[{i}]")
    missing = required - pin_labels
    assert not missing, f"missing top-level pin labels: {missing}"


def test_top_level_has_met2_power_rails():
    """The macro's PDN is two full-width met2 rails — VPWR at the top,
    VGND at the bottom — with LEF pin stubs straddling each edge."""
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    top = next(c for c in lib.cells if c.name == p.top_cell_name)
    # Count met2 (69, 20) polygons that span most of the macro width.
    fp = build_floorplan(p)
    macro_w_est = fp.macro_size[0]
    long_met2 = []
    for poly in top.polygons:
        if (poly.layer, poly.datatype) != (69, 20):
            continue
        bb = poly.bounding_box()
        if bb is None:
            continue
        w = bb[1][0] - bb[0][0]
        h = bb[1][1] - bb[0][1]
        if w > 0.8 * macro_w_est and h < 1.0:
            long_met2.append(poly)
    assert len(long_met2) >= 2, (
        f"expected >=2 full-width met2 power rails; got {len(long_met2)}"
    )


@pytest.mark.magic
def test_assemble_tiny_macro_full_pipeline_drc_clean(tmp_path):
    """Full assembler stack (C6.0-C6.5) DRC-clean for sram_test_tiny."""
    from rekolektion.verify.drc import run_drc
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lib, _ = assemble(p)
    gds = tmp_path / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))
    r = run_drc(gds, cell_name=p.top_cell_name, output_dir=tmp_path)
    assert r.clean, (
        f"real DRC errors ({r.real_error_count}): {r.real_errors[:5]}"
    )


# ---------------------------------------------------------------------------
# C6.7 — End-to-end DRC + LVS on sram_test_tiny (exit gate for C6)
# ---------------------------------------------------------------------------

@pytest.mark.magic
def test_sram_test_tiny_end_to_end_drc_clean(tmp_path):
    """Exit gate for C6: sram_test_tiny (32 words x 8 bits x mux4) must
    be DRC-clean as a complete assembled macro."""
    from rekolektion.verify.drc import run_drc
    from rekolektion.macro_v2.spice_generator import generate_reference_spice
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)

    # Assemble GDS
    lib, _ = assemble(p)
    gds = tmp_path / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))

    # Generate reference SPICE (structural only per D3)
    sp = tmp_path / f"{p.top_cell_name}.sp"
    generate_reference_spice(p, sp)
    assert sp.exists() and sp.stat().st_size > 0

    # DRC: must be clean (real=0)
    r = run_drc(gds, cell_name=p.top_cell_name, output_dir=tmp_path)
    assert r.clean, (
        f"C6 EXIT GATE FAILED: real DRC errors ({r.real_error_count}). "
        f"Top rules: {r.real_errors[:5]}"
    )
    # Report waiver count for visibility
    print(
        f"\nsram_test_tiny: DRC real={r.real_error_count}, "
        f"waivers={r.waiver_error_count}"
    )
