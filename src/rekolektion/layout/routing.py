"""Routing helpers — pin patches, wires, via stacks.

Block-level routing needs three primitive operations:

  1. **Patch a cell pin.** `_core` primitives expose their S/D/G
     terminals on `li1` (the contact-up layer) without a met1 cap.
     Routing from those pins requires a parent-painted met1 patch
     widened to satisfy via1's asymmetric enclosure rule (~0.26 µm
     along one axis, ~0.32 µm along the other). `pin_patch` does this.

  2. **Paint a wire.** Manhattan segment on a specified metal
     layer, defaulting to that layer's preferred routing axis (per
     `tech.sky130.ROUTING_DIRECTION`). For an L-shape between two
     points, `place_wire` paints the two segments and a via stack
     at the corner.

  3. **Stitch between layers.** `place_via` paints the contact
     stack between any two adjacent metal layers, with the
     enclosure rectangles above and below.

The helpers are deliberately low-level. They paint geometry but
don't run a router — that's a much bigger lift. For a typical
analog block (≤ 20 routing segments), an agent can call these
explicitly and produce DRC-clean Manhattan routes.
"""

from __future__ import annotations

import warnings
from dataclasses import dataclass

from rekolektion.io import rkt
from rekolektion.layout.placement import inspect_primitive
from rekolektion.tech.sky130 import Axis, ROUTING_DIRECTION, SKY130Rules


# ─── via stack metadata ──────────────────────────────────────────────


# Each entry: (cut_layer, lower_layer, upper_layer,
#              cut_size_um, lower_encl_um, upper_encl_um).
# Enclosures use the *more conservative* of the asymmetric pair so
# the painted rect satisfies both axes by construction.
_VIA_STACK = [
    ("licon1", "diff",  "li1",  0.17, 0.06, 0.08),
    ("mcon",   "li1",   "met1", 0.17, 0.00, 0.06),
    ("via",    "met1",  "met2", 0.15, 0.085, 0.085),
    ("via2",   "met2",  "met3", 0.20, 0.085, 0.095),
]


def _via_info(from_layer: str, to_layer: str) -> tuple[str, float, float, float]:
    """Return (cut_layer_name, cut_size_um, lower_encl_um, upper_encl_um)
    for the via between `from_layer` and `to_layer`, regardless of
    argument order. Raises if the layer pair isn't a known via."""

    for cut, lo, up, size, lo_e, up_e in _VIA_STACK:
        if {from_layer, to_layer} == {lo, up}:
            # Caller's `from_layer` determines which side gets which
            # enclosure; we return them in (lower_encl, upper_encl)
            # order regardless of arg ordering.
            return cut, size, lo_e, up_e
    raise ValueError(
        f"no via defined between '{from_layer}' and '{to_layer}'. "
        f"Known pairs: {[{lo, up} for _, lo, up, *_ in _VIA_STACK]}"
    )


def _um_to_dbu(value: float, dbu_nm: int = 1) -> int:
    return int(round(value * 1000 / dbu_nm))


# ─── PinPatch ────────────────────────────────────────────────────────


@dataclass(frozen=True)
class PinPatch:
    """A placed cell pin, ready to route from.

    `met1_rect` is the parent-painted met1 patch sized for via1
    enclosure — this is the rect routing helpers expect at endpoints.

    `center` is the patch's centroid in **parent** coords; convenient
    for `place_wire(start=patch.center, ...)`.

    `terminal` and `cell` are the source identity (`"D"` on cell
    `"nfet_hv_W1p2_L1p0_core"`), useful for diagnostics and for
    bundling pins into a net.

    `mcon_rects` is the contact array bridging the patch down to the
    cell's existing `li1` strap. Already in `elements`; exposed
    separately for callers that want to inspect.
    """

    cell: str
    terminal: str
    center: tuple[int, int]
    met1_rect: rkt.Rect
    mcon_rects: tuple[rkt.Rect, ...]

    @property
    def elements(self) -> list[rkt.Element]:
        """met1 rect + every mcon. Drop into `cell.elements`."""

        return [self.met1_rect, *self.mcon_rects]


