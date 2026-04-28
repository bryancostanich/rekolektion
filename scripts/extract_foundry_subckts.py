"""Extract DRC + port-patched .subckt files for every foundry cell.

For each cell in `src/rekolektion/peripherals/cells/*.gds`:
  1. Run Magic `extract all` + `ext2spice` via `verify.lvs.extract_netlist`.
     This produces a .spice file whose .subckt line only lists VDD/GND
     as ports (Magic fails to preserve A/B/Z/... because the foundry
     GDS doesn't carry port-layer markers Magic expects).
  2. Parse the cell's .magic.lef to get the real port list +
     directions.
  3. Rewrite the .subckt line to include those ports.
  4. Cache to peripherals/cells/extracted_subckt/<name>.subckt.sp.

Usage:  python3 scripts/extract_foundry_subckts.py

Re-run after PDK updates.
"""
from __future__ import annotations

import re
import sys
import tempfile
from pathlib import Path

import gdstk

from rekolektion.verify.lvs import extract_netlist


_ROOT: Path = Path(__file__).parent.parent
_CELLS_DIR: Path = _ROOT / "src/rekolektion/peripherals/cells"
_OUT_DIR: Path = _CELLS_DIR / "extracted_subckt"


_POWER_PIN_NAMES: frozenset[str] = frozenset({
    "VDD", "VSS", "GND", "VPWR", "VGND", "VPB", "VNB",
})


# Cells whose LEF port lists are incomplete — the bitcell's WL lives
# on poly (no LEF PIN declaration) but Magic extraction sees it via
# the poly label, so we add it explicitly.
_PORT_ADDITIONS: dict[str, list[str]] = {
    "sky130_fd_bd_sram__sram_sp_cell_opt1": ["WL"],
}


def parse_lef_ports(lef_path: Path) -> list[str]:
    """Return list of pin names in LEF declaration order.

    sky130 foundry LEFs don't always include DIRECTION, so we match
    on `PIN <name>` / `END <name>` pairs and rely on the caller to
    order power pins last.
    """
    pins: list[str] = []
    pin_re = re.compile(r"^\s*PIN\s+(\S+)")
    for line in lef_path.read_text().splitlines():
        m = pin_re.match(line)
        if m:
            pins.append(m.group(1))
    return pins


def patch_subckt_ports(
    extracted_spice_text: str,
    cell_name: str,
    ordered_port_list: list[str],
) -> str:
    """Rewrite the `.subckt CELL ...` line to list the correct ports.

    Strategy: find the line that starts with `.subckt <cell_name>`
    and replace the trailing port list with the supplied one.  Leave
    the rest of the file alone (transistor body, .ends).
    """
    new_subckt_line = (
        f".subckt {cell_name} " + " ".join(ordered_port_list)
    )
    out_lines: list[str] = []
    replaced = False
    for line in extracted_spice_text.splitlines():
        if not replaced and line.startswith(f".subckt {cell_name}"):
            out_lines.append(new_subckt_line)
            replaced = True
        else:
            out_lines.append(line)
    if not replaced:
        raise ValueError(
            f"did not find .subckt line for {cell_name} in extracted SPICE"
        )
    return "\n".join(out_lines) + "\n"


def _normalize_gds(cell_name: str, gds_path: Path, tmp_dir: Path) -> Path:
    """Round-trip the GDS through gdstk to produce a Magic-compatible
    file.  Handles two legacy-GDS quirks:

    1. Old format that Magic's Calma reader rejects ("Expected ENDSTR
       record but got BGNSTR").  Re-writing via gdstk normalises it.
    2. Duplicate cell names (some foundry GDSes contain an empty
       placeholder cell with the same name as the real cell).  We
       drop any empty-bbox cells that collide with a populated one.
    """
    src = gdstk.read_gds(str(gds_path))
    # Build a fresh library containing only the non-empty version of
    # each named cell.
    clean_lib = gdstk.Library(name=src.name)
    by_name: dict[str, gdstk.Cell] = {}
    for c in src.cells:
        bb = c.bounding_box()
        existing = by_name.get(c.name)
        if existing is None:
            by_name[c.name] = c
        elif existing.bounding_box() is None and bb is not None:
            by_name[c.name] = c  # prefer the populated one
        # else: keep whichever we already have
    for c in by_name.values():
        clean_lib.add(c.copy(c.name))
    clean = tmp_dir / f"{cell_name}_clean.gds"
    clean_lib.write_gds(str(clean))
    return clean


