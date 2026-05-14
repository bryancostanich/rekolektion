"""Power-rail helper.

`place_rail` paints a met1 rail rectangle, optionally labels it
(`VDD` / `VSS` / etc.), and — most usefully — auto-stitches any
provided li1 straps up to the rail with mcon contact arrays in
the overlap region. This is the missing piece between
`place_taps_around` (which stops at li1) and a fully-routed block.

Typical use:

    tap = place_taps_around(active_bbox, "pwell")
    rail = place_rail(
        (0, -2200, block_width, -1700),
        label="VSS",
        stitch_li1_straps=tap.li1_straps,
    )
    cell_elements = [..., *tap.elements, *rail]

Without the stitch the tap strap and rail are electrically
disconnected — LVS extracts them as separate nets, and the well
ends up floating from the supply.
"""

from __future__ import annotations

import warnings
from typing import Literal

from rekolektion.io import rkt
from rekolektion.tech.sky130 import SKY130Rules


# Cardinality. The label sits at the center of the rail unless the
# caller overrides — center is always inside the rail (no need to
# worry about label-origin-on-poly etc.).


def _um_to_dbu(value: float, dbu_nm: int = 1) -> int:
    return int(round(value * 1000 / dbu_nm))


def _overlap(
    a: tuple[int, int, int, int],
    b: tuple[int, int, int, int],
) -> tuple[int, int, int, int] | None:
    """Axis-aligned intersection of two bboxes. None if disjoint."""

    x1 = max(a[0], b[0])
    y1 = max(a[1], b[1])
    x2 = min(a[2], b[2])
    y2 = min(a[3], b[3])
    if x2 <= x1 or y2 <= y1:
        return None
    return x1, y1, x2, y2


def _mcon_array_in_overlap(
    overlap: tuple[int, int, int, int],
    rules: SKY130Rules,
    dbu_nm: int,
) -> list[rkt.Rect]:
    """Fill the overlap region with an mcon contact array.

    Each mcon is `MCON_SIZE` square at `mcon_pitch` (size + spacing).
    The array is inset from the overlap edges by `MET1_ENCLOSURE_OF_MCON`
    so the met1 rail has room to enclose each contact.
    """

    mcon_size = _um_to_dbu(rules.MCON_SIZE, dbu_nm)
    mcon_pitch = _um_to_dbu(rules.mcon_pitch, dbu_nm)
    met1_encl = _um_to_dbu(rules.MET1_ENCLOSURE_OF_MCON, dbu_nm)

    ox1, oy1, ox2, oy2 = overlap
    first_x = ox1 + met1_encl
    last_x = ox2 - met1_encl - mcon_size
    first_y = oy1 + met1_encl
    last_y = oy2 - met1_encl - mcon_size

    contacts: list[rkt.Rect] = []
    if last_x < first_x or last_y < first_y:
        return contacts

    y = first_y
    while y <= last_y:
        x = first_x
        while x <= last_x:
            contacts.append(
                rkt.Rect(
                    layer=rkt.named("sky130", "mcon"),
                    x1=x,
                    y1=y,
                    x2=x + mcon_size,
                    y2=y + mcon_size,
                )
            )
            x += mcon_pitch
        y += mcon_pitch
    return contacts


def place_rail_from_strap(
    strap: rkt.Rect,
    *,
    label: str | None = None,
    layer: str = "met1",
    extend_um: float = 0.5,
    side: Literal["away", "covering"] = "covering",
    dbu_nm: int = 1,
) -> list[rkt.Element]:
    """Convenience factory: build a rail bbox from a tap strap's li1
    rect, then call `place_rail` with the strap auto-stitched.

    `side` controls where the rail extends relative to the strap:
      - `"covering"` (default): rail fully covers the strap and
        extends `extend_um` above and below it. The mcon stitch
        fills the full overlap. Use when the rail can occupy the
        same vertical band as the tap.
      - `"away"`: rail sits *next to* the strap (below it for a
        bottom-side strap, above for a top-side strap), with their
        rects sharing exactly one edge plus `extend_um` of overlap
        toward the active region's interior. Use when the rail
        should clear the active area entirely.

    Removes the "what y-extent should the rail have?" guesswork
    that callers hit with raw `place_rail`. For most blocks, the
    `"covering"` default is what you want.
    """

    sx1, sy1, sx2, sy2 = strap.x1, strap.y1, strap.x2, strap.y2
    extend_dbu = int(round(extend_um * 1000 / dbu_nm))

    if side == "covering":
        # Rail bbox grows by extend_um on top and bottom of the strap.
        rail_bbox = (sx1, sy1 - extend_dbu, sx2, sy2 + extend_dbu)
    elif side == "away":
        # Rail sits adjacent: pick the longer of the strap's two
        # halves' distance from y=0 to decide which side is "outer."
        # Simple heuristic: if strap is below y=0 it's a bottom band,
        # rail goes further below (extend down). Otherwise it goes
        # further up.
        if (sy1 + sy2) < 0:
            rail_bbox = (sx1, sy1 - extend_dbu, sx2, sy1 + extend_dbu)
        else:
            rail_bbox = (sx1, sy2 - extend_dbu, sx2, sy2 + extend_dbu)
    else:
        raise ValueError(
            f"side must be 'covering' or 'away', got {side!r}"
        )

    return place_rail(
        rail_bbox,
        layer=layer,
        label=label,
        stitch_li1_straps=[strap],
        dbu_nm=dbu_nm,
    )


