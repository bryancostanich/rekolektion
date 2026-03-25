"""Tests for SRAM macro generation (Phases 3 & 4)."""

from __future__ import annotations

import math
from pathlib import Path

import pytest
import gdstk


# ---------------------------------------------------------------------------
# Phase 3 — Peripheral cells
# ---------------------------------------------------------------------------

class TestPeripheralCells:
    """Tests for foundry peripheral cell loading."""

    def test_load_sense_amp(self):
        from rekolektion.peripherals.foundry_cells import get_peripheral_cell
        sa = get_peripheral_cell("sense_amp")
        assert sa.width > 0
        assert sa.height > 0
        assert sa.gds_path.exists()
        # Sense amp should have BL, BR, DOUT, EN pins
        assert "BL" in sa.pins
        assert "BR" in sa.pins
        assert "DOUT" in sa.pins
        assert "EN" in sa.pins

    def test_load_write_driver(self):
        from rekolektion.peripherals.foundry_cells import get_peripheral_cell
        wd = get_peripheral_cell("write_driver")
        assert wd.width > 0
        assert wd.height > 0
        assert "BL" in wd.pins
        assert "BR" in wd.pins
        assert "DIN" in wd.pins
        assert "EN" in wd.pins

    def test_load_nand_gates(self):
        from rekolektion.peripherals.foundry_cells import get_peripheral_cell
        for name in ("nand2_dec", "nand3_dec", "nand4_dec"):
            cell = get_peripheral_cell(name)
            assert cell.width > 0
            assert cell.height > 0
            assert "Z" in cell.pins, f"{name} missing Z pin"

    def test_load_dff(self):
        from rekolektion.peripherals.foundry_cells import get_peripheral_cell
        dff = get_peripheral_cell("dff")
        assert dff.width > 0
        assert dff.height > 0
        assert "CLK" in dff.pins
        assert "D" in dff.pins
        assert "Q" in dff.pins

    def test_list_peripheral_cells(self):
        from rekolektion.peripherals.foundry_cells import list_peripheral_cells
        names = list_peripheral_cells()
        assert "sense_amp" in names
        assert "write_driver" in names
        assert "nand2_dec" in names

    def test_unknown_cell_raises(self):
        from rekolektion.peripherals.foundry_cells import get_peripheral_cell
        with pytest.raises(KeyError):
            get_peripheral_cell("nonexistent_cell")


class TestColumnMux:
    """Tests for column mux placeholder generator."""

    def test_mux_2_1(self):
        from rekolektion.peripherals.column_mux import generate_column_mux
        cell, lib = generate_column_mux(num_cols=8, mux_ratio=2)
        assert cell is not None
        bb = cell.bounding_box()
        assert bb is not None
        w = bb[1][0] - bb[0][0]
        assert w > 0

    def test_mux_4_1(self):
        from rekolektion.peripherals.column_mux import generate_column_mux
        cell, lib = generate_column_mux(num_cols=16, mux_ratio=4)
        bb = cell.bounding_box()
        assert bb is not None

    def test_mux_1_1_passthrough(self):
        from rekolektion.peripherals.column_mux import generate_column_mux
        cell, lib = generate_column_mux(num_cols=8, mux_ratio=1)
        assert cell is not None

    def test_invalid_ratio(self):
        from rekolektion.peripherals.column_mux import generate_column_mux
        with pytest.raises(ValueError):
            generate_column_mux(num_cols=8, mux_ratio=3)

    def test_cols_not_divisible(self):
        from rekolektion.peripherals.column_mux import generate_column_mux
        with pytest.raises(ValueError):
            generate_column_mux(num_cols=7, mux_ratio=4)

    def test_write_gds(self, tmp_path):
        from rekolektion.peripherals.column_mux import generate_column_mux
        out = tmp_path / "mux.gds"
        cell, lib = generate_column_mux(
            num_cols=8, mux_ratio=2, output_path=out,
        )
        assert out.exists()


