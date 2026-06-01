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

from rekolektion.verify._primitive_footprints import compute_primitive_footprints
from rekolektion.verify.drc import DRCResult, run_drc
from rekolektion.verify.grid import verify_grid


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
    waiver_margin_um: float = 0.0,
    allow_global_waivers: bool = False,
    keep_gds: bool = False,
    full: bool = False,
    strict_grid: bool = True,
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
        full: When True, Magic runs `drc style drc(full)` — the
            sign-off rule set (latch-up LU.2/LU.3, implant-aware
            diff/tap.9 + licon.9, nwell.4 connectivity, etc.).
            Default False uses the fast geometry-only style. Slower
            on larger cells; opt in when you want sign-off-grade
            results.
        strict_grid: When True (default), runs `verify_grid` before
            Magic and escalates any off-grid coords into the
            returned `DRCResult` (clean=False, violations folded
            into `real_errors`). Set to False to observe off-grid
            without escalating — the grid count still appears in
            the summary but doesn't gate `clean`. Off-grid coords
            are a foundry sign-off failure in their own right; the
            strict default catches them before they reach Magic
            (where they manifest as phantom 14-nm-sliver poly.2
            violations under rotated SRefs and are hard to
            attribute).

    Returns:
        `DRCResult` with `.clean`, `.real_error_count`, `.real_errors`,
        etc. Same surface as `run_drc`.
    """

    # Resolve to an absolute path BEFORE handing the rkt off to any
    # subprocess. `_convert_rkt_to_gds` runs `dotnet -- to-gds` with
    # `cwd=repo` (the rekolektion repo root) — a relative `rkt_path`
    # would then resolve against the repo root instead of the
    # caller's cwd, breaking the loader with "Could not find a part
    # of the path" + exit 134. Reported 2026-06-01.
    rkt = Path(rkt_path).resolve()
    if not rkt.is_file():
        raise FileNotFoundError(rkt)

    # Phase 0: grid check.  Runs before the Magic conversion so
    # off-grid drift surfaces with a clear cell+coord report rather
    # than as phantom poly.2 tiles deep inside the DRC log.
    grid_result = verify_grid(rkt)

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
        # Auto-compute waiver footprints from the cell hierarchy. Every
        # SRef of a `(meta (generator …))` primitive contributes its
        # parent-coord bbox as a footprint. Tiles inside those bboxes
        # that match a known-waiver rule are classified as waivers, not
        # real errors. The caller can override by passing
        # `waiver_footprints` explicitly (an empty list disables the
        # auto-compute path; None — the default — uses it).
        if waiver_footprints is None:
            auto_footprints = compute_primitive_footprints(
                rkt, gds,
                top_cell_name=cell_name,
                margin_um=waiver_margin_um,
            )
        else:
            auto_footprints = waiver_footprints
        drc_result = run_drc(
            gds,
            cell_name=cell_name,
            pdk_root=pdk_root,
            output_dir=output_dir,
            waiver_footprints=auto_footprints,
            allow_global_waivers=allow_global_waivers,
            full=full,
        )
        # Fold grid violations into the DRCResult so callers see one
        # combined verdict.  Grid is reported as a single synthesized
        # error line per cell to keep the report scannable; details
        # live on `grid_result` (which we attach as an attribute
        # since DRCResult doesn't carry that field natively).
        if grid_result.off_grid:
            by_cell: dict[str, int] = {}
            for v in grid_result.off_grid:
                by_cell[v.cell] = by_cell.get(v.cell, 0) + 1
            for cell, count in sorted(by_cell.items()):
                msg = (
                    f"({count}) grid: off-grid coords in cell "
                    f"{cell!r} (grid={grid_result.grid} nm)"
                )
                drc_result.errors.append(msg)
                if strict_grid:
                    drc_result.real_errors.append(msg)
            drc_result.error_count += len(grid_result.off_grid)
            if strict_grid:
                drc_result.real_error_count += len(grid_result.off_grid)
                drc_result.clean = False
        # Expose the detailed grid report for callers that want it.
        drc_result.grid = grid_result  # type: ignore[attr-defined]
        return drc_result
    finally:
        if cleanup and gds.exists():
            try:
                gds.unlink()
            except OSError:
                pass
