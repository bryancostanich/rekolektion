"""LEF abstract generator for SRAM macros.

Generates LEF (Library Exchange Format) files for use in OpenLane
place-and-route.  The LEF describes macro dimensions, pin locations
and directions, and obstruction layers.

OBS (obstruction) layers are generated from actual GDS metal usage:
- Parse the GDS file to extract metal shape bounding boxes
- Expand each shape by DRC spacing margin
- Merge overlapping shapes into minimal OBS rectangles
- Emit per-layer OBS that matches actual metal, not full-area

This approach frees 60-70% of met1/met2 routing resources over
each macro compared to full-area OBS.

Usage::

    from rekolektion.macro.assembler import compute_macro_params
    from rekolektion.macro.lef_generator import generate_lef

    params = compute_macro_params(words=1024, bits=32, mux_ratio=8)
    params.macro_width = 500.0
    params.macro_height = 400.0
    generate_lef(params, "output/sram_1024x32.lef",
                 gds_path="output/sram_1024x32.gds")
"""

from __future__ import annotations

import math
import struct
from pathlib import Path

from rekolektion.macro.assembler import MacroParams
from rekolektion.macro.outputs import _pn


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_PIN_WIDTH = 0.14    # met2 minimum width (um)
_PIN_HEIGHT = 0.28   # pin rect height (um)
_PIN_PITCH = 0.28    # met2 pitch (um)
_PIN_LAYER = "met2"

# SKY130 DRC spacing rules (um)
_MET1_SPACING = 0.14
_MET2_SPACING = 0.14
_MET3_SPACING = 0.30

# GDS layer numbers for SKY130
_GDS_LAYERS = {
    68: ("met1", _MET1_SPACING),
    69: ("met2", _MET2_SPACING),
    70: ("met3", _MET3_SPACING),
}

# Merge grid resolution — shapes within this distance are merged
# into a single OBS rect. Smaller = more precise OBS but more rects.
# Using 1.0 um gives a good balance (few hundred rects vs 300K shapes).
_MERGE_GRID = 1.0  # um


# ---------------------------------------------------------------------------
# GDS shape extraction
# ---------------------------------------------------------------------------

def _extract_metal_shapes(gds_path: Path) -> dict[int, list[tuple[float, float, float, float]]]:
    """Extract bounding boxes of all metal shapes from a GDS file.

    Returns dict mapping GDS layer number to list of (x1, y1, x2, y2) in um.
    """
    shapes: dict[int, list[tuple[float, float, float, float]]] = {
        layer: [] for layer in _GDS_LAYERS
    }

    current: dict = {}
    with open(gds_path, "rb") as f:
        while True:
            header = f.read(4)
            if len(header) < 4:
                break
            length, rtype, dtype = struct.unpack(">HBB", header)
            data = f.read(length - 4) if length > 4 else b""

            if rtype == 0x08:  # BOUNDARY
                current = {}
            elif rtype == 0x0D and len(data) >= 2:  # LAYER
                current["layer"] = struct.unpack(">h", data)[0]
            elif rtype == 0x10:  # XY
                pts = []
                for j in range(0, len(data), 8):
                    x, y = struct.unpack(">ii", data[j : j + 8])
                    pts.append((x / 1000, y / 1000))  # nm to um
                current["pts"] = pts
            elif rtype == 0x11:  # ENDEL
                layer = current.get("layer")
                if layer in shapes and "pts" in current:
                    pts = current["pts"]
                    if len(pts) >= 4:
                        xs = [p[0] for p in pts]
                        ys = [p[1] for p in pts]
                        shapes[layer].append(
                            (min(xs), min(ys), max(xs), max(ys))
                        )
                current = {}

    return shapes


