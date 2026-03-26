#!/usr/bin/env python3
"""Convert a GDS layout to 3D STL and GLB using SKY130 layer stackup.

Extrudes each GDS layer into a 3D solid based on real SKY130 process
thicknesses. Outputs:
  - Per-layer STL files
  - Combined STL
  - Combined GLB with per-layer colors (viewable in macOS Finder/Quick Look)

Usage:
    python scripts/gds_to_stl.py [gds_file] [output_dir]
    python scripts/gds_to_stl.py output/sky130_sram_6t_bitcell.gds output/3d/

View the GLB in macOS Finder (spacebar preview) or any glTF viewer.

SKY130 layer thicknesses from the process cross-section:
    Substrate → Diffusion → Poly → LI1 → Met1 → Met2 → ...
"""

import sys
from pathlib import Path

import gdstk
import numpy as np
import triangle
from stl import mesh as stl_mesh


# ---------------------------------------------------------------------------
# SKY130 layer stackup: (gds_layer, gds_datatype) → (z_bottom, z_top) in μm
# Approximate thicknesses from SKY130 process documentation.
# ---------------------------------------------------------------------------

SKY130_STACKUP = {
    # (layer, datatype): (z_bot, z_top, name, color_hint)
    (64, 20):  (-0.20, 0.00, "nwell",  "#A0C8FF"),  # N-well (below surface)
    (65, 20):  (-0.10, 0.05, "diff",   "#FFD080"),  # Diffusion
    (65, 44):  (-0.10, 0.05, "tap",    "#FFD080"),  # Tap (same level as diff)
    (93, 44):  (-0.02, 0.02, "nsdm",   "#FFFF80"),  # N+ implant (skip for 3D)
    (94, 20):  (-0.02, 0.02, "psdm",   "#FF80FF"),  # P+ implant (skip for 3D)
    (66, 20):  ( 0.00, 0.18, "poly",   "#FF4040"),  # Polysilicon
    (66, 44):  ( 0.05, 0.43, "licon",  "#808080"),  # Contact: diff/poly → li1
    (67, 20):  ( 0.43, 0.53, "li1",    "#C080FF"),  # Local interconnect
    (67, 44):  ( 0.53, 0.89, "mcon",   "#606060"),  # Contact: li1 → met1
    (68, 20):  ( 0.89, 1.25, "met1",   "#4090FF"),  # Metal 1
    (68, 44):  ( 1.25, 1.61, "via",    "#505050"),  # Via: met1 → met2
    (69, 20):  ( 1.61, 1.97, "met2",   "#40FF90"),  # Metal 2
    (235, 4):  None,  # Boundary — skip
}

# Layers to skip in 3D (implants are not physical structures you'd want to see)
SKIP_LAYERS = {(93, 44), (94, 20), (235, 4)}


def polygon_to_triangles(vertices: np.ndarray) -> np.ndarray:
    """Triangulate a 2D polygon using constrained Delaunay triangulation.

    Handles triangles (3 vertices), quads (4), and complex polygons (5+).
    Removes duplicate/near-duplicate vertices that cause degenerate triangulations.

    Args:
        vertices: Nx2 array of polygon vertices.

    Returns:
        Mx3 array of triangle vertex indices.
    """
    n = len(vertices)
    if n < 3:
        return np.array([], dtype=int).reshape(0, 3)

    # Already a triangle — no triangulation needed
    if n == 3:
        return np.array([[0, 1, 2]])

    # Quad — split into two triangles (most common case)
    if n == 4:
        return np.array([[0, 1, 2], [0, 2, 3]])

    # Remove near-duplicate vertices (within 1nm) that cause degenerate triangulations
    clean_verts = [vertices[0]]
    clean_indices = [0]
    for i in range(1, n):
        dist = np.sqrt(np.sum((vertices[i] - clean_verts[-1]) ** 2))
        if dist > 0.001:  # 1nm threshold
            clean_verts.append(vertices[i])
            clean_indices.append(i)

    clean_verts = np.array(clean_verts)
    nc = len(clean_verts)

    if nc < 3:
        return np.array([], dtype=int).reshape(0, 3)
    if nc == 3:
        return np.array([[clean_indices[0], clean_indices[1], clean_indices[2]]])

    # Build segments connecting consecutive vertices
    segments = np.array([[i, (i + 1) % nc] for i in range(nc)])

    tri_input = {"vertices": clean_verts, "segments": segments}
    try:
        tri_output = triangle.triangulate(tri_input, "p")
        tris = tri_output.get("triangles", np.array([], dtype=int).reshape(0, 3))
        # Map back to original indices
        if len(tris) > 0:
            mapped = np.array([[clean_indices[t[0]], clean_indices[t[1]], clean_indices[t[2]]] for t in tris])
            return mapped
        return tris
    except Exception:
        # Fallback: simple fan triangulation using cleaned indices
        return np.array([[clean_indices[0], clean_indices[i], clean_indices[i + 1]]
                        for i in range(1, nc - 1)])


