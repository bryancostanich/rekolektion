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
import warnings
from pathlib import Path
from typing import Literal

from rekolektion.verify._primitive_footprints import compute_primitive_footprints
from rekolektion.verify.drc import DRCResult, run_drc
from rekolektion.verify.drc_klayout import run_drc_fsharp, run_drc_klayout
from rekolektion.verify.grid import verify_grid


Compat = Literal["klayout", "magic"]
DEFAULT_COMPAT: Compat = "klayout"


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
    compat: Compat = DEFAULT_COMPAT,
    external: bool | None = None,
) -> DRCResult:
    """Run DRC on a `.rkt` block.

    Converts the block to GDS via the viz CLI's `to-gds` verb, then
    delegates to the matching engine for the requested `compat` target.
    Returns the same `DRCResult` type either path produces.

    The `compat` flag selects **which authority's rules** the result is
    checked against — KLayout (default) or Magic (permanent supported
    alternate). `external=True` (Phase 2) routes through the matching
    external binary; Phase 5 will introduce `external=False` to drive
    the F# in-viz checker directly with the same compat target.

    Args:
        rkt_path: Path to the `.rkt` block. Supports the full
            LayoutLoader range (`.rkt`, `.mag`, `.gds`) — the verb
            dispatches by extension.
        cell_name: Top cell name. If empty, the GDS's first cell is
            used (matches `run_drc`'s default behavior).
        pdk_root / output_dir / waiver_footprints / allow_global_waivers:
            Forwarded verbatim to the engine.
        keep_gds: When True, the intermediate `.gds` is left on disk in
            `output_dir` (or a tempfile path) for inspection.
        full: Magic-compat only. When True under `compat="magic"`,
            Magic runs `drc style drc(full)` (sign-off rule set:
            latch-up LU.2/LU.3, implant-aware diff/tap.9 + licon.9,
            nwell.4 connectivity). Under `compat="klayout"` the
            parameter is IGNORED (KLayout has no fast/full split —
            it's always full) and a DeprecationWarning is issued so
            call sites notice.
        strict_grid: When True (default), runs `verify_grid` before the
            DRC engine and escalates any off-grid coords into the
            returned `DRCResult`. See Track 01 (silicon_correct).
        compat: Which authority's DRC rules to evaluate against.
            Default `"klayout"`; `"magic"` keeps the legacy path
            available as a permanent supported alternate. See
            Track 02 (silicon_correct).
        external: Routes the DRC call through the matching external
            binary (`external=True`) or the F# in-process checker
            (`external=False`). Default is compat-conditional per
            Track 02 Phase 5 Fork #4 (autonomous_2026-06-01.md):
              * compat="klayout" → external=False (F# primary).
                Klayout side has 100% per-rule equivalency on the
                Phase 4 corpus, so F# is the validated fast path.
              * compat="magic" → external=True (Magic primary).
                F# Magic has known deltas vs ext-Magic that haven't
                been worked yet; until they do, ext-Magic stays the
                validated path.
            Pass `external=True` or `external=False` explicitly to
            override the compat-conditional default.

    Returns:
        `DRCResult` with `.clean`, `.real_error_count`, `.real_errors`,
        etc. Engine-agnostic.
    """
    if compat not in ("klayout", "magic"):
        raise ValueError(
            f"compat must be 'klayout' or 'magic', got {compat!r}"
        )
    # Resolve compat-conditional default for external.
    if external is None:
        external = (compat == "magic")
    if full and compat == "klayout":
        warnings.warn(
            "full=True is Magic-only (KLayout has no fast/full split — "
            "it is always full). Ignored under compat='klayout'.",
            DeprecationWarning,
            stacklevel=2,
        )
        full = False

    # Resolve to an absolute path BEFORE handing the rkt off to any
    # subprocess. `_convert_rkt_to_gds` runs `dotnet -- to-gds` with
    # `cwd=repo` (the rekolektion repo root) — a relative `rkt_path`
    # would then resolve against the repo root instead of the
    # caller's cwd, breaking the loader with "Could not find a part
    # of the path" + exit 134. Reported 2026-06-01.
    rkt = Path(rkt_path).resolve()
    if not rkt.is_file():
        raise FileNotFoundError(rkt)

    # Auto-extract the .rkt's `(top ...)` cell name when the caller
    # didn't pin one explicitly. The KLayout deck behaves differently
    # depending on whether `$top_cell` is set — `m1.space` etc. only
    # fire correctly when the top cell is known. Without this, calls
    # like `verify_drc('viol_met1.2_subspacing.rkt', compat='klayout')`
    # silently report 0 violations on cells that should fail.
    if not cell_name:
        try:
            from rekolektion.io import rkt as rkt_io
            doc = rkt_io.read_file(rkt)
            if doc.top_cell:
                cell_name = doc.top_cell
        except Exception:
            # Best-effort — fall through to the engine's own default
            # if the .rkt parse fails.  The engine still works without
            # a top cell, just with the caveats above.
            pass

    # Phase 0: grid check.  Runs before the Magic conversion so
    # off-grid drift surfaces with a clear cell+coord report rather
    # than as phantom poly.2 tiles deep inside the DRC log.
    grid_result = verify_grid(rkt)

    # Phase 5 F#-primary fast path: external=False routes straight
    # to the F# CLI (`viz drc --compat ...`).  No GDS conversion,
    # no foundry-footprint waiver pass — the F# checker walks the
    # .rkt directly via LayoutLoader.  Foundry-waiver coverage on
    # this path is deferred to Phase 6.
    if not external:
        drc_result = run_drc_fsharp(
            rkt, cell_name=cell_name, compat=compat, output_dir=output_dir,
        )
        # Fold grid violations into the result so callers see one
        # unified verdict.  Same shape as the external paths below.
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
        drc_result.grid = grid_result  # type: ignore[attr-defined]
        return drc_result

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
        if compat == "magic":
            drc_result = run_drc(
                gds,
                cell_name=cell_name,
                pdk_root=pdk_root,
                output_dir=output_dir,
                waiver_footprints=auto_footprints,
                allow_global_waivers=allow_global_waivers,
                full=full,
            )
        else:
            # KLayout's `offgrid=true` would double-report what
            # `verify_grid` (Track 01) already surfaces.  Suppress it
            # here so the unified DRCResult doesn't show the same
            # off-grid coord twice — once on the grid line, once on the
            # KLayout-rule line.
            drc_result = run_drc_klayout(
                gds,
                cell_name=cell_name,
                pdk_root=pdk_root,
                output_dir=output_dir,
                waiver_footprints=auto_footprints,
                allow_global_waivers=allow_global_waivers,
                offgrid=False,
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
