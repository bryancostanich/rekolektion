#!/usr/bin/env python3
"""Render GDS layout layers to PNG images for visual comparison.

Generates top-down and isometric views of each layer independently,
plus a composite view. No OpenGL or 3D library needed — uses simple
orthographic projection with PIL.

Usage:
    python scripts/render_cell.py output/foundry_sp_cell.gds output/renders/foundry/
    python scripts/render_cell.py output/sky130_6t_lr.gds output/renders/lr/
"""

import sys
from pathlib import Path

import gdstk
import numpy as np
from PIL import Image, ImageDraw

# Layer colors (RGBA)
LAYER_COLORS = {
    (64, 20): (160, 200, 255, 80),    # nwell - light blue, transparent
    (65, 20): (255, 208, 128, 200),    # diff - orange
    (65, 44): (255, 208, 128, 200),    # tap - orange
    (66, 20): (255, 64, 64, 220),      # poly - red
    (66, 44): (128, 128, 128, 240),    # licon - gray
    (67, 20): (192, 128, 255, 200),    # li1 - purple
    (67, 44): (96, 96, 96, 240),       # mcon - dark gray
    (68, 20): (64, 144, 255, 200),     # met1 - blue
    (68, 44): (80, 80, 80, 240),       # via - dark gray
    (69, 20): (64, 255, 144, 200),     # met2 - green
    (93, 44): (255, 255, 128, 60),     # nsdm - yellow, faint
    (94, 20): (255, 128, 255, 60),     # psdm - pink, faint
}

LAYER_NAMES = {
    (64, 20): "nwell", (65, 20): "diff", (65, 44): "tap",
    (66, 20): "poly", (66, 44): "licon", (67, 20): "li1",
    (67, 44): "mcon", (68, 20): "met1", (68, 44): "via",
    (69, 20): "met2", (93, 44): "nsdm", (94, 20): "psdm",
}

# Layers to render (in order, bottom to top)
RENDER_LAYERS = [
    (64, 20), (65, 20), (65, 44), (93, 44), (94, 20),
    (66, 20), (66, 44), (67, 20), (67, 44),
    (68, 20), (68, 44), (69, 20),
]


def render_top_down(cell: gdstk.Cell, output_dir: Path, scale: int = 600,
                    margin: int = 20) -> dict[str, Path]:
    """Render top-down PNG for each layer and a composite."""
    bb = cell.bounding_box()
    if bb is None:
        return {}

    x0, y0 = bb[0]
    x1, y1 = bb[1]
    w = x1 - x0
    h = y1 - y0

    img_w = int(w * scale) + 2 * margin
    img_h = int(h * scale) + 2 * margin

    def to_px(x, y):
        px = int((x - x0) * scale) + margin
        py = img_h - (int((y - y0) * scale) + margin)  # flip Y
        return (px, py)

    def poly_to_points(points):
        return [to_px(p[0], p[1]) for p in points]

    # Group polygons by layer
    layer_polys = {}
    for p in cell.polygons:
        key = (p.layer, p.datatype)
        if key not in layer_polys:
            layer_polys[key] = []
        layer_polys[key].append(p.points)

    output_dir.mkdir(parents=True, exist_ok=True)
    results = {}

    # Render each layer individually
    for layer_key in RENDER_LAYERS:
        if layer_key not in layer_polys:
            continue

        name = LAYER_NAMES.get(layer_key, f"L{layer_key[0]}_{layer_key[1]}")
        color = LAYER_COLORS.get(layer_key, (200, 200, 200, 150))

        img = Image.new("RGBA", (img_w, img_h), (0, 0, 0, 255))
        draw = ImageDraw.Draw(img)

        # Draw cell boundary
        corners = [to_px(x0, y0), to_px(x1, y0), to_px(x1, y1), to_px(x0, y1)]
        draw.polygon(corners, outline=(60, 60, 60, 128))

        for pts in layer_polys[layer_key]:
            px_pts = poly_to_points(pts)
            if len(px_pts) >= 3:
                draw.polygon(px_pts, fill=color, outline=(255, 255, 255, 100))

        path = output_dir / f"layer_{name}.png"
        img.save(str(path))
        results[name] = path

    # Composite: all layers overlaid
    composite = Image.new("RGBA", (img_w, img_h), (20, 20, 30, 255))
    draw = ImageDraw.Draw(composite)

    # Draw cell boundary
    corners = [to_px(x0, y0), to_px(x1, y0), to_px(x1, y1), to_px(x0, y1)]
    draw.polygon(corners, outline=(80, 80, 80, 128))

    for layer_key in RENDER_LAYERS:
        if layer_key not in layer_polys:
            continue
        color = LAYER_COLORS.get(layer_key, (200, 200, 200, 100))
        # Use a temporary layer for alpha compositing
        layer_img = Image.new("RGBA", (img_w, img_h), (0, 0, 0, 0))
        layer_draw = ImageDraw.Draw(layer_img)

        for pts in layer_polys[layer_key]:
            px_pts = poly_to_points(pts)
            if len(px_pts) >= 3:
                layer_draw.polygon(px_pts, fill=color, outline=(*color[:3], min(255, color[3] + 40)))

        composite = Image.alpha_composite(composite, layer_img)

    path = output_dir / "composite.png"
    composite.save(str(path))
    results["composite"] = path

    return results


def main():
    gds_path = sys.argv[1] if len(sys.argv) > 1 else "output/foundry_sp_cell.gds"
    output_dir = sys.argv[2] if len(sys.argv) > 2 else "output/renders/"

    lib = gdstk.read_gds(gds_path)
    cell = lib.top_level()[0]

    bb = cell.bounding_box()
    w = bb[1][0] - bb[0][0]
    h = bb[1][1] - bb[0][1]
    print(f"Rendering: {cell.name} ({w:.3f} x {h:.3f} um)")
    print(f"Polygons: {len(cell.polygons)}")

    results = render_top_down(cell, Path(output_dir))

    print(f"\nGenerated {len(results)} images in {output_dir}:")
    for name, path in sorted(results.items()):
        print(f"  {name}: {path}")


if __name__ == "__main__":
    main()
