"""Placement helpers — Pattern A (abut) and Pattern B (tub).

The two patterns documented in
`docs/workflows/rkt_primitive_workflow.md` express the only two
DRC-clean ways to compose `_core` primitives:

* **`place_row`** — Pattern A. Lay primitives end-to-end with pitch
  equal to each cell's bbox extent. Wells abut and merge under
  nwell.2a. Use for std-cell-row-style layout where same-well-type
  cells share a continuous well band.

* **`place_tub`** — Pattern B. Paint a parent well rectangle large
  enough to contain every primitive (plus margin) and drop the
  primitives inside at caller-specified origins. Use when matching
  / symmetry demands non-uniform spacing — diff pairs, current
  mirrors, cascodes.

Both helpers reject mixed well-types as a hard error: an nfet and a
pfet in the same row or tub means at least one well violates either
abutment or tub coverage. The error message tells the caller which
pattern to switch to.

Helpers return plain `rekolektion.io.rkt` constructs (SRefs, Rects).
The caller assembles them into a `Cell`. No magic, no parallel
schema.
"""

from __future__ import annotations

import warnings
from dataclasses import dataclass, field
from pathlib import Path

from rekolektion.io import rkt
from rekolektion.layout._rkt_bbox import RktPrimitiveSummary, read_primitive


# ─── Inspection ──────────────────────────────────────────────────────


@dataclass(frozen=True)
class PrimitiveInfo:
    """Inspected metadata about one primitive cell on disk."""

    name: str
    generator: str | None
    bbox: tuple[int, int, int, int]  # (x_min, y_min, x_max, y_max) in DBU

    @property
    def width(self) -> int:
        return self.bbox[2] - self.bbox[0]

    @property
    def height(self) -> int:
        return self.bbox[3] - self.bbox[1]

    @property
    def is_nmos(self) -> bool:
        """True for `sky130/nfet_*` generators. Derives from the
        meta block, NOT the cell name (which is user-controllable)."""

        return self.generator is not None and self.generator.startswith(
            "sky130/nfet"
        )

    @property
    def is_pmos(self) -> bool:
        return self.generator is not None and self.generator.startswith(
            "sky130/pfet"
        )


def _default_primitives_dir() -> Path:
    """Locate `cell_designs/primitives/` by walking up from cwd. Used
    when the caller doesn't pass `primitives_dir` explicitly."""

    here = Path.cwd().resolve()
    for ancestor in [here, *here.parents]:
        candidate = ancestor / "cell_designs" / "primitives"
        if candidate.is_dir():
            return candidate
    return here / "cell_designs" / "primitives"


def inspect_primitive(
    name: str,
    primitives_dir: Path | None = None,
) -> PrimitiveInfo:
    """Read a primitive .rkt and return its bbox + generator classification.

    Pass `name` either as the bare cell name (`"nfet_hv_W1p2_L1p0_core"`)
    — in which case the helper resolves it under `primitives_dir/<name>.rkt`
    — or as a path-like string ending in `.rkt`.
    """

    if name.endswith(".rkt"):
        path = Path(name)
    else:
        base = primitives_dir or _default_primitives_dir()
        path = base / f"{name}.rkt"
    summary = read_primitive(path)
    return PrimitiveInfo(
        name=summary.name,
        generator=summary.generator,
        bbox=summary.bbox,
    )


# ─── Pattern A: place_row ────────────────────────────────────────────


def _classify_well_type(infos: list[PrimitiveInfo]) -> str:
    """Return 'nmos' / 'pmos' if all infos share a well type, else
    raise ValueError describing the mismatch."""

    nmos = [i.name for i in infos if i.is_nmos]
    pmos = [i.name for i in infos if i.is_pmos]
    other = [i.name for i in infos if not i.is_nmos and not i.is_pmos]
    if nmos and pmos:
        raise ValueError(
            f"cannot mix nmos and pmos primitives in one placement "
            f"(got {len(nmos)} nmos + {len(pmos)} pmos). Their wells "
            f"don't share a continuous region. Place them in separate "
            f"rows / tubs, or use one tub per well-type."
        )
    if other:
        raise ValueError(
            f"primitive(s) without a recognized generator: "
            f"{', '.join(other[:5])}. Well-type can't be inferred — "
            f"placement helpers refuse to guess. If this is a new "
            f"generator, update the is_nmos/is_pmos classification."
        )
    return "nmos" if nmos else "pmos"