def extrude_polygon(vertices_2d: np.ndarray, z_bot: float, z_top: float) -> np.ndarray:
    """Extrude a 2D polygon into a 3D solid (triangulated surface mesh).

    Returns an Nx3x3 array of triangles (each triangle = 3 vertices × 3 coords).
    """
    n = len(vertices_2d)
    if n < 3:
        return np.array([]).reshape(0, 3, 3)

    triangles = []

    # Top and bottom faces
    tri_indices = polygon_to_triangles(vertices_2d)
    for idx in tri_indices:
        # Bottom face (reversed winding for outward normal)
        triangles.append([
            [vertices_2d[idx[0]][0], vertices_2d[idx[0]][1], z_bot],
            [vertices_2d[idx[2]][0], vertices_2d[idx[2]][1], z_bot],
            [vertices_2d[idx[1]][0], vertices_2d[idx[1]][1], z_bot],
        ])
        # Top face
        triangles.append([
            [vertices_2d[idx[0]][0], vertices_2d[idx[0]][1], z_top],
            [vertices_2d[idx[1]][0], vertices_2d[idx[1]][1], z_top],
            [vertices_2d[idx[2]][0], vertices_2d[idx[2]][1], z_top],
        ])

    # Side faces (quads split into two triangles each)
    for i in range(n):
        j = (i + 1) % n
        v0 = vertices_2d[i]
        v1 = vertices_2d[j]
        triangles.append([
            [v0[0], v0[1], z_bot],
            [v1[0], v1[1], z_bot],
            [v1[0], v1[1], z_top],
        ])
        triangles.append([
            [v0[0], v0[1], z_bot],
            [v1[0], v1[1], z_top],
            [v0[0], v0[1], z_top],
        ])

    return np.array(triangles)


def triangles_to_stl(all_triangles: list[np.ndarray], output_path: Path) -> None:
    """Write a list of triangle arrays to an STL file."""
    if not all_triangles:
        return

    combined = np.concatenate(all_triangles, axis=0)
    n = len(combined)
    stl_obj = stl_mesh.Mesh(np.zeros(n, dtype=stl_mesh.Mesh.dtype))
    for i, tri in enumerate(combined):
        stl_obj.vectors[i] = tri

    stl_obj.save(str(output_path))


