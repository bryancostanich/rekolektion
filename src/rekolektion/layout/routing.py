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


# ─── Gate extension ──────────────────────────────────────────────────


@dataclass(frozen=True)
class GateExtension:
    """Result of `gate_extension`. Drop `elements` into the parent
    cell's elements list, then route to `center` / `met1_rect` like
    any other patched pin.

    `center` is the new gate contact's centroid in **parent** coords
    — `(gate_pin_x_parent, contact_y)`.

    `met1_rect` is the met1 patch at the new contact, sized for a
    via1 landing. Equivalent to what `pin_patch` would produce, just
    relocated outside the FET diff envelope.
    """

    cell: str
    terminal: str
    center: tuple[int, int]
    met1_rect: rkt.Rect
    elements: list[rkt.Element]


def gate_extension(
    sref: rkt.SRef,
    *,
    contact_y: int,
    primitives_dir=None,
    patch_half_um: float = 0.16,
    dbu_nm: int = 1,
) -> GateExtension:
    """Extend a topgate/botgate FET primitive's gate poly out of the
    cell to a parent-chosen Y, with a fresh polycont + li1 + mcon +
    met1 stack at the new location.

    **Why this exists.** When a digital stdcell crams gates and S/D
    contacts into a tight column pitch, the in-cell gate contact
    sits inside the diff envelope (Y ≈ ±620 for default sky130
    FETs) and forces every cross-row met2 trace to thread between
    the S/D contacts' met1 strips. Pulling the gate contact OUT of
    the cell into the inter-row channel decouples the gate via's
    X position from the S/D vias' X positions — no more competing
    for sub-220 nm gaps. The poly carrying the gate net runs from
    its in-cell location up (topgate) or down (botgate) past the
    cell edge to the new contact site.

    **Mechanism.** Paints, at parent level:

      1. Poly extension from the primitive's poly edge to
         `(gate_pin_x ± 165, contact_y ± 165)` — 330 nm wide,
         enough to land a polycont with the required 80 nm
         enclosure on every side.
      2. `licon1` (polycont) 170×170 at `(gate_pin_x, contact_y)`.
      3. `li1` 330 nm wide centered on the new contact (matching
         the primitive's in-cell gate-li1 shape).
      4. `mcon` 170×170 at `(gate_pin_x, contact_y)` so li1 can
         bridge up to met1.
      5. `met1` square 2 × `patch_half_um` on a side — the via1
         landing pad. Equivalent to what `pin_patch` paints.

    The in-cell gate contact is left in place (electrically the
    same net via poly); we just add a second contact at the new
    location for routing.

    **Direction inference.** Reads the `G` pin label. If its Y is
    above the primitive's bbox center it's a topgate (extend up);
    below, a botgate (extend down). Pass `contact_y` accordingly —
    above the cell for topgate, below for botgate.

    Args:
        sref: SRef to a `*_topgate` or `*_botgate` FET primitive
            (mint with `gen_*_01v8(topc=True, botc=False)` or
            `gen_*_hv(topc=False, botc=True)` etc.). Will refuse
            if the primitive has both gate contacts (no
            `_topgate` / `_botgate` suffix) — the cell already
            exposes a gate contact on both sides, so use that
            instead of fabricating a third.
        contact_y: parent-coord Y for the new gate contact. Must
            sit at least 165 nm past the cell's poly edge on the
            extension side, or the polycont won't have its 80 nm
            poly enclosure.
        patch_half_um: half-side of the met1 patch (defaults to
            0.16 µm → 320 nm square, same default as `pin_patch`).

    Returns:
        `GateExtension` with `.center`, `.met1_rect`, and `.elements`
        ready to splice into the cell.

    Raises:
        ValueError: missing `G` pin, ambiguous gate direction
            (both-contact primitive), or `contact_y` too close to
            the cell's poly edge.
    """

    info = inspect_primitive(sref.cell, primitives_dir=primitives_dir)
    g = info.pin("G")
    if g is None:
        available = ", ".join(p.terminal for p in info.pins) or "(none)"
        raise ValueError(
            f"primitive '{sref.cell}' has no 'G' pin label. "
            f"Available: {available}."
        )

    # Direction from gate-pin Y relative to bbox center.
    bbox = info.bbox
    cy_local = (bbox[1] + bbox[3]) // 2
    is_topgate = g.origin[1] > cy_local

    # Refuse on both-contact primitives — they already expose gate
    # contacts on both sides, no need to fabricate a third. Caller
    # should mint a `_topgate` or `_botgate` variant instead.
    name = sref.cell
    if "_topgate" not in name and "_botgate" not in name:
        raise ValueError(
            f"gate_extension expects a '_topgate' or '_botgate' "
            f"primitive (mint with topc=True, botc=False or vice "
            f"versa); got '{name}'. The both-contact variant "
            f"already has gate access on both sides — use the "
            f"existing in-cell contact via pin_patch / pin_to_rail."
        )

    # Gate pin in parent coords. The X is the gate poly column
    # (centered on x=0 in primitive); Y is the in-cell contact.
    gx_parent = sref.origin[0] + g.origin[0]

    # Geometry constants (sky130 sky130B, all in nm):
    LICON_HALF = 85     # 170 nm licon → ±85
    POLY_ENCL_LICON = 80  # poly must extend ≥80 nm past licon
    LI1_HALF = 165      # 330 nm li1 over gate (matches primitive)
    MCON_HALF = 85      # 170 nm mcon → ±85
    POLY_HALF = 165     # 330 nm poly extension (= polycont landing
                        # min width: 170 licon + 2×80 encl)
    # The primitive's in-cell gate met1 strip extends MET1_ENCL_MCON
    # (30 nm) past the gate mcon → its outer edge is G_pin_Y ± 115.
    # Captured here so we can paint a met1 bridge that abuts it.
    IN_CELL_MET1_HALF = MCON_HALF + 30  # = 115
    patch_half = _um_to_dbu(patch_half_um, dbu_nm)

    # The primitive's poly edge on the extension side. The wide
    # polycont-landing section always extends exactly LICON_HALF +
    # POLY_ENCL_LICON (= 165 nm) past the in-cell gate contact —
    # that's the polycont-enclosure rule playing out the same way
    # every FET generator paints it. So poly_edge = G_pin_Y ± 165,
    # which scales correctly with W (taller diff → poly_edge moves
    # with the gate contact, since the gate contact is at a fixed
    # offset from the diff edge).
    edge_offset = LICON_HALF + POLY_ENCL_LICON
    poly_edge_local = g.origin[1] + (edge_offset if is_topgate else -edge_offset)
    poly_edge_parent = sref.origin[1] + poly_edge_local

    # Validate contact_y has room for the polycont + enclosure.
    if is_topgate:
        if contact_y < poly_edge_parent + POLY_ENCL_LICON + LICON_HALF:
            raise ValueError(
                f"contact_y={contact_y} is too close to topgate poly "
                f"edge ({poly_edge_parent}); need ≥ "
                f"{poly_edge_parent + POLY_ENCL_LICON + LICON_HALF} "
                f"for the polycont's 80 nm poly enclosure."
            )
        # Poly extension runs from cell poly top up past the new
        # contact by enough to satisfy poly encl of licon (80 nm).
        poly_y1 = poly_edge_parent
        poly_y2 = contact_y + LICON_HALF + POLY_ENCL_LICON
    else:
        if contact_y > poly_edge_parent - POLY_ENCL_LICON - LICON_HALF:
            raise ValueError(
                f"contact_y={contact_y} is too close to botgate poly "
                f"edge ({poly_edge_parent}); need ≤ "
                f"{poly_edge_parent - POLY_ENCL_LICON - LICON_HALF} "
                f"for the polycont's 80 nm poly enclosure."
            )
        poly_y1 = contact_y - LICON_HALF - POLY_ENCL_LICON
        poly_y2 = poly_edge_parent

    elements: list[rkt.Element] = []

    # Poly extension.
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "poly"),
            x1=gx_parent - POLY_HALF, y1=poly_y1,
            x2=gx_parent + POLY_HALF, y2=poly_y2,
        )
    )

    # Polycont (licon1).
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "licon1"),
            x1=gx_parent - LICON_HALF, y1=contact_y - LICON_HALF,
            x2=gx_parent + LICON_HALF, y2=contact_y + LICON_HALF,
        )
    )

    # li1 over polycont.
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "li1"),
            x1=gx_parent - LI1_HALF, y1=contact_y - LICON_HALF,
            x2=gx_parent + LI1_HALF, y2=contact_y + LICON_HALF,
        )
    )

    # mcon at the same spot to bridge li1 → met1.
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "mcon"),
            x1=gx_parent - MCON_HALF, y1=contact_y - MCON_HALF,
            x2=gx_parent + MCON_HALF, y2=contact_y + MCON_HALF,
        )
    )

    # met1 landing patch.
    met1_rect = rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=gx_parent - patch_half, y1=contact_y - patch_half,
        x2=gx_parent + patch_half, y2=contact_y + patch_half,
    )
    elements.append(met1_rect)

    # Met1 bridge from the primitive's in-cell gate met1 strip to the
    # new patch. Without this, the in-cell strip is left as an
    # isolated 290×230 nm polygon (for LV variants, where the strip
    # width matches the 290 nm gate-li1 minus enclosure) — area
    # 66700 nm², below the 83000 nm² met1.6 minimum. With the
    # bridge they merge into one large polygon that easily clears
    # min area. Harmless for HV (where the in-cell strip is already
    # above min area on its own): the bridge just adds more area
    # to a same-net polygon.
    in_cell_met1_outer_local = g.origin[1] + (
        IN_CELL_MET1_HALF if is_topgate else -IN_CELL_MET1_HALF
    )
    in_cell_met1_outer_parent = sref.origin[1] + in_cell_met1_outer_local
    patch_inner_y = contact_y - patch_half if is_topgate else contact_y + patch_half
    # Overlap the bridge into both the in-cell met1 strip and the
    # ext patch by 10 nm at each end. Pure abutment (shared edge,
    # no shared area) keeps three separate rectangles in the GDS;
    # GDS readers that don't flood-fill across abutments would then
    # see three disconnected polygons. 10 nm of interior overlap
    # forces a single merged polygon, so any reader sees one rect.
    OVERLAP = 10
    if is_topgate:
        bridge_y1 = in_cell_met1_outer_parent - OVERLAP
        bridge_y2 = patch_inner_y + OVERLAP
    else:
        bridge_y1 = patch_inner_y - OVERLAP
        bridge_y2 = in_cell_met1_outer_parent + OVERLAP
    if bridge_y2 > bridge_y1:
        elements.append(
            rkt.Rect(
                layer=rkt.named("sky130", "met1"),
                x1=gx_parent - patch_half, y1=bridge_y1,
                x2=gx_parent + patch_half, y2=bridge_y2,
            )
        )

    return GateExtension(
        cell=sref.cell,
        terminal="G",
        center=(gx_parent, contact_y),
        met1_rect=met1_rect,
        elements=elements,
    )


