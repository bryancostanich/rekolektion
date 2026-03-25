"""Command-line interface for rekolektion SRAM generator."""

import argparse
import sys


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="rekolektion",
        description="Open-source SRAM generator for SkyWater SKY130",
    )
    parser.add_argument("--version", action="version", version=f"%(prog)s {_get_version()}")

    subparsers = parser.add_subparsers(dest="command")

    # Phase 1: Generate a single 6T bitcell
    bitcell_parser = subparsers.add_parser("bitcell", help="Generate a 6T SRAM bitcell")
    bitcell_parser.add_argument(
        "-o", "--output", default="sky130_6t_bitcell.gds", help="Output GDS file path"
    )
    bitcell_parser.add_argument(
        "--spice", action="store_true", help="Also generate SPICE netlist"
    )

    args = parser.parse_args(argv)

    if args.command == "bitcell":
        from rekolektion.bitcell.sky130_6t import generate_bitcell

        generate_bitcell(output_path=args.output, generate_spice=args.spice)
        return 0

    parser.print_help()
    return 0


def _get_version() -> str:
    from rekolektion import __version__

    return __version__


if __name__ == "__main__":
    sys.exit(main())