def gds_to_stl(
    gds_path: str | Path,
    output_dir: str | Path = "output/3d",
    scale: float = 10.0,
) -> list[Path]:
    """Convert a GDS file to 3D STL files.

    Args:
        gds_path: Path to the input GDS file.
        output_dir: Directory for output STL files.
        scale: Scale factor (GDS is in μm; scale=10 → 10 units per μm for
               better visibility in 3D viewers).

    Returns:
        List of generated STL file paths.
    """
    gds_path = Path(gds_path)
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    lib = gdstk.read_gds(str(gds_path))
    top_cells = lib.top_level()
    if not top_cells:
        raise ValueError(f"No top-level cells found in {gds_path}")
    cell = top_cells[0]

    print(f"Processing cell: {cell.name}")
    print(f"Polygons: {len(cell.polygons)}, Labels: {len(cell.labels)}")

    # Group polygons by layer
    layer_polys: dict[tuple[int, int], list[np.ndarray]] = {}
    for poly in cell.polygons:
        key = (poly.layer, poly.datatype)
        if key not in layer_polys:
            layer_polys[key] = []
        layer_polys[key].append(poly.points)

    generated: list[Path] = []
    all_layer_triangles: list[np.ndarray] = []

    for layer_key, polys in sorted(layer_polys.items()):
        stackup = SKY130_STACKUP.get(layer_key)
        if stackup is None or layer_key in SKIP_LAYERS:
            continue

        z_bot, z_top, name, _ = stackup
        print(f"  Layer {layer_key} ({name}): {len(polys)} polygons, "
              f"z=[{z_bot:.2f}, {z_top:.2f}] μm")

        layer_triangles: list[np.ndarray] = []
        for pts in polys:
            tris = extrude_polygon(pts * scale, z_bot * scale, z_top * scale)
            if len(tris) > 0:
                layer_triangles.append(tris)

        if layer_triangles:
            # Per-layer STL
            layer_path = output_dir / f"{name}_{layer_key[0]}_{layer_key[1]}.stl"
            triangles_to_stl(layer_triangles, layer_path)
            generated.append(layer_path)
            all_layer_triangles.extend(layer_triangles)

    # Combined STL
    if all_layer_triangles:
        combined_path = output_dir / "bitcell_3d_combined.stl"
        triangles_to_stl(all_layer_triangles, combined_path)
        generated.append(combined_path)
        print(f"\nCombined STL: {combined_path}")

    # --- GLB with per-layer colors ---
    glb_path = _export_glb(gds_path, output_dir, scale)
    if glb_path:
        generated.append(glb_path)

    print(f"Generated {len(generated)} files in {output_dir}/")
    return generated


def _hex_to_rgba(hex_color: str, alpha: int = 255) -> list[int]:
    """Convert '#RRGGBB' to [R, G, B, A]."""
    h = hex_color.lstrip("#")
    return [int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), alpha]


def _export_glb(
    gds_path: Path, output_dir: Path, scale: float = 10.0
) -> Path | None:
    """Export a colored GLB (binary glTF 2.0) with per-layer materials.

    Uses the same mesh pipeline as the in-situ version (which renders
    correctly) but without the strata/font layers.
    """
    meshes = _build_glb_mesh_data(gds_path, scale)
    if not meshes:
        return None

    buffer_data = bytearray()
    accessors, buffer_views = [], []
    gltf_meshes, materials, nodes = [], [], []

    for name, color_f, vertices, indices in meshes:
        # Force fully opaque
        color_f = list(color_f)
        color_f[3] = 1.0
        _add_mesh_to_glb(
            name, color_f, vertices, indices,
            buffer_data, accessors, buffer_views,
            gltf_meshes, materials, nodes,
            alpha_mode="OPAQUE",
        )

    gltf = {
        "asset": {"version": "2.0", "generator": "rekolektion gds_to_stl.py"},
        "scene": 0,
        "scenes": [{"nodes": list(range(len(nodes)))}],
        "nodes": nodes,
        "meshes": gltf_meshes,
        "materials": materials,
        "accessors": accessors,
        "bufferViews": buffer_views,
        "buffers": [{"byteLength": len(buffer_data)}],
    }

    glb_path = output_dir / "bitcell_3d.glb"
    _write_glb(gltf, buffer_data, glb_path)

    print(f"GLB (colored): {glb_path}")
    return glb_path


