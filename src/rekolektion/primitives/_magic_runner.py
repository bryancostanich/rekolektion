"""Magic CIF subprocess driver.

Mirrors `tools/viz/src/Rekolektion.Viz.Core/Cif/Cif.fs` on the Python
side. The viz tool uses the F# version when opening a `.mag`; the
primitive runner uses this version when minting a new primitive from
a PDK generator call (`sky130::mos_draw`, etc.). Both expect the
same Magic install and PDK layout, so the discovery logic is
identical:

  1. `$REKOLEKTION_MAGIC` env override
  2. `~/.local/bin/magic` (the rekolektion / khalkulo install location)
  3. `magic` on PATH

The Tcl script generated here is template-based — generators pass in
the body that creates the cell (e.g. `sky130::mos_draw {...}`), and
this module wraps it with `cellname create`, `gds write`, and
`quit -noprompt`. The runner returns the path to the freshly-written
GDS; the caller decides what to do with it (typically: read it via
the GDS importer, convert to `.rkt`, persist).
"""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path


class MagicNotFoundError(RuntimeError):
    """No Magic binary located via env var, ~/.local/bin, or PATH."""


class MagicFailedError(RuntimeError):
    """Magic ran but exited non-zero, or didn't produce the expected GDS."""

    def __init__(self, exit_code: int, stderr: str) -> None:
        super().__init__(f"magic exited {exit_code}: {stderr.strip()}")
        self.exit_code = exit_code
        self.stderr = stderr


class TechFileNotFoundError(RuntimeError):
    """The PDK's `.magicrc` for the requested tech is missing."""


@dataclass(frozen=True)
class MagicRun:
    """Result of a successful Magic invocation."""

    gds_path: Path
    stderr: str


def locate_magic() -> Path:
    """Find the Magic binary or raise `MagicNotFoundError`.

    Order:
      1. `$REKOLEKTION_MAGIC` (if it points at an existing file)
      2. `~/.local/bin/magic`
      3. `shutil.which("magic")`
    """

    override = os.environ.get("REKOLEKTION_MAGIC", "").strip()
    if override and Path(override).is_file():
        return Path(override)
    home_build = Path.home() / ".local" / "bin" / "magic"
    if home_build.is_file():
        return home_build
    on_path = shutil.which("magic")
    if on_path:
        return Path(on_path)
    raise MagicNotFoundError(
        "magic not found (checked $REKOLEKTION_MAGIC, "
        "~/.local/bin/magic, PATH)"
    )


def locate_magicrc(tech: str = "sky130B") -> Path:
    """Locate the `<tech>.magicrc` for the requested PDK flavor.

    Honors `$PDK_ROOT`; falls back to `~/.volare` (the rekolektion /
    khalkulo convention). Raises `TechFileNotFoundError` if missing.
    """

    root = os.environ.get("PDK_ROOT", "").strip() or str(Path.home() / ".volare")
    candidate = Path(root) / tech / "libs.tech" / "magic" / f"{tech}.magicrc"
    if not candidate.is_file():
        raise TechFileNotFoundError(
            f"'{tech}.magicrc' not found under {root}"
        )
    return candidate


def _escape(s: str) -> str:
    return s.replace("\\", "\\\\").replace('"', '\\"')


def build_script(
    cell_name: str,
    body_tcl: str,
    output_gds: Path,
    extra_search_dirs: list[Path] | None = None,
) -> str:
    """Compose the Tcl script Magic will run.

    `body_tcl` is the per-generator block — typically a single
    `sky130::mos_draw {w 1.2 l 1.0 ...}` call, or a sequence of
    `box / paint / getcell` lines for composite primitives. The
    surrounding template handles cell creation, GDS write, and exit.
    """

    add_path_lines = []
    for d in extra_search_dirs or []:
        if d.is_dir():
            add_path_lines.append(f'addpath "{_escape(str(d))}"')
    add_paths_block = "\n".join(add_path_lines)
    # Bodies are responsible for creating + loading their target cell.
    # The runner only sets up the search path, disables DRC during
    # generation, and bookends with `gds write` + `quit`. `cell_name`
    # is informational — used as the GDS output suffix — but the
    # actual cell entry happens inside `body_tcl`.
    _ = cell_name
    return (
        "drc off\n"
        f"{add_paths_block}\n"
        f"{body_tcl}\n"
        # `gds write` here writes every loaded structure. The named
        # cell becomes the top because `body_tcl` ends inside it via
        # `cellname create / load`.
        f'gds write "{_escape(str(output_gds))}"\n'
        "quit -noprompt\n"
    )


def run_magic(
    cell_name: str,
    body_tcl: str,
    tech: str = "sky130B",
    extra_search_dirs: list[Path] | None = None,
    output_dir: Path | None = None,
) -> MagicRun:
    """Spawn Magic, execute the generator script, return the GDS path.

    The caller owns the returned `gds_path`. The file lives in a
    caller-supplied `output_dir` if given, otherwise in `tempfile`'s
    default directory.

    Raises:
        MagicNotFoundError: no Magic binary on this machine
        TechFileNotFoundError: no `<tech>.magicrc` under `$PDK_ROOT`
        MagicFailedError: Magic ran but failed or produced no GDS
    """

    magic_bin = locate_magic()
    rcfile = locate_magicrc(tech)

    if output_dir is not None:
        output_dir.mkdir(parents=True, exist_ok=True)
        out_gds = output_dir / f"{cell_name}.gds"
    else:
        # tempfile.NamedTemporaryFile would auto-delete on close;
        # we want the file to outlive this function so the caller
        # can read it. They're responsible for cleanup.
        fd, tmp_path = tempfile.mkstemp(
            prefix=f"prim-{cell_name}-", suffix=".gds"
        )
        os.close(fd)
        out_gds = Path(tmp_path)

    script = build_script(cell_name, body_tcl, out_gds, extra_search_dirs)
    proc = subprocess.run(
        [
            str(magic_bin),
            "-dnull",
            "-noconsole",
            "-rcfile",
            str(rcfile),
        ],
        input=script,
        capture_output=True,
        text=True,
        check=False,
    )
    if proc.returncode != 0 or not out_gds.is_file():
        raise MagicFailedError(proc.returncode, proc.stderr)
    return MagicRun(gds_path=out_gds, stderr=proc.stderr)