def place_via(
    point: tuple[int, int],
    from_layer: str,
    to_layer: str,
    *,
    cuts: tuple[int, int] = (1, 1),
    up_encl_um: float | tuple[float, float] | None = None,
    dbu_nm: int = 1,
) -> list[rkt.Element]:
    """Paint a single via stack centered on `point` between two
    metal layers. `cuts` is the (columns, rows) of contact cuts —
    default is a single 1×1 contact. Use larger arrays for
    high-current rails.

    `up_encl_um` overrides the upper-layer enclosure from `_VIA_STACK`.

      - `None` (default): use the symmetric value from `_VIA_STACK`.
      - `float`: symmetric (same value all four sides).
      - `(x_encl, y_encl)` tuple: asymmetric. SKY130's via.4a/met2.4
        rule has different x vs y minimums (narrow-axis vs wide-axis)
        — use the asymmetric form for tight stdcell-pitch placements
        where the default symmetric enclosure would collide with
        neighboring routes.

    Returns the cut rectangles plus the upper-layer enclosure rect.
    The caller is responsible for the lower-layer rect (typically
    already painted as part of the wire or pin patch).
    """

    cut_name, cut_size_um, lo_encl_um, default_up_encl_um = _via_info(
        from_layer, to_layer
    )
    rules = SKY130Rules()
    cut_size = _um_to_dbu(cut_size_um, dbu_nm)
    # Spacing between adjacent cuts on a via2+ depends on the rule;
    # for our purposes the per-layer constants in `SKY130Rules`
    # cover it.
    if cut_name == "licon1":
        cut_spacing = _um_to_dbu(rules.LICON_SPACING, dbu_nm)
    elif cut_name == "mcon":
        cut_spacing = _um_to_dbu(rules.MCON_SPACING, dbu_nm)
    elif cut_name == "via":
        cut_spacing = _um_to_dbu(rules.VIA_SPACING, dbu_nm)
    elif cut_name == "via2":
        cut_spacing = _um_to_dbu(rules.VIA2_SPACING, dbu_nm)
    else:
        raise AssertionError(f"unhandled cut layer {cut_name}")

    cx, cy = point
    cols, rows = cuts

    # Total array span (DBU).
    array_w = cols * cut_size + (cols - 1) * cut_spacing
    array_h = rows * cut_size + (rows - 1) * cut_spacing
    array_x0 = cx - array_w // 2
    array_y0 = cy - array_h // 2

    elements: list[rkt.Element] = []
    for i in range(cols):
        for j in range(rows):
            x1 = array_x0 + i * (cut_size + cut_spacing)
            y1 = array_y0 + j * (cut_size + cut_spacing)
            elements.append(
                rkt.Rect(
                    layer=rkt.named("sky130", cut_name),
                    x1=x1, y1=y1,
                    x2=x1 + cut_size, y2=y1 + cut_size,
                )
            )

    # Upper-layer enclosure rect — symmetric by default, asymmetric
    # if the caller passed a (x_encl, y_encl) tuple.
    if up_encl_um is None:
        x_encl = y_encl = _um_to_dbu(default_up_encl_um, dbu_nm)
    elif isinstance(up_encl_um, tuple):
        x_encl = _um_to_dbu(up_encl_um[0], dbu_nm)
        y_encl = _um_to_dbu(up_encl_um[1], dbu_nm)
    else:
        x_encl = y_encl = _um_to_dbu(up_encl_um, dbu_nm)
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", to_layer),
            x1=array_x0 - x_encl,
            y1=array_y0 - y_encl,
            x2=array_x0 + array_w + x_encl,
            y2=array_y0 + array_h + y_encl,
        )
    )
    return elements