def _write_glb(gltf: dict, buffer_data: bytearray, output_path: Path) -> None:
    """Write a glTF JSON + binary buffer to a GLB file."""
    import json
    import struct

    json_bytes = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    while len(json_bytes) % 4 != 0:
        json_bytes += b" "

    bin_bytes = bytes(buffer_data)
    total_length = 12 + 8 + len(json_bytes) + 8 + len(bin_bytes)

    with open(output_path, "wb") as f:
        f.write(struct.pack("<I", 0x46546C67))  # "glTF"
        f.write(struct.pack("<I", 2))
        f.write(struct.pack("<I", total_length))
        f.write(struct.pack("<I", len(json_bytes)))
        f.write(struct.pack("<I", 0x4E4F534A))  # "JSON"
        f.write(json_bytes)
        f.write(struct.pack("<I", len(bin_bytes)))
        f.write(struct.pack("<I", 0x004E4942))  # "BIN\0"
        f.write(bin_bytes)


def _build_glb_mesh_data(
    gds_path: Path, scale: float,
) -> list[tuple[str, list[float], np.ndarray, np.ndarray]]:
    """Extract per-layer mesh data from a GDS file.

    Returns list of (name, color_rgba_float, vertices_f32, indices_u32).
    Vertices are already in Y-up (glTF) orientation.
    """
    lib = gdstk.read_gds(str(gds_path))
    cell = lib.top_level()[0]

    layer_polys: dict[tuple[int, int], list[np.ndarray]] = {}
    for poly in cell.polygons:
        key = (poly.layer, poly.datatype)
        if key not in layer_polys:
            layer_polys[key] = []
        layer_polys[key].append(poly.points)

    meshes = []
    for layer_key, polys in sorted(layer_polys.items()):
        stackup = SKY130_STACKUP.get(layer_key)
        if stackup is None or layer_key in SKIP_LAYERS:
            continue
        z_bot, z_top, name, color_hex = stackup
        rgba = _hex_to_rgba(color_hex, alpha=255)
        color_f = [c / 255.0 for c in rgba]
        color_f[3] = 1.0  # force fully opaque

        all_verts = []
        for pts in polys:
            tris = extrude_polygon(pts * scale, z_bot * scale, z_top * scale)
            if len(tris) > 0:
                all_verts.append(tris.reshape(-1, 3))
        if not all_verts:
            continue

        vertices = np.concatenate(all_verts, axis=0).astype(np.float32)
        vertices = vertices[:, [0, 2, 1]]  # Z-up → Y-up
        # Fix winding order reversed by the Y↔Z swap
        for ti in range(0, len(vertices) - 2, 3):
            vertices[ti + 1], vertices[ti + 2] = (
                vertices[ti + 2].copy(), vertices[ti + 1].copy()
            )
        indices = np.arange(len(vertices), dtype=np.uint32)
        meshes.append((name, color_f, vertices, indices))

    return meshes


def _make_box(x0: float, y0: float, z0: float,
              x1: float, y1: float, z1: float) -> np.ndarray:
    """Create a simple axis-aligned box as triangles (Nx3x3).

    Coordinates are already in Y-up glTF space.
    """
    # 8 corners
    v = np.array([
        [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],  # bottom
        [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1],  # top
    ], dtype=np.float32)
    # 12 triangles (2 per face)
    faces = [
        [0,2,1],[0,3,2],  # bottom (-Y)
        [4,5,6],[4,6,7],  # top (+Y)
        [0,1,5],[0,5,4],  # front (-Z)
        [2,3,7],[2,7,6],  # back (+Z)
        [0,4,7],[0,7,3],  # left (-X)
        [1,2,6],[1,6,5],  # right (+X)
    ]
    tris = np.array([[v[f[0]], v[f[1]], v[f[2]]] for f in faces], dtype=np.float32)
    return tris


# ---------------------------------------------------------------------------
# Pixel font for 3D text labels (4×6 grid per character)
# Each character is a list of (row, col) positions where a pixel is "on".
# Row 0 = top, Col 0 = left. Characters are 4 wide × 6 tall.
# ---------------------------------------------------------------------------

