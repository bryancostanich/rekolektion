"""LVS verification for `.rkt` blocks.

Mirrors `rkt_drc.verify_drc`: takes a block's `.rkt` plus a reference
SPICE schematic, converts the `.rkt` to GDS via the viz CLI's `to-gds`
verb, then runs Magic (extract) + netgen (compare) via
`rekolektion.verify.lvs.run_lvs`.

    from rekolektion.verify import verify_lvs

    result = verify_lvs(
        "cell_designs/bl_clamp/blc_comparator.rkt",
        "cell_designs/bl_clamp/blc_comparator_sch.spice",
        cell_name="blc_comparator",
    )
    if not result.match:
        print("LVS mismatch — see", result.log_path)

LVS catches what DRC can't:

- Labels that exist but live on a polygon disconnected from the rest
  of the net (Phase-2 bridge missing → two same-named islands).
- A via stack landing on the wrong met1 polygon (correct DRC, wrong
  electrical net).
- Missing connections to a primitive's terminal (e.g. M3.D1 unlabeled
  diff strip not actually shorted to M3.D0).

`verify_drc` is the FIRST gate — geometry must be manufacturable
before electricals matter. `verify_lvs` is the SECOND gate — the
electrical net graph must match the reference schematic.
"""

from __future__ import annotations

import os
import subprocess
import tempfile
from pathlib import Path

from rekolektion.verify.lvs import LVSResult, extract_netlist, run_lvs


def _repo_root() -> Path:
    """Locate the rekolektion repo root by walking up from this file."""

    here = Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        if (ancestor / "tools" / "viz" / "src" / "Rekolektion.Viz.Cli").is_dir():
            return ancestor
    raise RuntimeError(
        "couldn't locate repo root from "
        f"{here} — verify_lvs needs the viz CLI source tree"
    )


def _convert_rkt_to_gds(rkt_path: Path, gds_path: Path) -> None:
    """Shell out to viz CLI's `to-gds` verb."""

    repo = _repo_root()
    cli_proj = repo / "tools" / "viz" / "src" / "Rekolektion.Viz.Cli"
    subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(cli_proj),
            "--",
            "to-gds",
            str(rkt_path),
            str(gds_path),
        ],
        cwd=repo,
        check=True,
        capture_output=True,
        text=True,
    )


def verify_lvs(
    rkt_path: str | Path,
    schematic_path: str | Path,
    *,
    cell_name: str = "",
    pdk_root: str | Path | None = None,
    output_dir: str | Path | None = None,
    keep_gds: bool = False,
    netgen_timeout: int = 3600,
    extra_flatten_cells: list[str] | None = None,
    extra_equates: list[tuple[str, str]] | None = None,
    port_aliases: list[tuple[str, str]] | None = None,
) -> LVSResult:
    """Run LVS on a `.rkt` block against a reference SPICE schematic.

    Converts the block to GDS via the viz CLI's `to-gds` verb, then
    delegates to `rekolektion.verify.lvs.run_lvs` for the Magic
    extraction + netgen comparison.

    Args:
        rkt_path: Path to the `.rkt` block.
        schematic_path: Path to the reference SPICE netlist (the
            hand-authored schematic the layout must match).
        cell_name: Top cell name. If empty, the GDS's first cell is
            used (matches `run_lvs`'s default).
        pdk_root: PDK root.  Auto-detected if None.
        output_dir: Where to write the extracted netlist and netgen
            log.  A tempdir is used if None.
        keep_gds: When True, the intermediate GDS is left on disk for
            inspection.  Default False — the temp file is cleaned up.
        netgen_timeout: Seconds to allow netgen to complete.  Default
            3600 (1 h) — small cells finish in seconds.
        extra_flatten_cells: Subcell names to flatten before LVS,
            forwarded to `run_lvs`.

    Returns:
        `LVSResult` with `.match`, `.log_path`, `.cell_name`,
        `.extracted_netlist_path`.  Same surface as `run_lvs`.
    """

    rkt = Path(rkt_path)
    if not rkt.is_file():
        raise FileNotFoundError(rkt)

    schematic = Path(schematic_path)
    if not schematic.is_file():
        raise FileNotFoundError(schematic)

    cleanup = False
    if output_dir is not None and keep_gds:
        Path(output_dir).mkdir(parents=True, exist_ok=True)
        gds = Path(output_dir) / f"{rkt.stem}.gds"
    else:
        fd, tmp_path = tempfile.mkstemp(
            prefix=f"lvs-{rkt.stem}-", suffix=".gds"
        )
        os.close(fd)
        gds = Path(tmp_path)
        cleanup = not keep_gds

    try:
        _convert_rkt_to_gds(rkt, gds)
        # Extract with make_ports=True so Magic promotes the block's
        # top-level labels (VDD/VSS/signals) to subckt ports.  Without
        # this, the extracted .subckt has no port list and netgen fails
        # pin-matching against any schematic that declares ports —
        # which every hand-authored reference SPICE does.
        if output_dir is None:
            from tempfile import mkdtemp
            ext_dir = Path(mkdtemp(prefix=f"lvs-extract-{rkt.stem}-"))
        else:
            ext_dir = Path(output_dir)
            ext_dir.mkdir(parents=True, exist_ok=True)
        extracted = extract_netlist(
            gds,
            cell_name=cell_name,
            pdk_root=pdk_root,
            output_dir=ext_dir,
            make_ports=True,
        )
        return run_lvs(
            gds,
            schematic,
            cell_name=cell_name,
            pdk_root=pdk_root,
            output_dir=output_dir,
            extracted_netlist=extracted,
            netgen_timeout=netgen_timeout,
            extra_flatten_cells=extra_flatten_cells,
            extra_equates=extra_equates,
            port_aliases=port_aliases,
        )
    finally:
        if cleanup and gds.exists():
            try:
                gds.unlink()
            except OSError:
                pass
