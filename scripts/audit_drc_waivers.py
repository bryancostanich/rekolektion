"""Audit every DRC waiver rule by verifying tile provenance.

For each rule currently in _KNOWN_WAIVER_RULES / _KNOWN_WAIVER_MESSAGES,
runs DRC on a representative assembled macro and classifies each tile by
whether its centre lands inside a FOUNDRY cell's footprint (waiver
justified) or somewhere else (inter-block gap, our own drawn geometry —
potentially a real bug).

A foundry cell is any gdstk cell whose name starts with
"sky130_fd_bd_sram__" or "sky130_fd_bd_sram_" (both with and without the
double underscore — OpenRAM cell naming is inconsistent).

Result columns per rule:
  total    : total tile count
  in_fd    : tiles whose centre is inside a foundry cell footprint
  in_block : tiles inside a non-foundry block (e.g. inside the spanning
             WL/BL strips we added in bitcell_array) — suspicious
  outside  : tiles fully outside any known block — definitely suspicious

A rule is a JUSTIFIED waiver iff in_block == 0 and outside == 0.
"""
from __future__ import annotations

import subprocess
import tempfile
from pathlib import Path

import gdstk

from rekolektion.macro.assembler import MacroParams, assemble
from rekolektion.tech.sky130 import pdk_path, magic_techfile, magic_rcfile
from rekolektion.verify.drc import _KNOWN_WAIVER_RULES, _KNOWN_WAIVER_MESSAGES


# Magic internal unit: 5 nm for sky130 (scalefactor 10, multiplier 2).
MAGIC_UNIT_UM: float = 0.005


def _foundry_cell(name: str) -> bool:
    """True for third-party (foundry / OpenRAM) cells we don't own.

    - sky130_fd_bd_sram__*   — SkyWater SRAM library
    - precharge_0, single_level_column_mux — OpenRAM
    - Their internal sub-cells: contact_*, pmos_*, nmos_*, *_cell_opt1
    """
    prefixes = (
        "sky130_fd_bd_sram_",
        "sky130_sram_",
        "contact_",
        "pmos_",
        "nmos_",
    )
    exact = {
        "precharge_0",
        "single_level_column_mux",
    }
    return name in exact or any(name.startswith(p) for p in prefixes)


def _collect_foundry_bboxes_recursive(
    cell: gdstk.Cell,
) -> list[tuple[float, float, float, float]]:
    """Return absolute bounding boxes of every foundry-cell instance
    reachable from `cell` via references.

    Uses a manual transform walk: parent x/y translation AND
    x_reflection compose as we descend into non-foundry sub-cells.
    Rotation is assumed 0 (our blocks don't rotate references).
    """
    bboxes: list[tuple[float, float, float, float]] = []

    def _walk(
        c: gdstk.Cell,
        parent_dx: float,
        parent_dy: float,
        parent_xr: bool,
    ) -> None:
        for ref in c.references:
            rx, ry = ref.origin
            # Compose parent x_reflection into this ref's origin.
            if parent_xr:
                ry = -ry
            child_dx = parent_dx + rx
            child_dy = parent_dy + ry
            child_xr = parent_xr ^ ref.x_reflection
            if _foundry_cell(ref.cell.name):
                bb = ref.cell.bounding_box()
                if bb is None:
                    continue
                x0, y0 = bb[0]
                x1, y1 = bb[1]
                # Apply composed x_reflection around the child's own
                # origin (reflection happens BEFORE translation in
                # gdstk's convention).
                if child_xr:
                    y0, y1 = -y1, -y0
                bboxes.append(
                    (
                        x0 + child_dx, y0 + child_dy,
                        x1 + child_dx, y1 + child_dy,
                    )
                )
            else:
                _walk(ref.cell, child_dx, child_dy, child_xr)

    _walk(cell, 0.0, 0.0, False)
    return bboxes


def _collect_top_block_bboxes(
    top: gdstk.Cell,
) -> list[tuple[str, tuple[float, float, float, float]]]:
    """Return (block_name, abs_bbox) for each immediate sub-block
    placed directly in the macro top cell."""
    out: list[tuple[str, tuple[float, float, float, float]]] = []
    for ref in top.references:
        bb = ref.cell.bounding_box()
        if bb is None:
            continue
        x0 = bb[0][0] + ref.origin[0]
        y0 = bb[0][1] + ref.origin[1]
        x1 = bb[1][0] + ref.origin[0]
        y1 = bb[1][1] + ref.origin[1]
        out.append((ref.cell.name, (x0, y0, x1, y1)))
    return out


def _tile_inside(
    cx: float, cy: float, bb: tuple[float, float, float, float]
) -> bool:
    x0, y0, x1, y1 = bb
    return x0 <= cx <= x1 and y0 <= cy <= y1