_PIXEL_FONT: dict[str, list[tuple[int, int]]] = {
    "A": [(0,1),(0,2),(1,0),(1,3),(2,0),(2,1),(2,2),(2,3),(3,0),(3,3),(4,0),(4,3),(5,0),(5,3)],
    "B": [(0,0),(0,1),(0,2),(1,0),(1,3),(2,0),(2,1),(2,2),(3,0),(3,3),(4,0),(4,3),(5,0),(5,1),(5,2)],
    "C": [(0,1),(0,2),(0,3),(1,0),(2,0),(3,0),(4,0),(5,1),(5,2),(5,3)],
    "D": [(0,0),(0,1),(0,2),(1,0),(1,3),(2,0),(2,3),(3,0),(3,3),(4,0),(4,3),(5,0),(5,1),(5,2)],
    "E": [(0,0),(0,1),(0,2),(0,3),(1,0),(2,0),(2,1),(2,2),(3,0),(4,0),(5,0),(5,1),(5,2),(5,3)],
    "F": [(0,0),(0,1),(0,2),(0,3),(1,0),(2,0),(2,1),(2,2),(3,0),(4,0),(5,0)],
    "G": [(0,1),(0,2),(0,3),(1,0),(2,0),(3,0),(3,2),(3,3),(4,0),(4,3),(5,1),(5,2),(5,3)],
    "H": [(0,0),(0,3),(1,0),(1,3),(2,0),(2,1),(2,2),(2,3),(3,0),(3,3),(4,0),(4,3),(5,0),(5,3)],
    "I": [(0,0),(0,1),(0,2),(1,1),(2,1),(3,1),(4,1),(5,0),(5,1),(5,2)],
    "J": [(0,3),(1,3),(2,3),(3,3),(4,0),(4,3),(5,1),(5,2)],
    "K": [(0,0),(0,3),(1,0),(1,2),(2,0),(2,1),(3,0),(3,1),(4,0),(4,2),(5,0),(5,3)],
    "L": [(0,0),(1,0),(2,0),(3,0),(4,0),(5,0),(5,1),(5,2),(5,3)],
    "M": [(0,0),(0,3),(1,0),(1,1),(1,2),(1,3),(2,0),(2,2),(2,3),(3,0),(3,3),(4,0),(4,3),(5,0),(5,3)],
    "N": [(0,0),(0,3),(1,0),(1,1),(1,3),(2,0),(2,2),(2,3),(3,0),(3,3),(4,0),(4,3),(5,0),(5,3)],
    "O": [(0,1),(0,2),(1,0),(1,3),(2,0),(2,3),(3,0),(3,3),(4,0),(4,3),(5,1),(5,2)],
    "P": [(0,0),(0,1),(0,2),(1,0),(1,3),(2,0),(2,1),(2,2),(3,0),(4,0),(5,0)],
    "Q": [(0,1),(0,2),(1,0),(1,3),(2,0),(2,3),(3,0),(3,3),(4,0),(4,2),(5,1),(5,2),(5,3)],
    "R": [(0,0),(0,1),(0,2),(1,0),(1,3),(2,0),(2,1),(2,2),(3,0),(3,3),(4,0),(4,2),(5,0),(5,3)],
    "S": [(0,1),(0,2),(0,3),(1,0),(2,1),(2,2),(3,3),(4,3),(5,0),(5,1),(5,2)],
    "T": [(0,0),(0,1),(0,2),(0,3),(1,1),(1,2),(2,1),(2,2),(3,1),(3,2),(4,1),(4,2),(5,1),(5,2)],
    "U": [(0,0),(0,3),(1,0),(1,3),(2,0),(2,3),(3,0),(3,3),(4,0),(4,3),(5,1),(5,2)],
    "V": [(0,0),(0,3),(1,0),(1,3),(2,0),(2,3),(3,0),(3,3),(4,1),(4,2),(5,1),(5,2)],
    "W": [(0,0),(0,3),(1,0),(1,3),(2,0),(2,3),(3,0),(3,2),(3,3),(4,0),(4,1),(4,2),(4,3),(5,0),(5,3)],
    "X": [(0,0),(0,3),(1,0),(1,3),(2,1),(2,2),(3,1),(3,2),(4,0),(4,3),(5,0),(5,3)],
    "Y": [(0,0),(0,3),(1,0),(1,3),(2,1),(2,2),(3,1),(3,2),(4,1),(4,2),(5,1),(5,2)],
    "Z": [(0,0),(0,1),(0,2),(0,3),(1,3),(2,2),(3,1),(4,0),(5,0),(5,1),(5,2),(5,3)],
    "0": [(0,1),(0,2),(1,0),(1,3),(2,0),(2,3),(3,0),(3,3),(4,0),(4,3),(5,1),(5,2)],
    "1": [(0,1),(1,0),(1,1),(2,1),(3,1),(4,1),(5,0),(5,1),(5,2)],
    " ": [],
    "-": [(3,0),(3,1),(3,2),(3,3)],
    "/": [(5,0),(4,1),(3,2),(2,2),(1,3),(0,3)],
    "(": [(0,2),(1,1),(2,1),(3,1),(4,1),(5,2)],
    ")": [(0,1),(1,2),(2,2),(3,2),(4,2),(5,1)],
    ".": [(5,1)],
}


