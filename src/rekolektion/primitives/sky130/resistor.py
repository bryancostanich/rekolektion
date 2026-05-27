"""Poly-resistor primitive generators.

SKY130 ships a family of high-sheet-resistance poly resistors
(``xpoly`` flavour, ~2 kΩ/□) at five fixed widths:

    sky130_fd_pr__res_xhigh_po_0p35   — W = 0.35 µm
    sky130_fd_pr__res_xhigh_po_0p69   — W = 0.69 µm
    sky130_fd_pr__res_xhigh_po_1p41   — W = 1.41 µm
    sky130_fd_pr__res_xhigh_po_2p85   — W = 2.85 µm
    sky130_fd_pr__res_xhigh_po_5p73   — W = 5.73 µm

Each variant has its own draw proc. Length `l_um` is freely
parameterised (≥ 0.5 µm — the PDK enforces `lmin`).

The caller picks a width with `width_um=` (must match one of the
five fixed values). The cell name encodes both dimensions.
"""

from __future__ import annotations

from pathlib import Path

from rekolektion.io import rkt
from rekolektion.primitives.sky130._device_builder import build_device, fmt_um


# Width → suffix (matches the PDK draw-proc naming).
_RES_XHIGH_PO_WIDTHS: dict[float, str] = {
    0.35: "0p35",
    0.69: "0p69",
    1.41: "1p41",
    2.85: "2p85",
    5.73: "5p73",
}

# Bump on emit-side change. Invalidates cached resistor .rkt files.
#  v2 (2026-05-18): tag PDK-emitted "R1"/"R2" li1_label labels as
#  LabelKind.DEVICE_TERMINAL so ratlines / LVS flood-fill don't see
#  them as same-name nets across multiple resistor instances. See
#  rkt_primitive_workflow.md §"Label kinds".
_RES_GENERATOR_VERSION = 2


def _tag_res_port_labels(doc: rkt.Document) -> None:
    """Mark every `li1_label`-layered label in any cell of the
    resistor primitive doc as `LabelKind.DEVICE_TERMINAL`.

    The PDK's xpoly draw proc emits "R1"/"R2" terminal labels;
    these are device-terminal annotations, not signal nets.
    Without this tag, two resistor SRefs in a parent each carry
    a "R1" label that aliases into a single fake "R1" net with
    spurious ratlines between them.
    """
    li1_label_layer = rkt.named("sky130", "li1_label")
    for cell in doc.cells:
        for el in cell.elements:
            if isinstance(el, rkt.Label) and el.layer == li1_label_layer:
                el.kind = rkt.LabelKind.DEVICE_TERMINAL


def gen_res_xhigh_po(
    width_um: float,
    l_um: float,
    *,
    m: int = 1,
    guard: bool = False,
    nx: int = 1,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) an xpoly high-sheet-resistance resistor.

    Parameters
    ----------
    width_um : 0.35 | 0.69 | 1.41 | 2.85 | 5.73
        Resistor width in µm. Must match one of the PDK's fixed
        widths — any other value raises ValueError.
    l_um : float
        Resistor length per finger in µm. ≥ 0.5 (lmin enforced by
        the PDK).  For a snake (`nx > 1`), this is the length of
        ONE finger — the total resistor length is `l_um × nx`.
    m : int, default 1
        Multiplicity (M=N draws N parallel devices).
    guard : bool, default False
        ``True`` adds the PDK's substrate guard ring around the
        resistor. ``False`` (default) returns a ``_core`` variant
        for hand-routed analog where the parent tub supplies the
        well + tap. The PDK default is ``guard=1``; we flip it for
        consistency with the FET generators.
    nx : int, default 1
        Number of fingers in a serpentine layout.  `nx=1` (default)
        is a straight bar.  `nx>1` folds the resistor — each finger
        has length `l_um`; the total electrical length is
        `l_um × nx`.  Use this to control aspect ratio: a 530 kΩ
        resistor at W=1.41 needs ~375 µm total, which is unwieldy as
        a straight bar; `nx=10, l_um=37.5` gives the same R in a
        ~20 × 42 µm footprint.

        Note: The PDK draw proc uses `snake=1` (a boolean enable)
        with `nx` as the fold count.  This generator exposes only
        `nx` for clarity — `snake` is set automatically when `nx>1`.

    Cell-name convention:
        ``res_xhigh_po_W<width>_L<length>[_m{N}][_nx{X}]{_core | ""}``
    """

    if width_um not in _RES_XHIGH_PO_WIDTHS:
        raise ValueError(
            f"width_um={width_um} not in PDK set "
            f"{sorted(_RES_XHIGH_PO_WIDTHS)} — pick one of the fixed widths"
        )
    if nx < 1:
        raise ValueError(f"nx must be ≥ 1, got {nx}")

    width_suffix = _RES_XHIGH_PO_WIDTHS[width_um]

    parts = ["res_xhigh_po", f"W{width_suffix}", f"L{fmt_um(l_um)}"]
    if m != 1:
        parts.append(f"m{m}")
    if nx != 1:
        parts.append(f"nx{nx}")
    if not guard:
        parts.append("core")
    cell_name = "_".join(parts)

    generator = "sky130/res_xhigh_po"
    defaults_proc = (
        f"sky130::sky130_fd_pr__res_xhigh_po_{width_suffix}_defaults"
    )
    draw_proc = (
        f"sky130::sky130_fd_pr__res_xhigh_po_{width_suffix}_draw"
    )
    snake_enable = 1 if nx > 1 else 0
    overrides: dict[str, object] = {
        "w": float(width_um),
        "l": float(l_um),
        "m": int(m),
        "nx": int(nx),
        "snake": int(snake_enable),
        # `guard` toggles the per-cell substrate ring. `n_guard`
        # and `hv_guard` only apply to the n-poly variants; we
        # leave them at PDK default for xpoly.
        "guard": bool(guard),
    }
    meta_params = [
        rkt.Property("width", float(width_um)),
        rkt.Property("l", float(l_um)),
        rkt.Property("m", int(m)),
        rkt.Property("guard", rkt.Symbol("true" if guard else "false")),
        rkt.Property("nx", int(nx)),
    ]

    return build_device(
        cell_name=cell_name,
        generator=generator,
        defaults_proc=defaults_proc,
        draw_proc=draw_proc,
        overrides=overrides,
        meta_params=meta_params,
        primitives_dir=primitives_dir,
        generator_version=_RES_GENERATOR_VERSION,
        post_process=_tag_res_port_labels,
    )