def place_rail(
    rail_bbox: tuple[int, int, int, int],
    *,
    layer: str = "met1",
    label: str | None = None,
    label_origin: tuple[int, int] | None = None,
    stitch_li1_straps: list[rkt.Rect] | None = None,
    dbu_nm: int = 1,
) -> list[rkt.Element]:
    """Paint a rail rectangle on `layer`, label it, and stitch any
    li1 straps that overlap it with mcon arrays.

    `rail_bbox` is `(x_min, y_min, x_max, y_max)` in DBU.

    `layer` defaults to `met1`. The label, when provided, lands on
    `<layer>_label` (the SKY130 convention — `met1_label`, `met2_label`,
    …). Override `label_origin` to put the label at a specific
    coordinate; default is the rail's centroid.

    `stitch_li1_straps` is the list of li1 strap rects to bridge up to
    this rail. The helper computes the rect overlap between each
    strap and the rail, then fills it with an mcon contact array.
    Straps that don't overlap the rail get a warning and are skipped.

    Returns the rail rect, the label (if requested), and every mcon
    contact, in a single flat element list.

    Caveats / current limitations:

      - Only met1 is tested as the rail layer. Higher metals work
        geometrically but their via stack (`via`, `via2`, …) isn't
        wired up here — callers needing a met2 rail today should
        paint met1 first, run `place_rail` for it, then paint met2
        and stitch via1 themselves.
      - The mcon array fills the entire overlap region densely. For
        most rails this is what you want (low rail resistance). If
        you want sparser stitching, run `place_rail` without straps
        and add your own mcons.
    """

    if not isinstance(rail_bbox, tuple) or len(rail_bbox) != 4:
        raise ValueError("rail_bbox must be a 4-tuple (x1, y1, x2, y2)")
    if rail_bbox[2] <= rail_bbox[0] or rail_bbox[3] <= rail_bbox[1]:
        raise ValueError(
            f"rail_bbox is empty or inverted: {rail_bbox}"
        )

    rules = SKY130Rules()
    elements: list[rkt.Element] = []

    # The rail rect itself.
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", layer),
            x1=rail_bbox[0],
            y1=rail_bbox[1],
            x2=rail_bbox[2],
            y2=rail_bbox[3],
        )
    )

    # The label (always on <layer>_label per SKY130 convention).
    if label is not None:
        origin = label_origin or (
            (rail_bbox[0] + rail_bbox[2]) // 2,
            (rail_bbox[1] + rail_bbox[3]) // 2,
        )
        elements.append(
            rkt.Label(
                layer=rkt.named("sky130", f"{layer}_label"),
                text=label,
                origin=origin,
            )
        )

    # Stitches: one mcon array per overlapping strap.
    if stitch_li1_straps:
        for strap in stitch_li1_straps:
            strap_bbox = (strap.x1, strap.y1, strap.x2, strap.y2)
            overlap = _overlap(strap_bbox, rail_bbox)
            if overlap is None:
                warnings.warn(
                    f"strap at {strap_bbox} doesn't overlap rail at "
                    f"{rail_bbox} — skipping stitch. Extend the rail "
                    f"or the strap so they share at least an mcon's "
                    f"worth of area.",
                    stacklevel=2,
                )
                continue
            mcons = _mcon_array_in_overlap(overlap, rules, dbu_nm)
            if not mcons:
                warnings.warn(
                    f"strap/rail overlap {overlap} too small for any "
                    f"mcon contacts — strap stays disconnected. "
                    f"Increase the overlap by at least mcon size + "
                    f"2×met1-enclosure-of-mcon.",
                    stacklevel=2,
                )
                continue
            elements.extend(mcons)

    return elements
