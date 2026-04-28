"""Shared LEF emission helpers.

Self-contained utilities used by both the SRAM LEF generator and the
CIM LEF generator: GDS shape extraction, OBS rect merging, and
manufacturing-grid pin geometry primitives.

These were originally embedded in the V1 `lef_generator.py` and have
been carved out so the V1 SRAM file could be retired without breaking
CIM (which still relies on the V1 emission style).
"""
from __future__ import annotations

import math
import struct
from pathlib import Path


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

_PIN_WIDTH: float = 0.14    # met2 minimum width (um)
_PIN_HEIGHT: float = 0.28   # pin rect height (um)
_PIN_PITCH: float = 0.28    # met2 pitch (um)
_PIN_LAYER: str = "met2"

# SKY130 DRC spacing rules (um)
_MET1_SPACING: float = 0.14
_MET2_SPACING: float = 0.14
_MET3_SPACING: float = 0.30

# GDS layer numbers for SKY130 (drawing dtype)
_GDS_LAYERS: dict[int, tuple[str, float]] = {
    68: ("met1", _MET1_SPACING),
    69: ("met2", _MET2_SPACING),
    70: ("met3", _MET3_SPACING),
}

# Merge grid resolution — shapes within this distance are merged
# into a single OBS rect.  1.0 µm gives a few hundred rects vs the
# 300K shapes a fully-rasterised macro would produce.
_MERGE_GRID: float = 1.0  # um


# ---------------------------------------------------------------------------
# Geometry primitives
# ---------------------------------------------------------------------------

def _snap(v: float, grid: float = 0.005) -> float:
    """Snap coordinate to manufacturing grid (5 nm for SKY130)."""
    return round(v / grid) * grid


def _pin_rect(cx: float, cy: float) -> str:
    """Return a LEF RECT string centred on (cx, cy), snapped to mfg grid."""
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
# GDS shape extraction
# ---------------------------------------------------------------------------

def _extract_metal_shapes(
    gds_path: Path,
) -> dict[int, list[tuple[float, float, float, float]]]:
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
    then extract rectangular regions.  Naturally merges nearby shapes.
    """
    if not shapes:
        return []

    nx = int(math.ceil(macro_w / grid))
    ny = int(math.ceil(macro_h / grid))

    occupied = [[False] * ny for _ in range(nx)]
    for x1, y1, x2, y2 in shapes:
        gx1 = max(0, int((x1 - spacing) / grid))
        gy1 = max(0, int((y1 - spacing) / grid))
        gx2 = min(nx, int(math.ceil((x2 + spacing) / grid)))
        gy2 = min(ny, int(math.ceil((y2 + spacing) / grid)))
        for gx in range(gx1, gx2):
            for gy in range(gy1, gy2):
                occupied[gx][gy] = True

    rects: list[tuple[float, float, float, float]] = []
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

    visited = [[False] * ny for _ in range(nx)]
    for gy in range(ny):
        for run_start, run_end in row_runs[gy]:
            if visited[run_start][gy]:
                continue
            gy_end = gy + 1
            while gy_end < ny:
                found = False
                for rs, re in row_runs[gy_end]:
                    if rs == run_start and re == run_end:
                        found = True
                        break
                if found:
                    gy_end += 1
                else:
                    break
            for y in range(gy, gy_end):
                for x in range(run_start, run_end):
                    visited[x][y] = True
            rect = (
                _snap(run_start * grid),
                _snap(gy * grid),
                _snap(min(run_end * grid, macro_w)),
                _snap(min(gy_end * grid, macro_h)),
            )
            rects.append(rect)

    return rects
