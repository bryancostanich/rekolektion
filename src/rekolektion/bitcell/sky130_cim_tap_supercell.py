"""CIM tap supercell — body-bias contact for CIM bitcell arrays.

Mirrors `sky130_sp_wlstrap_bridged.py` pattern: wraps the foundry
`sky130_fd_bd_sram__sram_sp_wlstrap` (which carries N+/P+ taps for
PMOS/NMOS body bias) with NWELL filler and SRAM-COREID marker, sized
to match the CIM bitcell supercell dimensions.  Inserted periodically
in CIM arrays via `cim_supercell_array._place_strap_columns`.

Width is the CIM bitcell pitch (2.31 µm).  The foundry wlstrap occupies
the LEFT 1.41 µm; the right 0.90 µm is filler matching the CIM bitcell's
east-annex (T7+cap) PSUB region.

Per-variant: supercell_h matches the bitcell variant.  Annex height
(y above the foundry strap) is `supercell_h - 1.88` (1.35–2.36 µm
depending on variant).

LVS topology mirrors production wlstrap_bridged: the foundry strap's
internal VPB (NWELL→VPWR) and VNB (PSUB→VGND) ports propagate up
through Magic's hierarchical extraction.  No wrapper-level rewiring
needed — same as production.

See conductor `projects/v1b_cim_module/tracks/02_sram_cim_cells/
cim_tap_supercell_plan.md` for full architecture rationale.
"""
from __future__ import annotations

from pathlib import Path
from typing import Tuple

import gdstk

from rekolektion.bitcell.sky130_cim_drain_bridge import BRIDGE_H
from rekolektion.bitcell.sky130_cim_supercell import (
    CIM_SUPERCELL_VARIANTS,
    SupercellVariant,
)


# Foundry wlstrap GDS lives in the production array cells folder
# (re-used across production and CIM — same foundry strap).
_FOUNDRY_STRAP_GDS = (
    Path(__file__).parent.parent
    / "array" / "cells" / "sky130_fd_bd_sram__sram_sp_wlstrap.gds"
)
_FOUNDRY_STRAP_NAME = "sky130_fd_bd_sram__sram_sp_wlstrap"

# Foundry wlstrap dimensions (matches the cell's LEF SIZE).
_FOUNDRY_STRAP_W: float = 1.410
_FOUNDRY_STRAP_H: float = 1.580

# Foundry strap NWELL X extent (from production wlstrap_bridged
# inspection: NWELL polygon spans foundry-local x=[0.000, 1.300];
# the cell pitch 1.410 has 0.110 µm right-side margin where adjacent
# N+ DIFF tucks under SRAM-COREID diff/tap.9 relaxation).  We mirror
# this exactly in the CIM tap so the foundry strap's internal NWELL
# enclosure rules behave identically.
_FOUNDRY_NWELL_X0: float = 0.000
_FOUNDRY_NWELL_X1: float = 1.300

# CIM bitcell supercell pitch (must match SupercellVariant.supercell_w).
# Hard-coded here because every variant has the same width — the only
# per-variant dimension is height (annex extent).
_TAP_SUPERCELL_W: float = 2.31

# GDS layer constants (sky130 standard mapping; must match the rest
# of the CIM supercell layer mapping).
_LAYER_NWELL       = (64, 20)
_LAYER_AREAID_SRAM = (81, 2)


def _rect(cell: gdstk.Cell, layer: Tuple[int, int],
          x0: float, y0: float, x1: float, y1: float) -> None:
    cell.add(gdstk.rectangle(
        (x0, y0), (x1, y1), layer=layer[0], datatype=layer[1]
    ))


def _load_foundry_strap() -> gdstk.Cell:
    """Load the foundry sram_sp_wlstrap GDS and return its top cell."""
    src = gdstk.read_gds(str(_FOUNDRY_STRAP_GDS))
    return next(c for c in src.cells if c.name == _FOUNDRY_STRAP_NAME)


def cell_name(variant: str) -> str:
    """Cell name for the given variant (matches the GDS top-cell name)."""
    if variant not in CIM_SUPERCELL_VARIANTS:
        raise ValueError(f"Unknown variant: {variant}")
    slug = variant.lower().replace("-", "_")
    return f"sky130_cim_tap_supercell_{slug}"


