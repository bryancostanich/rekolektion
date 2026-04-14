"""Automated LVS verification using Magic (extraction) + netgen (comparison).

Flow:
1. Magic reads GDS, extracts SPICE netlist from layout
2. netgen compares extracted netlist against schematic/reference netlist
"""

import os
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

from rekolektion.verify.drc import find_pdk_root


@dataclass
class LVSResult:
    """Result of an LVS comparison."""
    match: bool
    log_path: Path
    cell_name: str
    extracted_netlist_path: Path | None = None

    def summary(self) -> str:
        if self.match:
            return f"LVS MATCH: {self.cell_name}"
        return f"LVS MISMATCH: {self.cell_name} — see {self.log_path}"


def extract_netlist(
    gds_path: str | Path,
    cell_name: str = "",
    pdk_root: str | Path | None = None,
    output_dir: str | Path | None = None,
) -> Path:
    """Extract SPICE netlist from GDS using Magic.

    Returns path to the extracted .spice file.
    """
    gds_path = Path(gds_path)
    if pdk_root is None:
        pdk_root = find_pdk_root()
    pdk_root = Path(pdk_root)

    if output_dir is None:
        output_dir = Path(tempfile.mkdtemp(prefix="rekolektion_lvs_"))
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    gds_path = gds_path.resolve()

    from rekolektion.tech.sky130 import magic_rcfile
    magicrc = magic_rcfile(pdk_root)

    extracted_spice = output_dir / f"{cell_name or 'top'}_extracted.spice"

    # Magic writes .ext files relative to CWD, so we run from output_dir
    # and use absolute paths for GDS and output.
    tcl_script = f"""\
gds read {gds_path}
{"" if not cell_name else f"load {cell_name}"}
select top cell
extract all
ext2spice lvs
ext2spice -o {extracted_spice.resolve()}
quit -noprompt
"""
    tcl_path = output_dir / "extract.tcl"
    tcl_path.write_text(tcl_script)

    cmd = ["magic", "-dnull", "-noconsole"]
    if magicrc.exists():
        cmd.extend(["-rcfile", str(magicrc)])
    cmd.append(str(tcl_path.resolve()))

    result = subprocess.run(
        cmd, capture_output=True, text=True, timeout=300,
        cwd=str(output_dir),
    )

    # Write Magic log for debugging
    log_path = output_dir / "extract.log"
    log_path.write_text(result.stdout + "\n" + result.stderr)

    if not extracted_spice.exists():
        raise RuntimeError(
            f"Extraction failed — no output at {extracted_spice}. "
            f"See {log_path}"
        )

    return extracted_spice


def run_lvs(
    gds_path: str | Path,
    schematic_path: str | Path,
    cell_name: str = "",
    pdk_root: str | Path | None = None,
    output_dir: str | Path | None = None,
) -> LVSResult:
    """Run LVS: extract layout netlist, compare against schematic.

    Args:
        gds_path: Path to layout GDS.
        schematic_path: Path to reference SPICE netlist.
        cell_name: Top cell name.
        pdk_root: PDK root path.
        output_dir: Output directory for results.

    Returns:
        LVSResult indicating match/mismatch.
    """
    if output_dir is None:
        output_dir = Path(tempfile.mkdtemp(prefix="rekolektion_lvs_"))
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    if pdk_root is None:
        pdk_root = find_pdk_root()
    pdk_root = Path(pdk_root)

    from rekolektion.tech.sky130 import netgen_setup

    # Step 1: Extract netlist from layout
    extracted = extract_netlist(gds_path, cell_name, pdk_root, output_dir)

    # Step 2: Run netgen comparison
    setup_file = netgen_setup(pdk_root)
    log_path = output_dir / "lvs_results.log"

    subckt = cell_name or "sky130_sram_6t_bitcell"
    netgen_cmd = [
        "netgen", "-batch", "lvs",
        f"{extracted} {subckt}",
        f"{schematic_path} {subckt}",
    ]
    if setup_file.exists():
        netgen_cmd.append(str(setup_file))
    netgen_cmd.extend(["-o", str(log_path)])

    try:
        result = subprocess.run(
            netgen_cmd,
            capture_output=True,
            text=True,
            timeout=300,
            cwd=str(output_dir),
        )
    except FileNotFoundError:
        raise RuntimeError(
            "netgen not found on PATH. Install netgen: "
            "http://opencircuitdesign.com/netgen/"
        )

    # Parse result
    match = False
    output_text = result.stdout + (log_path.read_text() if log_path.exists() else "")
    if "Circuits match uniquely" in output_text:
        match = True

    return LVSResult(
        match=match,
        log_path=log_path,
        cell_name=cell_name or "(top)",
        extracted_netlist_path=extracted,
    )
