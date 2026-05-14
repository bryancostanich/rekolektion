"""HV FET primitive generators.

Wraps the SKY130 5 V FET draw procs (`sky130_fd_pr__nfet_g5v0d10v5_draw`
and its p-channel sibling) so callers can mint a parameterized
primitive cell as a one-liner:

    name = gen_nfet_hv(w_um=1.2, l_um=1.0, guard=False)
    # → "nfet_hv_W1p2_L1p0_core", with the .rkt at
    #   cell_designs/primitives/nfet_hv_W1p2_L1p0_core.rkt

Subsequent calls with the same params are cache hits and don't
re-run Magic.

Cell-name convention:
    nfet_hv_W{w}_L{l}[_nf{N}][_m{M}]{_core | "" if guard=True}

`{w}` and `{l}` are micrometers with `.` → `p`; `nf=1` and `m=1` are
omitted. `guard=True` produces the standalone (guard-ringed) cell;
`guard=False` produces the `_core` variant used in arrayed layouts
where the parent supplies a single shared guard ring.
"""

from __future__ import annotations

import datetime
from pathlib import Path

from rekolektion.io import rkt
from rekolektion.primitives import _magic_runner
from rekolektion.primitives._cache import (
    cache_hit,
    cache_path,
    compute_digest,
)
from rekolektion.primitives.sky130._gds_to_rkt import read_gds


def _fmt_um(value: float) -> str:
    """Format a micron value the way device names want it: `1.2` → `1p2`,
    `0.15` → `0p15`. Trailing zeros after the decimal are kept (a 1.0 µm
    nfet has a different name than a 1 µm nfet to make the param
    encoding unambiguous — though numerically equal)."""

    return f"{value}".replace(".", "p")


def _fet_cell_name(
    prefix: str,
    w_um: float,
    l_um: float,
    nf: int,
    m: int,
    guard: bool,
) -> str:
    parts = [prefix, f"W{_fmt_um(w_um)}", f"L{_fmt_um(l_um)}"]
    if nf != 1:
        parts.append(f"nf{nf}")
    if m != 1:
        parts.append(f"m{m}")
    if not guard:
        parts.append("core")
    return "_".join(parts)


def _build_fet(
    *,
    prefix: str,
    draw_proc: str,
    defaults_proc: str,
    w_um: float,
    l_um: float,
    nf: int,
    m: int,
    guard: bool,
    primitives_dir: Path | None,
) -> str:
    name = _fet_cell_name(prefix, w_um, l_um, nf, m, guard)
    params = [
        rkt.Property("w", float(w_um)),
        rkt.Property("l", float(l_um)),
        rkt.Property("nf", int(nf)),
        rkt.Property("m", int(m)),
        rkt.Property("guard", rkt.Symbol("true" if guard else "false")),
    ]
    generator = f"sky130/{prefix}"
    digest = compute_digest(generator, params)
    if cache_hit(name, digest, primitives_dir):
        return name

    # The PDK's draw procs read ~30 parameters (poverlap, topc, botc,
    # diffcov, viasrc, …). The corresponding `_defaults` proc returns
    # the canonical full dict; we merge our overrides on top so the
    # caller sees only the parameters that matter at the design level.
    # `cellname create` + `load` enters an empty named cell so the
    # painted geometry lands there instead of Magic's `(UNNAMED)`.
    body = (
        f'cellname create "{name}"\n'
        f'load "{name}"\n'
        f"set defaults [{defaults_proc}]\n"
        f"set override [dict create w {w_um} l {l_um} nf {nf} m {m} "
        f"guard {1 if guard else 0}]\n"
        "set drawdict [dict merge $defaults $override]\n"
        f"{draw_proc} $drawdict\n"
    )
    run = _magic_runner.run_magic(
        cell_name=name,
        body_tcl=body,
        tech="sky130B",
    )
    try:
        doc = read_gds(run.gds_path)
        meta = rkt.Meta(
            generator=generator,
            params=params,
            source="magic-cif sky130B",
            generated=datetime.date.today().isoformat(),
            digest=digest,
        )
        for cell in doc.cells:
            if cell.name == name:
                cell.meta = meta
                break
        else:
            # Magic didn't emit a top cell with the expected name —
            # surface this loudly rather than writing a half-baked
            # .rkt that won't validate as a primitive.
            raise RuntimeError(
                f"magic produced no '{name}' cell in {run.gds_path}; "
                f"stderr: {run.stderr.strip()}"
            )
        doc.top_cell = name
        out_path = cache_path(name, primitives_dir)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(rkt.write(doc), encoding="utf-8")
        return name
    finally:
        try:
            run.gds_path.unlink(missing_ok=True)
        except OSError:
            pass


def gen_nfet_hv(
    w_um: float,
    l_um: float,
    *,
    nf: int = 1,
    m: int = 1,
    guard: bool = False,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) a 5 V HV nfet primitive `.rkt`.

    Returns the cell name. The file lives at
    `cell_designs/primitives/<name>.rkt` (or under `primitives_dir`
    when explicitly provided).
    """

    return _build_fet(
        prefix="nfet_hv",
        draw_proc="sky130::sky130_fd_pr__nfet_g5v0d10v5_draw",
        defaults_proc="sky130::sky130_fd_pr__nfet_g5v0d10v5_defaults",
        w_um=w_um,
        l_um=l_um,
        nf=nf,
        m=m,
        guard=guard,
        primitives_dir=primitives_dir,
    )


def gen_pfet_hv(
    w_um: float,
    l_um: float,
    *,
    nf: int = 1,
    m: int = 1,
    guard: bool = False,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) a 5 V HV pfet primitive `.rkt`.

    Returns the cell name. The file lives at
    `cell_designs/primitives/<name>.rkt` (or under `primitives_dir`
    when explicitly provided).
    """

    return _build_fet(
        prefix="pfet_hv",
        draw_proc="sky130::sky130_fd_pr__pfet_g5v0d10v5_draw",
        defaults_proc="sky130::sky130_fd_pr__pfet_g5v0d10v5_defaults",
        w_um=w_um,
        l_um=l_um,
        nf=nf,
        m=m,
        guard=guard,
        primitives_dir=primitives_dir,
    )
