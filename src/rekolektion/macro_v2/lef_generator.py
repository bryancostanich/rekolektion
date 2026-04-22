"""LEF file generator for v2 SRAM macros (OpenRAM-style, interior pins).

Reads the assembler's floorplan to determine pin positions, then emits
a standard LEF 5.7 file. All signal pins are declared on met3 as
interior pin rectangles; VPWR/VGND are on met4 as horizontal straps.

OBS (obstruction) declarations (issue #5 fix): full-SIZE on met1 and
met2 — blocks stdcell placement and forces OpenROAD's cut_rows to cut
rows around the macro, preventing tapcell insertion inside. Middle-
band OBS on met3 covers internal routing between the top and bottom
pin strips; pin strips remain OBS-free for router access. No OBS on
met4 (all shapes are declared VPWR/VGND PORTs) or li1 (bitcell-
internal, unreachable once met1 is blocked).

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
    ys_hi_tight = pins_top_y_tmp + _PIN_STUB_LEN + 0.5

    # Snap the macro's top edge UP to the nearest sky130_fd_sc_hd
    # stdcell row-boundary (pitch 2.72 μm) above ys_hi_tight, relative
    # to ys_lo (= macro-local y=0).  The met2 VPWR rail sits at this
    # snapped edge; the consuming chip's stdcell row grid will have a
    # met1 power rail at exactly the same chip-y, allowing OpenROAD's
    # default sky130 PDN macro-template to land a trivial via1/via
    # stack at the pin.
    #
    # Without this snap, the macro's top edge is at an arbitrary
    # fractional row position and PSM-0069 reports every macro's
    # VPWR pin as "Unconnected instance" (observed: 81/81 macros on
    # khalkulo chip flow RUN_2026-04-19 + RUN_2026-04-20).
    import math as _math
    _ROW_PITCH = 2.72
    _macro_h_tight = ys_hi_tight - ys_lo
    _macro_h_snapped = _math.ceil(_macro_h_tight / _ROW_PITCH) * _ROW_PITCH
    ys_hi = ys_lo + _macro_h_snapped

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

        # Power/Ground pins.  Each PIN declares TWO PORT rectangles
        # on different layers — a met2 access stub (what sky130's
        # default OpenLane PDN auto-template expects for macro pin
        # layers; without this OpenROAD skips "Inserting grid: macro"
        # and fails PDN-0178/0179 at channel-repair) PLUS a full-width
        # met4 strap (chip PDN interface: met4 is the sky130 default
        # FP_PDN_VPITCH stripe layer, so any chip vstripe crossing
        # the macro merges stripe-on-stripe without needing an
        # explicit connect rule).
        #
        # Without the met4 strap, 81/81 macro VPWR pins came out
        # unconnected in the khalkulo chip flow (PSM-0069,
        # RUN_2026-04-19_14-55-13).  With met4-only (no met2 stub),
        # OpenLane's default sky130 PDN can't auto-template the macro
        # and the portability test itself fails PDN-0179.  Both
        # surfaces keep both customers happy.
        #
        # Corresponding physical met4 strap + via2+via3 stacks down
        # to the met2 rail are drawn by
        # `assembler._draw_power_network`.
        stub_w = 0.14
        stub_h = 0.28
        strap_half = _PDN_STRAP_W / 2
        bot_y = 0.0          # LEF macro_h=0 bottom edge
        top_y = macro_h      # LEF macro_h top edge
        inner_x0 = x0
        inner_x1 = x_end
        vpwr_x_left = tx(inner_x0 + (inner_x1 - inner_x0) * 1.0 / 3.0)
        vpwr_x_right = tx(inner_x0 + (inner_x1 - inner_x0) * 2.0 / 3.0)
        vgnd_x_left = tx(inner_x0 + (inner_x1 - inner_x0) * 1.0 / 3.0)
        vgnd_x_right = tx(inner_x0 + (inner_x1 - inner_x0) * 2.0 / 3.0)

        # VPWR/VGND PIN declarations — dual-layer PORTs: met2 rail
        # (internal power distribution) + met4 strap (chip-PDN
        # interface).
        #
        # Per OpenROAD PDN docs + OpenRAM sky130 SRAM reference LEF
        # (`$PDK_ROOT/.../sky130_sram_macros/lef/sky130_sram_*.lef`),
        # a sub-macro consumed by the default sky130 hierarchical PDN
        # template must declare power pins on the chip's
        # `FP_PDN_VERTICAL_LAYER` (met4 for sky130) — that's the layer
        # the default macro-grid template (pdn_cfg.tcl line 126-135)
        # can actually merge with.  The default template only has
        # `add_pdn_connect -grid macro -layers met4 met5`; there is
        # no met4→met3 or met3→met2 step-down rule, so declaring
        # pins only on met2 leaves every macro VPWR pin
        # PSM-0069 "Unconnected instance" at chip integration.
        #
        # SHAPE ABUTMENT annotation signals the PDN template these
        # pins merge via same-layer abutment with chip stripes.
        #
        # Horizontal met4 strap orientation: chip met4 vstripes
        # (vertical) crossing a horizontal macro strap form a
        # 1.6 × 1.6 μm same-layer same-net intersection, enough
        # for polygon merge + electrical continuity.
        _PDN_MET2_RAIL_W = 0.40
        rail_half = _PDN_MET2_RAIL_W / 2
        met4_half = _PDN_STRAP_W / 2
        top_rail_y_lef = top_y - met4_half
        bot_rail_y_lef = bot_y + met4_half

        # met4 vertical-strap x positions — must match the GDS geometry
        # drawn by `assembler._draw_power_network`.  Layout:
        #   1 VPWR strap at macro x-center
        #   2 VGND straps at left + right edges (0.5 + strap_half in)
        macro_w_local = tx(xs_hi)  # macro_w in 0-origin LEF coords
        edge_margin_lef = met4_half + 0.5
        vpwr_strap_x_center = macro_w_local / 2
        vgnd_strap_x_left = edge_margin_lef
        vgnd_strap_x_right = macro_w_local - edge_margin_lef

        # met4 straps span y=bot_rail_y_lef to y=top_rail_y_lef
        # (full interior height between the two met2 rails).
        met4_strap_y0 = bot_rail_y_lef
        met4_strap_y1 = top_rail_y_lef

        # Per-strap local met2 pads (match GDS `_draw_power_network`).
        # Full-width met2 rails caused signal/signal shorts at macro
        # level; pads only need to back the via2 stack under each
        # met4 strap terminal.
        pad_half_x = met4_half + 0.3   # 1.1 µm
        pad_half_y = rail_half         # 0.20 µm
        _write_pin_ports(
            f, "VPWR", "INOUT", "POWER", abutment=True,
            ports=[
                # Local met2 pad at VPWR strap's top anchor
                ("met2", (vpwr_strap_x_center - pad_half_x,
                          top_rail_y_lef - pad_half_y,
                          vpwr_strap_x_center + pad_half_x,
                          top_rail_y_lef + pad_half_y)),
                # Vertical met4 strap at center
                ("met4", (vpwr_strap_x_center - met4_half, met4_strap_y0,
                          vpwr_strap_x_center + met4_half, met4_strap_y1)),
            ],
        )
        _write_pin_ports(
            f, "VGND", "INOUT", "GROUND", abutment=True,
            ports=[
                # Local met2 pads at VGND straps' bottom anchors
                ("met2", (vgnd_strap_x_left - pad_half_x,
                          bot_rail_y_lef - pad_half_y,
                          vgnd_strap_x_left + pad_half_x,
                          bot_rail_y_lef + pad_half_y)),
                ("met2", (vgnd_strap_x_right - pad_half_x,
                          bot_rail_y_lef - pad_half_y,
                          vgnd_strap_x_right + pad_half_x,
                          bot_rail_y_lef + pad_half_y)),
                # Vertical met4 straps at left + right edges
                ("met4", (vgnd_strap_x_left - met4_half, met4_strap_y0,
                          vgnd_strap_x_left + met4_half, met4_strap_y1)),
                ("met4", (vgnd_strap_x_right - met4_half, met4_strap_y0,
                          vgnd_strap_x_right + met4_half, met4_strap_y1)),
            ],
        )

        # OBS: full-SIZE met1/met2 + middle-band met3. See docstring.
        # Band starts above the top of the output-pin stubs at the
        # bottom, ends below the bottom of the input-pin stubs at the
        # top, so the router keeps both pin strips fully accessible.
        _OBS_PIN_BAND_MARGIN = 1.0
        met3_band_y0 = _snap(
            ty(pins_bot_y) + _PIN_STUB_LEN + _OBS_PIN_BAND_MARGIN
        )
        met3_band_y1 = _snap(ty(pins_top_y) - _OBS_PIN_BAND_MARGIN)
        f.write("  OBS\n")
        f.write("    LAYER met1 ;\n")
        f.write(f"      RECT 0.000 0.000 {macro_w:.3f} {macro_h:.3f} ;\n")
        f.write("    LAYER met2 ;\n")
        f.write(f"      RECT 0.000 0.000 {macro_w:.3f} {macro_h:.3f} ;\n")
        f.write("    LAYER met3 ;\n")
        f.write(
            f"      RECT 0.000 {met3_band_y0:.3f} "
            f"{macro_w:.3f} {met3_band_y1:.3f} ;\n"
        )
        f.write("  END\n")

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


def _write_pin_multi(
    f: TextIO,
    name: str,
    direction: str,
    use: str,
    ports: list[tuple[str, tuple[float, float, float, float]]],
) -> None:
    """Write a single PIN with multiple PORT shapes on potentially
    different layers.  Each port merges at the same LEF pin name —
    useful for power pins that need to advertise access on both a
    chip-PDN layer (met4 strap) AND the sky130 default macro-template
    layer (met2 stub)."""
    f.write(f"  PIN {name}\n")
    f.write(f"    DIRECTION {direction} ;\n")
    f.write(f"    USE {use} ;\n")
    for layer, rect in ports:
        f.write("    PORT\n")
        f.write(f"      LAYER {layer} ;\n")
        f.write(
            f"        RECT {rect[0]:.3f} {rect[1]:.3f} "
            f"{rect[2]:.3f} {rect[3]:.3f} ;\n"
        )
        f.write("    END\n")
    f.write(f"  END {name}\n\n")


def _write_pin_ports(
    f: TextIO,
    name: str,
    direction: str,
    use: str,
    ports: list[tuple[str, tuple[float, float, float, float]]],
    *,
    abutment: bool = False,
) -> None:
    """Write a single PIN with multiple PORTs and optional
    SHAPE ABUTMENT annotation (matches OpenRAM sky130 SRAM reference
    LEF convention for chip-PDN-consumable macro power pins)."""
    f.write(f"  PIN {name}\n")
    f.write(f"    DIRECTION {direction} ;\n")
    f.write(f"    USE {use} ;\n")
    if abutment:
        f.write("    SHAPE ABUTMENT ;\n")
    for layer, rect in ports:
        f.write("    PORT\n")
        f.write(f"      LAYER {layer} ;\n")
        f.write(
            f"        RECT {rect[0]:.3f} {rect[1]:.3f} "
            f"{rect[2]:.3f} {rect[3]:.3f} ;\n"
        )
        f.write("    END\n")
    f.write(f"  END {name}\n\n")
