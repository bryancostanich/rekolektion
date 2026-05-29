"""ReRAM primitive generator (static fixed-geometry PDK cell).

SkyWater ships `sky130_fd_pr_reram__reram_cell` as a static `.gds`
device in a separate add-on repo (`skywater-pdk-libs-sky130_fd_pr_reram`)
with NO Tcl `defaults`+`draw` procs and NO `.mag` source. Unlike the
FET/PNP path that drives the PDK's own draw procs, the reram cell
is `gds read`-loaded into Magic's DB then `getcell`-instanced into a
wrapper. This matches the workflow doc's "Fixed-geometry primitives
are multi-cell `.rkt` files" pattern.

Repo location is found via `SKY130_FD_PR_RERAM_ROOT` env var, falling
back to the default checkout path under `pub_code/`.
"""

from __future__ import annotations

import os
from pathlib import Path

from rekolektion.io import rkt
from rekolektion.primitives.sky130._device_builder import build_static_device


_DEFAULT_RERAM_REPO = Path(
    "/Users/bryancostanich/git_repos/pub_code/skywater-pdk-libs-sky130_fd_pr_reram"
)

# Bump on emit-side semantic changes (label tagging, post-process
# additions) to invalidate cached `.rkt` files predating the bump.
#  v2 (2026-05-28): center wrapper SRef at (0, 0) per workflow doc
#  fixed-geometry convention. v1 left it at Magic's getcell offset.
#  v3 (2026-05-28): GDS (201, 20) registered as `sky130:rram` in
#  _layer_map.py — previously emitted as `unknown:201/20`.
#  v4 (2026-05-28): rename layer (201, 20) from `rram` → `reram` to
#  match the F# viz layer table (Layer.fs); fixes a silent (0, 0)
#  layer-name miss on `.rkt`→GDS round-trip.
_RERAM_GENERATOR_VERSION = 4


def _reram_gds_path() -> Path:
    root = Path(
        os.environ.get("SKY130_FD_PR_RERAM_ROOT", str(_DEFAULT_RERAM_REPO))
    )
    gds = root / "cells" / "reram_cell" / "sky130_fd_pr_reram__reram_cell.gds"
    if not gds.is_file():
        raise FileNotFoundError(
            f"sky130_fd_pr_reram GDS not found at {gds}; set "
            "SKY130_FD_PR_RERAM_ROOT to the cloned repo root"
        )
    return gds


def _tag_reram_port_labels(doc: rkt.Document) -> None:
    """Tag PDK-emitted TE/BE labels as `DeviceTerminal`.

    The reram cell carries terminal labels on its m1 (BE) and m2 (TE)
    stubs. They are device-terminal annotations, not signal nets —
    mark them so net consumers (viz ratlines, LVS flood-fill) skip
    them, same fix as the FET D/G/S/B and PNP Emitter/Base/Collector
    paths. Walks every cell in the doc since the labels live inside
    the PDK child cell, not the wrapper.
    """
    m1_label = rkt.named("sky130", "met1_label")
    m2_label = rkt.named("sky130", "met2_label")
    for cell in doc.cells:
        for el in cell.elements:
            if isinstance(el, rkt.Label) and el.layer in (m1_label, m2_label):
                el.kind = rkt.LabelKind.DEVICE_TERMINAL


def _center_wrapper_sref(doc: rkt.Document, wrapper_name: str) -> None:
    """Snap the wrapper's child SRef to origin (0, 0).

    Magic's `getcell` after `box 0 0 0 0` places the PDK cell's
    lower-left at parent origin, not its center — so the resulting
    SRef sits at the cell's half-extent (e.g. (160, 160) for the
    reram cell whose bbox is -160..160). The workflow doc's fixed-
    geometry convention puts the child SRef at `(origin 0 0)` so
    `place_row` / `inspect_primitive` see a bbox centered on the
    wrapper origin. Translate the SRef back to origin; the child
    cell's internal geometry already sits centered around its own
    origin, so the resulting wrapper bbox is symmetric.
    """
    for cell in doc.cells:
        if cell.name != wrapper_name:
            continue
        for el in cell.elements:
            if isinstance(el, rkt.SRef):
                el.origin = (0, 0)


def _post_process(doc: rkt.Document) -> None:
    _tag_reram_port_labels(doc)
    _center_wrapper_sref(doc, "reram_cell")


def gen_reram_cell(*, primitives_dir: Path | None = None) -> str:
    """Mint (or fetch cached) the SKY130 reram primitive `.rkt`.

    Wraps the foundry static cell `sky130_fd_pr_reram__reram_cell`.
    Parameterless — one fixed device (0.32 × 0.32 µm `r1c` rect with
    BE on m1, TE on m2). Returns the cell name (`reram_cell`).

    The resulting `.rkt` has two cells: the wrapper `reram_cell` at
    top, instancing `sky130_fd_pr_reram__reram_cell` as a child SRef
    at origin (0, 0).
    """
    return build_static_device(
        cell_name="reram_cell",
        generator="sky130/reram_cell",
        foundry_cell="sky130_fd_pr_reram__reram_cell",
        foundry_gds=_reram_gds_path(),
        meta_params=[],
        primitives_dir=primitives_dir,
        generator_version=_RERAM_GENERATOR_VERSION,
        post_process=_post_process,
    )
