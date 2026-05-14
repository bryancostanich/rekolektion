"""DRC verification for `.rkt` blocks.

Composes the existing GDS-based `run_drc` (Magic in batch mode)
with the viz CLI's `to-gds` verb to give callers a one-line DRC
check on a block authored with the layout helpers:

    from rekolektion.verify import verify_drc

    result = verify_drc("cell_designs/bl_clamp/blc_comparator.rkt")
    if not result.clean:
        for err in result.real_errors:
            print(err)

The conversion goes through the same `Rkt.ToGds.toLibrary` pipeline
the rest of the tooling uses (no second writer to maintain). The
helper shells out to `dotnet run -- to-gds` for the conversion, then
hands the resulting GDS to `verify.drc.run_drc`.

For agents using the workflow doc, this closes the loop: build a
block, call `verify_drc`, fix the violations, iterate. `viz read`
output is no longer the only feedback signal.
"""

from __future__ import annotations

import os
import subprocess
import tempfile
from pathlib import Path

from rekolektion.verify.drc import DRCResult, run_drc


def _repo_root() -> Path:
    """Locate the rekolektion repo root by walking up from this file."""

    here = Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        if (ancestor / "tools" / "viz" / "src" / "Rekolektion.Viz.Cli").is_dir():
            return ancestor
    raise RuntimeError(
        "couldn't locate repo root from "
        f"{here} — verify_drc needs the viz CLI source tree"
    )


def _convert_rkt_to_gds(rkt_path: Path, gds_path: Path) -> None:
    """Shell out to viz CLI's `to-gds` verb. Raises CalledProcessError
    on non-zero exit; stderr is captured into the raised exception."""

    repo = _repo_root()
    cli_proj = repo / "tools" / "viz" / "src" / "Rekolektion.Viz.Cli"
    # We run from the repo root so that dotnet's project lookups (and
    # any relative imports inside the .rkt) resolve normally.
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


def verify_drc(
    rkt_path: str | Path,
    *,
    cell_name: str = "",
    pdk_root: str | Path | None = None,
    output_dir: str | Path | None = None,
    waiver_footprints: list[tuple[str, float, float, float, float]] | None = None,
    allow_global_waivers: bool = False,
    keep_gds: bool = False,
) -> DRCResult:
    """Run Magic DRC on a `.rkt` block.

    Converts the block to GDS via the viz CLI's `to-gds` verb,
    then delegates to `rekolektion.verify.drc.run_drc` for the
    Magic invocation and report parsing. Returns the same
    `DRCResult` type the existing flow uses.

    Args:
        rkt_path: Path to the `.rkt` block. Supports the full
            LayoutLoader range (`.rkt`, `.mag`, `.gds`) — the verb
            dispatches by extension.
        cell_name: Top cell name. If empty, the GDS's first cell is
            used (matches `run_drc`'s default behavior).
        pdk_root / output_dir / waiver_footprints / allow_global_waivers:
            Forwarded verbatim to `run_drc`.
        keep_gds: When True, the intermediate `.gds` is left on
            disk in `output_dir` (or a tempfile path) for inspection.
            Default False — the temp file gets cleaned up.

    Returns:
        `DRCResult` with `.clean`, `.real_error_count`, `.real_errors`,
        etc. Same surface as `run_drc`.
    """

    rkt = Path(rkt_path)
    if not rkt.is_file():
        raise FileNotFoundError(rkt)

    # Materialize the GDS. Either to a stable location (when output_dir
    # supplied AND keep_gds=True) or to a tempfile.
    cleanup = False
    if output_dir is not None and keep_gds:
        Path(output_dir).mkdir(parents=True, exist_ok=True)
        gds = Path(output_dir) / f"{rkt.stem}.gds"
    else:
        fd, tmp_path = tempfile.mkstemp(
            prefix=f"drc-{rkt.stem}-", suffix=".gds"
        )
        os.close(fd)
        gds = Path(tmp_path)
        cleanup = not keep_gds

    try:
        _convert_rkt_to_gds(rkt, gds)
        return run_drc(
            gds,
            cell_name=cell_name,
            pdk_root=pdk_root,
            output_dir=output_dir,
            waiver_footprints=waiver_footprints,
            allow_global_waivers=allow_global_waivers,
        )
    finally:
        if cleanup and gds.exists():
            try:
                gds.unlink()
            except OSError:
                pass
