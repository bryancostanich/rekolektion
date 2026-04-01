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


# ---------------------------------------------------------------------------
# Phase 1 — Bit-level write enables
# ---------------------------------------------------------------------------

class TestWriteEnableGates:
    """Tests for write enable AND gate peripheral generator."""

    def test_basic_generation(self):
        from rekolektion.peripherals.write_enable_gate import generate_write_enable_gates
        cell, lib = generate_write_enable_gates(num_bits=32, ben_bits=4)
        assert cell is not None
        bb = cell.bounding_box()
        assert bb is not None
        w = bb[1][0] - bb[0][0]
        assert w > 0

    def test_8bit_single_ben(self):
        from rekolektion.peripherals.write_enable_gate import generate_write_enable_gates
        cell, lib = generate_write_enable_gates(num_bits=8, ben_bits=1)
        assert cell is not None

    def test_write_gds(self, tmp_path):
        from rekolektion.peripherals.write_enable_gate import generate_write_enable_gates
        out = tmp_path / "we_gates.gds"
        cell, lib = generate_write_enable_gates(
            num_bits=16, ben_bits=2, output_path=out,
        )
        assert out.exists()

    def test_invalid_params(self):
        from rekolektion.peripherals.write_enable_gate import generate_write_enable_gates
        with pytest.raises(ValueError):
            generate_write_enable_gates(num_bits=0, ben_bits=1)

    def test_macro_with_write_enable_gds(self, tmp_path):
        """Full macro generation with write_enable includes AND gates."""
        from rekolektion.macro.assembler import generate_sram_macro
        out = tmp_path / "macro_we.gds"
        lib, params = generate_sram_macro(
            words=64, bits=8, mux_ratio=2, output_path=out,
            write_enable=True,
        )
        assert out.exists()
        assert params.write_enable is True
        assert params.num_ben_bits == 1


class TestWriteEnableParams:
    """Tests for write_enable in MacroParams."""

    def test_ben_bits_32bit_word(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=True)
        assert p.write_enable is True
        assert p.num_ben_bits == 4  # 32 / 8 = 4 bytes

    def test_ben_bits_8bit_word(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=64, bits=8, mux_ratio=1, write_enable=True)
        assert p.num_ben_bits == 1  # 8 / 8 = 1 byte

    def test_ben_bits_64bit_word(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=128, bits=64, mux_ratio=2, write_enable=True)
        assert p.num_ben_bits == 8  # 64 / 8 = 8 bytes

    def test_ben_bits_disabled(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=64, bits=32, mux_ratio=1, write_enable=False)
        assert p.num_ben_bits == 0

    def test_ben_bits_sub_byte(self):
        """Words narrower than 8 bits get 1 BEN bit."""
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=64, bits=4, mux_ratio=1, write_enable=True)
        assert p.num_ben_bits == 1


class TestWriteEnableVerilog:
    """Tests for Verilog generation with write enables."""

    def test_verilog_ben_port(self, tmp_path):
        """Verify BEN port appears in Verilog when write_enable=True."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=True)
        out = tmp_path / "sram_we.v"
        generate_verilog(params, out)
        text = out.read_text()

        assert "ben" in text
        assert "[3:0]" in text  # 4 BEN bits
        assert "if (ben_reg[" in text  # byte-masked write logic (3-block pattern)

    def test_verilog_no_ben_when_disabled(self, tmp_path):
        """Verify BEN port absent when write_enable=False."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=False)
        out = tmp_path / "sram_nowe.v"
        generate_verilog(params, out)
        text = out.read_text()

        assert "ben" not in text

    def test_verilog_byte_mask_coverage(self, tmp_path):
        """Verify all 4 bytes are covered in 32-bit write enable."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=32, mux_ratio=1, write_enable=True)
        out = tmp_path / "sram_mask.v"
        generate_verilog(params, out)
        text = out.read_text()

        # All 4 byte lanes (3-block pattern uses addr_reg)
        assert "mem[addr_reg][7:0]" in text
        assert "mem[addr_reg][15:8]" in text
        assert "mem[addr_reg][23:16]" in text
        assert "mem[addr_reg][31:24]" in text

    def test_blackbox_ben_port(self, tmp_path):
        """Verify blackbox includes BEN port."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog_blackbox

        params = compute_macro_params(words=256, bits=16, mux_ratio=2, write_enable=True)
        out = tmp_path / "sram_bb.v"
        generate_verilog_blackbox(params, out)
        text = out.read_text()

        assert "ben" in text
        assert "[1:0]" in text  # 2 BEN bits for 16-bit word