def _make_text_mesh(
    text: str,
    x: float, y_center: float, z: float,
    pixel_size: float = 0.3,
    depth: float = 0.1,
    face: str = "front",
) -> np.ndarray:
    """Generate 3D mesh triangles for a text string as pixel-font blocks.

    Text is placed on the specified face of the strata box.
    Coordinates are in Y-up glTF space (X=layout X, Y=height, Z=layout Y).

    Args:
        text: String to render (uppercase + digits + some punctuation).
        x: X position of the text start.
        y_center: Y center of the text (vertical center of the stratum).
        z: Z position of the text plane.
        pixel_size: Size of each font pixel.
        depth: How far the text extrudes from the surface.
        face: "front" (−Z face) or "back" (+Z face).

    Returns:
        Nx3x3 float32 array of triangles.
    """
    text = text.upper()
    char_w = 4  # pixels wide per char
    char_h = 6  # pixels tall per char
    char_spacing = 1  # pixel gap between chars

    total_h = char_h * pixel_size
    y_start = y_center + total_h / 2.0  # top of text (Y increases upward)

    # On the −Z face, X runs right-to-left from the viewer's perspective.
    # Mirror the text by building it right-to-left so it reads correctly.
    total_text_w = len(text) * (char_w + char_spacing) * pixel_size

    all_tris = []
    cursor_x = x + total_text_w  # start from the right, move left

    for ch in text:
        cursor_x -= (char_w + char_spacing) * pixel_size
        pixels = _PIXEL_FONT.get(ch, [])
        for row, col in pixels:
            # Mirror col: place (char_w - 1 - col) instead of col
            mirrored_col = (char_w - 1) - col
            px = cursor_x + mirrored_col * pixel_size
            py = y_start - row * pixel_size - pixel_size  # row 0 = top
            if face == "front":
                box = _make_box(
                    px, py, z - depth,
                    px + pixel_size, py + pixel_size, z,
                )
            else:
                box = _make_box(
                    px, py, z,
                    px + pixel_size, py + pixel_size, z + depth,
                )
            all_tris.append(box)

    if not all_tris:
        return np.array([], dtype=np.float32).reshape(0, 3, 3)

    return np.concatenate(all_tris, axis=0)