def _merge_shapes_to_obs(
    shapes: list[tuple[float, float, float, float]],
    spacing: float,
    macro_w: float,
    macro_h: float,
    grid: float = _MERGE_GRID,
) -> list[tuple[float, float, float, float]]:
    """Merge metal shapes into OBS rectangles with DRC spacing margin.

    Strategy: rasterize shapes onto a grid, expand by spacing margin,
    then extract rectangular regions. This naturally merges nearby shapes.
    """
    if not shapes:
        return []

    # Grid dimensions
    nx = int(math.ceil(macro_w / grid))
    ny = int(math.ceil(macro_h / grid))

    # Rasterize: mark grid cells that contain metal (with spacing expansion)
    occupied = [[False] * ny for _ in range(nx)]
    margin_cells = int(math.ceil(spacing / grid))

    for x1, y1, x2, y2 in shapes:
        # Expand by spacing margin, clamp to macro bounds
        gx1 = max(0, int((x1 - spacing) / grid))
        gy1 = max(0, int((y1 - spacing) / grid))
        gx2 = min(nx, int(math.ceil((x2 + spacing) / grid)))
        gy2 = min(ny, int(math.ceil((y2 + spacing) / grid)))
        for gx in range(gx1, gx2):
            for gy in range(gy1, gy2):
                occupied[gx][gy] = True

    # Extract horizontal runs and merge into rectangles
    # Greedy: scan columns, find contiguous occupied spans, merge adjacent
    rects: list[tuple[float, float, float, float]] = []

    # Simple approach: find contiguous horizontal runs per row,
    # then merge vertically adjacent identical runs
    row_runs: list[list[tuple[int, int]]] = []
    for gy in range(ny):
        runs = []
        gx = 0
        while gx < nx:
            if occupied[gx][gy]:
                start = gx
                while gx < nx and occupied[gx][gy]:
                    gx += 1
                runs.append((start, gx))
            else:
                gx += 1
        row_runs.append(runs)

    # Merge vertically: extend each run downward as long as the same
    # horizontal span exists in the next row
    visited = [[False] * ny for _ in range(nx)]
    for gy in range(ny):
        for run_start, run_end in row_runs[gy]:
            if visited[run_start][gy]:
                continue
            # Extend this run downward
            gy_end = gy + 1
            while gy_end < ny:
                # Check if this exact run exists in gy_end
                found = False
                for rs, re in row_runs[gy_end]:
                    if rs == run_start and re == run_end:
                        found = True
                        break
                if found:
                    gy_end += 1
                else:
                    break
            # Mark visited
            for y in range(gy, gy_end):
                for x in range(run_start, run_end):
                    visited[x][y] = True
            # Convert back to um coordinates, snap to manufacturing grid
            rect = (
                _snap(run_start * grid),
                _snap(gy * grid),
                _snap(min(run_end * grid, macro_w)),
                _snap(min(gy_end * grid, macro_h)),
            )
            rects.append(rect)

    return rects


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _snap(v: float, grid: float = 0.005) -> float:
    """Snap coordinate to manufacturing grid (5nm for SKY130)."""
    return round(v / grid) * grid


def _pin_rect(cx: float, cy: float) -> str:
    """Return a RECT string centred on (cx, cy), snapped to mfg grid."""
    x1 = _snap(cx - _PIN_WIDTH / 2)
    y1 = _snap(cy - _PIN_HEIGHT / 2)
    x2 = _snap(cx + _PIN_WIDTH / 2)
    y2 = _snap(cy + _PIN_HEIGHT / 2)
    return f"        RECT {x1:.3f} {y1:.3f} {x2:.3f} {y2:.3f} ;"


