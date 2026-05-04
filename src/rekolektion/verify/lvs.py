"""Automated LVS verification using Magic (extraction) + netgen (comparison).

Flow:
1. Magic reads GDS, extracts SPICE netlist from layout
2. netgen compares extracted netlist against schematic/reference netlist
"""

import glob
import os
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

from rekolektion.verify.drc import find_pdk_root


def _find_netgen() -> str:
    """Locate a netgen binary that supports `-batch lvs`.

    The netgen in `~/.local/bin/netgen` on this machine was built without
    Tcl/Tk batch support and hangs on a GUI console. The Nix-store netgen
    (installed as a dependency of OpenLane) has batch support.

    Resolution order:
    1. $NETGEN env var (caller override)
    2. ~/tools/openlane2/result/*/netgen (Nix flake symlink)
    3. /nix/store/*/netgen/bin/netgen (any Nix-store netgen)
    4. `netgen` on PATH (fallback, may lack batch)
    """
    override = os.environ.get("NETGEN")
    if override and Path(override).is_file():
        return override

    # Prefer an OpenLane-shipped netgen
    ol_candidates = glob.glob(str(Path.home() / "tools/openlane2/result/**/netgen"), recursive=True)
    for c in ol_candidates:
        if Path(c).is_file() and os.access(c, os.X_OK):
            return c

    # Any Nix-store netgen
    nix_candidates = glob.glob("/nix/store/*-netgen*/bin/netgen")
    for c in nix_candidates:
        if Path(c).is_file() and os.access(c, os.X_OK):
            return c

    return "netgen"


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
    make_ports: bool = False,
    timeout: int = 1800,
) -> Path:
    """Extract SPICE netlist from GDS using Magic.

    Returns path to the extracted .spice file.

    timeout defaults to 1800 s (30 min) — the 300 s default was too
    short for production-scale macros.  Measured ~12 min for the full
    sram_weight_bank_small top extraction on this machine; 30 min
    leaves headroom for larger variants.  Small cells (<1 s) pay
    nothing for a high ceiling.

    make_ports: if True, promote every label attached to geometry into a
    subckt port via `port makeall`. Required for downstream PVT SPICE
    where the subckt must expose named ports (bitlines, enables, power).
    Default False keeps the LVS call site unchanged (LVS relies on
    schematic-side port declarations, not extracted ones).

    timeout: Magic subprocess timeout in seconds. Default 300 s suffices
    for the LVS flow on small cells; full-macro extractions need more.
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
    #
    # `port makeall` only operates on the currently-selected cell, so a
    # single invocation only promotes labels in the top cell.  Sub-cell
    # ports (e.g. `addr[i]` rails inside `row_decoder`) need their own
    # port-make pass BEFORE `extract all` so the .ext files capture the
    # promoted ports.  Without it, the parent's hierarchical instance
    # call can't pass nets into them and Magic reports the parent's
    # feeders as dangling top-level pins (observed on F11b/F12).
    if make_ports and cell_name:
        port_make_block = f"""\
set _cells [cellname list allcells]
foreach _c $_cells {{
    if {{$_c eq "(UNNAMED)"}} {{ continue }}
    load $_c
    select top cell
    catch {{port makeall}}
}}
load {cell_name}
select top cell
"""
    else:
        port_make_block = ""
    tcl_script = f"""\
