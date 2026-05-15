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
    topc: bool,
    botc: bool,
) -> str:
    parts = [prefix, f"W{_fmt_um(w_um)}", f"L{_fmt_um(l_um)}"]
    if nf != 1:
        parts.append(f"nf{nf}")
    if m != 1:
        parts.append(f"m{m}")
    if not guard:
        parts.append("core")
    # Topology suffix — encodes which gate contacts are present.
    # Default (top + bottom both) carries no suffix to preserve
    # backwards-compatible names for already-minted primitives.
    if topc and not botc:
        parts.append("topgate")    # gate accessed from above only
    elif botc and not topc:
        parts.append("botgate")    # gate accessed from below only
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
    topc: bool,
    botc: bool,
    primitives_dir: Path | None,
    abut_pad_nm: int = 0,
) -> str:
    if not (topc or botc):
        raise ValueError(
            "at least one of topc / botc must be True — the FET needs "
            "a gate contact somewhere"
        )
    name = _fet_cell_name(
        prefix, w_um, l_um, nf, m, guard, topc, botc
    )
    params = [
        rkt.Property("w", float(w_um)),
        rkt.Property("l", float(l_um)),
        rkt.Property("nf", int(nf)),
        rkt.Property("m", int(m)),
        rkt.Property("guard", rkt.Symbol("true" if guard else "false")),
        rkt.Property("topc", rkt.Symbol("true" if topc else "false")),
        rkt.Property("botc", rkt.Symbol("true" if botc else "false")),
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
        f"guard {1 if guard else 0} "
        f"topc {1 if topc else 0} "
        f"botc {1 if botc else 0}]\n"
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
                # If the caller asked for abut padding (typically because
                # this device family has implant overhang too tight to
                # satisfy diff/tap.3 at bbox-edge abutment), inject a
                # boundary rect that extends the bbox accordingly. The
                # boundary layer (GDS 235/4) is fab-inert and DRC-clean,
                # so this only changes what `_extract_bbox` sees — exactly
                # the contract `place_row` relies on.
                if abut_pad_nm > 0:
                    xs: list[int] = []
                    ys: list[int] = []
                    for el in cell.elements:
                        if isinstance(el, rkt.Rect):
                            xs.extend((el.x1, el.x2))
                            ys.extend((el.y1, el.y2))
                    if xs:
                        x_min = min(xs) - abut_pad_nm
                        x_max = max(xs) + abut_pad_nm
                        y_min = min(ys) - abut_pad_nm
                        y_max = max(ys) + abut_pad_nm
                        cell.elements.append(
                            rkt.Rect(
                                layer=rkt.named("sky130", "boundary"),
                                x1=x_min,
                                y1=y_min,
                                x2=x_max,
                                y2=y_max,
                            )
                        )
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
    topc: bool = True,
    botc: bool = True,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) a 5 V HV nfet primitive `.rkt`.

    Returns the cell name. The file lives at
    `cell_designs/primitives/<name>.rkt` (or under `primitives_dir`
    when explicitly provided).

    `topc` / `botc` control which gate contacts the primitive carries:

      - `topc=True, botc=True` (default) — gate contacts on both
        sides. Right for hand-routed analog where you might tap
        the gate from either direction. Suffix: none.
      - `topc=True, botc=False` (suffix `_topgate`) — gate accessed
        from above only. Source/drain li1 has clear vertical egress
        to a rail BELOW the FET; use this when the FET sits *above*
        a VSS/VDD rail and you want a clean `pin_to_rail` stitch.
      - `topc=False, botc=True` (suffix `_botgate`) — gate accessed
        from below only. Mirror case: FET sits *below* a rail.

    Pick the topology that matches your block's rail placement. For
    a typical std-cell-row arrangement (nfet over VSS, pfet under
    VDD), use `topc=True, botc=False` for the nfet and
    `topc=False, botc=True` for the pfet.
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
        topc=topc,
        botc=botc,
        primitives_dir=primitives_dir,
    )


def gen_pfet_hv(
    w_um: float,
    l_um: float,
    *,
    nf: int = 1,
    m: int = 1,
    guard: bool = False,
    topc: bool = True,
    botc: bool = True,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) a 5 V HV pfet primitive `.rkt`.

    Returns the cell name. The file lives at
    `cell_designs/primitives/<name>.rkt` (or under `primitives_dir`
    when explicitly provided).

    See `gen_nfet_hv` for the `topc` / `botc` semantics — same
    rules for picking topology.
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
        topc=topc,
        botc=botc,
        primitives_dir=primitives_dir,
    )


def gen_nfet_01v8(
    w_um: float,
    l_um: float,
    *,
    nf: int = 1,
    m: int = 1,
    guard: bool = False,
    topc: bool = True,
    botc: bool = True,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) a 1.8 V LV nfet primitive `.rkt`.

    Same semantics as `gen_nfet_hv` (`topc` / `botc` control gate
    contacts; `guard=False` returns a `_core` variant). Uses the
    PDK's `sky130::sky130_fd_pr__nfet_01v8_draw` proc.
    """

    # 1.8 V draw procs give nsdm/psdm overhang of 125 nm to diff. Bbox
    # = nsdm/psdm extent, so bbox-edge abutment puts adjacent diffs at
    # 250 nm — 20 nm short of the diff/tap.3 rule (270 nm). Pad bbox
    # by 10 nm per side via a fab-inert boundary rect so `place_row`
    # gives DRC-clean abutment by construction.
    return _build_fet(
        prefix="nfet_01v8",
        draw_proc="sky130::sky130_fd_pr__nfet_01v8_draw",
        defaults_proc="sky130::sky130_fd_pr__nfet_01v8_defaults",
        w_um=w_um,
        l_um=l_um,
        nf=nf,
        m=m,
        guard=guard,
        topc=topc,
        botc=botc,
        primitives_dir=primitives_dir,
        abut_pad_nm=10,
    )


def gen_pfet_01v8(
    w_um: float,
    l_um: float,
    *,
    nf: int = 1,
    m: int = 1,
    guard: bool = False,
    topc: bool = True,
    botc: bool = True,
    primitives_dir: Path | None = None,
) -> str:
    """Mint (or fetch cached) a 1.8 V LV pfet primitive `.rkt`.

    Same semantics as `gen_pfet_hv` (`topc` / `botc` control gate
    contacts; `guard=False` returns a `_core` variant). Uses the
    PDK's `sky130::sky130_fd_pr__pfet_01v8_draw` proc.
    """

    # See gen_nfet_01v8 for the abut_pad_nm rationale — same 10 nm
    # padding for the matching diff/tap.3 abutment safety on PFETs.
    return _build_fet(
        prefix="pfet_01v8",
        draw_proc="sky130::sky130_fd_pr__pfet_01v8_draw",
        defaults_proc="sky130::sky130_fd_pr__pfet_01v8_defaults",
        w_um=w_um,
        l_um=l_um,
        nf=nf,
        m=m,
        guard=guard,
        topc=topc,
        botc=botc,
        primitives_dir=primitives_dir,
        abut_pad_nm=10,
    )