def _pin_block(
    name: str,
    direction: str,
    cx: float,
    cy: float,
    *,
    use: str | None = None,
) -> list[str]:
    """Generate LEF lines for a single pin."""
    lines = [
        f"  PIN {name}",
        f"    DIRECTION {direction} ;",
    ]
    if use:
        lines.append(f"    USE {use} ;")
    lines += [
        "    PORT",
        f"      LAYER {_PIN_LAYER} ;",
        _pin_rect(cx, cy),
        "    END",
        f"  END {name}",
    ]
    return lines


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def generate_lef(
    params: MacroParams,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    uppercase_ports: bool = False,
    gds_path: str | Path | None = None,
) -> Path:
    """Generate a LEF abstract for the SRAM macro.

    Parameters
    ----------
    params : MacroParams
        Macro parameters (words, bits, dimensions, etc.).
    output_path : path
        Write LEF to this file.
    gds_path : path, optional
        Path to the GDS file for this macro. If provided, OBS layers
        are generated from actual metal usage instead of full-area.

    Returns
    -------
    Path
        The output file path.
    """
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    w = params.macro_width
    h = params.macro_height
    if not macro_name:
        macro_name = f"sram_{params.words}x{params.bits}_mux{params.mux_ratio}"
    addr_bits = params.num_addr_bits
    data_bits = params.bits
    ben_bits = params.num_ben_bits
    scan = params.scan_chain

    lines: list[str] = []

    # Header
    lines += [
        "VERSION 5.7 ;",
        "BUSBITCHARS \"[]\" ;",
        "DIVIDERCHAR \"/\" ;",
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

    # --- Pin placement ---
    up = uppercase_ports

    # Left edge: address pins, evenly spaced vertically
    addr_start_y = h * 0.1
    addr_span = h * 0.8
    addr_step = addr_span / max(addr_bits, 1)
    for i in range(addr_bits):
        cy = addr_start_y + i * addr_step + addr_step / 2
        lines += _pin_block(_pn(f"addr[{i}]", up), "INPUT", cx=0.0, cy=cy)
        lines.append("")

    # Right edge: din and dout, evenly spaced vertically
    data_pins_total = data_bits * 2  # din + dout
    if ben_bits:
        data_pins_total += ben_bits
    data_step = h * 0.8 / max(data_pins_total, 1)
    data_cy = h * 0.1

    for i in range(data_bits):
        lines += _pin_block(_pn(f"din[{i}]", up), "INPUT", cx=w, cy=data_cy)
        data_cy += data_step
        lines.append("")

    for i in range(data_bits):
        lines += _pin_block(_pn(f"dout[{i}]", up), "OUTPUT", cx=w, cy=data_cy)
        data_cy += data_step
        lines.append("")

    if ben_bits:
        for i in range(ben_bits):
            lines += _pin_block(_pn(f"ben[{i}]", up), "INPUT", cx=w, cy=data_cy)
            data_cy += data_step
            lines.append("")

    # Top edge: control + power
    top_pins = [
        (_pn("clk", up), "INPUT", None),
        (_pn("cs", up), "INPUT", None),
        (_pn("we", up), "INPUT", None),
    ]
    if scan:
        top_pins += [
            (_pn("scan_en", up), "INPUT", None),
            (_pn("scan_in", up), "INPUT", None),
            (_pn("scan_out", up), "OUTPUT", None),
        ]
    top_pins += [
        (_pn("VPWR", up), "INOUT", "POWER"),
        (_pn("VGND", up), "INOUT", "GROUND"),
    ]
    top_step = w / (len(top_pins) + 1)
    for idx, (pname, pdir, puse) in enumerate(top_pins):
        cx = top_step * (idx + 1)
        lines += _pin_block(pname, pdir, cx=cx, cy=h, use=puse)
        lines.append("")

    # Bottom edge: power (mirrored for abutment)
    bottom_pins = [
        (_pn("VPWR", up), "INOUT", "POWER"),
        (_pn("VGND", up), "INOUT", "GROUND"),
    ]
    bottom_step = w / (len(bottom_pins) + 1)
    for idx, (pname, pdir, puse) in enumerate(bottom_pins):
        cx = bottom_step * (idx + 1)
        lines += _pin_block(pname, pdir, cx=cx, cy=0.0, use=puse)
        lines.append("")

    # --- OBS (obstruction) ---
    # Generate from actual GDS metal usage if gds_path provided,
    # otherwise fall back to full-area OBS.
    lines += [
        "  OBS",
    ]

    if gds_path and Path(gds_path).exists():
        # Extract metal shapes from GDS and generate precise OBS
        gds_shapes = _extract_metal_shapes(Path(gds_path))

        for gds_layer, (layer_name, spacing) in _GDS_LAYERS.items():
            layer_shapes = gds_shapes.get(gds_layer, [])
            if not layer_shapes:
                continue

            obs_rects = _merge_shapes_to_obs(
                layer_shapes, spacing, w, h
            )

            if obs_rects:
                lines.append(f"    LAYER {layer_name} ;")
                for x1, y1, x2, y2 in obs_rects:
                    lines.append(
                        f"      RECT {x1:.3f} {y1:.3f} {x2:.3f} {y2:.3f} ;"
                    )

            shape_count = len(layer_shapes)
            obs_count = len(obs_rects)
            total_area = w * h
            obs_area = sum((r[2]-r[0]) * (r[3]-r[1]) for r in obs_rects)
            pct = obs_area / total_area * 100
            # Comment with stats
            lines.append(
                f"    ; {layer_name}: {obs_count} OBS rects from "
                f"{shape_count} shapes ({pct:.0f}% area blocked)"
            )
    else:
        # Fallback: full-area OBS (conservative)
        for layer in ("met1", "met2", "met3"):
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
