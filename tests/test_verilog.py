"""Tests for generated Verilog models of V1 SRAM macros.

Reads the Verilog files produced by generate_v1_macros.py and verifies:
- Module name matches expected pattern
- Correct number of address bits (log2(words))
- Correct data width
- Read/write logic is present
"""

from __future__ import annotations

import math
import re
from pathlib import Path

import pytest

MACRO_DIR = Path(__file__).resolve().parent.parent / "output" / "macros"

# (verilog_filename, words, bits, mux_ratio)
CONFIGS = [
    ("weight_32kb.v", 1024, 32, 8),
    ("activation_3kb.v", 384, 64, 2),
    ("test_64x8.v", 64, 8, 2),
]


def _read_verilog(filename: str) -> str:
    """Read a Verilog file from the macros output directory."""
    path = MACRO_DIR / filename
    if not path.exists():
        pytest.skip(f"Verilog file not found: {path} (run generate_v1_macros.py first)")
    return path.read_text()


class TestVerilogModuleName:
    """Verify module name matches expected sram_WxB pattern."""

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_module_name(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        expected = f"module sram_{words}x{bits}"
        assert expected in text, (
            f"Expected '{expected}' in {filename}, got:\n{text[:200]}"
        )


class TestVerilogAddressBits:
    """Verify correct number of address bits = ceil(log2(words))."""

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_address_width(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        addr_bits = int(math.ceil(math.log2(words))) if words > 1 else 1
        # Look for ADDR port declaration: [N-1:0]  ADDR
        pattern = rf"\[{addr_bits - 1}:0\]\s+ADDR"
        assert re.search(pattern, text), (
            f"Expected address width [{addr_bits-1}:0] ADDR in {filename}"
        )


class TestVerilogDataWidth:
    """Verify correct data width in DIN, DOUT, and memory array."""

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_data_width(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        data_pattern = rf"\[{bits - 1}:0\]\s+DIN"
        assert re.search(data_pattern, text), (
            f"Expected data width [{bits-1}:0] DIN in {filename}"
        )
        dout_pattern = rf"\[{bits - 1}:0\]\s+DOUT"
        assert re.search(dout_pattern, text), (
            f"Expected data width [{bits-1}:0] DOUT in {filename}"
        )

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_memory_array_depth(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        mem_pattern = rf"mem\s+\[0:{words - 1}\]"
        assert re.search(mem_pattern, text), (
            f"Expected mem [0:{words-1}] in {filename}"
        )


class TestVerilogReadWriteLogic:
    """Verify read/write logic is present."""

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_clock_edge(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        assert "posedge CLK" in text, f"Missing 'posedge CLK' in {filename}"

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_write_enable(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        assert "WE" in text, f"Missing write enable (WE) in {filename}"

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_chip_select(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        assert "CS" in text, f"Missing chip select (CS) in {filename}"

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_write_operation(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        assert "mem[ADDR] <= DIN" in text, (
            f"Missing write operation 'mem[ADDR] <= DIN' in {filename}"
        )

    @pytest.mark.parametrize("filename,words,bits,mux", CONFIGS)
    def test_read_operation(self, filename: str, words: int, bits: int, mux: int) -> None:
        text = _read_verilog(filename)
        assert "DOUT <= mem[ADDR]" in text, (
            f"Missing read operation 'DOUT <= mem[ADDR]' in {filename}"
        )
