"""LEF generator for CIM SRAM macros.

Generates LEF abstracts with CIM-specific pins:
  MWL_EN[0..rows-1] — input, left edge (one per row)
  MBL_OUT[0..cols-1] — output (analog), bottom edge (one per column)
  MBL_PRE            — input, top edge (precharge control)
  VREF               — inout, top edge (precharge reference voltage)
  VBIAS              — input, bottom edge (sense buffer bias)
  VDD, VSS           — inout, top/bottom edges

OBS layers generated from GDS metal usage when gds_path is provided.
"""

from __future__ import annotations

import math
from pathlib import Path

from rekolektion.macro.cim_assembler import CIMMacroParams
from rekolektion.macro.lef_generator import (
    _snap, _pin_rect, _pin_block, _extract_metal_shapes,
    _merge_shapes_to_obs, _GDS_LAYERS, _PIN_WIDTH, _PIN_HEIGHT,
)


def generate_cim_lef(
    params: CIMMacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    gds_path: str | Path | None = None,
) -> Path:
    """Generate a LEF abstract for a CIM SRAM macro."""
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    w = params.macro_width
    h = params.macro_height
    rows = params.rows
    cols = params.cols
    if not macro_name:
        macro_name = f"cim_{params.variant.lower().replace('-', '_')}_{rows}x{cols}"

    lines: list[str] = []

    # Header
    lines += [
        "VERSION 5.7 ;",
        'BUSBITCHARS "[]" ;',
        'DIVIDERCHAR "/" ;',
        "",
        "UNITS",
        "  DATABASE MICRONS 1000 ;",
        "END UNITS",
        "",
        f"MACRO {macro_name}",
        "  CLASS BLOCK ;",
        f"  SIZE {w:.3f} BY {h:.3f} ;",
        "  SYMMETRY X Y ;",
        "",
    ]

    # --- MWL_EN pins (input, left edge, one per row) ---
    mwl_step = h * 0.8 / max(rows, 1)
    mwl_y_start = h * 0.1
    for i in range(rows):
        cy = mwl_y_start + i * mwl_step + mwl_step / 2
        lines += _pin_block(f"MWL_EN[{i}]", "INPUT", cx=0.0, cy=cy)
        lines.append("")

    # --- MBL_OUT pins (output, bottom edge, one per column) ---
    mbl_step = w * 0.8 / max(cols, 1)
    mbl_x_start = w * 0.1
    for i in range(cols):
        cx = mbl_x_start + i * mbl_step + mbl_step / 2
        lines += _pin_block(f"MBL_OUT[{i}]", "OUTPUT", cx=cx, cy=0.0)
        lines.append("")

    # --- Control pins (top/bottom edges) ---
    lines += _pin_block("MBL_PRE", "INPUT", cx=w * 0.3, cy=h)
    lines.append("")
    lines += _pin_block("VREF", "INOUT", cx=w * 0.5, cy=h)
    lines.append("")
    lines += _pin_block("VBIAS", "INPUT", cx=w * 0.5, cy=0.0)
    lines.append("")

    # --- Power pins ---
    lines += _pin_block("VDD", "INOUT", cx=w * 0.7, cy=h, use="POWER")
    lines.append("")
    lines += _pin_block("VSS", "INOUT", cx=w * 0.3, cy=0.0, use="GROUND")
    lines.append("")

    # --- OBS (obstruction) ---
    lines += ["  OBS"]

    if gds_path and Path(gds_path).exists():
        gds_shapes = _extract_metal_shapes(Path(gds_path))
        for gds_layer, (layer_name, spacing) in _GDS_LAYERS.items():
            layer_shapes = gds_shapes.get(gds_layer, [])
            if not layer_shapes:
                continue
            obs_rects = _merge_shapes_to_obs(layer_shapes, spacing, w, h)
            if obs_rects:
                lines.append(f"    LAYER {layer_name} ;")
                for x1, y1, x2, y2 in obs_rects:
                    lines.append(f"      RECT {x1:.3f} {y1:.3f} {x2:.3f} {y2:.3f} ;")
    else:
        for layer in ("met1", "met2", "met3", "met4"):
            lines += [
                f"    LAYER {layer} ;",
                f"      RECT 0.000 0.000 {w:.3f} {h:.3f} ;",
            ]

    lines += [
        "  END",
        "",
        f"END {macro_name}",
        "",
        "END LIBRARY",
        "",
    ]

    out.write_text("\n".join(lines))
    return out
