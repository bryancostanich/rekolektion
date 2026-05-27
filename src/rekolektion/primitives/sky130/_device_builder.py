"""Generic SKY130 device-primitive builder.

Shared backbone for non-FET generators (BJTs, resistors, MIM caps).
The FET path lives in `fet.py` because the gate-topology semantics
(`topc`/`botc`/`guard`) are FET-specific. Everything else funnels
through `build_device` here:

    cellname create "<name>"
    load "<name>"
    set defaults [<defaults_proc>]
    set override [dict create k1 v1 k2 v2 ...]
    set drawdict [dict merge $defaults $override]
    <draw_proc> $drawdict

The caller supplies:

- A unique cell name (encoding all caller params).
- The Magic defaults+draw proc pair for the device.
- A dict of overrides to merge on top of the defaults.
- A `(meta …)` provenance params list (what gets recorded in the
  `.rkt`'s meta block — should be every design-level param the
  caller exposed, so cache digests are stable).

The runner shells out to Magic, parses the resulting GDS into a
`.rkt`, attaches the meta block, and writes to the primitives dir.
Cache-aware: identical `(generator, params)` pairs short-circuit.
"""

from __future__ import annotations

import datetime
import os
from collections.abc import Callable
from pathlib import Path

from rekolektion.io import rkt
from rekolektion.primitives import _magic_runner
from rekolektion.primitives._cache import (
    cache_hit,
    cache_path,
    compute_digest,
)
from rekolektion.primitives.sky130._gds_to_rkt import read_gds


def _pdk_cell_libraries(tech: str = "sky130B") -> list[Path]:
    """Return PDK cell-library dirs that should be on Magic's addpath.

    SKY130's "fixed-geometry" devices (substrate PNPs, varactors,
    inductors, vpp caps, …) ship as pre-laid-out `.mag` cells under
    ``$PDK_ROOT/<tech>/libs.ref/sky130_fd_pr/mag/``. Their draw procs
    call ``getcell <name>`` to instance them, which only works when
    Magic can find them on its search path. Returns existing dirs
    only; absent dirs are silently skipped.
    """

    root = os.environ.get("PDK_ROOT", "").strip() or str(Path.home() / ".volare")
    candidates = [
        Path(root) / tech / "libs.ref" / "sky130_fd_pr" / "mag",
    ]
    return [p for p in candidates if p.is_dir()]


def _format_override_value(value) -> str:
    """Serialise a Python value into a Tcl dict literal token.

    Booleans become 1/0 the same way the FET builder does it.
    Strings get quoted; numbers pass through as-is.
    """

    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, (int, float)):
        return str(value)
    return '"' + str(value).replace('"', '\\"') + '"'


def build_device(
    *,
    cell_name: str,
    generator: str,
    defaults_proc: str,
    draw_proc: str,
    overrides: dict[str, object],
    meta_params: list[rkt.Property],
    primitives_dir: Path | None = None,
    generator_version: int = 1,
    post_process: Callable[[rkt.Document], None] | None = None,
) -> str:
    """Mint (or fetch cached) a SKY130 primitive `.rkt`.

    Returns `cell_name`. Cache hit when `(generator, meta_params,
    generator_version)` matches a prior call. On miss: spawn Magic,
    run the device's draw proc, convert GDS → `.rkt`, attach
    `(meta …)`, optionally post-process the doc, write.

    `post_process(doc)` runs after `cell.meta` is set but before the
    `.rkt` is written. Generators use it to apply emit-time
    invariants — e.g. tagging foundry-emitted device-terminal labels
    as `LabelKind.DEVICE_TERMINAL` so ratlines and LVS skip them.
    Bump `generator_version` whenever the post-process semantics
    change so cached `.rkt` files predating the change re-mint.
    """

    digest = compute_digest(
        generator, meta_params, generator_version=generator_version,
    )
    if cache_hit(cell_name, digest, primitives_dir):
        return cell_name

    override_pairs = " ".join(
        f"{k} {_format_override_value(v)}" for k, v in overrides.items()
    )
    body = (
        f'cellname create "{cell_name}"\n'
        f'load "{cell_name}"\n'
        # Initialise the box at origin. The PDK's `fixed_draw` helper
        # places its getcell-instanced device at the current box; if
        # the box is undefined (the default on a fresh `load`), the
        # instance ends up in `(UNNAMED)` rather than our target cell
        # and the GDS write loses it.
        "box 0 0 0 0\n"
        f"set defaults [{defaults_proc}]\n"
        f"set override [dict create {override_pairs}]\n"
        "set drawdict [dict merge $defaults $override]\n"
        f"{draw_proc} $drawdict\n"
    )
    run = _magic_runner.run_magic(
        cell_name=cell_name,
        body_tcl=body,
        tech="sky130B",
        extra_search_dirs=_pdk_cell_libraries("sky130B"),
    )
    try:
        doc = read_gds(run.gds_path)
        meta = rkt.Meta(
            generator=generator,
            params=meta_params,
            source="magic-cif sky130B",
            generated=datetime.date.today().isoformat(),
            digest=digest,
        )
        for cell in doc.cells:
            if cell.name == cell_name:
                cell.meta = meta
                break
        else:
            raise RuntimeError(
                f"magic produced no '{cell_name}' cell in {run.gds_path}; "
                f"stderr: {run.stderr.strip()}"
            )
        doc.top_cell = cell_name
        if post_process is not None:
            post_process(doc)
        out_path = cache_path(cell_name, primitives_dir)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(rkt.write(doc), encoding="utf-8")
        return cell_name
    finally:
        try:
            run.gds_path.unlink(missing_ok=True)
        except OSError:
            pass


def fmt_um(value: float) -> str:
    """Format a micron value the way device names want it: `0.68` →
    `0p68`. Trailing zeros after the decimal are kept (a 1.0 µm
    device name differs from a 1 µm name to keep the param encoding
    unambiguous)."""

    return f"{value}".replace(".", "p")