class TestWriteEnableSpice:
    """Tests for SPICE generation with write enables."""

    def test_spice_ben_pins(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_spice

        params = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=True)
        out = tmp_path / "sram_we.sp"
        generate_spice(params, out)
        text = out.read_text()

        assert "ben[0]" in text
        assert "ben[3]" in text

    def test_spice_no_ben_when_disabled(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_spice

        params = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=False)
        out = tmp_path / "sram_nowe.sp"
        generate_spice(params, out)
        text = out.read_text()

        assert "ben" not in text


class TestWriteEnableLef:
    """Tests for LEF generation with write enables."""

    def test_lef_ben_pins(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=True)
        params.macro_width = 200.0
        params.macro_height = 150.0
        out = tmp_path / "sram_we.lef"
        generate_lef(params, out)
        text = out.read_text()

        for i in range(4):
            assert f"PIN ben[{i}]" in text
        assert text.count("PIN ben[") == 4

    def test_lef_no_ben_when_disabled(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=False)
        params.macro_width = 200.0
        params.macro_height = 150.0
        out = tmp_path / "sram_nowe.lef"
        generate_lef(params, out)
        text = out.read_text()

        assert "ben" not in text

    def test_lef_pin_count_with_ben(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(words=64, bits=8, mux_ratio=2, write_enable=True)
        params.macro_width = 100.0
        params.macro_height = 80.0
        out = tmp_path / "sram_we_small.lef"
        generate_lef(params, out)
        text = out.read_text()

        # 6 addr + 8 din + 8 dout + clk + we + cs + VPWR + VGND + 1 ben = 28
        pin_count = text.count("  PIN ")
        expected = params.num_addr_bits + params.bits * 2 + 5 + params.num_ben_bits
        assert pin_count == expected


class TestWriteEnableLiberty:
    """Tests for Liberty generation with write enables."""

    def test_liberty_ben_pins(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.liberty_generator import generate_liberty

        params = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=True)
        params.macro_width = 200.0
        params.macro_height = 150.0
        out = tmp_path / "sram_we.lib"
        generate_liberty(params, out)
        text = out.read_text()

        for i in range(4):
            assert f"pin (ben[{i}])" in text
        # BEN pins should have setup/hold timing
        assert text.count("pin (ben[") == 4

    def test_liberty_no_ben_when_disabled(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.liberty_generator import generate_liberty

        params = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=False)
        params.macro_width = 200.0
        params.macro_height = 150.0
        out = tmp_path / "sram_nowe.lib"
        generate_liberty(params, out)
        text = out.read_text()

        assert "ben" not in text


# ---------------------------------------------------------------------------
# Phase 2 — Scan chain DFT
# ---------------------------------------------------------------------------

class TestScanChainParams:
    """Tests for scan_chain in MacroParams."""

    def test_scan_flop_count_basic(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=256, bits=16, mux_ratio=4, scan_chain=True)
        # 8 addr + 1 we + 1 cs + 16 din = 26
        assert p.num_scan_flops == 26

    def test_scan_flop_count_with_ben(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=256, bits=32, mux_ratio=4, write_enable=True, scan_chain=True)
        # 8 addr + 1 we + 1 cs + 32 din + 4 ben = 46
        assert p.num_scan_flops == 46

    def test_scan_disabled(self):
        from rekolektion.macro.assembler import compute_macro_params
        p = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=False)
        assert p.num_scan_flops == 0