# ─── Poly bridge ─────────────────────────────────────────────────────


@dataclass(frozen=True)
class PolyBridge:
    """Result of `poly_bridge`. The bridge ties two FETs' gates together
    via a continuous gate-poly strap across the inter-row channel,
    with one pin contact along the run. The cell-level "input pin"
    for the net is at `.center` on met1, sized by `patch_half_um`.

    `top_in_cell_met1` and `bot_in_cell_met1` are parent-painted
    met1 enlargers, each overlapping one FET's primitive gate-met1
    strip. Without them the small primitive strips (290×230 nm)
    violate met1.6 minimum area, because in this topology there's
    no other parent-paint met1 covering them.

    All three met1 rects are on the same electrical net but, by
    design, NOT geometrically connected — the poly strap carries
    the net between them. Each is its own polygon and needs its
    own label for tools that don't propagate names through
    licon/mcon stacks.
    """

    top_cell: str
    bot_cell: str
    center: tuple[int, int] | None
    met1_rect: rkt.Rect | None
    top_in_cell_met1: rkt.Rect
    bot_in_cell_met1: rkt.Rect
    elements: list[rkt.Element]


def poly_bridge(
    top_sref: rkt.SRef,
    bot_sref: rkt.SRef,
    *,
    pin_y: int | None = None,
    primitives_dir=None,
    patch_half_um: float = 0.16,
    dbu_nm: int = 1,
) -> PolyBridge:
    """Bridge two FETs' gate poly across the inter-row channel.

    **Why this exists.** In a digital stdcell, the PMOS and NMOS that
    share a gate net (e.g. PA and NA on net A in a NAND2) connect
    their gates via the gate poly itself, not via metal. The PFET's
    poly extends past its diff downward; the NFET's poly extends
    upward; the parent paints a poly strap that fills the gap, and
    a single pin contact (polycont + li1 + mcon + met1) somewhere
    along the strap is the cell's pin for the net.

    This sidesteps the trunk-via-vs-gate-wire collision that
    `gate_extension` runs into: there is no gate-net met2 wire
    at all — the gate signal is on poly. The pin's met1 patch
    can sit anywhere along the bridge that's clear of other
    geometry.

    Args:
        top_sref: the upper FET. Must be a `*_botgate` variant —
            its gate poly extends downward past its diff.
        bot_sref: the lower FET. Must be a `*_topgate` variant —
            its gate poly extends upward past its diff.
        pin_y: parent-coord Y for an optional in-channel pin contact.
            Choose a Y inside the inter-row channel where you want
            an additional met1 access point (e.g. directly on the
            trunk Y if you want this pin to merge with a horizontal
            met1 net). Pass `None` (default) to skip the in-channel
            contact altogether — for nets whose only metal access
            is the in-cell met1 enlargers, the channel pin is
            redundant load (the gate poly already carries the net
            between the two FETs).

    Returns:
        `PolyBridge` with:
          - `.center` — pin's (gx_parent, pin_y), the cell-level
            input/output pin location
          - `.met1_rect` — pin's met1 patch (320 nm square at default
            patch_half_um), ready for via1 stacking or trunk merge
          - `.top_in_cell_met1` / `.bot_in_cell_met1` — parent-paint
            met1 enlargers that satisfy met1.6 at each FET's
            primitive gate-met1 strip
          - `.elements` — every painted rect, drop into the cell

    Raises:
        ValueError: top_sref isn't `_botgate`, bot_sref isn't
            `_topgate`, gates aren't at the same X column, or
            pin_y is outside the inter-row gap.
    """

    if "_botgate" not in top_sref.cell:
        raise ValueError(
            f"poly_bridge top_sref must be a '_botgate' primitive "
            f"(gate extends DOWN); got '{top_sref.cell}'."
        )
    if "_topgate" not in bot_sref.cell:
        raise ValueError(
            f"poly_bridge bot_sref must be a '_topgate' primitive "
            f"(gate extends UP); got '{bot_sref.cell}'."
        )

    top_info = inspect_primitive(top_sref.cell, primitives_dir=primitives_dir)
    bot_info = inspect_primitive(bot_sref.cell, primitives_dir=primitives_dir)
    top_g = top_info.pin("G")
    bot_g = bot_info.pin("G")
    if top_g is None or bot_g is None:
        raise ValueError("both primitives must expose a 'G' pin label.")

    # Gate X in parent coords, must match (same poly column).
    top_gx_parent = top_sref.origin[0] + top_g.origin[0]
    bot_gx_parent = bot_sref.origin[0] + bot_g.origin[0]
    if top_gx_parent != bot_gx_parent:
        raise ValueError(
            f"poly_bridge requires aligned gate columns; got "
            f"top gate x={top_gx_parent}, bot gate x={bot_gx_parent}."
        )
    gx_parent = top_gx_parent

    # Same geometry constants as gate_extension (sky130 sky130B, nm).
    LICON_HALF = 85
    POLY_ENCL_LICON = 80
    LI1_HALF = 165
    MCON_HALF = 85
    POLY_HALF = 165
    IN_CELL_MET1_HALF = MCON_HALF + 30  # 115 nm: half of the in-cell
                                        # gate-met1 strip's height
    EDGE_OFFSET = LICON_HALF + POLY_ENCL_LICON  # 165 nm

    # Primitive poly edges in parent coords (same derivation as
    # gate_extension): poly edge sits EDGE_OFFSET past the gate pin.
    top_poly_edge_parent = top_sref.origin[1] + top_g.origin[1] - EDGE_OFFSET
    bot_poly_edge_parent = bot_sref.origin[1] + bot_g.origin[1] + EDGE_OFFSET

    # The inter-row gap that the strap must fill.
    if top_poly_edge_parent <= bot_poly_edge_parent:
        raise ValueError(
            f"top FET (sref y={top_sref.origin[1]}) and bot FET "
            f"(sref y={bot_sref.origin[1]}) are not separated; "
            f"top poly bottom edge ({top_poly_edge_parent}) must "
            f"be above bot poly top edge ({bot_poly_edge_parent})."
        )

    # If the caller wants an in-channel pin, validate pin_y has room
    # for the polycontact + its poly enclosure within the strap.
    if pin_y is not None and not (
        bot_poly_edge_parent + EDGE_OFFSET
        <= pin_y
        <= top_poly_edge_parent - EDGE_OFFSET
    ):
        raise ValueError(
            f"pin_y={pin_y} doesn't leave room for the polycont's "
            f"80 nm poly enclosure; valid range is "
            f"[{bot_poly_edge_parent + EDGE_OFFSET}, "
            f"{top_poly_edge_parent - EDGE_OFFSET}]."
        )

    patch_half = _um_to_dbu(patch_half_um, dbu_nm)
    elements: list[rkt.Element] = []

    # 1. Vertical poly strap spanning the gap.
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "poly"),
            x1=gx_parent - POLY_HALF, y1=bot_poly_edge_parent,
            x2=gx_parent + POLY_HALF, y2=top_poly_edge_parent,
        )
    )

    # 2. Optional in-channel pin contact: licon1 + li1 + mcon + met1.
    pin_center: tuple[int, int] | None = None
    met1_rect: rkt.Rect | None = None
    if pin_y is not None:
        elements.append(
            rkt.Rect(
                layer=rkt.named("sky130", "licon1"),
                x1=gx_parent - LICON_HALF, y1=pin_y - LICON_HALF,
                x2=gx_parent + LICON_HALF, y2=pin_y + LICON_HALF,
            )
        )
        elements.append(
            rkt.Rect(
                layer=rkt.named("sky130", "li1"),
                x1=gx_parent - LI1_HALF, y1=pin_y - LICON_HALF,
                x2=gx_parent + LI1_HALF, y2=pin_y + LICON_HALF,
            )
        )
        elements.append(
            rkt.Rect(
                layer=rkt.named("sky130", "mcon"),
                x1=gx_parent - MCON_HALF, y1=pin_y - MCON_HALF,
                x2=gx_parent + MCON_HALF, y2=pin_y + MCON_HALF,
            )
        )
        met1_rect = rkt.Rect(
            layer=rkt.named("sky130", "met1"),
            x1=gx_parent - patch_half, y1=pin_y - patch_half,
            x2=gx_parent + patch_half, y2=pin_y + patch_half,
        )
        elements.append(met1_rect)
        pin_center = (gx_parent, pin_y)

    # 3. Met1 enlargers at each FET's in-cell gate-met1 strip. The
    # primitive paints a 290×230 strip there which is below met1.6
    # min area when isolated. A 320×320 parent-paint patch
    # (overlapping the strip) merges with it and clears the rule.
    #
    # The enlarger is anchored at the cell's outer boundary on the
    # gate side, not centered on the gate pin: centering would push
    # the enlarger's INNER edge too close to the FET's S/D met1
    # strips and violate met1.2 (140 nm). With the outer edge at
    # the bbox boundary, the inner edge sits 320 nm into the cell,
    # which still fully covers the 230 nm in-cell gate strip and
    # leaves room from the S/D met1 below/above.
    top_bbox = top_info.bbox
    bot_bbox = bot_info.bbox
    top_bbox_outer_parent = top_sref.origin[1] + top_bbox[1]  # PFET bot
    bot_bbox_outer_parent = bot_sref.origin[1] + bot_bbox[3]  # NFET top
    top_enlarger = rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=gx_parent - patch_half,
        y1=top_bbox_outer_parent,
        x2=gx_parent + patch_half,
        y2=top_bbox_outer_parent + 2 * patch_half,
    )
    bot_enlarger = rkt.Rect(
        layer=rkt.named("sky130", "met1"),
        x1=gx_parent - patch_half,
        y1=bot_bbox_outer_parent - 2 * patch_half,
        x2=gx_parent + patch_half,
        y2=bot_bbox_outer_parent,
    )
    elements.extend([top_enlarger, bot_enlarger])

    return PolyBridge(
        top_cell=top_sref.cell,
        bot_cell=bot_sref.cell,
        center=pin_center,
        met1_rect=met1_rect,
        top_in_cell_met1=top_enlarger,
        bot_in_cell_met1=bot_enlarger,
        elements=elements,
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