class TestPrecharge:
    """Tests for precharge placeholder generator."""

    def test_basic(self):
        from rekolektion.peripherals.precharge import generate_precharge
        cell, lib = generate_precharge(num_cols=8)
        assert cell is not None
        bb = cell.bounding_box()
        assert bb is not None

    def test_write_gds(self, tmp_path):
        from rekolektion.peripherals.precharge import generate_precharge
        out = tmp_path / "pre.gds"
        cell, lib = generate_precharge(num_cols=8, output_path=out)
        assert out.exists()


# ---------------------------------------------------------------------------
# Phase 4 — Macro assembly
# ---------------------------------------------------------------------------

class TestMacroParams:
    """Tests for macro parameter computation."""

    def test_basic_params(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=1024, bits=32, mux_ratio=8)
        assert p.rows == 128
        assert p.cols == 256
        assert p.num_addr_bits == 10
        assert p.num_row_bits == 7
        assert p.num_col_bits == 3

    def test_no_mux(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=64, bits=8, mux_ratio=1)
        assert p.rows == 64
        assert p.cols == 8
        assert p.num_col_bits == 0

    def test_invalid_mux(self):
        from rekolektion.macro.assembler import compute_macro_params
        with pytest.raises(ValueError):
            compute_macro_params(words=64, bits=8, mux_ratio=3)


class TestMacroGeneration:
    """Tests for full macro generation."""

    def test_macro_generation(self, tmp_path):
        """Generate a small macro (64 words x 8 bits, mux 2) and verify GDS."""
        from rekolektion.macro.assembler import generate_sram_macro

        out = tmp_path / "test_macro.gds"
        lib, params = generate_sram_macro(
            words=64, bits=8, mux_ratio=2, output_path=out,
        )

        assert out.exists()
        assert out.stat().st_size > 0

        # Verify we can read the GDS back
        read_lib = gdstk.read_gds(str(out))
        assert len(read_lib.cells) > 0

        # Top cell should exist
        top_names = [c.name for c in read_lib.cells]
        assert "sram_64x8_mux2" in top_names

    def test_macro_dimensions(self, tmp_path):
        """Verify assembled macro has reasonable dimensions."""
        from rekolektion.macro.assembler import generate_sram_macro

        out = tmp_path / "dim_macro.gds"
        lib, params = generate_sram_macro(
            words=64, bits=8, mux_ratio=2, output_path=out,
        )

        assert params.macro_width > 0
        assert params.macro_height > 0
        # Width should be at least cols * cell_width
        min_width = params.cols * params.cell_width
        assert params.macro_width >= min_width * 0.8  # allow some tolerance

    def test_macro_no_mux(self, tmp_path):
        """Generate macro without column mux."""
        from rekolektion.macro.assembler import generate_sram_macro

        out = tmp_path / "nomux_macro.gds"
        lib, params = generate_sram_macro(
            words=32, bits=8, mux_ratio=1, output_path=out,
        )
        assert out.exists()
        assert params.rows == 32
        assert params.cols == 8


class TestVerilogGeneration:
    """Tests for Verilog model output."""

    def test_verilog_generation(self, tmp_path):
        """Verify Verilog model is generated with correct parameters."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=256, bits=16, mux_ratio=4)
        out = tmp_path / "sram.v"
        generate_verilog(params, out)

        assert out.exists()
        text = out.read_text()

        # Check module name
        assert "module sram_256x16" in text
        # Check port widths
        assert "[7:0]" in text  # addr_bits = 8, so [7:0]
        assert "[15:0]" in text  # data bits
        # Check memory declaration
        assert "mem [0:255]" in text
        # Check basic structure
        assert "posedge CLK" in text
        assert "WE" in text
        assert "CS" in text

    def test_spice_generation(self, tmp_path):
        """Verify SPICE model is generated."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_spice

        params = compute_macro_params(words=512, bits=32, mux_ratio=2)
        out = tmp_path / "sram.sp"
        generate_spice(params, out)

        assert out.exists()
        text = out.read_text()
        assert ".subckt sram_512x32" in text
        assert "A[0]" in text
        assert "DIN[0]" in text
        assert "DOUT[0]" in text
        assert ".ends" in text
