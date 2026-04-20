from pathlib import Path

import pytest

from rekolektion.macro_v2.assembler import MacroV2Params
from rekolektion.macro_v2.lef_generator import generate_lef


@pytest.fixture
def tiny_lef(tmp_path):
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    lef = generate_lef(p, tmp_path / "tiny.lef")
    return lef.read_text()


def test_lef_has_version_header(tiny_lef):
    assert "VERSION 5.7" in tiny_lef
    assert "DATABASE MICRONS 1000" in tiny_lef


def test_lef_has_macro_block(tiny_lef):
    assert "MACRO sram_32x8_mux4" in tiny_lef
    assert "CLASS BLOCK" in tiny_lef
    assert "SIZE" in tiny_lef


def test_lef_declares_every_signal_pin(tiny_lef):
    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    required = {"clk", "we", "cs", "VPWR", "VGND"}
    for i in range(p.num_addr_bits):
        required.add(f"addr[{i}]")
    for i in range(p.bits):
        required.add(f"din[{i}]")
        required.add(f"dout[{i}]")
    for r in required:
        assert f"PIN {r}" in tiny_lef, f"missing PIN {r}"


def test_lef_signal_pins_on_met3(tiny_lef):
    # All signal PINs (non-power/ground) must be on met3
    lines = tiny_lef.splitlines()
    current_pin: str | None = None
    current_use: str | None = None
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("PIN "):
            current_pin = stripped[len("PIN "):]
            current_use = None
        elif stripped.startswith("USE "):
            current_use = stripped[len("USE "):].rstrip(" ;")
        elif stripped.startswith("LAYER ") and current_pin is not None:
            layer = stripped[len("LAYER "):].rstrip(" ;")
            if current_use == "SIGNAL":
                assert layer == "met3", (
                    f"SIGNAL pin {current_pin} on layer {layer}, expected met3"
                )


def test_lef_power_pins_on_met2(tiny_lef):
    # VPWR/VGND on met2 as discrete access stubs (v1 convention so
    # OpenROAD's PDN router can tap met4 straps into them).
    lines = tiny_lef.splitlines()
    current_pin: str | None = None
    current_use: str | None = None
    seen: dict[str, set[str]] = {}
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("PIN "):
            current_pin = stripped[len("PIN "):]
            current_use = None
        elif stripped.startswith("USE "):
            current_use = stripped[len("USE "):].rstrip(" ;")
        elif stripped.startswith("LAYER ") and current_pin is not None:
            layer = stripped[len("LAYER "):].rstrip(" ;")
            if current_use in ("POWER", "GROUND"):
                seen.setdefault(current_pin, set()).add(layer)
    assert seen.get("VPWR") == {"met2"}
    assert seen.get("VGND") == {"met2"}


def test_lef_has_multiple_power_pin_declarations(tiny_lef):
    # v1 convention: multiple PIN VPWR / PIN VGND blocks (one per
    # access stub).
    n_vpwr = sum(1 for line in tiny_lef.splitlines()
                 if line.strip() == "PIN VPWR")
    n_vgnd = sum(1 for line in tiny_lef.splitlines()
                 if line.strip() == "PIN VGND")
    assert n_vpwr >= 2
    assert n_vgnd >= 2


def test_lef_ends_library(tiny_lef):
    assert tiny_lef.rstrip().endswith("END LIBRARY")


def test_lef_macro_size_positive(tiny_lef):
    import re
    m = re.search(r"SIZE\s+([\d.]+)\s+BY\s+([\d.]+)", tiny_lef)
    assert m is not None
    w = float(m.group(1))
    h = float(m.group(2))
    assert w > 10 and h > 10, f"macro size suspiciously small: {w} x {h}"
