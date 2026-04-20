"""LEF file generator for v2 SRAM macros (OpenRAM-style, interior pins).

Reads the assembler's floorplan to determine pin positions, then emits
a standard LEF 5.7 file. All signal pins are declared on met3 as
interior pin rectangles; VPWR/VGND are on met4 as horizontal straps.
No OBS (obstruction) declarations — OpenRAM-style LEFs let the router
use the macro's interior freely.

Coordinate translation: the assembler places the array at (0, 0) with
the row decoder and control logic at negative x, and peripheral rows
extending to negative y. The LEF coordinate system puts the macro's
lower-left corner at (0, 0). `generate_lef()` reads the floorplan's
bounding box and translates every coordinate.
"""
from __future__ import annotations

from pathlib import Path
from typing import TextIO

from rekolektion.macro_v2.assembler import (
    MacroV2Params,
    build_floorplan,
    _PDN_STRAP_W,
    _PDN_STRAP_MARGIN,
    _PIN_LAYER,
    _PIN_STUB_LEN,
    _PIN_STUB_W,
)


def generate_lef(
    p: MacroV2Params,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    uppercase_ports: bool = False,
) -> Path:
    """Emit a LEF file for the assembled macro.

    `uppercase_ports=True` matches the convention used by rekolektion's
    v1 Liberty files (ADDR/DIN/DOUT/CLK/WE/CS instead of lowercase) so
    the same .lib files can be paired with the v2 .lef at P&R time.
    """
    path = Path(output_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    name = macro_name or p.top_cell_name
    fp = build_floorplan(p)

    def fmt_addr(i: int) -> str:
        return f"ADDR[{i}]" if uppercase_ports else f"addr[{i}]"

    def fmt_din(i: int) -> str:
        return f"DIN[{i}]" if uppercase_ports else f"din[{i}]"

    def fmt_dout(i: int) -> str:
        return f"DOUT[{i}]" if uppercase_ports else f"dout[{i}]"

    def fmt_ctrl(name: str) -> str:
        return name.upper() if uppercase_ports else name

    # Macro bounding box in assembler coordinates. Must include the
    # PDN straps AND the pin stubs (which extend _PIN_STUB_LEN above
    # pins_top_y at the top and below pins_bot_y at the bottom).
    xs_lo = min(x for x, _ in fp.positions.values()) - 1.0
    xs_hi = max(
        fp.positions[n][0] + fp.sizes[n][0] for n in fp.positions
    ) + 1.0
    prec_top_tmp = fp.positions["precharge"][1] + fp.sizes["precharge"][1]
    wd_bot_tmp = fp.positions["write_driver"][1]
    pins_top_y_tmp = (
        prec_top_tmp + _PDN_STRAP_MARGIN + _PDN_STRAP_W + _PDN_STRAP_MARGIN
    )
    pins_bot_y_tmp = (
        wd_bot_tmp - _PDN_STRAP_MARGIN - _PDN_STRAP_W - _PDN_STRAP_MARGIN
    )
    ys_lo = pins_bot_y_tmp - 0.5   # small extra margin beyond pin bottom
    ys_hi = pins_top_y_tmp + _PIN_STUB_LEN + 0.5

    macro_w = xs_hi - xs_lo
    macro_h = ys_hi - ys_lo

    # All LEF RECT coordinates must land on the sky130 manufacturing
    # grid (5 nm).  Un-snapped fractional coordinates trigger
    # chip-level DRT-0416 "offgrid pin shape" errors during detailed
    # routing when this macro is integrated into a chip.
    def _snap(v: float, grid: float = 0.005) -> float:
        return round(v / grid) * grid

    def tx(x: float) -> float:
        return _snap(x - xs_lo)

    def ty(y: float) -> float:
        return _snap(y - ys_lo)

    # Pin positions from assembler._place_top_pins (identical math)
    array_x, _ = fp.positions["array"]
    array_w = fp.sizes["array"][0]
    prec_top = fp.positions["precharge"][1] + fp.sizes["precharge"][1]
    pins_top_y = (
        prec_top + _PDN_STRAP_MARGIN + _PDN_STRAP_W + _PDN_STRAP_MARGIN
    )
    wd_bot = fp.positions["write_driver"][1]
    pins_bot_y = (
        wd_bot - _PDN_STRAP_MARGIN - _PDN_STRAP_W - _PDN_STRAP_MARGIN
    )

    input_names: list[str] = []
    for i in range(p.num_addr_bits):
        input_names.append(fmt_addr(i))
    input_names += [fmt_ctrl("clk"), fmt_ctrl("we"), fmt_ctrl("cs")]
    for i in range(p.bits):
        input_names.append(fmt_din(i))

    total_inputs = len(input_names)
    x0 = array_x + 1.0
    x_end = array_x + array_w - 1.0
    step = (x_end - x0) / max(total_inputs - 1, 1) if total_inputs > 1 else 0.0

    output_positions: list[tuple[str, float]] = []
    for i in range(p.bits):
        step_b = (x_end - x0) / max(p.bits - 1, 1) if p.bits > 1 else 0.0
        output_positions.append(
            (fmt_dout(i), x0 + i * step_b)
        )

    # Power strap y-coords
    vpwr_y = prec_top + _PDN_STRAP_MARGIN + _PDN_STRAP_W / 2
    vgnd_y = wd_bot - _PDN_STRAP_MARGIN - _PDN_STRAP_W / 2

    with path.open("w") as f:
        _write_header(f)
        f.write(f"\nMACRO {name}\n")
        f.write("  CLASS BLOCK ;\n")
        f.write(f"  FOREIGN {name} ;\n")
        f.write("  ORIGIN 0 0 ;\n")
        f.write(f"  SIZE {macro_w:.3f} BY {macro_h:.3f} ;\n")
        f.write("  SYMMETRY X Y ;\n\n")

        # Input pins
        for idx, pin_name in enumerate(input_names):
            px = _snap(x0 + idx * step)
            _write_pin(
                f, pin_name, "INPUT", "SIGNAL",
                _PIN_LAYER,
                (tx(px - _PIN_STUB_W / 2), ty(pins_top_y),
                 tx(px + _PIN_STUB_W / 2), ty(pins_top_y + _PIN_STUB_LEN)),
            )

        # Output pins
        for pin_name, px in output_positions:
            px = _snap(px)
            _write_pin(
                f, pin_name, "OUTPUT", "SIGNAL",
                _PIN_LAYER,
                (tx(px - _PIN_STUB_W / 2), ty(pins_bot_y),
                 tx(px + _PIN_STUB_W / 2), ty(pins_bot_y + _PIN_STUB_LEN)),
            )

        # Power/Ground as met2 pin stubs straddling the macro edges.
        # BOTH VPWR pins sit on the top rail (VPWR net); BOTH VGND
        # pins sit on the bottom rail (VGND net). Different nets MUST
        # use different rails to avoid a short in the extracted GDS.
        # Two pins per rail gives the chip-level PDN router two
        # access points per net.
        stub_w = 0.14
        stub_h = 0.28
        bot_y = 0.0          # LEF macro_h=0 bottom edge
        top_y = macro_h      # LEF macro_h top edge
        inner_x0 = x0
        inner_x1 = x_end
        vpwr_x_left = tx(inner_x0 + (inner_x1 - inner_x0) * 1.0 / 3.0)
        vpwr_x_right = tx(inner_x0 + (inner_x1 - inner_x0) * 2.0 / 3.0)
        vgnd_x_left = tx(inner_x0 + (inner_x1 - inner_x0) * 1.0 / 3.0)
        vgnd_x_right = tx(inner_x0 + (inner_x1 - inner_x0) * 2.0 / 3.0)

        # VPWR pins — both on the top rail (VPWR net)
        _write_pin(
            f, "VPWR", "INOUT", "POWER", "met2",
            (vpwr_x_left - stub_w/2, top_y - stub_h/2,
             vpwr_x_left + stub_w/2, top_y + stub_h/2),
        )
        _write_pin(
            f, "VPWR", "INOUT", "POWER", "met2",
            (vpwr_x_right - stub_w/2, top_y - stub_h/2,
             vpwr_x_right + stub_w/2, top_y + stub_h/2),
        )
        # VGND pins — both on the bottom rail (VGND net)
        _write_pin(
            f, "VGND", "INOUT", "GROUND", "met2",
            (vgnd_x_left - stub_w/2, bot_y - stub_h/2,
             vgnd_x_left + stub_w/2, bot_y + stub_h/2),
        )
        _write_pin(
            f, "VGND", "INOUT", "GROUND", "met2",
            (vgnd_x_right - stub_w/2, bot_y - stub_h/2,
             vgnd_x_right + stub_w/2, bot_y + stub_h/2),
        )

        f.write(f"END {name}\n\n")
        f.write("END LIBRARY\n")

    return path


def _write_header(f: TextIO) -> None:
    f.write("VERSION 5.7 ;\n")
    f.write('BUSBITCHARS "[]" ;\n')
    f.write('DIVIDERCHAR "/" ;\n\n')
    f.write("UNITS\n")
    f.write("  DATABASE MICRONS 1000 ;\n")
    f.write("END UNITS\n")


def _write_pin(
    f: TextIO,
    name: str,
    direction: str,
    use: str,
    layer: str,
    rect: tuple[float, float, float, float],
) -> None:
    f.write(f"  PIN {name}\n")
    f.write(f"    DIRECTION {direction} ;\n")
    f.write(f"    USE {use} ;\n")
    f.write("    PORT\n")
    f.write(f"      LAYER {layer} ;\n")
    f.write(
        f"        RECT {rect[0]:.3f} {rect[1]:.3f} "
        f"{rect[2]:.3f} {rect[3]:.3f} ;\n"
    )
    f.write("    END\n")
    f.write(f"  END {name}\n\n")
