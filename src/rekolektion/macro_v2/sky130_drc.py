"""SKY130B DRC rule constants used by macro_v2 routing primitives.

Sourced from:
- sky130A.tech (Magic)
- OpenRAM compiler/base/sky130.py
- SkyWater PDK drc/sky130A.lydrc

Values in microns. Layer tuples are (GDS layer, GDS datatype).
"""
from __future__ import annotations


# Manufacturing grid
MFG_GRID: float = 0.005  # 5 nm


# Metal minimum widths (um)
LI1_MIN_WIDTH: float = 0.17
MET1_MIN_WIDTH: float = 0.14
MET2_MIN_WIDTH: float = 0.14
MET3_MIN_WIDTH: float = 0.30
MET4_MIN_WIDTH: float = 0.30
MET5_MIN_WIDTH: float = 1.60  # yes — met5 has a much larger min width in SKY130

# Metal minimum spacings (um)
LI1_MIN_SPACE: float = 0.17
MET1_MIN_SPACE: float = 0.14
MET2_MIN_SPACE: float = 0.14
MET3_MIN_SPACE: float = 0.30
MET4_MIN_SPACE: float = 0.30
MET5_MIN_SPACE: float = 1.60

# Via dimensions (um) — all SKY130 vias are square cuts
MCON_SIZE: float = 0.17   # li1 to met1
VIA_SIZE: float = 0.15    # met1 to met2
VIA2_SIZE: float = 0.20   # met2 to met3
VIA3_SIZE: float = 0.20   # met3 to met4
VIA4_SIZE: float = 0.80   # met4 to met5 (large)

# Minimum enclosure of via by adjacent metal, per direction (um)
MET1_ENCLOSURE_VIA: float = 0.055
MET2_ENCLOSURE_VIA: float = 0.055
MET2_ENCLOSURE_VIA2: float = 0.040
MET3_ENCLOSURE_VIA2: float = 0.065
MET3_ENCLOSURE_VIA3: float = 0.060
MET4_ENCLOSURE_VIA3: float = 0.060
MET4_ENCLOSURE_VIA4: float = 0.210
MET5_ENCLOSURE_VIA4: float = 0.310


# GDS layer numbers (SKY130)
GDS_LAYER: dict[str, tuple[int, int]] = {
    # Metal drawing layers (purpose 20)
    "li1":  (67, 20),
    "met1": (68, 20),
    "met2": (69, 20),
    "met3": (70, 20),
    "met4": (71, 20),
    "met5": (72, 20),
    # Via layers (cut purpose 44)
    "mcon": (67, 44),
    "via":  (68, 44),
    "via2": (69, 44),
    "via3": (70, 44),
    "via4": (71, 44),
    # Pin purpose (16) — LEF port marker
    "met1.pin": (68, 16),
    "met2.pin": (69, 16),
    "met3.pin": (70, 16),
    "met4.pin": (71, 16),
    "met5.pin": (72, 16),
    # Label purpose (5) — text annotation tying to a net
    "met1.label": (68, 5),
    "met2.label": (69, 5),
    "met3.label": (70, 5),
    "met4.label": (71, 5),
    "met5.label": (72, 5),
}


_MIN_WIDTH: dict[str, float] = {
    "li1":  LI1_MIN_WIDTH,
    "met1": MET1_MIN_WIDTH,
    "met2": MET2_MIN_WIDTH,
    "met3": MET3_MIN_WIDTH,
    "met4": MET4_MIN_WIDTH,
    "met5": MET5_MIN_WIDTH,
}


_MIN_SPACE: dict[str, float] = {
    "li1":  LI1_MIN_SPACE,
    "met1": MET1_MIN_SPACE,
    "met2": MET2_MIN_SPACE,
    "met3": MET3_MIN_SPACE,
    "met4": MET4_MIN_SPACE,
    "met5": MET5_MIN_SPACE,
}


def layer_min_width(layer: str) -> float:
    return _MIN_WIDTH[layer]


def layer_min_space(layer: str) -> float:
    return _MIN_SPACE[layer]


def snap(value: float) -> float:
    """Snap a coordinate to the manufacturing grid (5 nm)."""
    return round(value / MFG_GRID) * MFG_GRID