gds read {gds_path}
{"" if not cell_name else f"load {cell_name}"}
select top cell
{port_make_block}extract all
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

    # sky130B.magicrc resolves $env(PDK_ROOT) -> tech file; if unset, it
    # falls back to a hard-coded build-machine path that doesn't exist
    # on any other machine. Guarantee the env var is set so the rcfile
    # can locate the tech file.
    env = os.environ.copy()
    env["PDK_ROOT"] = str(pdk_root)

    result = subprocess.run(
        cmd, capture_output=True, text=True, timeout=timeout,
        cwd=str(output_dir), env=env,
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
    extracted_netlist: str | Path | None = None,
    netgen_timeout: int = 3600,
    extra_flatten_cells: list[str] | None = None,
) -> LVSResult:
    """Run LVS: extract layout netlist, compare against schematic.

    Args:
        gds_path: Path to layout GDS.
        schematic_path: Path to reference SPICE netlist.
        cell_name: Top cell name.
        pdk_root: PDK root path.
        output_dir: Output directory for results.
        extracted_netlist: if given, reuse this pre-extracted SPICE
            instead of running Magic again.  Saves ~10 min on
            production-size macros when iterating on netgen setup.
        netgen_timeout: seconds to allow netgen to complete.  Default
            3600 (1 h) — production-scale netlists (2000+ transistors,
            deep hierarchy) routinely need 15+ min in netgen's graph
            matcher.  Small cells finish in seconds regardless.

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

    # Step 1: Extract netlist from layout (unless caller already has one).
    if extracted_netlist is not None:
        extracted = Path(extracted_netlist)
        if not extracted.exists():
            raise RuntimeError(
                f"extracted_netlist {extracted} does not exist"
            )
    else:
        extracted = extract_netlist(gds_path, cell_name, pdk_root, output_dir)

    # Audit 2026-05-03 / task #108: VSUBS→VSS textual rewrite REMOVED.
    # The same alias is now expressed as a netgen `equate nets VSUBS VSS`
    # directive in the wrapper-setup.tcl below (already in
    # `_equate_pairs`).  The historical comment claimed netgen's equate
    # was "unreliable in batch-lvs mode" but the equate is in fact
    # present and CIM SRAM-D LVS topology match (verified by
    # audit/flood_fill_2026-05-03.md) shows it works.  If a future
    # macro fails LVS purely due to VSUBS alias issues, prefer fixing
    # the netgen-side equate over reintroducing the textual rewrite —
    # textually editing tool output between extract and netgen is the
    # exact pattern the audit anti-pattern rule prohibits.

    # Step 2: Run netgen comparison
    setup_file = netgen_setup(pdk_root)
    log_path = output_dir / "lvs_results.log"

    subckt = cell_name or "sky130_sram_6t_bitcell"
    netgen_bin = _find_netgen()
    # Use absolute paths; netgen runs with cwd=output_dir so relative paths
    # from repo root won't resolve.
    extracted_abs = Path(extracted).resolve()
    schematic_abs = Path(schematic_path).resolve()

    # Write a wrapper setup.tcl that sources the PDK's default setup
    # and then flattens decap / fill / tap / clock-buffer cells.  The
    # OpenLane P&R flow inserts many of these purely-physical cells
    # into the layout; our Python-generated reference doesn't
    # instantiate them, so without flattening the extracted side has
    # hundreds of extra instances that create spurious LVS mismatches.
    wrapper_setup = Path(output_dir) / "_lvs_wrapper_setup.tcl"
    pdk_setup = setup_file.resolve() if setup_file.exists() else None
    # Flatten list — audited 2026-05-03 (task #109).  Each entry was
    # confirmed by `grep` against `src/rekolektion` to verify it is
    # either purely-physical (P&R-inserted, no Python instantiation)
    # or a logic cell whose flattening is justified for LVS-flow
    # consistency between OpenLane-inserted and Python-generated
    # instances.
    #
    # Purely-physical (OpenLane fill/decap/tap/diode/clkbuf — none of
    # these have any Python `gdstk.Reference` to them anywhere in
    # src/rekolektion):
    flatten_cells = [
        "sky130_fd_sc_hd__fill_1",
        "sky130_fd_sc_hd__fill_2",
        "sky130_fd_sc_hd__fill_4",
        "sky130_fd_sc_hd__fill_8",
        "sky130_fd_sc_hd__decap_3",
        "sky130_fd_sc_hd__decap_4",
        "sky130_fd_sc_hd__decap_6",
        "sky130_fd_sc_hd__decap_8",
        "sky130_fd_sc_hd__decap_12",
        "sky130_ef_sc_hd__decap_12",
        "sky130_ef_sc_hd__fill_4",
        "sky130_ef_sc_hd__fill_8",
        "sky130_ef_sc_hd__fill_12",
        "sky130_fd_sc_hd__tapvpwrvgnd_1",
        "sky130_fd_sc_hd__diode_2",
        "sky130_fd_sc_hd__clkbuf_4",
        # buf_2 IS a real logic cell — used in CIM's MWL driver
        # (cim_mwl_driver_row.py) and production's WL driver chain.
        # Flattening here is for LVS-flow simplification: both extract
        # and reference contain buf_2, and OpenLane may insert
        # additional buf_2 instances during P&R for buffering.
        # Flattening on both sides reconciles those without claiming
        # a silicon match — it just lowers the comparison granularity
        # to the device level.  Internal buf_2 topology is foundry-
        # defined; if the device counts match, the cell is correct.
        "sky130_fd_sc_hd__buf_2",
    ]
    if extra_flatten_cells:
        flatten_cells.extend(extra_flatten_cells)
    lines: list[str] = []
    if pdk_setup is not None:
        lines.append(f"source {pdk_setup}")
    # netgen `equate nets` ledger.  Audit 2026-05-03 / task #108: each
    # entry annotated with its specific purpose.  The audit trail in
    # `audit/hack_inventory.md` (entry C2) covers the broader concern
    # that some equates bridge naming conventions (legitimate) while
    # others mask physical-isolation (silicon-correctness concern).
    # The equates that bridge naming convention should stay; equates
    # that mask isolation are covered by `docs/waivers/nwell_bias_disclosure.md`.
    _equate_pairs = [
        # VPB↔VPWR: PMOS body (NWELL) at chip level is connected to
        # VPWR.  In the bitcell + production precharge row, no DIRECT
        # metal contact ties NWELL to VPWR; bias is via subsurface
        # conduction from foundry sram_sp_wlstrap N-tap structures.
        # This equate is part of what `nwell_bias_disclosure.md`
        # waivers; netgen sees VPB and VPWR as separate nets in the
        # extract, the equate aliases them.
        ("VPB", "VPWR"),
        # VNB↔VGND: NMOS body (PSUB) at chip level is connected to
        # VGND.  PSUB is the chip substrate; bias is via the always-
        # present VGND-tap chain.  Naming-convention bridge.
        ("VNB", "VGND"),
        # VSUBS↔VGND: foundry mux/sram_array subckts use VSUBS as the
        # substrate node.  At chip level VSUBS == VGND.  Naming-
        # convention bridge.
        ("VSUBS", "VGND"),
        # VDD↔VPWR: CIM bitcell schematic uses VDD; macro-level
        # reference uses VPWR (foundry stdcell convention).  After
        # extract the macro has both names on the same physical metal.
        # Naming-convention bridge — verified by `audit/flood_fill_
        # 2026-05-03.md` (no separate VDD cluster, just legacy naming
        # in cim_mbl_sense.subckt internal).
        ("VDD", "VPWR"),
        # VSS↔VGND: same as VDD/VPWR but for ground.  Bitcell uses
        # VSS internally; macro uses VGND externally.  Naming-
        # convention bridge.
        ("VSS", "VGND"),
        # VSUBS↔VSS: bitcell-LVS (track 05) — standalone bitcell
        # schematics use VSS as NMOS body (no VPB/VNB).  Magic
        # extraction reports the substrate as VSUBS.  Used only by
        # the bitcell-standalone LVS flow.  Naming-convention bridge
        # for that specific flow.
        ("VSUBS", "VSS"),
    ]
    for c1, c2 in _equate_pairs:
        lines.append(f"catch {{equate nets -circuit1 {c1} {c2}}}")
        lines.append(f"catch {{equate nets -circuit2 {c1} {c2}}}")
    # netgen `equate classes` ledger.  Audit 2026-05-03 / task #101 +
    # follow-up: the foundry SRAM bitcell hand-written body uses the
    # SKY130 special_* SRAM models (special_nfet_pass for access W=0.14,
    # special_nfet_latch for pull-down W=0.21, special_pfet_latch for
    # pull-up W=0.14 + parasitic L=0.025).  Magic's ext2spice still
    # extracts the same DIFF as generic nfet_01v8 / pfet_01v8_hvt
    # because the sky130 tech file maps the layout DIFF+POLY to the
    # generic 1V8 device class — there's no Magic-side device-class
    # hint that says "this DIFF is in an SRAM cell, use special_*".
    #
    # Without these equates, netgen flags the qtap subckt as a device-
    # class mismatch (extracted has nfet_01v8 instances, reference has
    # special_nfet_pass / special_nfet_latch instances — different
    # netgen device classes by name).  These directives reconcile the
    # two by class-name aliasing.
    #
    # SPICE behavior with the special_* models is correct (narrow-W
    # bins exist for SRAM W=0.14/0.21, generic 1V8 has wmin=5µm).  LVS
    # behavior is correct after these equates (graph-iso + W/L
    # parametric matching disambiguates which extracted nfet maps to
    # which reference class within the merged super-class).
    _equate_class_pairs = [
        ("sky130_fd_pr__nfet_01v8", "sky130_fd_pr__special_nfet_pass"),
        ("sky130_fd_pr__nfet_01v8", "sky130_fd_pr__special_nfet_latch"),
        ("sky130_fd_pr__pfet_01v8_hvt", "sky130_fd_pr__special_pfet_latch"),
    ]
    for c_extracted, c_reference in _equate_class_pairs:
        # Netgen syntax: `equate classes -circuit1 <cls1> -circuit2 <cls2>`
        # tells netgen to treat these device classes as equivalent for
        # matching purposes.  `catch` swallows the error if either class
        # isn't present (e.g. production LVS where reference uses generic
        # 1V8 in both circuits — the equate is a no-op there).
        lines.append(
            f"catch {{equate classes "
            f"\"-circuit1 {c_extracted}\" \"-circuit2 {c_reference}\"}}"
        )
    for cell in flatten_cells:
        lines.append(f'catch {{flatten class "-circuit1 {cell}"}}')
        lines.append(f'catch {{flatten class "-circuit2 {cell}"}}')
    wrapper_setup.write_text("\n".join(lines) + "\n")

    netgen_cmd = [
        netgen_bin, "-batch", "lvs",
        f"{extracted_abs} {subckt}",
        f"{schematic_abs} {subckt}",
        str(wrapper_setup.resolve()),
    ]

    try:
        result = subprocess.run(
            netgen_cmd,
            capture_output=True,
            text=True,
            timeout=netgen_timeout,
            cwd=str(output_dir),
            stdin=subprocess.DEVNULL,  # prevent Tk GUI on netgen builds with TkCon
        )
    except FileNotFoundError:
        raise RuntimeError(
            "netgen not found on PATH. Install netgen: "
            "http://opencircuitdesign.com/netgen/"
        )

    # Save stdout+stderr to log (netgen itself doesn't write a full transcript
    # to a log file without explicit Tcl commands; capturing the batch stdout
    # gives us the authoritative LVS transcript).
    log_path.write_text(result.stdout + "\n--- STDERR ---\n" + result.stderr)

    # Parse result
    match = False
    output_text = result.stdout
    if "Circuits match uniquely" in output_text:
        match = True

    return LVSResult(
        match=match,
        log_path=log_path,
        cell_name=cell_name or "(top)",
        extracted_netlist_path=extracted,
    )