def create_cim_tap_supercell(
    variant: str,
) -> Tuple[gdstk.Library, SupercellVariant]:
    """Build the CIM tap supercell for one variant.

    Returns (library, variant_config).  The library contains the
    foundry sram_sp_wlstrap as a sub-cell and the wrapper top cell
    (`sky130_cim_tap_supercell_<variant>`) referencing it.

    Layout:
      y = 0 ........... BRIDGE_H (0.30):  NWELL filler + SRAM-COREID
                                          (matches BL-bridge area in
                                          bitcell supercell)
      y = BRIDGE_H .... BRIDGE_H + 1.58:  foundry sram_sp_wlstrap
                                          instance at (0, BRIDGE_H);
                                          provides N+ tap (→ VPWR) and
                                          P+ tap (→ VGND)
      y = 1.88 ........ supercell_h:      NWELL filler + SRAM-COREID
                                          (matches bitcell annex)

    X layout (independent of Y):
      x = 0 ........... 1.30:  NWELL filler in BRIDGE/annex regions
      x = 0 ........... 1.41:  foundry strap occupies this band (Y=BRIDGE..1.88)
      x = 0 ........... 2.31:  SRAM-COREID full extent
      x = 1.30 ........ 2.31:  PSUB (no NWELL) — matches bitcell east annex

    The 1.30 vs 1.41 X mismatch (0.11 µm) at the top/bottom of the foundry
    strap is the same gap production wlstrap_bridged carries — falls
    inside SRAM-COREID waivers.
    """
    if variant not in CIM_SUPERCELL_VARIANTS:
        raise ValueError(f"Unknown variant: {variant}")
    cfg = CIM_SUPERCELL_VARIANTS[variant]
    super_h = cfg.supercell_h

    name = cell_name(variant)
    lib = gdstk.Library(name=f"{name}_lib", unit=1e-6, precision=5e-9)

    # Load foundry strap unmodified.
    foundry = _load_foundry_strap()
    foundry_copy = foundry.copy(_FOUNDRY_STRAP_NAME)
    lib.add(foundry_copy)

    # Wrapper top cell.
    wrapper = gdstk.Cell(name)
    lib.add(wrapper)

    # Foundry strap shifted up by BRIDGE_H so its M1 VPWR/VGND rails
    # align with the bitcell supercells' M1 rails (both have foundry-
    # cell origin at wrapper-local y=BRIDGE_H).
    wrapper.add(gdstk.Reference(foundry_copy, origin=(0.0, BRIDGE_H)))

    # NWELL filler in the BRIDGE region (y=[0, BRIDGE_H]).  X range
    # matches foundry strap NWELL so the strap's internal NWELL rules
    # behave identically.
    _rect(wrapper, _LAYER_NWELL,
          _FOUNDRY_NWELL_X0, 0.0,
          _FOUNDRY_NWELL_X1, BRIDGE_H)

    # NWELL filler in the annex region (y=[BRIDGE_H+1.58, supercell_h]).
    # Same X range; the parent-level NWELL row strips bridge across
    # columns, so per-tap NWELL doesn't need to extend full width.
    _rect(wrapper, _LAYER_NWELL,
          _FOUNDRY_NWELL_X0, BRIDGE_H + _FOUNDRY_STRAP_H,
          _FOUNDRY_NWELL_X1, super_h)

    # SRAM-COREID covering the entire wrapper (relaxed-rule region for
    # the foundry strap's N+/P+/contacts).
    _rect(wrapper, _LAYER_AREAID_SRAM,
          0.0, 0.0, _TAP_SUPERCELL_W, super_h)

    return lib, cfg


def generate_tap_supercell_gds(
    variant: str, output_path: str | Path
) -> Path:
    """Generate the CIM tap supercell GDS for `variant` and write it."""
    lib, _ = create_cim_tap_supercell(variant)
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    lib.write_gds(str(out))
    return out


def generate_all_tap_supercells(
    output_dir: str | Path = "output/cim_tap_supercells",
) -> None:
    """Generate GDS files for all 4 tap supercell variants."""
    out_dir = Path(output_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    for variant in CIM_SUPERCELL_VARIANTS:
        slug = variant.lower().replace("-", "_")
        gds_path = out_dir / f"sky130_cim_tap_supercell_{slug}.gds"
        generate_tap_supercell_gds(variant, gds_path)
        cfg = CIM_SUPERCELL_VARIANTS[variant]
        print(
            f"  {variant}: tap supercell {_TAP_SUPERCELL_W:.3f} × "
            f"{cfg.supercell_h:.3f} µm² → {gds_path}"
        )


if __name__ == "__main__":
    generate_all_tap_supercells()
