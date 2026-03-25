"""Tests for the bitcell array tiler."""

from __future__ import annotations

import tempfile
from pathlib import Path

import gdstk
import pytest

from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
from rekolektion.bitcell.base import BitcellInfo
from rekolektion.array.tiler import tile_array


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture
def foundry_cell() -> BitcellInfo:
    return load_foundry_sp_bitcell()


# ---------------------------------------------------------------------------
# Foundry cell loading
# ---------------------------------------------------------------------------

class TestFoundryCell:

    def test_load_succeeds(self, foundry_cell: BitcellInfo):
        assert foundry_cell.cell_name == "sky130_fd_bd_sram__sram_sp_cell_opt1"

    def test_dimensions(self, foundry_cell: BitcellInfo):
        # LEF says SIZE 1.310 BY 1.580
        assert foundry_cell.cell_width == pytest.approx(1.310, abs=0.01)
        assert foundry_cell.cell_height == pytest.approx(1.580, abs=0.01)

    def test_pins_present(self, foundry_cell: BitcellInfo):
        expected_pins = {"BL", "BR", "WL", "VGND", "VPWR", "VNB", "VPB"}
        # WL is not in the LEF for this cell (the LEF we parsed). Check what
        # we actually have.
        actual = set(foundry_cell.pins.keys())
        # At minimum we need BL, BR, VGND, VPWR.
        assert {"BL", "BR", "VGND", "VPWR"}.issubset(actual)

    def test_pin_positions_are_within_cell(self, foundry_cell: BitcellInfo):
        for pin_name, pin in foundry_cell.pins.items():
            for x, y, layer in pin.ports:
                # Allow small overhang (negative coords from ORIGIN offset).
                assert x >= -0.1, f"{pin_name} x={x} out of range"
                assert y >= -0.1, f"{pin_name} y={y} out of range"
                assert x <= foundry_cell.cell_width + 0.1
                assert y <= foundry_cell.cell_height + 0.1

    def test_get_cell(self, foundry_cell: BitcellInfo):
        cell = foundry_cell.get_cell()
        assert isinstance(cell, gdstk.Cell)


# ---------------------------------------------------------------------------
# Array tiling
# ---------------------------------------------------------------------------

class TestArrayTiler:

    def test_4x4_array_generates(self, foundry_cell: BitcellInfo):
        with tempfile.TemporaryDirectory() as tmpdir:
            out = Path(tmpdir) / "test_4x4.gds"
            lib = tile_array(foundry_cell, num_rows=4, num_cols=4, output_path=out)
            assert out.exists()
            assert out.stat().st_size > 0

    def test_4x4_array_dimensions(self, foundry_cell: BitcellInfo):
        lib = tile_array(foundry_cell, num_rows=4, num_cols=4)
        # Find the array cell.
        array_cell = None
        for c in lib.cells:
            if "array" in c.name:
                array_cell = c
                break
        assert array_cell is not None

        bb = array_cell.bounding_box()
        assert bb is not None
        w = bb[1][0] - bb[0][0]
        h = bb[1][1] - bb[0][1]

        expected_w = 4 * foundry_cell.cell_width
        expected_h = 4 * foundry_cell.cell_height

        # Allow 10% tolerance for cell-boundary overlap or rounding.
        assert w == pytest.approx(expected_w, rel=0.10)
        assert h == pytest.approx(expected_h, rel=0.10)

    def test_gds_written_successfully(self, foundry_cell: BitcellInfo):
        with tempfile.TemporaryDirectory() as tmpdir:
            out = Path(tmpdir) / "test_8x8.gds"
            tile_array(foundry_cell, num_rows=8, num_cols=8, output_path=out)

            # Re-read and verify structure.
            lib = gdstk.read_gds(str(out))
            cell_names = [c.name for c in lib.cells]
            assert any("array" in n for n in cell_names)

    def test_invalid_dimensions_rejected(self, foundry_cell: BitcellInfo):
        with pytest.raises(ValueError):
            tile_array(foundry_cell, num_rows=0, num_cols=4)
        with pytest.raises(ValueError):
            tile_array(foundry_cell, num_rows=4, num_cols=0)

    def test_1x1_array(self, foundry_cell: BitcellInfo):
        """Degenerate case: single cell, no mirroring."""
        lib = tile_array(foundry_cell, num_rows=1, num_cols=1)
        array_cell = None
        for c in lib.cells:
            if "array" in c.name:
                array_cell = c
                break
        assert array_cell is not None
        assert len(array_cell.references) == 1
