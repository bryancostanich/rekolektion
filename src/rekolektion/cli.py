"""Command-line interface for rekolektion SRAM generator."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path


def _cmd_array(args: argparse.Namespace) -> None:
    """Generate a tiled bitcell array."""
    from rekolektion.array.tiler import tile_array

    if args.cell == "foundry":
        from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell

        bitcell = load_foundry_sp_bitcell()
    else:
        print(f"Unknown cell type: {args.cell}", file=sys.stderr)
        sys.exit(1)

    output = Path(args.output)
    print(
        f"Generating {args.rows}x{args.cols} array "
        f"using {bitcell.cell_name} ({bitcell.cell_width:.3f} x "
        f"{bitcell.cell_height:.3f} um)"
    )

    lib = tile_array(
        bitcell,
        num_rows=args.rows,
        num_cols=args.cols,
        output_path=output,
    )

    # Report results.
    array_cell = None
    for c in lib.cells:
        if "array" in c.name:
            array_cell = c
            break

    if array_cell:
        bb = array_cell.bounding_box()
        if bb is not None:
            w = bb[1][0] - bb[0][0]
            h = bb[1][1] - bb[0][1]
            print(f"Array dimensions: {w:.3f} x {h:.3f} um")
            print(f"Array area: {w * h:.2f} um^2")

    print(f"Written to {output}")


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(
        prog="rekolektion",
        description="SRAM generator for SKY130",
    )
    sub = parser.add_subparsers(dest="command")

    # --- array subcommand ---
    p_array = sub.add_parser("array", help="Generate a tiled bitcell array")
    p_array.add_argument(
        "--cell",
        default="foundry",
        choices=["foundry"],
        help="Bitcell to use (default: foundry)",
    )
    p_array.add_argument("--rows", type=int, required=True, help="Number of rows (word lines)")
    p_array.add_argument("--cols", type=int, required=True, help="Number of columns (bit-line pairs)")
    p_array.add_argument("-o", "--output", required=True, help="Output GDS path")
    p_array.set_defaults(func=_cmd_array)

    args = parser.parse_args(argv)
    if not hasattr(args, "func"):
        parser.print_help()
        sys.exit(1)
    args.func(args)


if __name__ == "__main__":
    main()
