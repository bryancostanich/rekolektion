"""Audit DRC waivers spatially: split waiver tiles into "inside a known
foundry-cell footprint" vs "outside".  Tiles inside the bitcell array,
sense row, precharge row, or MWL driver column are legitimate
foundry-density waivers; tiles outside those footprints are SUSPECT —
the global rule-id waiver may be hiding a user-routing bug.

The current `_KNOWN_WAIVER_RULES` filter in `verify/drc.py` is global:
once a rule (e.g. `met1.2`) is on the waiver list, every tile fires
silently regardless of where it is.  This script does the spatial
follow-up that rule-id-only filtering can't do, so an actual
met1.2 / li.3 bug in user routing gets flagged.

Usage::

    python3 scripts/audit_drc_spatial.py [SRAM-A SRAM-B SRAM-C SRAM-D]

Reads `output/drc_cim/flat/<variant>/drc_results.log` (run
`run_drc_cim.py` first) and prints a per-variant breakdown.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams, build_cim_floorplan


# Magic emits coordinates in DBU = 1/200 µm.
_DBU_PER_UM = 200.0

# "  at: <x0> <y0> <x1> <y1>" — Magic prints all four corners.
_TILE_RE = re.compile(r"^\s*at:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$")
_VIOLATION_RE = re.compile(r"^Violation \((\d+) tiles\): (.*)$")


def _parse_tiles(log_path: Path) -> list[tuple[str, tuple[float, float, float, float]]]:
    """Returns [(rule_message, (x0_um, y0_um, x1_um, y1_um)), ...]."""
    out: list[tuple[str, tuple[float, float, float, float]]] = []
    current_msg = ""
    for line in log_path.read_text().splitlines():
        m = _VIOLATION_RE.match(line)
        if m:
            current_msg = m.group(2).strip()
            continue
        m = _TILE_RE.match(line)
        if m and current_msg:
            x0, y0, x1, y1 = (int(c) for c in m.groups())
            out.append((current_msg, (
                x0 / _DBU_PER_UM, y0 / _DBU_PER_UM,
                x1 / _DBU_PER_UM, y1 / _DBU_PER_UM,
            )))
    return out


def _foundry_footprints(variant: str) -> list[tuple[str, tuple[float, float, float, float]]]:
    """Returns [(name, (x0, y0, x1, y1)), ...] for each region whose
    interior is allowed to host foundry-density-pattern waivers.

    The footprints are inflated by `_OVERHANG_MARGIN` to absorb the
    bitcell / sense / precharge cell overhangs (each cell's GDS bbox
    extends past its tiling pitch by 0.17–0.5 µm to allow shared
    nwell / SDM / li1 between mirror-tiled neighbors).  Without this
    margin, tiles inside the cell's overhang region are misclassified
    as "outside the foundry footprint".
    """
    p = CIMMacroParams.from_variant(variant)
    fp = build_cim_floorplan(p)
    footprints: list[tuple[str, tuple[float, float, float, float]]] = []
    for name in ("array", "mwl_driver", "mbl_sense", "mbl_precharge"):
        x, y = fp.positions[name]
        w, h = fp.sizes[name]
        m = _OVERHANG_MARGIN
        footprints.append((name, (x - m, y - m, x + w + m, y + h + m)))
    return footprints


# Bitcell / sense / precharge cells extend up to ~0.5 µm past their
# tiling pitch (overhang for shared nwell / SDM).  Inflate every
# foundry footprint by this much before deciding "tile is inside".
_OVERHANG_MARGIN: float = 0.6


def _tile_inside(tile: tuple[float, float, float, float],
                 footprint: tuple[float, float, float, float]) -> bool:
    tx0, ty0, tx1, ty1 = tile
    fx0, fy0, fx1, fy1 = footprint
    # Tile center is enough — DRC tiles are tiny (sub-micron) and
    # always sit fully inside or fully outside a multi-µm footprint.
    cx = (tx0 + tx1) / 2.0
    cy = (ty0 + ty1) / 2.0
    return fx0 <= cx <= fx1 and fy0 <= cy <= fy1


def _audit_one(variant: str) -> None:
    p = CIMMacroParams.from_variant(variant)
    log = Path("output/drc_cim/flat") / p.top_cell_name / "drc_results.log"
    if not log.exists():
        print(f"[{variant}] no log at {log} — run run_drc_cim.py first")
        return
    tiles = _parse_tiles(log)
    footprints = _foundry_footprints(variant)
    counts_by_region: dict[str, int] = {n: 0 for n, _ in footprints}
    counts_outside: dict[str, int] = {}  # rule_msg → count
    for msg, tile in tiles:
        placed = False
        for fname, footprint in footprints:
            if _tile_inside(tile, footprint):
                counts_by_region[fname] += 1
                placed = True
                break
        if not placed:
            counts_outside[msg] = counts_outside.get(msg, 0) + 1

    total = len(tiles)
    inside = sum(counts_by_region.values())
    outside = total - inside
    print(f"\n=== {variant} ({p.top_cell_name}) ===")
    print(f"total tiles: {total}  inside foundry footprints: {inside}  outside: {outside}")
    for fname, cnt in counts_by_region.items():
        print(f"  inside {fname:<14}: {cnt}")
    if counts_outside:
        print(f"  OUTSIDE foundry footprints (suspect — review):")
        for msg, cnt in sorted(counts_outside.items(), key=lambda kv: -kv[1]):
            print(f"    {cnt:>5}  {msg}")
    else:
        print("  no waivers outside foundry footprints — global rule-id filter is safe here.")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("variants", nargs="*",
                    help="Variants to audit (default: all four)")
    args = ap.parse_args(argv[1:])
    variants = args.variants if args.variants else list(CIM_VARIANTS.keys())
    for v in variants:
        if v not in CIM_VARIANTS:
            raise SystemExit(f"Unknown variant {v!r}.")
    for v in variants:
        _audit_one(v)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
