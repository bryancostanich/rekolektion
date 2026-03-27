"""Command-line interface for rekolektion SRAM generator."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path


def _cmd_macro(args: argparse.Namespace) -> None:
    """Generate a complete SRAM macro."""
    from rekolektion.macro.assembler import generate_sram_macro
    from rekolektion.macro.outputs import generate_spice, generate_verilog
    from rekolektion.macro.lef_generator import generate_lef
    from rekolektion.macro.liberty_generator import generate_liberty

    output = Path(args.output)
    print(
        f"Generating SRAM macro: {args.words} words x {args.bits} bits, "
        f"mux ratio {args.mux}"
    )

    cell_type = getattr(args, "cell", "foundry")
    lib, params = generate_sram_macro(
        words=args.words,
        bits=args.bits,
        mux_ratio=args.mux,
        output_path=output,
        cell_type=cell_type,
    )

    print(f"Array: {params.rows} rows x {params.cols} columns")
    print(f"Address bits: {params.num_addr_bits} ({params.num_row_bits} row + {params.num_col_bits} col)")
    print(f"Macro dimensions: {params.macro_width:.3f} x {params.macro_height:.3f} um")
    print(f"GDS written to {output}")

    # Generate behavioral models alongside the GDS
    stem = output.stem
    out_dir = output.parent

    if args.spice:
        sp_path = generate_spice(params, out_dir / f"{stem}.sp")
        print(f"SPICE model written to {sp_path}")

    if args.verilog:
        v_path = generate_verilog(params, out_dir / f"{stem}.v")
        print(f"Verilog model written to {v_path}")

    if args.lef:
        lef_path = generate_lef(params, out_dir / f"{stem}.lef")
        print(f"LEF abstract written to {lef_path}")

    if args.liberty:
        lib_path = generate_liberty(params, out_dir / f"{stem}.lib")
        print(f"Liberty model written to {lib_path}")


def _cmd_array(args: argparse.Namespace) -> None:
    """Generate a tiled bitcell array."""
    from rekolektion.array.tiler import tile_array

    if args.cell == "foundry":
        from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
        bitcell = load_foundry_sp_bitcell()
    elif args.cell == "lr":
        from rekolektion.bitcell.sky130_6t_lr import load_lr_bitcell
        bitcell = load_lr_bitcell()
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
        with_dummy=args.with_dummy,
        strap_interval=args.strap_interval,
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

    # --- macro subcommand ---
    p_macro = sub.add_parser("macro", help="Generate a complete SRAM macro")
    p_macro.add_argument("--words", type=int, required=True, help="Number of words (memory depth)")
    p_macro.add_argument("--bits", type=int, required=True, help="Word width (data bits)")
    p_macro.add_argument("--mux", type=int, default=1, choices=[1, 2, 4, 8], help="Column mux ratio (default: 1)")
    p_macro.add_argument("--cell", default="foundry", choices=["foundry", "lr"], help="Bitcell type (default: foundry)")
    p_macro.add_argument("-o", "--output", required=True, help="Output GDS path")
    p_macro.add_argument("--spice", action="store_true", default=True, help="Generate SPICE model (default: True)")
    p_macro.add_argument("--no-spice", action="store_false", dest="spice", help="Skip SPICE model generation")
    p_macro.add_argument("--verilog", action="store_true", default=True, help="Generate Verilog model (default: True)")
    p_macro.add_argument("--no-verilog", action="store_false", dest="verilog", help="Skip Verilog model generation")
    p_macro.add_argument("--lef", action="store_true", default=True, help="Generate LEF abstract (default: True)")
    p_macro.add_argument("--no-lef", action="store_false", dest="lef", help="Skip LEF abstract generation")
    p_macro.add_argument("--liberty", action="store_true", default=True, help="Generate Liberty timing model (default: True)")
    p_macro.add_argument("--no-liberty", action="store_false", dest="liberty", help="Skip Liberty model generation")
    p_macro.set_defaults(func=_cmd_macro)

    # --- array subcommand ---
    p_array = sub.add_parser("array", help="Generate a tiled bitcell array")
    p_array.add_argument(
        "--cell",
        default="foundry",
        choices=["foundry", "lr"],
        help="Bitcell to use (default: foundry)",
    )
    p_array.add_argument("--rows", type=int, required=True, help="Number of rows (word lines)")
    p_array.add_argument("--cols", type=int, required=True, help="Number of columns (bit-line pairs)")
    p_array.add_argument("-o", "--output", required=True, help="Output GDS path")
    p_array.add_argument(
        "--with-dummy",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="Add dummy cell border (default: True)",
    )
    p_array.add_argument(
        "--strap-interval",
        type=int,
        default=16,
        metavar="N",
        help="Insert WL strap every N columns (0 = none, default: 16)",
    )
    p_array.set_defaults(func=_cmd_array)

    args = parser.parse_args(argv)
    if not hasattr(args, "func"):
        parser.print_help()
        sys.exit(1)
    args.func(args)


if __name__ == "__main__":
    main()
