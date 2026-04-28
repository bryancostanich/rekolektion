from pathlib import Path

import pytest

from rekolektion.macro.assembler import MacroParams
from rekolektion.macro.lef_generator import generate_lef


@pytest.fixture
def tiny_lef(tmp_path):
    p = MacroParams(words=32, bits=8, mux_ratio=4)
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
    p = MacroParams(words=32, bits=8, mux_ratio=4)
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


def test_lef_has_obs_block(tiny_lef):
    # Issue #5: LEF must emit OBS so OpenROAD's cut_rows sees the
    # macro as a placement blockage and tapcell doesn't insert cells
    # inside the footprint.
    assert "\n  OBS\n" in tiny_lef, "missing OBS block"


def test_lef_obs_blocks_met1_met2_full_size(tiny_lef):
    import re
    size = re.search(r"SIZE\s+([\d.]+)\s+BY\s+([\d.]+)", tiny_lef)
    w = float(size.group(1))
    h = float(size.group(2))
    obs = re.search(r"\n  OBS\n(.*?)\n  END\n", tiny_lef, re.DOTALL).group(1)
    # met1 and met2 must each have a full-SIZE rect
    for layer in ("met1", "met2"):
        pattern = (
            rf"LAYER {layer}\s*;\s*"
            rf"RECT\s+0\.000\s+0\.000\s+{w:.3f}\s+{h:.3f}\s*;"
        )
        assert re.search(pattern, obs), (
            f"{layer} OBS must be full-SIZE {w:.3f} x {h:.3f}"
        )


def test_lef_obs_met3_band_excludes_pin_strips(tiny_lef):
    # met3 OBS must be a band in the middle — not touching y=0 or y=h.
    # This keeps the top and bottom pin strips OBS-free so the router
    # can access signal pins.
    import re
    size = re.search(r"SIZE\s+([\d.]+)\s+BY\s+([\d.]+)", tiny_lef)
    h = float(size.group(2))
    obs = re.search(r"\n  OBS\n(.*?)\n  END\n", tiny_lef, re.DOTALL).group(1)
    m = re.search(
        r"LAYER met3\s*;\s*"
        r"RECT\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*;",
        obs,
    )
    assert m, "met3 OBS rect missing"
    y0 = float(m.group(2))
    y1 = float(m.group(4))
    assert y0 > 0.0, f"met3 OBS touches bottom pin strip (y0={y0})"
    assert y1 < h, f"met3 OBS touches top pin strip (y1={y1}, h={h})"
    assert y1 > y0, "met3 OBS band has non-positive height"


def test_lef_obs_omits_met4_and_li1(tiny_lef):
    # met4 is fully declared via VPWR/VGND PORTs; adding OBS there
    # would fight chip PDN merges. li1 is bitcell-internal and
    # unreachable once met1 is blocked.
    import re
    obs = re.search(r"\n  OBS\n(.*?)\n  END\n", tiny_lef, re.DOTALL).group(1)
    assert "LAYER met4" not in obs, "OBS must not declare met4"
    assert "LAYER li1" not in obs, "OBS must not declare li1"