def run_audit(p: MacroParams) -> dict:
    tmp = Path(tempfile.mkdtemp(prefix="rekolektion_audit_"))
    lib, _ = assemble(p)
    gds = tmp / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))

    # Reload the written GDS so `cell.references` is canonical.
    lib2 = gdstk.read_gds(str(gds))
    top = next(c for c in lib2.cells if c.name == p.top_cell_name)

    foundry_bboxes = _collect_foundry_bboxes_recursive(top)
    block_bboxes = _collect_top_block_bboxes(top)

    # Run DRC and capture raw per-rule tile lists via a Tcl probe.
    pr = pdk_path().parent
    tech = magic_techfile(pr)
    rc = magic_rcfile(pr)
    tcl = tmp / "audit.tcl"
    results_path = tmp / "audit_results.txt"
    tcl.write_text(
        f"""tech load {tech}
gds read {gds}
load {p.top_cell_name}
select top cell
drc catchup
set f [open {results_path} w]
set w [drc listall why]
foreach {{msg boxes}} $w {{
    puts $f "RULE: $msg"
    foreach b $boxes {{
        puts $f "  $b"
    }}
}}
close $f
quit -noprompt
"""
    )
    import os

    env = os.environ.copy()
    env["PDK_ROOT"] = str(pr)
    subprocess.run(
        ["magic", "-dnull", "-noconsole", "-rcfile", str(rc), str(tcl)],
        cwd=str(tmp),
        env=env,
        capture_output=True,
        text=True,
        timeout=300,
    )

    # Parse rule → tiles. Each tile is a (x0, y0, x1, y1) in um.
    rules: dict[str, list[tuple[float, float, float, float]]] = {}
    current_rule: str | None = None
    for line in results_path.read_text().splitlines():
        if line.startswith("RULE: "):
            current_rule = line[len("RULE: "):]
            rules.setdefault(current_rule, [])
        elif current_rule is not None and line.startswith("  "):
            parts = line.strip().split()
            if len(parts) == 4:
                try:
                    x0, y0, x1, y1 = (int(v) * MAGIC_UNIT_UM for v in parts)
                    rules[current_rule].append((x0, y0, x1, y1))
                except ValueError:
                    pass

    # Classify each rule's tiles.
    results: list[dict] = []
    for rule_msg, tiles in rules.items():
        in_fd = in_block = outside = 0
        for t in tiles:
            cx = (t[0] + t[2]) / 2
            cy = (t[1] + t[3]) / 2
            if any(_tile_inside(cx, cy, bb) for bb in foundry_bboxes):
                in_fd += 1
            elif any(_tile_inside(cx, cy, bb) for _, bb in block_bboxes):
                in_block += 1
            else:
                outside += 1
        results.append(
            {
                "rule": rule_msg,
                "total": len(tiles),
                "in_fd": in_fd,
                "in_block": in_block,
                "outside": outside,
            }
        )

    return {
        "results": sorted(results, key=lambda r: -r["total"]),
        "foundry_bboxes_count": len(foundry_bboxes),
        "block_bboxes_count": len(block_bboxes),
    }


def _rule_ids_in(msg: str) -> list[str]:
    import re

    m = re.search(r"\(([^()]+)\)\s*$", msg)
    if not m:
        return []
    inner = m.group(1).strip()
    return [s.strip() for s in re.split(r"\s*[-+]\s*", inner) if s.strip()]


def main() -> None:
    p = MacroParams(words=32, bits=8, mux_ratio=4)
    audit = run_audit(p)
    print(
        f"\nFootprints: {audit['foundry_bboxes_count']} foundry-cell "
        f"bboxes, {audit['block_bboxes_count']} top-level block bboxes.\n"
    )
    print(
        f"{'status':8s}  {'total':>8s}  {'in_fd':>8s}  {'in_block':>8s}  "
        f"{'outside':>8s}  rule"
    )
    print("-" * 100)
    bad_rules: list[str] = []
    for r in audit["results"]:
        in_fd = r["in_fd"]
        in_block = r["in_block"]
        outside = r["outside"]
        status = "OK" if (in_block == 0 and outside == 0) else "SUSPECT"
        # Check if this rule is in our waiver list
        ids = _rule_ids_in(r["rule"])
        is_waived = (
            all(i in _KNOWN_WAIVER_RULES for i in ids) if ids
            else r["rule"].strip() in _KNOWN_WAIVER_MESSAGES
        )
        tag = f"{status}{'*' if is_waived else ''}"
        print(
            f"{tag:8s}  {r['total']:>8d}  {in_fd:>8d}  {in_block:>8d}  "
            f"{outside:>8d}  {r['rule']}"
        )
        if is_waived and (in_block > 0 or outside > 0):
            bad_rules.append(r["rule"])
    print()
    print(
        "* = currently waived. "
        "SUSPECT rows with * = waiver masks tiles outside foundry cells."
    )
    if bad_rules:
        print("\nWAIVERS WITH NON-FOUNDRY TILES (review these):")
        for r in bad_rules:
            print(f"  - {r}")
    else:
        print("\nAll current waivers are justified: every tile sits "
              "inside a foundry cell footprint.")


if __name__ == "__main__":
    main()