def pin_to_rail(
    sref: rkt.SRef,
    terminal: str,
    dest: "rkt.Rect | tuple[int, int, int, int]",
    *,
    primitives_dir=None,
    li1_width_um: float | None = None,
    dbu_nm: int = 1,
) -> list[rkt.Element]:
    """Tie a cell pin to a power destination — either an existing
    li1 tap strap (preferred path, no new mcons) or a met1 rail
    (direct path, mcon stitch added).

    **Use this — NOT `pin_patch` — when a FET S/D drops onto VDD
    or VSS.** The std-cell idiom is li1 vertical, not a met1 +
    via1 stack.

    `dest` is an `rkt.Rect`. Its `layer.name` selects the mode:

      - `"li1"` — typical case. `dest` is the tap band's li1 strap
        (as returned by `TapBandResult.li1_straps_by_side`). We
        paint an li1 extension from the pin merging into the strap.
        **No mcons are added** — the strap is assumed to already
        have its own mcon stitch from `place_rail.stitch_li1_straps`.
        This is the path that avoids the screenshot's met1-overuse
        problem.

      - `"met1"` — direct path. `dest` is the rail's met1 rect.
        We paint li1 from the pin to the rail and add an mcon array
        in the overlap. Use only when there's no intermediate tap
        strap. **Watch for collisions** with the rail's tap stitch
        if one exists — pick the li1 path instead.

    `dest` may also be a `(x1, y1, x2, y2)` tuple; in that case we
    assume li1 mode (the typical "I just have an existing strap"
    case).

    `li1_width_um` defaults to `LI1_MIN_WIDTH` (0.17 µm). Override
    when matching a primitive whose li1 stub is wider, so the strap
    abuts the existing geometry cleanly.

    Returns `[li1_strap, *mcon_array]` (mcon array empty in li1
    mode). Drop straight into your cell's elements list.

    Raises:
        ValueError: pin not labeled in the primitive, or pin's X is
            outside the destination's X extent (the strap would
            dangle off the side with nothing to merge with).
    """

    info = inspect_primitive(sref.cell, primitives_dir=primitives_dir)
    pin = info.pin(terminal)
    if pin is None:
        available = ", ".join(p.terminal for p in info.pins) or "(none)"
        raise ValueError(
            f"primitive '{sref.cell}' has no pin labeled "
            f"'{terminal}'. Available labels: {available}."
        )

    # Pin position in parent coords.
    px = sref.origin[0] + pin.origin[0]
    py = sref.origin[1] + pin.origin[1]

    # Detect destination mode: li1 strap (no mcon) vs met1 rail
    # (add mcon array). Layer name drives the decision; bbox-tuple
    # input defaults to li1 mode.
    direct_to_met1 = False
    if isinstance(dest, rkt.Rect):
        dest_bbox = (dest.x1, dest.y1, dest.x2, dest.y2)
        if dest.layer.kind == "named":
            direct_to_met1 = dest.layer.name == "met1"
    else:
        dest_bbox = dest
    rx1, ry1, rx2, ry2 = dest_bbox

    # Validate: pin must sit within the destination's x-extent or
    # the strap has nowhere to merge with. Caller can extend the
    # destination, or use pin_patch + place_wire for a routed run.
    if not (rx1 <= px <= rx2):
        raise ValueError(
            f"pin at x={px} is outside destination x-extent "
            f"[{rx1}, {rx2}]. Either extend the destination to "
            f"cover the pin, or use pin_patch + place_wire for a "
            f"routed connection."
        )

    rules = SKY130Rules()
    li1_w_um = li1_width_um if li1_width_um is not None else rules.LI1_MIN_WIDTH
    li1_w = _um_to_dbu(li1_w_um, dbu_nm)
    half = li1_w // 2

    # Vertical strap from pin Y to rail Y. Three cases:
    #   - pin below rail (py < ry1): strap from py up to ry2
    #   - pin above rail (py > ry2): strap from ry1 down to py
    #   - pin already inside rail Y (ry1 <= py <= ry2): no real
    #     strap needed; paint a tiny one for the mcon to land on
    if py < ry1:
        strap_y1, strap_y2 = py, ry2
    elif py > ry2:
        strap_y1, strap_y2 = ry1, py
    else:
        strap_y1, strap_y2 = py - half, py + half

    strap = rkt.Rect(
        layer=rkt.named("sky130", "li1"),
        x1=px - half,
        y1=strap_y1,
        x2=px + half,
        y2=strap_y2,
    )

    # li1-mode (destination is a tap strap): no mcons — the strap is
    # assumed to have its own existing mcon stitch to the rail, and
    # adding more would either short or violate spacing. The li1
    # extension above merges with the strap on the same layer.
    if not direct_to_met1:
        return [strap]

    # met1-mode (destination is a rail directly): add an mcon array
    # in the strap/rail overlap. The more-restrictive enclosure rule
    # applies on each edge — li1 enclosure of mcon is 0 (li1 can be
    # exactly mcon-sized), met1 enclosure is 30 nm.
    mcon_size = _um_to_dbu(rules.MCON_SIZE, dbu_nm)
    mcon_pitch = _um_to_dbu(rules.mcon_pitch, dbu_nm)
    met1_encl = _um_to_dbu(rules.MET1_ENCLOSURE_OF_MCON, dbu_nm)
    li1_encl = _um_to_dbu(rules.LI1_ENCLOSURE_OF_MCON, dbu_nm)

    left_lo = max(strap.x1 + li1_encl, rx1 + met1_encl)
    right_hi = min(strap.x2 - li1_encl, rx2 - met1_encl)
    bot_lo = max(strap.y1 + li1_encl, ry1 + met1_encl)
    top_hi = min(strap.y2 - li1_encl, ry2 - met1_encl)

    mcons: list[rkt.Element] = []
    first_x, last_x = left_lo, right_hi - mcon_size
    first_y, last_y = bot_lo, top_hi - mcon_size

    if last_x >= first_x and last_y >= first_y:
        y = first_y
        while y <= last_y:
            x = first_x
            while x <= last_x:
                mcons.append(
                    rkt.Rect(
                        layer=rkt.named("sky130", "mcon"),
                        x1=x, y1=y,
                        x2=x + mcon_size, y2=y + mcon_size,
                    )
                )
                x += mcon_pitch
            y += mcon_pitch

    return [strap, *mcons]


