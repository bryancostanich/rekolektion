"""CLI: 2×2 abutment DRC validator for any SRAM/CIM bitcell.

Tile a cell into a 2×2 with a configurable mirror pattern and run Magic
DRC on the assembled tile via ``rekolektion.verify.drc.run_drc``. Auto-
opens the resulting tile in ``rekolektion-viz`` so the geometry is
eyeball-verifiable (counters the identity-transform-as-mirror anti-
pattern — DRC clean is no proof a tile is actually mirrored).

Usage:
    python scripts/validate_2x2_abutment.py --cell-gds <path.gds>
        [--top-cell <NAME>]
        [--mirror-pattern {xy,y,x,none}]
        [--out-dir <PATH>]
        [--no-viz]

Exit 0 on DRC clean, 1 on real errors.

For library use see :mod:`rekolektion.verify.abutment_2x2`.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

from rekolektion.verify.abutment_2x2 import mag_to_gds, validate_abutment_2x2


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(
        description="2×2 abutment DRC validator for SRAM/CIM bitcells",
    )
    src = p.add_mutually_exclusive_group(required=True)
    src.add_argument("--cell-gds", type=Path,
                     help="Source GDS containing the bitcell to tile.")
    src.add_argument("--cell-mag", type=Path,
                     help="Magic .mag source; gds-written internally before "
                          "tiling (saves a manual `gds write` between Magic "
                          "edits and the validator). Top cell defaults to the "
                          "file stem; sub-cells must be sibling .mag files.")
    p.add_argument("--top-cell", default=None,
                   help="Top cell name. With --cell-mag, defaults to the file "
                        "stem; with --cell-gds, defaults to auto-discover "
                        "(filtering Magic's (UNNAMED) scratch buffer).")
    p.add_argument("--mirror-pattern", default="xy",
                   choices=("xy", "y", "x", "none"),
                   help="Tile-time mirror pattern (default: xy — the standard "
                        "symmetric-bitcell pattern).")
    p.add_argument("--out-dir", type=Path, default=None,
                   help="Output dir for tile + DRC artifacts "
                        "(default: <source_parent>/abutment_2x2/).")
    p.add_argument("--no-viz", action="store_true",
                   help="Skip auto-opening rekolektion-viz on the tile.")
    args = p.parse_args(argv)

    # Resolve out_dir up-front so both branches can share it.
    source = args.cell_mag if args.cell_mag else args.cell_gds
    out_dir = args.out_dir if args.out_dir else source.parent / "abutment_2x2"
    out_dir.mkdir(parents=True, exist_ok=True)

    print("2×2 abutment DRC validator")
    if args.cell_mag:
        print(f"  cell MAG:       {args.cell_mag}")
    print(f"  mirror pattern: {args.mirror_pattern}")
    print()

    # If given a .mag, stream out to GDS first; otherwise use the GDS as-is.
    try:
        if args.cell_mag:
            print(f"  streaming .mag → GDS via Magic (cwd={args.cell_mag.parent})")
            cell_gds = mag_to_gds(
                args.cell_mag,
                out_gds=out_dir / f"{args.cell_mag.stem}.gds",
                top_cell=args.top_cell,
            )
            print(f"  cell GDS:       {cell_gds}  (streamed)")
            top = args.top_cell or args.cell_mag.stem
        else:
            cell_gds = args.cell_gds
            print(f"  cell GDS:       {cell_gds}")
            top = args.top_cell
        print(f"  top cell:       {top or '(auto-discover)'}")
        print()

        result = validate_abutment_2x2(
            cell_gds,
            top_cell=top,
            mirror_pattern=args.mirror_pattern,
            out_dir=out_dir,
        )
    except (FileNotFoundError, ValueError, RuntimeError) as e:
        sys.exit(f"ERROR: {e}")

    cw, ch = result.cell_pitch
    tw, th = result.tile_pitch
    print(f"  tile written:   {result.tile_gds}")
    print(f"  parent cell:    {result.parent_cell}")
    print(f"  cell pitch:     {cw:.3f} × {ch:.3f} µm")
    print(f"  tile pitch:     {tw:.3f} × {th:.3f} µm")
    print()
    print(result.drc.summary())
    print(f"  log:            {result.drc.log_path}")

    if result.drc.real_errors:
        print()
        print("  real errors:")
        for line in result.drc.real_errors[:25]:
            print(f"    {line}")

    if not args.no_viz:
        viz = shutil.which("rekolektion-viz")
        if viz is None:
            print()
            print("  viz: rekolektion-viz not on PATH — skipping auto-open. "
                  "Add ~/.local/bin/rekolektion-viz symlink or pass --no-viz "
                  "to silence.")
        else:
            print()
            print(f"  viz: launching rekolektion-viz app on {result.tile_gds}")
            subprocess.Popen(
                [viz, "app", str(result.tile_gds)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                stdin=subprocess.DEVNULL,
                start_new_session=True,
            )

    print()
    failed = bool(result.drc.real_errors)
    print("RESULT:", "FAIL" if failed else "PASS")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