def _add_mesh_to_glb(
    name: str, color_f: list[float], vertices: np.ndarray, indices: np.ndarray,
    buffer_data: bytearray, accessors: list, buffer_views: list,
    gltf_meshes: list, materials: list, nodes: list,
    alpha_mode: str = "BLEND",
) -> None:
    """Add a mesh with material to the glTF builder lists."""
    mat_idx = len(materials)
    materials.append({
        "name": name,
        "pbrMetallicRoughness": {
            "baseColorFactor": color_f,
            "metallicFactor": 0.0 if alpha_mode == "BLEND" else 0.1,
            "roughnessFactor": 0.8 if alpha_mode == "BLEND" else 0.6,
        },
        "alphaMode": alpha_mode,
        "doubleSided": True,
    })

    # Indices
    idx_bytes = indices.tobytes()
    idx_bv_offset = len(buffer_data)
    buffer_data.extend(idx_bytes)
    while len(buffer_data) % 4 != 0:
        buffer_data.append(0)
    idx_bv_idx = len(buffer_views)
    buffer_views.append({
        "buffer": 0, "byteOffset": idx_bv_offset,
        "byteLength": len(idx_bytes), "target": 34963,
    })
    idx_acc_idx = len(accessors)
    accessors.append({
        "bufferView": idx_bv_idx, "componentType": 5125,
        "count": len(indices), "type": "SCALAR",
        "max": [int(indices.max())], "min": [int(indices.min())],
    })

    # Vertices
    vert_bytes = vertices.tobytes()
    vert_bv_offset = len(buffer_data)
    buffer_data.extend(vert_bytes)
    while len(buffer_data) % 4 != 0:
        buffer_data.append(0)
    vert_bv_idx = len(buffer_views)
    buffer_views.append({
        "buffer": 0, "byteOffset": vert_bv_offset,
        "byteLength": len(vert_bytes), "target": 34962, "byteStride": 12,
    })
    vert_acc_idx = len(accessors)
    v_min = vertices.min(axis=0).tolist()
    v_max = vertices.max(axis=0).tolist()
    accessors.append({
        "bufferView": vert_bv_idx, "componentType": 5126,
        "count": len(vertices), "type": "VEC3",
        "max": v_max, "min": v_min,
    })

    mesh_idx = len(gltf_meshes)
    gltf_meshes.append({
        "name": name,
        "primitives": [{"attributes": {"POSITION": vert_acc_idx},
                        "indices": idx_acc_idx, "material": mat_idx}],
    })
    nodes.append({"mesh": mesh_idx, "name": name})