def pin_patch(
    sref: rkt.SRef,
    terminal: str,
    *,
    primitives_dir=None,
    patch_half_um: float = 0.16,
    mcon: bool = True,
    dbu_nm: int = 1,
) -> PinPatch:
    """Paint a met1 contact patch over an SRef'd cell's named pin.

    **Use this for cross-row signal endpoints**, where the pin will
    be touched by a met2 wire requiring a via1 stack. For FET-to-rail
    connections (S/D dropping directly onto VDD or VSS), use
    `pin_to_rail` instead — the std-cell idiom is li1 vertical, not
    a met1 patch with via1 stack.

    Reads the primitive's labels via the memoized `read_primitive`
    cache, finds the pin location in primitive-local coords,
    translates to parent coords using `sref.origin`, and paints:

      1. An met1 square centered on the pin, side =
         `2 × patch_half_um`. Defaults sized for SKY130's worst-axis
         via1 enclosure (0.32 µm = 320 nm wide patch).
      2. A 1×1 mcon at the pin center, with its met1 enclosure
         rect folded into the met1 patch (we paint the patch
         oversize so the via fits comfortably).

    `mcon` controls whether to paint the mcon. Most sky130 FET
    primitives — including the 1.8 V LV and 5 V HV families generated
    by `gen_*_01v8` and `gen_*_hv` — already paint mcon at every S/D
    and gate contact internally; an additional parent-level mcon at
    the same coords stacks two contacts and fails `mcon.spacing`.
    **Pass `mcon=False` whenever the primitive already provides mcon
    coverage at the pin** (which is the common case for FETs);
    `mcon=True` is only correct for primitives that expose a bare
    li1 pin needing a parent-painted contact (rare — most are
    fully-contacted primitives).

    Returns a `PinPatch` with the painted geometry and the pin
    center in parent coords — ready for `place_wire(start=p.center, ...)`.

    Raises:
        ValueError: the SRef'd cell has no label matching `terminal`.
            Inspect `inspect_primitive(sref.cell).pins` to see what's
            actually available.
    """

    info = inspect_primitive(sref.cell, primitives_dir=primitives_dir)
    pin = info.pin(terminal)
    if pin is None:
        available = ", ".join(p.terminal for p in info.pins) or "(none)"
        raise ValueError(
            f"primitive '{sref.cell}' has no pin labeled "
            f"'{terminal}'. Available labels: {available}."
        )

    # Translate pin from primitive-local to parent coords.
    px = sref.origin[0] + pin.origin[0]
    py = sref.origin[1] + pin.origin[1]

    half = _um_to_dbu(patch_half_um, dbu_nm)
    met1_rect = rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=px - half, y1=py - half,
        x2=px + half, y2=py + half,
    )

    # mcon: 1×1 contact at pin center, sized via _VIA_STACK rules.
    # Skipped when `mcon=False` (typical case for FET primitives that
    # already provide mcon coverage at every contact internally).
    mcon_rects: tuple[rkt.Rect, ...] = ()
    if mcon:
        rules = SKY130Rules()
        mcon_size = _um_to_dbu(rules.MCON_SIZE, dbu_nm)
        mcon_rect = rkt.Rect(
            layer=rkt.named("sky130", "mcon"),
            x1=px - mcon_size // 2,
            y1=py - mcon_size // 2,
            x2=px - mcon_size // 2 + mcon_size,
            y2=py - mcon_size // 2 + mcon_size,
        )
        mcon_rects = (mcon_rect,)

    return PinPatch(
        cell=sref.cell,
        terminal=terminal,
        center=(px, py),
        met1_rect=met1_rect,
        mcon_rects=mcon_rects,
    )