def place_row(
    primitive_names: list[str],
    *,
    axis: str = "x",
    origin: tuple[int, int] = (0, 0),
    primitives_dir: Path | None = None,
) -> list[rkt.SRef]:
    """Abut primitives end-to-end (Pattern A). Returns SRefs ready to
    drop into a Cell's elements.

    Pitch equals each primitive's bbox extent along `axis` ('x' for a
    horizontal row, 'y' for a vertical column), so adjacent primitives'
    well/implant rectangles share an edge and merge — satisfying
    `nwell.2a` and the analogous psdm/nsdm/hvi rules by construction.

    Raises:
        ValueError: if primitives mix nmos and pmos, or the axis isn't 'x'/'y'.

    Warns:
        UserWarning: if primitives in the row have mismatched extents
            on the *other* axis (e.g. different heights in an x-row).
            The row works geometrically but top-edge alignment won't
            be uniform — usually a sign the caller mixed cell flavors.

    Example:
        >>> nfet = gen_nfet_hv(w_um=1.2, l_um=1.0)
        >>> srefs = place_row([nfet, nfet, nfet], origin=(0, 0))
        >>> # Three identical nfets, abutting, wells merge.
    """

    if axis not in ("x", "y"):
        raise ValueError(f"axis must be 'x' or 'y', got {axis!r}")
    if not primitive_names:
        return []
    infos = [inspect_primitive(n, primitives_dir) for n in primitive_names]
    _classify_well_type(infos)  # raises on mix

    # Warn on cross-axis mismatch — not a DRC fault but usually a bug.
    if axis == "x":
        sizes = {i.height for i in infos}
        cross = "height"
    else:
        sizes = {i.width for i in infos}
        cross = "width"
    if len(sizes) > 1:
        warnings.warn(
            f"place_row got primitives with mismatched {cross}s "
            f"({sorted(sizes)} DBU). The {axis}-row will work, but "
            f"the opposite edges won't align — usually a sign of mixed "
            f"cell flavors.",
            stacklevel=2,
        )

    srefs: list[rkt.SRef] = []
    cursor_x, cursor_y = origin
    for info in infos:
        # SRef origin places the primitive's local (0, 0) at (X, Y) in
        # parent coords. We want the primitive's left edge (smallest x
        # in parent coords) to land at cursor_x:
        #   cursor_x = X + bbox.x_min  →  X = cursor_x - bbox.x_min
        if axis == "x":
            srefs.append(
                rkt.SRef(
                    cell=info.name,
                    origin=(cursor_x - info.bbox[0], cursor_y),
                )
            )
            cursor_x += info.width
        else:
            srefs.append(
                rkt.SRef(
                    cell=info.name,
                    origin=(cursor_x, cursor_y - info.bbox[1]),
                )
            )
            cursor_y += info.height
    return srefs


# ─── Pattern B: place_tub ────────────────────────────────────────────


@dataclass
class TubResult:
    """Return value of `place_tub`.

    Drop `well_rects + srefs` straight into a `rkt.Cell.elements` —
    they're already in parent-coordinate space and ordered so the
    tub paint sits *under* the primitive instances (rendering and
    most flatten passes are order-stable, so order is meaningful
    for visual layering).
    """

    well_rects: list[rkt.Rect] = field(default_factory=list)
    srefs: list[rkt.SRef] = field(default_factory=list)

    @property
    def elements(self) -> list[rkt.Element]:
        """All elements, well paint first then SRefs. Most callers
        spread this directly into `rkt.Cell(elements=...)`."""

        return [*self.well_rects, *self.srefs]


def place_tub(
    primitives: list[tuple[str, tuple[int, int]]],
    *,
    well_layer: str | None = None,
    extra_layers: list[str] | None = None,
    margin_um: float = 0.4,
    primitives_dir: Path | None = None,
    dbu_nm: int = 1,
) -> TubResult:
    """Paint a parent well rectangle covering every primitive + margin,
    plus any extra implants/markers (e.g. `hvi`), and place the
    primitives at their caller-specified origins inside.

    `primitives` is a list of `(cell_name, (x, y))` pairs. The origin
    is the SRef's origin in parent coordinates — same semantics as
    every other `rkt.SRef`. Spacing between primitives is up to the
    caller (they can be matched, cascoded, whatever); the tub
    absorbs the inter-primitive well-spacing question.

    `well_layer` defaults from the inferred well-type: `nwell` for
    pmos primitives, `pwell` for nmos. Override when the design needs
    something else (e.g. `dnwell` for deep-well isolation).

    `margin_um` is the surround the tub extends past the union bbox.
    Defaults to 0.4 µm — slightly more than the `nwell.4` enclosure
    rule for diffusion. Increase for safety, decrease at your own DRC
    peril.

    Raises:
        ValueError: if primitives mix nmos and pmos, or list is empty.
    """

    if not primitives:
        raise ValueError("place_tub needs at least one primitive")
    extra_layers = list(extra_layers or [])

    pairs = [
        (inspect_primitive(name, primitives_dir), origin)
        for name, origin in primitives
    ]
    infos = [info for info, _ in pairs]
    well_type = _classify_well_type(infos)  # raises on mix

    if well_layer is None:
        well_layer = "nwell" if well_type == "pmos" else "pwell"

    # Auto-add hvi for HV devices (any generator containing "_hv").
    if any(
        info.generator and "_hv" in info.generator for info in infos
    ) and "hvi" not in extra_layers:
        extra_layers.append("hvi")

    # Union bbox in parent coords: each primitive's bbox shifted by
    # its origin.
    min_x = min(o[0] + info.bbox[0] for info, o in pairs)
    min_y = min(o[1] + info.bbox[1] for info, o in pairs)
    max_x = max(o[0] + info.bbox[2] for info, o in pairs)
    max_y = max(o[1] + info.bbox[3] for info, o in pairs)
    margin_dbu = int(round(margin_um * 1000 / dbu_nm))
    tub = (
        min_x - margin_dbu,
        min_y - margin_dbu,
        max_x + margin_dbu,
        max_y + margin_dbu,
    )

    well_rects = [
        rkt.Rect(
            layer=rkt.named("sky130", well_layer),
            x1=tub[0],
            y1=tub[1],
            x2=tub[2],
            y2=tub[3],
        )
    ]
    for layer_name in extra_layers:
        well_rects.append(
            rkt.Rect(
                layer=rkt.named("sky130", layer_name),
                x1=tub[0],
                y1=tub[1],
                x2=tub[2],
                y2=tub[3],
            )
        )

    srefs = [
        rkt.SRef(cell=info.name, origin=origin) for info, origin in pairs
    ]
    return TubResult(well_rects=well_rects, srefs=srefs)
