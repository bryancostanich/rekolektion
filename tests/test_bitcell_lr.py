"""Tests for the 6T SRAM bitcell generator — left/right topology."""

import tempfile
from pathlib import Path

import pytest

from rekolektion.bitcell.sky130_6t_lr import (
    CELL_HEIGHT,
    CELL_WIDTH,
    create_bitcell,
    generate_bitcell,
)


def test_create_bitcell_returns_cell():
    """Verify create_bitcell returns a gdstk Cell with geometry."""
    cell = create_bitcell()
    assert cell.name == "sky130_sram_6t_bitcell_lr"
    assert len(cell.polygons) > 0, "Cell should contain polygons"
    assert len(cell.labels) > 0, "Cell should contain labels"


def test_cell_area():
    """Cell area should be reasonable for LR topology."""
    area = CELL_WIDTH * CELL_HEIGHT
    # LR topology should be more compact than TB topology (6.89 um^2)
    # but still DRC-clean with standard rules
    assert area <= 10.0, f"Cell area {area:.3f} um^2 exceeds limit"
    print(f"LR Cell area: {area:.3f} um^2 ({CELL_WIDTH:.3f} x {CELL_HEIGHT:.3f} um)")


def test_cell_has_required_labels():
    """Cell must have labels for all ports: VDD, VSS, BL, BLB, WL."""
    cell = create_bitcell()
    label_texts = {lbl.text for lbl in cell.labels}
    required = {"VDD", "VSS", "BL", "BLB", "WL"}
    missing = required - label_texts
    assert not missing, f"Missing port labels: {missing}"


def test_cell_bounding_box():
    """Verify cell fits within declared dimensions."""
    cell = create_bitcell()
    bbox = cell.bounding_box()
    assert bbox is not None, "Cell has no geometry"
    width = bbox[1][0] - bbox[0][0]
    height = bbox[1][1] - bbox[0][1]
    # Allow margin for nwell extension past cell boundary
    assert width < CELL_WIDTH + 1.0, f"Cell width {width:.3f} um exceeds expected"
    assert height < CELL_HEIGHT + 1.0, f"Cell height {height:.3f} um exceeds expected"


def test_generate_gds_file():
    """Verify GDS file is generated successfully."""
    with tempfile.TemporaryDirectory() as tmpdir:
        out = Path(tmpdir) / "test_bitcell_lr.gds"
        result = generate_bitcell(output_path=str(out))
        assert out.exists(), "GDS file not created"
        assert out.stat().st_size > 0, "GDS file is empty"


def test_generate_spice_netlist():
    """Verify SPICE netlist is generated when requested."""
    with tempfile.TemporaryDirectory() as tmpdir:
        gds_out = Path(tmpdir) / "test_bitcell_lr.gds"
        generate_bitcell(output_path=str(gds_out), generate_spice=True)
        spice_out = gds_out.with_suffix(".spice")
        assert spice_out.exists(), "SPICE file not created"

        content = spice_out.read_text()
        assert ".subckt sky130_sram_6t_bitcell_lr" in content
        assert "BL BLB WL VDD VSS" in content
        assert "sky130_fd_pr__nfet_01v8" in content
        assert "sky130_fd_pr__pfet_01v8" in content
        # 6 transistors total
        assert content.count("XPD_") == 2
        assert content.count("XPU_") == 2
        assert content.count("XPG_") == 2


def test_lr_vs_tb_area_comparison():
    """Compare LR topology area against TB topology."""
    from rekolektion.bitcell.sky130_6t import CELL_WIDTH as TB_W, CELL_HEIGHT as TB_H
    tb_area = TB_W * TB_H
    lr_area = CELL_WIDTH * CELL_HEIGHT
    print(f"TB topology: {TB_W:.3f} x {TB_H:.3f} = {tb_area:.3f} um^2")
    print(f"LR topology: {CELL_WIDTH:.3f} x {CELL_HEIGHT:.3f} = {lr_area:.3f} um^2")
    print(f"Ratio LR/TB: {lr_area/tb_area:.2f}")
