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


class TestLefGeneration:
    """Tests for LEF abstract generation."""

    def test_lef_generation(self, tmp_path):
        """Verify LEF has correct SIZE and all pins present."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(words=256, bits=16, mux_ratio=4)
        params.macro_width = 200.0
        params.macro_height = 150.0

        out = tmp_path / "sram.lef"
        generate_lef(params, out)

        assert out.exists()
        text = out.read_text()

        # Check macro name and SIZE
        assert "MACRO sram_256x16_mux4" in text
        assert "SIZE 200.000 BY 150.000" in text
        assert "CLASS BLOCK" in text
        assert "SYMMETRY X Y" in text

        # Check UNITS
        assert "DATABASE MICRONS 1000" in text

        # Check all address pins (8 bits for 256 words)
        for i in range(8):
            assert f"PIN addr[{i}]" in text

        # Check data pins
        for i in range(16):
            assert f"PIN din[{i}]" in text
            assert f"PIN dout[{i}]" in text

        # Check control/power pins
        assert "PIN clk" in text
        assert "PIN we" in text
        assert "PIN VPWR" in text
        assert "PIN VGND" in text

        # Check directions
        assert "DIRECTION INPUT" in text
        assert "DIRECTION OUTPUT" in text
        assert "DIRECTION INOUT" in text

        # Check power use
        assert "USE POWER" in text
        assert "USE GROUND" in text

        # Check obstruction layers
        assert "OBS" in text
        assert "LAYER met1" in text
        assert "LAYER met2" in text
        assert "LAYER met3" in text

        # Check footer
        assert "END LIBRARY" in text

    def test_lef_pin_count(self, tmp_path):
        """Verify total pin count matches expected."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(words=64, bits=8, mux_ratio=2)
        params.macro_width = 100.0
        params.macro_height = 80.0

        out = tmp_path / "sram_small.lef"
        generate_lef(params, out)

        text = out.read_text()
        # 6 addr + 8 din + 8 dout + clk + we + cs + VPWR + VGND = 27 pins
        pin_count = text.count("  PIN ")
        expected = params.num_addr_bits + params.bits * 2 + 5  # addr + din + dout + clk,we,cs,VPWR,VGND
        assert pin_count == expected


class TestLibertyGeneration:
    """Tests for Liberty timing model generation."""

    def test_liberty_generation(self, tmp_path):
        """Verify .lib has correct cell name and pin definitions."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.liberty_generator import generate_liberty

        params = compute_macro_params(words=256, bits=16, mux_ratio=4)
        params.macro_width = 200.0
        params.macro_height = 150.0

        out = tmp_path / "sram.lib"
        generate_liberty(params, out)

        assert out.exists()
        text = out.read_text()

        # Check library and cell name
        assert "library (sram_256x16_mux4_lib)" in text
        assert "cell (sram_256x16_mux4)" in text

        # Check area
        area = 200.0 * 150.0
        assert f"area : {area:.3f}" in text

        # Check operating conditions
        assert "nom_voltage : 1.8" in text
        assert "nom_temperature : 25.0" in text
        assert "nom_process : 1.0" in text

        # Check clk pin is declared as clock
        assert "pin (clk)" in text
        assert "clock : true" in text

        # Check address pins
        for i in range(8):
            assert f"pin (addr[{i}])" in text

        # Check data pins
        for i in range(16):
            assert f"pin (din[{i}])" in text
            assert f"pin (dout[{i}])" in text

        # Check we pin
        assert "pin (we)" in text

        # Check power pins
        assert "pin (VPWR)" in text
        assert "pin (VGND)" in text

        # Check timing arcs exist
        assert "setup_rising" in text
        assert "hold_rising" in text
        assert "rising_edge" in text

        # Check that computed timing values are present and reasonable
        import re
        timing_values = re.findall(r'values \("([\d.]+)"\)', text)
        assert len(timing_values) > 0, "No timing values found"
        for v in timing_values:
            val = float(v)
            assert 0.01 < val < 100.0, f"Timing value {val} out of range"

    def test_liberty_pin_directions(self, tmp_path):
        """Verify pin directions are correct."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.liberty_generator import generate_liberty

        params = compute_macro_params(words=64, bits=8, mux_ratio=1)
        params.macro_width = 50.0
        params.macro_height = 40.0

        out = tmp_path / "sram_small.lib"
        generate_liberty(params, out)

        text = out.read_text()
        # dout pins should be output
        assert "direction : output" in text
        # addr/din/clk/we should be input
        assert "direction : input" in text
        # power pins should be inout
        assert "direction : inout" in text


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

        # Check module name (now includes mux ratio)
        assert "module sram_256x16_mux4" in text
        # Check port widths
        assert "[7:0]" in text  # addr_bits = 8, so [7:0]
        assert "[15:0]" in text  # data bits
        # Check memory declaration
        assert "mem [0:255]" in text
        # Check basic structure (lowercase pin names)
        assert "posedge clk" in text
        assert "we" in text
        assert "cs" in text
        assert "VPWR" in text
        assert "VGND" in text

    def test_spice_generation(self, tmp_path):
        """Verify SPICE model is generated."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_spice

        params = compute_macro_params(words=512, bits=32, mux_ratio=2)
        out = tmp_path / "sram.sp"
        generate_spice(params, out)

        assert out.exists()
        text = out.read_text()
        assert ".subckt sram_512x32_mux2" in text
        assert "addr[0]" in text
        assert "din[0]" in text
        assert "dout[0]" in text
        assert "VPWR" in text
        assert ".ends" in text
