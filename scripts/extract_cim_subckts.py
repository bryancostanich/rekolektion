"""Extract Magic-readable .subckt files for the CIM cells.

Mirrors `extract_foundry_subckts.py` but for cells we generate
in-tree (cim_mwl_driver, cim_mbl_precharge, cim_mbl_sense, and the
four CIM bitcell variants).  Saves each to
`peripherals/cells/extracted_subckt/<name>.subckt.sp` so the CIM
SPICE reference generator can include them verbatim.

Usage::

    python3 scripts/extract_cim_subckts.py
"""
from __future__ import annotations

import sys
from pathlib import Path

from rekolektion.peripherals.cim_mwl_driver import generate_mwl_driver
from rekolektion.peripherals.cim_mbl_precharge import generate_mbl_precharge
from rekolektion.peripherals.cim_mbl_sense import generate_mbl_sense
from rekolektion.bitcell.sky130_6t_lr_cim import (
    CIM_VARIANTS, generate_cim_bitcell,
)
from rekolektion.verify.lvs import extract_netlist


_ROOT: Path = Path(__file__).parent.parent
_OUT_DIR: Path = _ROOT / "src/rekolektion/peripherals/cells/extracted_subckt"
_GDS_DIR: Path = _ROOT / "output/cim_extracted_input"


def _ensure_periph_gds(name: str, generator) -> Path:
    """Generate a peripheral cell to GDS (one-shot, deterministic)."""
    _GDS_DIR.mkdir(parents=True, exist_ok=True)
    gds_path = _GDS_DIR / f"{name}.gds"
    cell, lib = generator()
    lib.write_gds(str(gds_path))
    return gds_path


def _ensure_bitcell_gds(variant: str) -> tuple[str, Path]:
    """Generate a CIM bitcell variant to GDS.

    Note: the bitcell generator hardcodes the cell name
    `sky130_sram_6t_cim_lr` regardless of variant; the variant only
    changes MIM dimensions.  We still emit one GDS per variant (each
    file with the variant-suffixed filename) so the extraction
    output captures variant-specific MIM geometry.
    """
    v = CIM_VARIANTS[variant]
    slug = variant.lower().replace("-", "_")
    cell_name = "sky130_sram_6t_cim_lr"   # fixed in the generator
    extracted_name = f"{cell_name}_{slug}"  # variant-specific output name
    _GDS_DIR.mkdir(parents=True, exist_ok=True)
    gds_path = _GDS_DIR / f"{extracted_name}.gds"
    generate_cim_bitcell(str(gds_path), mim_w=v["mim_w"], mim_l=v["mim_l"])
    return cell_name, extracted_name, gds_path


# Magic's extraction reliably promotes ports labeled on met layers,
# but poly/li1 ports (gate inputs, gate outputs labelled on li1)
# routinely fall off the .subckt port list even with a .pin shape.
# We patch the declaration post-extraction with the canonical port
# list we know each cell exposes.
_PORT_LIST: dict[str, list[str]] = {
    # Foundry buf_2 stdcell — Magic extraction promotes most ports already;
    # the explicit list ensures consistency with the foundry CDL.
    "sky130_fd_sc_hd__buf_2": ["A", "VGND", "VNB", "VPB", "VPWR", "X"],
    # Custom analog cells — VPWR/VGND included as ports so the macro
    # ties all instances to the same supply nets (otherwise Magic
    # extracts the well as an internal floating net per instance).
    "cim_mbl_precharge":      ["MBL_PRE", "VREF", "MBL", "VPWR"],
    "cim_mbl_sense":          ["VBIAS", "MBL", "VSS", "MBL_OUT", "VDD"],
    # Bitcell port list uses VPWR/VGND to match the macro's supply
    # naming (the body of the extracted subckt is rewritten to use
    # VPWR/VGND too — see _patch_subckt_ports).
    "sky130_sram_6t_cim_lr":  ["BL", "BLB", "WL", "MWL", "MBL", "VPWR", "VGND"],
}


