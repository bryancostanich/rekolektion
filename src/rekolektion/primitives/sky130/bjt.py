"""BJT primitive generators (substrate PNPs).

SKY130 ships fixed-size substrate PNPs at two emitter geometries:

    sky130_fd_pr__pnp_05v5_W0p68L0p68   — small (≈0.46 µm² emitter)
    sky130_fd_pr__pnp_05v5_W3p40L3p40   — large (≈11.56 µm², ~25× area)

Both are usable as the unit element in a sub-bandgap PTAT branch
(area ratio sets ln(N)·V_T). The Banba topology in Track 37 uses
one of each, biased to identical I_C — the area mismatch is what
generates the ΔV_BE.

The two variants share a common parameterless `defaults` proc (just
geometric placement params: `nx`/`ny`/`deltax`/`deltay`/`xstep`/
`ystep`). The caller picks the variant via `kind=`; everything else
funnels through the generic `build_device` path.
"""

from __future__ import annotations

from pathlib import Path

from rekolektion.io import rkt
from rekolektion.primitives.sky130._device_builder import build_device


# Map a friendly "kind" name → the PDK device's name + emitter dims.
# `kind` is what callers pass; the value is the variant identifier
# used in both the draw proc name and the resulting cell name.
_PNP_VARIANTS: dict[str, str] = {
    "small": "W0p68L0p68",   # unit PNP (≈ 0.46 µm² emitter)
    "large": "W3p40L3p40",   # ≈ 25× area PNP for sub-bandgap branches
}

# Bump on any emit-side change (e.g. label tagging). Invalidates
# every cached PNP `.rkt` predating the bump so the next call re-mints.
#  v2 (2026-05-18): tag PDK-emitted "Emitter"/"Base"/"Collector"
#  li1_label labels as LabelKind.DEVICE_TERMINAL so ratlines and
#  LVS flood-fill skip them — same fix the FET generator applies to
#  D/G/S/B labels. See rkt_primitive_workflow.md §"Label kinds".
_PNP_GENERATOR_VERSION = 2


def _tag_pnp_port_labels(doc: rkt.Document) -> None:
    """Mark every `li1_label`-layered label in any cell of the PNP
    primitive doc as `LabelKind.DEVICE_TERMINAL`.

    The PDK's PNP draw proc emits "Emitter", "Base", "Collector"
    labels inside the PDK sub-cell (e.g. `sky130_fd_pr__rf_pnp_05v5_W3p40L3p40`),
    NOT the generator's wrapper cell. So this walks all cells in the
    doc rather than just `doc.top_cell`. The labels are foundry
    device-terminal annotations, not signal nets — consumers like
    viz ratlines and Magic's LabelFlood already skip
    `DeviceTerminal`-kind labels.
    """
    li1_label_layer = rkt.named("sky130", "li1_label")
    for cell in doc.cells:
        for el in cell.elements:
            if isinstance(el, rkt.Label) and el.layer == li1_label_layer:
                el.kind = rkt.LabelKind.DEVICE_TERMINAL


def gen_pnp_05v5(
    kind: str = "small",
    *,
    nx: int = 1,
    ny: int = 1,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) a 5.5 V substrate PNP primitive `.rkt`.

    Parameters
    ----------
    kind : "small" | "large"
        Which PDK variant. `"small"` → W=0.68 µm × L=0.68 µm,
        `"large"` → W=3.40 µm × L=3.40 µm. The area ratio
        large : small ≈ 25 : 1, the canonical sub-bandgap pair.
    nx, ny :
        Tile count. Default 1×1 (single device); >1 mints an array
        sharing one collector tub.

    Cell-name convention:
        `pnp_05v5_<variant>[_nx{nx}][_ny{ny}]`

    Returns the cell name.
    """

    if kind not in _PNP_VARIANTS:
        raise ValueError(
            f"unknown PNP kind '{kind}'; expected one of "
            f"{sorted(_PNP_VARIANTS)}"
        )
    variant = _PNP_VARIANTS[kind]

    parts = ["pnp_05v5", variant]
    if nx != 1:
        parts.append(f"nx{nx}")
    if ny != 1:
        parts.append(f"ny{ny}")
    cell_name = "_".join(parts)

    generator = "sky130/pnp_05v5"
    defaults_proc = f"sky130::sky130_fd_pr__pnp_05v5_{variant}_defaults"
    draw_proc = f"sky130::sky130_fd_pr__pnp_05v5_{variant}_draw"
    overrides: dict[str, object] = {"nx": int(nx), "ny": int(ny)}
    meta_params = [
        rkt.Property("kind", rkt.Symbol(kind)),
        rkt.Property("nx", int(nx)),
        rkt.Property("ny", int(ny)),
    ]

    return build_device(
        cell_name=cell_name,
        generator=generator,
        defaults_proc=defaults_proc,
        draw_proc=draw_proc,
        overrides=overrides,
        meta_params=meta_params,
        primitives_dir=primitives_dir,
        generator_version=_PNP_GENERATOR_VERSION,
        post_process=_tag_pnp_port_labels,
    )
