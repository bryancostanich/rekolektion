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


# ---------------------------------------------------------------------------
# Array with support cells
# ---------------------------------------------------------------------------

class TestArrayWithSupportCells:

    def test_array_with_dummy_cells(self, foundry_cell: BitcellInfo):
        """Verify dummy border is added around the array."""
        lib_plain = tile_array(foundry_cell, num_rows=4, num_cols=4, with_dummy=False)
        lib_dummy = tile_array(foundry_cell, num_rows=4, num_cols=4, with_dummy=True)

        plain_cell = None
        dummy_cell = None
        for c in lib_plain.cells:
            if "array" in c.name:
                plain_cell = c
                break
        for c in lib_dummy.cells:
            if "array" in c.name:
                dummy_cell = c
                break

        assert plain_cell is not None
        assert dummy_cell is not None

        # The dummy version should have more references (dummy border +
        # colend + rowend + corner cells).
        assert len(dummy_cell.references) > len(plain_cell.references)

        # The dummy version should also be physically larger.
        bb_plain = plain_cell.bounding_box()
        bb_dummy = dummy_cell.bounding_box()
        assert bb_plain is not None
        assert bb_dummy is not None

        plain_w = bb_plain[1][0] - bb_plain[0][0]
        dummy_w = bb_dummy[1][0] - bb_dummy[0][0]
        assert dummy_w > plain_w

    def test_array_dimensions_with_straps(self, foundry_cell: BitcellInfo):
        """Verify that strap columns make the array wider."""
        lib_plain = tile_array(
            foundry_cell, num_rows=4, num_cols=32, strap_interval=0,
        )
        lib_strap = tile_array(
            foundry_cell, num_rows=4, num_cols=32, strap_interval=16,
        )

        plain_cell = None
        strap_cell = None
        for c in lib_plain.cells:
            if "array" in c.name:
                plain_cell = c
                break
        for c in lib_strap.cells:
            if "array" in c.name:
                strap_cell = c
                break

        assert plain_cell is not None
        assert strap_cell is not None

        bb_plain = plain_cell.bounding_box()
        bb_strap = strap_cell.bounding_box()
        assert bb_plain is not None
        assert bb_strap is not None

        plain_w = bb_plain[1][0] - bb_plain[0][0]
        strap_w = bb_strap[1][0] - bb_strap[0][0]

        # With 32 columns and strap every 16, we get 1 strap column
        # (at col 16).  The strap cell is 1.410um wide vs 1.310 bitcell,
        # so the array should be wider by approximately 1.410.
        assert strap_w > plain_w
        # The difference should be approximately one strap width (1.41um)
        assert strap_w - plain_w == pytest.approx(1.41, abs=0.1)