class TestScanChainVerilog:
    """Tests for Verilog generation with scan chain."""

    def test_verilog_scan_ports(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=True)
        out = tmp_path / "sram_scan.v"
        generate_verilog(params, out)
        text = out.read_text()

        assert "scan_in" in text
        assert "scan_out" in text
        assert "scan_en" in text
        assert "scan_chain" in text  # internal register
        assert "addr_int" in text    # muxed functional input

    def test_verilog_no_scan_when_disabled(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=False)
        out = tmp_path / "sram_noscan.v"
        generate_verilog(params, out)
        text = out.read_text()

        assert "scan_in" not in text
        assert "scan_out" not in text
        assert "scan_en" not in text

    def test_verilog_scan_chain_length(self, tmp_path):
        """Verify scan chain register width matches expected flop count."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=True)
        out = tmp_path / "sram_scan_len.v"
        generate_verilog(params, out)
        text = out.read_text()

        # 6 addr + 1 we + 1 cs + 8 din = 16 flops -> [15:0]
        assert f"[{params.num_scan_flops - 1}:0] scan_chain" in text

    def test_blackbox_scan_ports(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog_blackbox

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=True)
        out = tmp_path / "sram_scan_bb.v"
        generate_verilog_blackbox(params, out)
        text = out.read_text()

        assert "scan_in" in text
        assert "scan_out" in text
        assert "scan_en" in text

    def test_scan_with_write_enable(self, tmp_path):
        """Scan chain includes BEN bits when both features enabled."""
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(
            words=256, bits=32, mux_ratio=4,
            write_enable=True, scan_chain=True,
        )
        out = tmp_path / "sram_scan_we.v"
        generate_verilog(params, out)
        text = out.read_text()

        assert "scan_in" in text
        assert "ben_int" in text  # muxed BEN signal
        assert f"[{params.num_scan_flops - 1}:0] scan_chain" in text


class TestScanChainSpice:
    """Tests for SPICE generation with scan chain."""

    def test_spice_scan_pins(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_spice

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=True)
        out = tmp_path / "sram_scan.sp"
        generate_spice(params, out)
        text = out.read_text()

        assert "scan_in" in text
        assert "scan_out" in text
        assert "scan_en" in text

    def test_spice_no_scan_when_disabled(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_spice

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=False)
        out = tmp_path / "sram_noscan.sp"
        generate_spice(params, out)
        text = out.read_text()

        assert "scan_in" not in text


class TestScanChainLef:
    """Tests for LEF generation with scan chain."""

    def test_lef_scan_pins(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=True)
        params.macro_width = 100.0
        params.macro_height = 80.0
        out = tmp_path / "sram_scan.lef"
        generate_lef(params, out)
        text = out.read_text()

        assert "PIN scan_in" in text
        assert "PIN scan_out" in text
        assert "PIN scan_en" in text

    def test_lef_scan_pin_directions(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=True)
        params.macro_width = 100.0
        params.macro_height = 80.0
        out = tmp_path / "sram_scan_dir.lef"
        generate_lef(params, out)
        text = out.read_text()

        # scan_out should be OUTPUT
        lines = text.split("\n")
        for i, line in enumerate(lines):
            if "PIN scan_out" in line:
                assert "DIRECTION OUTPUT" in lines[i + 1]
                break


class TestScanChainLiberty:
    """Tests for Liberty generation with scan chain."""

    def test_liberty_scan_pins(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.liberty_generator import generate_liberty

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=True)
        params.macro_width = 100.0
        params.macro_height = 80.0
        out = tmp_path / "sram_scan.lib"
        generate_liberty(params, out)
        text = out.read_text()

        assert "pin (scan_in)" in text
        assert "pin (scan_out)" in text
        assert "pin (scan_en)" in text

    def test_liberty_scan_out_is_output(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.liberty_generator import generate_liberty

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=True)
        params.macro_width = 100.0
        params.macro_height = 80.0
        out = tmp_path / "sram_scan_dir.lib"
        generate_liberty(params, out)
        text = out.read_text()

        # scan_out should have direction : output and timing arcs
        assert "pin (scan_out)" in text
        # It uses _output_pin_with_timing which includes rising_edge
        idx = text.index("pin (scan_out)")
        section = text[idx:idx+300]
        assert "direction : output" in section

    def test_liberty_no_scan_when_disabled(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.liberty_generator import generate_liberty

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, scan_chain=False)
        params.macro_width = 100.0
        params.macro_height = 80.0
        out = tmp_path / "sram_noscan.lib"
        generate_liberty(params, out)
        text = out.read_text()

        assert "scan_in" not in text


# ---------------------------------------------------------------------------
# Phases 3-6 — Clock gating, power gating, WL switchoff, burn-in
# ---------------------------------------------------------------------------

class TestFeatureFlags:
    """Tests for Phase 3-6 feature flags across all output generators."""

    FEATURES = [
        ("clock_gating", "cen"),
        ("power_gating", "sleep"),
        ("wl_switchoff", "wl_off"),
        ("burn_in", "tm"),
    ]

    @pytest.mark.parametrize("feature,pin", FEATURES)
    def test_verilog_pin_present(self, tmp_path, feature, pin):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, **{feature: True})
        out = tmp_path / f"sram_{feature}.v"
        generate_verilog(params, out)
        text = out.read_text()
        assert pin in text

    @pytest.mark.parametrize("feature,pin", FEATURES)
    def test_verilog_pin_absent_when_disabled(self, tmp_path, feature, pin):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, **{feature: False})
        out = tmp_path / f"sram_no{feature}.v"
        generate_verilog(params, out)
        text = out.read_text()
        assert pin not in text

    @pytest.mark.parametrize("feature,pin", FEATURES)
    def test_blackbox_pin_present(self, tmp_path, feature, pin):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog_blackbox

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, **{feature: True})
        out = tmp_path / f"sram_{feature}_bb.v"
        generate_verilog_blackbox(params, out)
        text = out.read_text()
        assert pin in text

    @pytest.mark.parametrize("feature,pin", FEATURES)
    def test_spice_pin_present(self, tmp_path, feature, pin):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_spice

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, **{feature: True})
        out = tmp_path / f"sram_{feature}.sp"
        generate_spice(params, out)
        text = out.read_text()
        assert pin in text

    @pytest.mark.parametrize("feature,pin", FEATURES)
    def test_lef_pin_present(self, tmp_path, feature, pin):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, **{feature: True})
        params.macro_width = 100.0
        params.macro_height = 80.0
        out = tmp_path / f"sram_{feature}.lef"
        generate_lef(params, out)
        text = out.read_text()
        assert f"PIN {pin}" in text

    @pytest.mark.parametrize("feature,pin", FEATURES)
    def test_liberty_pin_present(self, tmp_path, feature, pin):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.liberty_generator import generate_liberty

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, **{feature: True})
        params.macro_width = 100.0
        params.macro_height = 80.0
        out = tmp_path / f"sram_{feature}.lib"
        generate_liberty(params, out)
        text = out.read_text()
        assert f"pin ({pin})" in text


class TestClockGatingBehavior:
    """Tests for clock gating behavioral logic."""

    def test_icg_logic_in_verilog(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, clock_gating=True)
        out = tmp_path / "sram_cg.v"
        generate_verilog(params, out)
        text = out.read_text()

        assert "clk_gated" in text
        assert "cen_latched" in text


class TestPowerGatingBehavior:
    """Tests for power gating behavioral logic."""

    def test_sleep_forces_x(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, power_gating=True)
        out = tmp_path / "sram_pg.v"
        generate_verilog(params, out)
        text = out.read_text()

        assert "sleep" in text
        assert "!sleep" in text


class TestWlSwitchoffBehavior:
    """Tests for WL switchoff behavioral logic."""

    def test_wl_off_gates_access(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(words=64, bits=8, mux_ratio=1, wl_switchoff=True)
        out = tmp_path / "sram_wl.v"
        generate_verilog(params, out)
        text = out.read_text()

        assert "wl_off" in text
        assert "!wl_off" in text


class TestAllFeaturesComposed:
    """Test that all features can be enabled simultaneously."""

    def test_all_features_verilog(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_verilog

        params = compute_macro_params(
            words=256, bits=32, mux_ratio=4,
            write_enable=True, scan_chain=True,
            clock_gating=True, power_gating=True,
            wl_switchoff=True, burn_in=True,
        )
        out = tmp_path / "sram_all.v"
        generate_verilog(params, out)
        text = out.read_text()

        for pin in ["ben", "scan_in", "scan_out", "scan_en", "cen", "sleep", "wl_off", "tm"]:
            assert pin in text, f"Missing pin {pin} in all-features Verilog"

    def test_all_features_lef(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.lef_generator import generate_lef

        params = compute_macro_params(
            words=256, bits=32, mux_ratio=4,
            write_enable=True, scan_chain=True,
            clock_gating=True, power_gating=True,
            wl_switchoff=True, burn_in=True,
        )
        params.macro_width = 200.0
        params.macro_height = 150.0
        out = tmp_path / "sram_all.lef"
        generate_lef(params, out)
        text = out.read_text()

        for pin in ["ben[0]", "scan_in", "scan_out", "scan_en", "cen", "sleep", "wl_off", "tm"]:
            assert f"PIN {pin}" in text, f"Missing PIN {pin} in all-features LEF"

    def test_all_features_spice(self, tmp_path):
        from rekolektion.macro.assembler import compute_macro_params
        from rekolektion.macro.outputs import generate_spice

        params = compute_macro_params(
            words=256, bits=32, mux_ratio=4,
            write_enable=True, scan_chain=True,
            clock_gating=True, power_gating=True,
            wl_switchoff=True, burn_in=True,
        )
        out = tmp_path / "sram_all.sp"
        generate_spice(params, out)
        text = out.read_text()

        for pin in ["ben[0]", "scan_in", "scan_out", "scan_en", "cen", "sleep", "wl_off", "tm"]:
            assert pin in text, f"Missing pin {pin} in all-features SPICE"