# ─── Wires ───────────────────────────────────────────────────────────


def _wire_width_dbu(layer: str, dbu_nm: int = 1) -> int:
    """Default wire width per layer, from SKY130Rules. Conservative
    — uses the layer's `*_MIN_WIDTH` rule."""

    rules = SKY130Rules()
    mapping = {
        "li1": rules.LI1_MIN_WIDTH,
        "met1": rules.MET1_MIN_WIDTH,
        "met2": rules.MET2_MIN_WIDTH,
        "met3": rules.MET3_MIN_WIDTH,
    }
    if layer not in mapping:
        raise ValueError(
            f"no default wire width for layer '{layer}'. "
            f"Supply `width_um` explicitly or extend the table."
        )
    return _um_to_dbu(mapping[layer], dbu_nm)


def _is_point(value: object) -> bool:
    """Heuristic: a 2-tuple/list of ints is a Point; anything else
    that's a sequence is treated as a list of points."""

    return (
        isinstance(value, (tuple, list))
        and len(value) == 2
        and all(isinstance(c, (int, float)) for c in value)
    )


def _simplify_chain(
    points: list[tuple[int, int]],
) -> list[tuple[int, int]]:
    """Drop intermediate points that are collinear with their two
    neighbours on the same axis. Collapses `[A, B, C]` where A, B, C
    share an x or y into `[A, C]` — the segment becomes one rect
    instead of two abutting rects.

    Why this matters: a label flood-fill on `met1_label` may not
    cross the seam between two abutting met1 rects, leaving one
    half on an autogenerated net and failing LVS port-matching.
    Producing one rect per straight run sidesteps the seam.
    """

    if len(points) < 3:
        return list(points)
    out: list[tuple[int, int]] = [points[0]]
    for i in range(1, len(points) - 1):
        prev = out[-1]
        curr = points[i]
        nxt = points[i + 1]
        same_x = prev[0] == curr[0] == nxt[0]
        same_y = prev[1] == curr[1] == nxt[1]
        if same_x or same_y:
            # Curr is collinear with prev/nxt on the same axis — drop it.
            continue
        out.append(curr)
    out.append(points[-1])
    return out