def extract_one(cell_name: str, gds_path: Path, lef_path: Path) -> Path:
    """Extract + patch one cell.  Returns path to the cached subckt."""
    _OUT_DIR.mkdir(parents=True, exist_ok=True)
    tmp_dir = Path(tempfile.mkdtemp(prefix=f"extract_{cell_name}_"))
    clean_gds = _normalize_gds(cell_name, gds_path, tmp_dir)
    print(f"[{cell_name}] extracting via Magic...", flush=True)
    sp = extract_netlist(clean_gds, cell_name=cell_name, output_dir=tmp_dir)

    raw_text = sp.read_text()

    # Parse the LEF to get real ports
    if not lef_path.exists():
        print(f"[{cell_name}] no LEF at {lef_path}, leaving extraction as-is")
        out_path = _OUT_DIR / f"{cell_name}.subckt.sp"
        out_path.write_text(raw_text)
        return out_path
    lef_ports = parse_lef_ports(lef_path)
    # Add any ports the LEF misses (e.g. WL on poly for the bitcell).
    for extra in _PORT_ADDITIONS.get(cell_name, []):
        if extra not in lef_ports:
            lef_ports.append(extra)
    # Split signal pins from power pins, order signals first.
    power_pins = [p for p in lef_ports if p.upper() in _POWER_PIN_NAMES]
    signal_pins = [p for p in lef_ports if p.upper() not in _POWER_PIN_NAMES]
    ordered = signal_pins + power_pins
    # Magic's extraction always labels the supply rails as GND/VDD
    # (regardless of what the LEF calls them).  Make sure both are
    # present so the patched .subckt line matches the body.
    for std in ("GND", "VDD"):
        if std not in ordered:
            ordered.append(std)

    patched = patch_subckt_ports(raw_text, cell_name, ordered)
    out_path = _OUT_DIR / f"{cell_name}.subckt.sp"
    out_path.write_text(patched)
    print(f"[{cell_name}] wrote {out_path} with ports: {ordered}")
    return out_path


def main() -> None:
    # Cells whose .magic.lef we'll use to derive the port list.
    foundry_cells = [
        "sky130_fd_bd_sram__openram_sp_nand2_dec",
        "sky130_fd_bd_sram__openram_sp_nand3_dec",
        "sky130_fd_bd_sram__openram_sp_nand4_dec",
        "sky130_fd_bd_sram__openram_dff",
        "sky130_fd_bd_sram__openram_sense_amp",
        "sky130_fd_bd_sram__openram_write_driver",
    ]
    # Custom/OpenRAM cells at peripherals/cells without a .magic.lef —
    # skip (the new Python generators replace them for macro use).

    # Bitcell lives in bitcell/cells/
    bitcell_name = "sky130_fd_bd_sram__sram_sp_cell_opt1"
    bitcell_gds = (
        _ROOT / "src/rekolektion/bitcell/cells" / f"{bitcell_name}.gds"
    )
    bitcell_lef = bitcell_gds.with_suffix(".magic.lef")

    results: list[Path] = []
    # Foundry peripheral cells
    for name in foundry_cells:
        gds = _CELLS_DIR / f"{name}.gds"
        lef = _CELLS_DIR / f"{name}.magic.lef"
        if not gds.exists():
            print(f"[{name}] GDS missing, skipping")
            continue
        results.append(extract_one(name, gds, lef))
    # Bitcell
    if bitcell_gds.exists():
        results.append(extract_one(bitcell_name, bitcell_gds, bitcell_lef))

    print(f"\nExtracted {len(results)} foundry cells to {_OUT_DIR}")


if __name__ == "__main__":
    sys.exit(main() or 0)