def _patch_subckt_ports(text: str, cell_name: str) -> str:
    """Rewrite the `.subckt <name> ...` line to include the canonical
    port list (Magic loses some poly/li1 ports during extraction).

    Also substitute auto-named well/substrate body-bias nets in the
    body with the conventional supply names (VDD / VSS).  Without
    this, every flattened bitcell instance in the macro would have a
    distinct `w_xx_yy#` well net while the layout's flat extraction
    sees one shared well per N-well region — netgen flags this as
    a per-instance net mismatch even though the topology is sound.
    """
    ports = _PORT_LIST.get(cell_name)
    if not ports:
        return text
    lines = text.splitlines(keepends=True)
    out: list[str] = []
    for ln in lines:
        stripped = ln.strip()
        if stripped.startswith(".subckt"):
            out.append(f".subckt {cell_name} {' '.join(ports)}\n")
            continue
        if cell_name == "sky130_sram_6t_cim_lr":
            # Magic auto-names the bitcell's n-well as `w_xx_yy#` and
            # the p-substrate as VSUBS.  Tie both to the supply rails
            # globally — this matches the macro-level physical
            # connectivity (the wells abut and merge into one net per
            # supply when the array is flattened).  Use VPWR/VGND
            # names so the bitcell's body terminals match the macro's
            # supply naming directly (no equate needed in netgen).
            import re as _re
            ln = _re.sub(r"\bw_\d+_n?\d+#", "VPWR", ln)
            ln = ln.replace("VSUBS", "VGND")
            # Also rename VDD/VSS port references in the body to
            # VPWR/VGND so the bitcell's instances pass the macro's
            # supplies directly.
            ln = _re.sub(r"\bVDD\b", "VPWR", ln)
            ln = _re.sub(r"\bVSS\b", "VGND", ln)
        out.append(ln)
    return "".join(out)


def _extract_one(cell_name: str, gds_path: Path) -> Path:
    """Run Magic extract -> ext2spice -> save .subckt.sp."""
    _OUT_DIR.mkdir(parents=True, exist_ok=True)
    work_dir = _OUT_DIR.parent / "_extract_work"
    work_dir.mkdir(parents=True, exist_ok=True)
    ext_path = extract_netlist(
        gds_path, cell_name=cell_name, output_dir=work_dir,
    )
    final = _OUT_DIR / f"{cell_name}.subckt.sp"
    final.write_text(_patch_subckt_ports(ext_path.read_text(), cell_name))
    return final


def _extract_one_with_alias(
    src_cell_name: str, output_basename: str, gds_path: Path,
) -> Path:
    """Like `_extract_one` but the cached file uses `output_basename`
    instead of the cell's actual name.  Used for the CIM bitcell
    variants (all four variants share the same cell name in the GDS,
    but we want them cached under variant-specific filenames)."""
    _OUT_DIR.mkdir(parents=True, exist_ok=True)
    work_dir = _OUT_DIR.parent / "_extract_work"
    work_dir.mkdir(parents=True, exist_ok=True)
    ext_path = extract_netlist(
        gds_path, cell_name=src_cell_name, output_dir=work_dir,
    )
    final = _OUT_DIR / f"{output_basename}.subckt.sp"
    final.write_text(_patch_subckt_ports(ext_path.read_text(), src_cell_name))
    return final


def main() -> int:
    print("Extracting CIM peripherals...")
    # Note: cim_mwl_driver is now the foundry sky130_fd_sc_hd__buf_2 stdcell;
    # generate_mwl_driver() returns a library whose top cell carries the
    # foundry name, so we extract under that name.
    periph_specs = [
        ("sky130_fd_sc_hd__buf_2", generate_mwl_driver),
        ("cim_mbl_precharge",      generate_mbl_precharge),
        ("cim_mbl_sense",          generate_mbl_sense),
    ]
    for cell_name, gen in periph_specs:
        gds = _ensure_periph_gds(cell_name, gen)
        out = _extract_one(cell_name, gds)
        print(f"  {cell_name} -> {out}")

    print("Extracting CIM bitcell variants...")
    for variant in CIM_VARIANTS:
        cell_name, extracted_name, gds = _ensure_bitcell_gds(variant)
        # Magic loads the cell named in the GDS, but we want each
        # variant cached under its own filename.  Override by passing
        # the variant-specific output name via _extract_one.
        out = _extract_one_with_alias(cell_name, extracted_name, gds)
        print(f"  {variant} ({cell_name} -> {extracted_name}) -> {out}")

    print("Done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