def _segment_rects(
    p1: tuple[int, int],
    p2: tuple[int, int],
    *,
    layer: str,
    half: int,
    preferred: "Axis",
) -> list[rkt.Rect]:
    """Two-point segment → 1 rect (straight) or 2 rects (L-shape)."""

    x1, y1 = p1
    x2, y2 = p2
    if (x1, y1) == (x2, y2):
        return []
    rects: list[rkt.Rect] = []
    if y1 == y2:
        if preferred is Axis.VERTICAL:
            warnings.warn(
                f"horizontal wire on '{layer}' (preferred vertical). "
                f"Legal but costs area.",
                stacklevel=3,
            )
        lo_x, hi_x = sorted((x1, x2))
        rects.append(
            rkt.Rect(
                layer=rkt.named("sky130", layer),
                x1=lo_x, y1=y1 - half,
                x2=hi_x, y2=y1 + half,
            )
        )
    elif x1 == x2:
        if preferred is Axis.HORIZONTAL:
            warnings.warn(
                f"vertical wire on '{layer}' (preferred horizontal). "
                f"Legal but costs area.",
                stacklevel=3,
            )
        lo_y, hi_y = sorted((y1, y2))
        rects.append(
            rkt.Rect(
                layer=rkt.named("sky130", layer),
                x1=x1 - half, y1=lo_y,
                x2=x1 + half, y2=hi_y,
            )
        )
    else:
        # L-shape — first leg along preferred axis.
        if preferred is Axis.VERTICAL:
            corner = (x1, y2)
        else:
            corner = (x2, y1)
        rects.extend(
            _segment_rects(
                p1, corner, layer=layer, half=half, preferred=preferred
            )
        )
        rects.extend(
            _segment_rects(
                corner, p2, layer=layer, half=half, preferred=preferred
            )
        )
    return rects


def place_wire(
    start: "tuple[int, int] | Sequence[tuple[int, int]]",
    end: "tuple[int, int] | None" = None,
    *,
    layer: str = "met1",
    width_um: float | None = None,
    via_to: str | None = None,
    dbu_nm: int = 1,
) -> list[rkt.Element]:
    """Paint a Manhattan wire on `layer`. Two call shapes:

    1. **Two-point form** — `place_wire(start, end, layer=...)`. Same
       behaviour as before: straight segment → 1 rect, diagonal →
       L-shape (corner along the layer's preferred axis).

    2. **Chain form** — `place_wire([p1, p2, p3, ...], layer=...)`.
       Pass a single list of points; the helper walks them in order
       and emits one rect per straight run. **Collinear intermediate
       points are collapsed into a single rect** rather than two
       abutting rects — important because Magic's
       `met1_label`-driven flood-fill can fail to cross the seam
       between two abutting met1 polygons, splitting what should be
       one net into two extracted nets and failing LVS.

    `via_to`, when set, paints a via stack at the chain's END
    (`end` in the 2-point form, `points[-1]` in chain form).

    `width_um` defaults to the layer's minimum width per
    `SKY130Rules`. Override for wider wires.

    Warns when the wire's natural direction conflicts with the
    layer's preferred axis. The wire is painted anyway — non-
    preferred routing is legal, just costs area.
    """

    if end is None:
        # Chain form: `start` is the list of points.
        if _is_point(start):
            raise ValueError(
                "place_wire: pass either (start, end) for a 2-point "
                "wire, or a list of points for a chain. Got a single "
                "point with no `end`."
            )
        points = [tuple(p) for p in start]  # type: ignore[union-attr]
    else:
        if not _is_point(start) or not _is_point(end):
            raise ValueError(
                "place_wire(start, end): start and end must be "
                "(x, y) point tuples."
            )
        points = [tuple(start), tuple(end)]  # type: ignore[arg-type]

    if len(points) < 2:
        return []

    width = (
        _um_to_dbu(width_um, dbu_nm)
        if width_um is not None
        else _wire_width_dbu(layer, dbu_nm)
    )
    half = width // 2
    preferred = ROUTING_DIRECTION.get(layer, Axis.FREE)

    simplified = _simplify_chain(points)
    elements: list[rkt.Element] = []
    for p1, p2 in zip(simplified, simplified[1:]):
        elements.extend(
            _segment_rects(
                p1, p2, layer=layer, half=half, preferred=preferred
            )
        )

    if via_to is not None:
        elements.extend(
            place_via(points[-1], layer, via_to, dbu_nm=dbu_nm)
        )

    return elements


