"""Dump sample non-foundry tile locations for a specific waiver rule.

Usage:
    python3 scripts/debug_waiver_tiles.py 'Metal1 spacing < 0.14um (met1.2)'
"""
from __future__ import annotations

import subprocess
import sys
import tempfile
from pathlib import Path

import gdstk

from rekolektion.macro_v2.assembler import MacroV2Params, assemble
from rekolektion.tech.sky130 import pdk_path, magic_techfile, magic_rcfile
import sys as _sys
_sys.path.insert(0, str(Path(__file__).parent))
from audit_drc_waivers import (  # noqa: E402
    _collect_foundry_bboxes_recursive,
    _collect_top_block_bboxes,
    _tile_inside,
    MAGIC_UNIT_UM,
)


def main() -> None:
    if len(sys.argv) < 2:
        print("usage: debug_waiver_tiles.py '<exact rule message>'")
        sys.exit(1)
    target = sys.argv[1]

    p = MacroV2Params(words=32, bits=8, mux_ratio=4)
    tmp = Path(tempfile.mkdtemp(prefix="rekolektion_debug_"))
    lib, _ = assemble(p)
    gds = tmp / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))

    lib2 = gdstk.read_gds(str(gds))
    top = next(c for c in lib2.cells if c.name == p.top_cell_name)
    foundry = _collect_foundry_bboxes_recursive(top)
    blocks = _collect_top_block_bboxes(top)

    pr = pdk_path().parent
    tech = magic_techfile(pr)
    rc = magic_rcfile(pr)
    tcl = tmp / "t.tcl"
    results = tmp / "r.txt"
    tcl.write_text(
        f"""tech load {tech}
gds read {gds}
load {p.top_cell_name}
select top cell
drc catchup
set f [open {results} w]
set w [drc listall why]
foreach {{msg boxes}} $w {{
    if {{[string equal $msg "{target}"]}} {{
        foreach b $boxes {{ puts $f $b }}
    }}
}}
close $f
quit -noprompt
"""
    )
    import os
    env = os.environ.copy(); env["PDK_ROOT"] = str(pr)
    subprocess.run(
        ["magic", "-dnull", "-noconsole", "-rcfile", str(rc), str(tcl)],
        cwd=str(tmp), env=env, timeout=300,
        capture_output=True, text=True,
    )

    tiles: list[tuple[float, float, float, float]] = []
    for line in results.read_text().splitlines():
        parts = line.strip().split()
        if len(parts) == 4:
            try:
                tiles.append(tuple(int(v) * MAGIC_UNIT_UM for v in parts))
            except ValueError:
                pass

    print(f"Rule '{target}': {len(tiles)} total tiles")
    # Find which block each non-foundry tile lives in
    block_hits: dict[str, int] = {}
    outside_samples: list[tuple[float, float, float, float]] = []
    for t in tiles:
        cx, cy = (t[0] + t[2]) / 2, (t[1] + t[3]) / 2
        if any(_tile_inside(cx, cy, bb) for bb in foundry):
            continue
        located = False
        for name, bb in blocks:
            if _tile_inside(cx, cy, bb):
                block_hits[name] = block_hits.get(name, 0) + 1
                if block_hits[name] <= 3:
                    print(f"  IN BLOCK {name}: tile=({t[0]:.3f},{t[1]:.3f})-({t[2]:.3f},{t[3]:.3f}) center=({cx:.3f},{cy:.3f})")
                located = True
                break
        if not located:
            outside_samples.append(t)

    print()
    print("Per-block counts (non-foundry tiles):")
    for name, n in sorted(block_hits.items(), key=lambda kv: -kv[1]):
        print(f"  {name}: {n}")
    if outside_samples:
        print()
        print(f"Tiles OUTSIDE any block ({len(outside_samples)}):")
        for t in outside_samples[:5]:
            print(f"  ({t[0]:.3f},{t[1]:.3f})-({t[2]:.3f},{t[3]:.3f})")


if __name__ == "__main__":
    main()
