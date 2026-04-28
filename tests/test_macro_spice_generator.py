from pathlib import Path

import pytest

from rekolektion.macro.assembler import MacroParams
from rekolektion.macro.spice_generator import generate_reference_spice


def test_generates_top_subckt_with_required_ports(tmp_path):
    p = MacroParams(words=32, bits=8, mux_ratio=4)
    sp = tmp_path / "tiny.sp"
    generate_reference_spice(p, sp)
    text = sp.read_text()
    assert f".subckt {p.top_cell_name}" in text
    for name in ("clk", "we", "cs", "VPWR", "VGND"):
        assert name in text
    for i in range(p.num_addr_bits):
        assert f"addr{i}" in text
    for i in range(p.bits):
        assert f"din{i}" in text
        assert f"dout{i}" in text


def test_generates_all_block_subckts(tmp_path):
    p = MacroParams(words=32, bits=8, mux_ratio=4)
    sp = tmp_path / "tiny.sp"
    generate_reference_spice(p, sp)
    text = sp.read_text()
    for req in (
        f".subckt row_decoder_{p.rows}",
        ".subckt ctrl_logic",
        f".subckt sram_array_{p.rows}x{p.cols}",
        f".subckt precharge_row_{p.bits}_mux{p.mux_ratio}",
        f".subckt column_mux_row_{p.bits}_mux{p.mux_ratio}",
        f".subckt sense_amp_row_{p.bits}_mux{p.mux_ratio}",
        f".subckt write_driver_row_{p.bits}_mux{p.mux_ratio}",
    ):
        assert req in text, f"missing: {req}"


def test_generates_instance_lines_for_every_block(tmp_path):
    p = MacroParams(words=32, bits=8, mux_ratio=4)
    sp = tmp_path / "tiny.sp"
    generate_reference_spice(p, sp)
    text = sp.read_text()
    # Top-level instance prefixes
    for inst in ("Xdecoder", "Xcontrol", "Xarray", "Xprecharge", "Xcolmux",
                 "Xsa", "Xwd"):
        assert inst in text, f"missing instance: {inst}"


def test_spice_ends_with_ends_for_each_subckt(tmp_path):
    p = MacroParams(words=32, bits=8, mux_ratio=4)
    sp = tmp_path / "tiny.sp"
    generate_reference_spice(p, sp)
    text = sp.read_text()
    # Count `.subckt` and `.ends` directives on directive lines only
    # (skip SPICE comments that mention them).
    n_subckt = sum(
        1 for line in text.splitlines()
        if line.lstrip().startswith(".subckt")
    )
    n_ends = sum(
        1 for line in text.splitlines()
        if line.lstrip().startswith(".ends")
    )
    assert n_subckt == n_ends, (
        f"{n_subckt} .subckt declarations but {n_ends} .ends terminators"
    )
