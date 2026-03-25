"""Tests for the 6T SRAM bitcell generator."""

import tempfile
from pathlib import Path

import pytest

from rekolektion.bitcell.sky130_6t import (
    CELL_HEIGHT,
    CELL_WIDTH,
    create_bitcell,
    generate_bitcell,
)


def test_create_bitcell_returns_cell():
    """Verify create_bitcell returns a gdstk Cell with geometry."""
    cell = create_bitcell()
    assert cell.name == "sky130_sram_6t_bitcell"
    # Should have rectangles (polygons) on multiple layers
    assert len(cell.polygons) > 0, "Cell should contain polygons"
    assert len(cell.labels) > 0, "Cell should contain labels"


def test_cell_area_target():
    """Cell area should be within the go/no-go threshold."""
    area = CELL_WIDTH * CELL_HEIGHT
    # With proper DRC-compliant spacing (0.27μm diff extension, 0.52μm N-P gap,
    # separate poly gates with contact pads), realistic area is ~3.0-3.5 μm².
    # Still well under OpenRAM's ~4.0 μm² for 6T on SKY130.
    # DRC-compliant cell with separate poly pads is ~5 μm² standalone.
    # When tiled (shared margins), effective area is ~4 μm².
    assert area <= 5.5, f"Cell area {area:.3f} um^2 exceeds target"
    print(f"Cell area: {area:.3f} um^2 ({CELL_WIDTH:.3f} x {CELL_HEIGHT:.3f} um)")


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
    # Allow some margin for nwell extension past cell boundary
    assert width < CELL_WIDTH + 0.5, f"Cell width {width:.3f} μm exceeds expected"
    assert height < CELL_HEIGHT + 0.5, f"Cell height {height:.3f} μm exceeds expected"


def test_generate_gds_file():
    """Verify GDS file is generated successfully."""
    with tempfile.TemporaryDirectory() as tmpdir:
        out = Path(tmpdir) / "test_bitcell.gds"
        result = generate_bitcell(output_path=str(out))
        assert out.exists(), "GDS file not created"
        assert out.stat().st_size > 0, "GDS file is empty"


def test_generate_spice_netlist():
    """Verify SPICE netlist is generated when requested."""
    with tempfile.TemporaryDirectory() as tmpdir:
        gds_out = Path(tmpdir) / "test_bitcell.gds"
        generate_bitcell(output_path=str(gds_out), generate_spice=True)
        spice_out = gds_out.with_suffix(".spice")
        assert spice_out.exists(), "SPICE file not created"

        content = spice_out.read_text()
        assert ".subckt sky130_sram_6t_bitcell" in content
        assert "BL BLB WL VDD VSS" in content
        assert "sky130_fd_pr__nfet_01v8" in content
        assert "sky130_fd_pr__pfet_01v8" in content
        # 6 transistors total
        assert content.count("XPD_") == 2  # Pull-down L and R
        assert content.count("XPU_") == 2  # Pull-up L and R
        assert content.count("XPG_") == 2  # Pass gate L and R


def test_density_estimate():
    """Verify density estimate is in reasonable range."""
    area = CELL_WIDTH * CELL_HEIGHT
    density = 1.0 / area * 1e6  # bits/mm² (cell only, no peripheral overhead)
    # Cell-only density should be well above 100K bits/mm²
    # With peripherals the macro density will be lower
    assert density > 200_000, f"Cell-only density {density:.0f} bits/mm² is too low"
    print(f"Cell-only density: {density:,.0f} bits/mm²")


def test_custom_transistor_sizing():
    """Verify cell can be generated with custom transistor widths."""
    cell = create_bitcell(pd_w=0.50, pg_w=0.36, pu_w=0.55)
    assert cell.name == "sky130_sram_6t_bitcell"
    assert len(cell.polygons) > 0