def export_glb_in_situ(
    gds_path: str | Path, output_dir: str | Path = "output/3d",
    scale: float = 10.0,
) -> Path:
    """Export a GLB showing the bitcell embedded in semi-transparent process strata.

    Adds layers representing the silicon substrate, field oxide, gate oxide,
    ILD0 (poly-to-li1 dielectric), ILD1 (li1-to-met1 dielectric), and
    passivation — all as semi-transparent colored slabs surrounding the
    actual cell geometry.
    """
    gds_path = Path(gds_path)
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    # Get actual cell mesh data
    meshes = _build_glb_mesh_data(gds_path, scale)

    # Derive cell extents from the GDS bounding box (works for any cell)
    lib = gdstk.read_gds(str(gds_path))
    top = lib.top_level()[0]
    bbox = top.bounding_box()
    # Use actual min/max coordinates, not just width/height,
    # so strata covers geometry with negative offsets (e.g., nwell, M2 extensions)
    x_min = bbox[0][0] * scale
    y_min = bbox[0][1] * scale  # layout Y → glTF Z
    x_max = bbox[1][0] * scale
    y_max = bbox[1][1] * scale
    cw = x_max - x_min  # for text sizing only
    ch = y_max - y_min
    # Add a small margin around the strata so the cell features aren't flush with edges
    margin = 0.5 * scale

    # -----------------------------------------------------------------------
    # SKY130 process cross-section strata (z values in μm, scaled)
    # These are the dielectric / bulk layers between the metal features.
    # Coordinates in Y-up glTF space: X = layout X, Y = stack height, Z = layout Y
    # -----------------------------------------------------------------------
    strata = [
        # (name, z_bot, z_top, color_hex, alpha)
        ("Si substrate",    -0.50, -0.20, "#8B7355", 50),   # dark brown silicon
        ("P-well / N-well", -0.20,  0.00, "#A09080", 40),   # well implant region
        ("STI oxide",       -0.10,  0.00, "#D0D0E0", 35),   # field oxide
        ("Gate oxide",       0.00,  0.01, "#E8E8F8", 30),   # very thin gate dielectric
        ("ILD0 (pre-metal)"  ,0.18,  0.43, "#C8E8C8", 35),  # poly → li1 dielectric
        ("ILD1 (via0)",      0.53,  0.89, "#E8E0C8", 35),   # li1 → met1 dielectric
        ("IMD1 (above met1)",1.25,  1.50, "#D0D8E8", 30),   # above met1
        ("Passivation",      1.50,  1.60, "#E0E0E0", 25),   # top passivation
    ]

    # Build GLB
    buffer_data = bytearray()
    accessors, buffer_views = [], []
    gltf_meshes, materials, nodes = [], [], []

    # Text label sizing — tiny engraved labels
    pixel_size = 0.010 * scale   # each font pixel
    text_depth = 0.007 * scale   # extrusion depth
    text_x = x_min - margin + 0.1 * scale  # start X for labels
    text_z = y_min - margin       # front face Z

    # Add strata + text labels
    for sname, z_bot_um, z_top_um, color_hex, alpha in strata:
        z_bot = z_bot_um * scale
        z_top = z_top_um * scale
        # Box in Y-up space: X=[x_min−margin, x_max+margin], Y=[z_bot, z_top], Z=[y_min−margin, y_max+margin]
        box_tris = _make_box(
            x_min - margin, z_bot, y_min - margin,
            x_max + margin, z_top, y_max + margin,
        )
        verts = box_tris.reshape(-1, 3).astype(np.float32)
        idxs = np.arange(len(verts), dtype=np.uint32)
        rgba = _hex_to_rgba(color_hex, alpha=alpha)
        color_f = [c / 255.0 for c in rgba]

        _add_mesh_to_glb(
            sname, color_f, verts, idxs,
            buffer_data, accessors, buffer_views,
            gltf_meshes, materials, nodes,
            alpha_mode="BLEND",
        )

        # Text label on the front face (−Z side)
        y_center = (z_bot + z_top) / 2.0
        layer_thickness = z_top - z_bot
        # Scale font to fit within the stratum height
        effective_pixel = min(pixel_size, layer_thickness / 7.0)
        if effective_pixel < 0.008 * scale:
            continue  # too thin to label (e.g., gate oxide)

        text_tris = _make_text_mesh(
            sname,
            x=text_x,
            y_center=y_center,
            z=text_z,
            pixel_size=effective_pixel,
            depth=text_depth,
            face="front",
        )
        if len(text_tris) > 0:
            # White-ish text, mostly opaque
            text_verts = text_tris.reshape(-1, 3).astype(np.float32)
            text_idxs = np.arange(len(text_verts), dtype=np.uint32)
            text_color = [1.0, 1.0, 1.0, 0.9]
            _add_mesh_to_glb(
                f"label: {sname}", text_color, text_verts, text_idxs,
                buffer_data, accessors, buffer_views,
                gltf_meshes, materials, nodes,
                alpha_mode="BLEND",
            )

    # Add actual cell features — fully opaque to avoid transparency artifacts
    for name, color_f, vertices, indices in meshes:
        color_f = list(color_f)
        color_f[3] = 1.0  # fully opaque
        _add_mesh_to_glb(
            name, color_f, vertices, indices,
            buffer_data, accessors, buffer_views,
            gltf_meshes, materials, nodes,
            alpha_mode="OPAQUE",
        )

    gltf = {
        "asset": {"version": "2.0", "generator": "rekolektion gds_to_stl.py (in-situ)"},
        "scene": 0,
        "scenes": [{"nodes": list(range(len(nodes)))}],
        "nodes": nodes,
        "meshes": gltf_meshes,
        "materials": materials,
        "accessors": accessors,
        "bufferViews": buffer_views,
        "buffers": [{"byteLength": len(buffer_data)}],
    }

    glb_path = output_dir / "bitcell_3d_in_situ.glb"
    _write_glb(gltf, buffer_data, glb_path)
    print(f"GLB (in-situ): {glb_path}")
    return glb_path


if __name__ == "__main__":
    gds_file = sys.argv[1] if len(sys.argv) > 1 else "output/sky130_sram_6t_bitcell.gds"
    out_dir = sys.argv[2] if len(sys.argv) > 2 else "output/3d"
    gds_to_stl(gds_file, out_dir)
    export_glb_in_situ(gds_file, out_dir)
