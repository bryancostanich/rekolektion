"""MIM capacitor primitive generators.

SKY130 offers two stacked MIM capacitor flavours:

    sky130_fd_pr__cap_mim_m3_1  — single-layer MIM between met3 + capm
    sky130_fd_pr__cap_mim_m3_2  — double-layer (capm + capm2), ~2× C

Both are freely parameterised in W and L (each ≥ 2 µm, ≤ 30 µm) and
sit *above* the active silicon — so the area underneath them on
lower metals (li1/met1/met2) is free for routing and devices.

The single-layer variant covers the analog use cases in this repo
(Track 37 Miller cap, etc.). The 2-stack flavour is exposed via the
``stack=`` arg for future use.
"""

from __future__ import annotations

from pathlib import Path

from rekolektion.io import rkt
from rekolektion.primitives.sky130._device_builder import build_device, fmt_um


_CAP_MIM_STACKS: dict[int, str] = {
    1: "1",   # cap_mim_m3_1 (single dielectric)
    2: "2",   # cap_mim_m3_2 (double dielectric, ~2x capacitance)
}


def gen_cap_mim_m3(
    w_um: float,
    l_um: float,
    *,
    stack: int = 1,
    bconnect: bool = True,
    tconnect: bool = True,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) a MIM (met3-stack) capacitor primitive.

    Parameters
    ----------
    w_um, l_um : float
        Plate width / length in µm. Each must be 2.0 ≤ x ≤ 30.0
        (PDK ``wmin``/``wmax`` / ``lmin``/``lmax``).
    stack : 1 | 2, default 1
        Which MIM stack to use. ``1`` is the single-dielectric
        ``cap_mim_m3_1`` (≈ 2 fF/µm²); ``2`` is the double-stack
        ``cap_mim_m3_2`` (≈ 4 fF/µm²) at higher mask cost.
    bconnect : bool, default True
        Route the bottom plate up to met3 via the standard bottom
        contact ring. PDK default is on; disable only if the parent
        block intends to contact the bottom plate directly.
    tconnect : bool, default True
        Route the top plate up to met4 via the standard top contact
        ring.

    Cell-name convention:
        ``cap_mim_m3_{stack}_W<width>_L<length>[_nobot][_notop]``
    """

    if stack not in _CAP_MIM_STACKS:
        raise ValueError(
            f"stack={stack} not supported; expected one of "
            f"{sorted(_CAP_MIM_STACKS)}"
        )

    parts = [
        f"cap_mim_m3_{stack}",
        f"W{fmt_um(w_um)}",
        f"L{fmt_um(l_um)}",
    ]
    if not bconnect:
        parts.append("nobot")
    if not tconnect:
        parts.append("notop")
    cell_name = "_".join(parts)

    generator = f"sky130/cap_mim_m3_{stack}"
    defaults_proc = f"sky130::sky130_fd_pr__cap_mim_m3_{stack}_defaults"
    draw_proc = f"sky130::sky130_fd_pr__cap_mim_m3_{stack}_draw"
    overrides: dict[str, object] = {
        "w": float(w_um),
        "l": float(l_um),
        "bconnect": bool(bconnect),
        "tconnect": bool(tconnect),
    }
    meta_params = [
        rkt.Property("w", float(w_um)),
        rkt.Property("l", float(l_um)),
        rkt.Property("stack", int(stack)),
        rkt.Property("bconnect", rkt.Symbol("true" if bconnect else "false")),
        rkt.Property("tconnect", rkt.Symbol("true" if tconnect else "false")),
    ]

    return build_device(
        cell_name=cell_name,
        generator=generator,
        defaults_proc=defaults_proc,
        draw_proc=draw_proc,
        overrides=overrides,
        meta_params=meta_params,
        primitives_dir=primitives_dir,
    )