def route_net_on_track(
    pins: list[tuple[int, int]],
    track_pos: int,
    *,
    axis: str = "x",
    track_layer: str = "met1",
    branch_layer: str = "met2",
    track_extend: int = 0,
    dbu_nm: int = 1,
) -> list[rkt.Element]:
    """Route a multi-pin net on a single dedicated track + per-pin
    branches.

    The standard digital std-cell pattern: each net gets one routing
    track (a horizontal `met1` strip in the inter-row channel, or a
    vertical `met2` column at the gate poly). Pins not on the track
    Y/X get a perpendicular branch on the orthogonal preferred-axis
    layer, with via1 stacks at both ends to bridge layers.

    Allocating one track per net by construction prevents the
    track-vs-track collisions that hand-routing with overlapping
    `place_wire` calls produces.

    Args:
        pins: list of `(x, y)` pin coordinates in DBU. Typically the
            `.center` field from a `pin_patch` result, but any DBU
            coord works.
        track_pos: when `axis='x'` the Y of the horizontal track;
            when `axis='y'` the X of the vertical track.
        axis: `'x'` for a horizontal track, `'y'` for a vertical one.
            Pick whichever axis the net's pins span more — for an
            inter-row 2-pin net, vertical (`'y'`) is usually right.
        track_layer: layer the track is painted on. Defaults to
            `met1` (horizontal preferred). Use `met2` for vertical
            tracks (where it's the preferred axis).
        branch_layer: layer the perpendicular branches use.
            Defaults to `met2` for horizontal tracks (vertical
            branches), or `met1` for vertical tracks (horizontal
            branches). Override when track / branch layers are the
            same (e.g. all met1 for short same-row routes).
        track_extend: extra DBU to extend the track past the
            leftmost/rightmost (or topmost/bottommost) pin. Use to
            land the track's edge inside an existing patch or rail.

    Returns:
        list of `rkt.Element` (Rect + via geometry). Append to the
        cell's `elements` directly.

    Notes:
        - `pin_patch` (or equivalent) MUST already exist at each pin
          coord on `branch_layer`. Otherwise the via1 lands on bare
          li1 and fails `via.5a` enclosure.
        - Single-pin "nets" produce no track, just a via if the pin
          is on a different layer than `track_layer`.
        - For 2-pin same-row nets where both pins share the track Y,
          the function returns just the track segment (no branches).
    """

    if not pins:
        return []
    if axis not in ("x", "y"):
        raise ValueError(f"axis must be 'x' or 'y', got {axis!r}")

    elements: list[rkt.Element] = []

    if axis == "x":
        xs = [p[0] for p in pins]
        x_min = min(xs) - track_extend
        x_max = max(xs) + track_extend
        # Horizontal track segment.
        if x_min != x_max:
            elements.extend(
                place_wire(
                    (x_min, track_pos),
                    (x_max, track_pos),
                    layer=track_layer,
                    dbu_nm=dbu_nm,
                )
            )
        # Vertical branches per pin not on the track Y.
        for px, py in pins:
            if py == track_pos:
                continue
            elements.extend(
                place_wire(
                    (px, track_pos),
                    (px, py),
                    layer=branch_layer,
                    dbu_nm=dbu_nm,
                )
            )
            if track_layer != branch_layer:
                elements.extend(
                    place_via(
                        (px, track_pos),
                        track_layer,
                        branch_layer,
                        dbu_nm=dbu_nm,
                    )
                )
                elements.extend(
                    place_via(
                        (px, py),
                        branch_layer,
                        track_layer,
                        dbu_nm=dbu_nm,
                    )
                )
    else:
        ys = [p[1] for p in pins]
        y_min = min(ys) - track_extend
        y_max = max(ys) + track_extend
        if y_min != y_max:
            elements.extend(
                place_wire(
                    (track_pos, y_min),
                    (track_pos, y_max),
                    layer=track_layer,
                    dbu_nm=dbu_nm,
                )
            )
        for px, py in pins:
            if px == track_pos:
                continue
            elements.extend(
                place_wire(
                    (track_pos, py),
                    (px, py),
                    layer=branch_layer,
                    dbu_nm=dbu_nm,
                )
            )
            if track_layer != branch_layer:
                elements.extend(
                    place_via(
                        (track_pos, py),
                        track_layer,
                        branch_layer,
                        dbu_nm=dbu_nm,
                    )
                )
                elements.extend(
                    place_via(
                        (px, py),
                        branch_layer,
                        track_layer,
                        dbu_nm=dbu_nm,
                    )
                )

    return elements
