"""Well-tap helpers.

`_core` primitives don't contain substrate taps — they assume the
parent block paints taps somewhere within latch-up distance of every
active area. For pfets that means an n+ contact to the nwell (tied
to VDD); for nfets a p+ contact to psub (tied to VSS). Without taps:

  - `tap.5` (every well needs at least one substrate connection)
    fails on extraction.
  - The block is latch-up vulnerable in silicon — local injection
    has no path to the supply rail to clamp.

`place_taps_around` is the parent-paint companion to `place_row` /
`place_tub`: given the bbox of an array of active primitives and
which well they sit in, it emits a DRC-clean tap-band on the
requested sides.

We deliberately don't shell out to Magic for this — tap strips are
parent-paint geometry, not devices. The 5-layer pattern (tap,
implant, licon1 array, li1 strap, optional mcon+met1) is computed
straight from `SKY130Rules` constants so the magic numbers
stay centralized.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Literal

from rekolektion.io import rkt
from rekolektion.tech.sky130 import SKY130Rules


# In SKY130A/B, every active area must be within ~14.85 µm of a
# substrate tap (the periodic-tap / latch-up rule). For arrays
# bigger than this, interspersed tap rows are required — surround-
# only isn't enough. We warn rather than refuse, since the rule has
# context-dependent exemptions and the caller may know better.
_LATCHUP_DISTANCE_UM = 14.85

# Default tap-strip dimensions. Wider than the strict 0.26 µm
# `TAP_MIN_WIDTH` so a single row of licon contacts lands inside
# the enclosure comfortably.
_DEFAULT_TAP_WIDTH_UM = 0.42


Side = Literal["top", "bottom", "left", "right"]
WellType = Literal["nwell", "pwell"]


@dataclass
class TapBandResult:
    """Geometry emitted by `place_taps_around`.

    `elements` is the flat list to splat into a `rkt.Cell.elements`.
    `bands` keeps the per-side breakdown if you want to inspect /
    label specific sides separately.
    """

    elements: list[rkt.Element] = field(default_factory=list)
    bands: dict[str, list[rkt.Element]] = field(default_factory=dict)

    @property
    def li1_straps(self) -> list[rkt.Rect]:
        """The li1 strap rectangles from every band, in band-iteration
        order. Useful when a single rail spans the entire block (rare).
        For the common case of separate top/bottom rails, use
        `li1_straps_by_side` so each rail receives only the straps it
        actually overlaps."""

        straps: list[rkt.Rect] = []
        for band in self.bands.values():
            for el in band:
                if (
                    isinstance(el, rkt.Rect)
                    and el.layer.kind == "named"
                    and el.layer.name == "li1"
                ):
                    straps.append(el)
        return straps

    @property
    def li1_straps_by_side(self) -> dict[str, list[rkt.Rect]]:
        """Side ('top' / 'bottom' / 'left' / 'right') → li1 strap rects
        on that band. The typical pattern:

            tap = place_taps_around(active, "pwell", sides=("top", "bottom"))
            vss = place_rail(
                vss_rail_bbox, label="VSS",
                stitch_li1_straps=tap.li1_straps_by_side["bottom"],
            )
            # No "doesn't overlap rail" warning, because the VSS rail
            # only receives the bottom strap.
        """

        result: dict[str, list[rkt.Rect]] = {}
        for side, band in self.bands.items():
            result[side] = [
                el for el in band
                if isinstance(el, rkt.Rect)
                and el.layer.kind == "named"
                and el.layer.name == "li1"
            ]
        return result


def _um_to_dbu(value: float, dbu_nm: int = 1) -> int:
    return int(round(value * 1000 / dbu_nm))


def _build_horizontal_band(
    x_min: int,
    x_max: int,
    y_center: int,
    well_type: WellType,
    rules: SKY130Rules,
    dbu_nm: int = 1,
) -> list[rkt.Element]:
    """Build one horizontal tap-band centered on `y_center` from
    `x_min` to `x_max`. The band consists of:

      - tap rectangle (the n+ or p+ contact diffusion)
      - implant (nsdm for nwell taps, psdm for pwell taps),
        enclosing the tap by IMPLANT_ENCLOSURE_OF_DIFF
      - periodic licon1 contacts along the strip
      - li1 strap covering the contacts plus LI1_ENCLOSURE_OF_LICON

    Returns the layer-ordered rect/poly elements ready to drop into
    a Cell.
    """

    elements: list[rkt.Element] = []

    width_dbu = _um_to_dbu(_DEFAULT_TAP_WIDTH_UM, dbu_nm)
    half_w = width_dbu // 2
    tap_x1 = x_min
    tap_x2 = x_max
    tap_y1 = y_center - half_w
    tap_y2 = y_center + half_w

    # Layer 1: tap rectangle.
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "tap"),
            x1=tap_x1, y1=tap_y1, x2=tap_x2, y2=tap_y2,
        )
    )

    # Layer 2: implant. Enclose the tap by IMPLANT_ENCLOSURE_OF_DIFF.
    # nwell taps are n+ (tied to VDD) → nsdm; pwell taps are p+ → psdm.
    implant_name = "nsdm" if well_type == "nwell" else "psdm"
    encl_dbu = _um_to_dbu(rules.NSDM_ENCLOSURE_OF_DIFF, dbu_nm)
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", implant_name),
            x1=tap_x1 - encl_dbu,
            y1=tap_y1 - encl_dbu,
            x2=tap_x2 + encl_dbu,
            y2=tap_y2 + encl_dbu,
        )
    )

    # Layer 3: licon1 contact array. Pitch = size + spacing. Center
    # vertically on the band; distribute along the length.
    licon_size = _um_to_dbu(rules.LICON_SIZE, dbu_nm)
    licon_pitch = _um_to_dbu(rules.licon_pitch, dbu_nm)
    licon_encl = _um_to_dbu(rules.LICON_DIFF_ENCLOSURE_OTHER, dbu_nm)

    # First contact starts LICON_DIFF_ENCLOSURE in from the tap edge,
    # so the contact's enclosure inside the tap is satisfied.
    licon_y1 = y_center - licon_size // 2
    licon_y2 = licon_y1 + licon_size
    first_x = tap_x1 + licon_encl
    last_x = tap_x2 - licon_encl - licon_size
    if last_x >= first_x:
        x = first_x
        while x <= last_x:
            elements.append(
                rkt.Rect(
                    layer=rkt.named("sky130", "licon1"),
                    x1=x, y1=licon_y1,
                    x2=x + licon_size, y2=licon_y2,
                )
            )
            x += licon_pitch

    # Layer 4: li1 strap. Covers every contact plus LI1_ENCLOSURE_OF_LICON
    # margin top/bottom. The strap spans the same x-extent as the band.
    li1_encl = _um_to_dbu(rules.LI1_ENCLOSURE_OF_LICON, dbu_nm)
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "li1"),
            x1=tap_x1,
            y1=licon_y1 - li1_encl,
            x2=tap_x2,
            y2=licon_y2 + li1_encl,
        )
    )

    return elements


def _build_vertical_band(
    y_min: int,
    y_max: int,
    x_center: int,
    well_type: WellType,
    rules: SKY130Rules,
    dbu_nm: int = 1,
) -> list[rkt.Element]:
    """Vertical-band variant of `_build_horizontal_band`. Same five
    layers, rotated 90°. Used for left/right side bands."""

    elements: list[rkt.Element] = []
    width_dbu = _um_to_dbu(_DEFAULT_TAP_WIDTH_UM, dbu_nm)
    half_w = width_dbu // 2

    tap_x1 = x_center - half_w
    tap_x2 = x_center + half_w
    tap_y1 = y_min
    tap_y2 = y_max

    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "tap"),
            x1=tap_x1, y1=tap_y1, x2=tap_x2, y2=tap_y2,
        )
    )

    implant_name = "nsdm" if well_type == "nwell" else "psdm"
    encl_dbu = _um_to_dbu(rules.NSDM_ENCLOSURE_OF_DIFF, dbu_nm)
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", implant_name),
            x1=tap_x1 - encl_dbu,
            y1=tap_y1 - encl_dbu,
            x2=tap_x2 + encl_dbu,
            y2=tap_y2 + encl_dbu,
        )
    )

    licon_size = _um_to_dbu(rules.LICON_SIZE, dbu_nm)
    licon_pitch = _um_to_dbu(rules.licon_pitch, dbu_nm)
    licon_encl = _um_to_dbu(rules.LICON_DIFF_ENCLOSURE_OTHER, dbu_nm)
    licon_x1 = x_center - licon_size // 2
    licon_x2 = licon_x1 + licon_size
    first_y = tap_y1 + licon_encl
    last_y = tap_y2 - licon_encl - licon_size
    if last_y >= first_y:
        y = first_y
        while y <= last_y:
            elements.append(
                rkt.Rect(
                    layer=rkt.named("sky130", "licon1"),
                    x1=licon_x1, y1=y,
                    x2=licon_x2, y2=y + licon_size,
                )
            )
            y += licon_pitch

    li1_encl = _um_to_dbu(rules.LI1_ENCLOSURE_OF_LICON, dbu_nm)
    elements.append(
        rkt.Rect(
            layer=rkt.named("sky130", "li1"),
            x1=licon_x1 - li1_encl,
            y1=tap_y1,
            x2=licon_x2 + li1_encl,
            y2=tap_y2,
        )
    )

    return elements


def place_taps_around(
    inner_bbox: tuple[int, int, int, int],
    well_type: WellType,
    *,
    sides: tuple[Side, ...] = ("top", "bottom"),
    clearance_um: float = 0.3,
    dbu_nm: int = 1,
) -> TapBandResult:
    """Place DRC-clean tap bands around an active region.

    `inner_bbox` is the bbox of the active geometry the taps should
    surround (typically the union bbox of the SRefs in a row or
    tub). Bands are placed `clearance_um` outside that bbox on the
    requested sides.

    `well_type`:
      - `"nwell"` → n+ tap (tied to VDD), implant = nsdm. Use when
        the surrounded primitives are pfets in an nwell tub.
      - `"pwell"` → p+ tap (tied to VSS), implant = psdm. Use under
        nfets (psub is the default substrate).

    `sides`: which sides to band. Default is top + bottom — typical
    for a horizontal row of FETs. Add `'left'` / `'right'` for an
    array that needs corner-to-corner tap coverage.

    `clearance_um`: gap between the inner bbox and the tap strip.
    Default 0.3 µm is conservative enough for the diff-to-tap
    spacing rule (DIFF_MIN_SPACING = 0.27 µm) plus a little slop.

    Returns a `TapBandResult` whose `.elements` is the flat element
    list (rects on tap / implant / licon1 / li1) ready to splat into
    a `rkt.Cell`.

    Caller is responsible for tying the tap's li1 strap to the
    appropriate rail (VSS / VDD) via mcon+met1. The helper stops at
    li1 because the rail layer choice and orientation depend on the
    surrounding block topology.

    Warns when the inner bbox's longest extent exceeds ~14.85 µm —
    above this distance the surround-only approach can miss the
    latch-up / periodic-tap rule and the block needs interspersed
    tap rows. Not refused, because exemptions exist and we'd rather
    err toward "compose anyway, surface the risk."
    """

    import warnings

    if well_type not in ("nwell", "pwell"):
        raise ValueError(
            f"well_type must be 'nwell' or 'pwell', got {well_type!r}"
        )
    for s in sides:
        if s not in ("top", "bottom", "left", "right"):
            raise ValueError(
                f"side must be one of top/bottom/left/right, got {s!r}"
            )

    rules = SKY130Rules()
    x_min, y_min, x_max, y_max = inner_bbox

    # Latch-up sanity check.
    longest_um = max(x_max - x_min, y_max - y_min) / 1000.0
    if longest_um > _LATCHUP_DISTANCE_UM:
        warnings.warn(
            f"active region is {longest_um:.1f} µm at its longest — "
            f"above the ~{_LATCHUP_DISTANCE_UM} µm latch-up distance. "
            f"Surround-only tap bands may miss the periodic-tap rule. "
            f"Consider interspersed tap rows.",
            stacklevel=2,
        )

    clearance = _um_to_dbu(clearance_um, dbu_nm)
    band_half = _um_to_dbu(_DEFAULT_TAP_WIDTH_UM, dbu_nm) // 2

    bands: dict[str, list[rkt.Element]] = {}
    elements: list[rkt.Element] = []

    if "bottom" in sides:
        y_center = y_min - clearance - band_half
        b = _build_horizontal_band(
            x_min, x_max, y_center, well_type, rules, dbu_nm
        )
        bands["bottom"] = b
        elements.extend(b)

    if "top" in sides:
        y_center = y_max + clearance + band_half
        b = _build_horizontal_band(
            x_min, x_max, y_center, well_type, rules, dbu_nm
        )
        bands["top"] = b
        elements.extend(b)

    if "left" in sides:
        x_center = x_min - clearance - band_half
        b = _build_vertical_band(
            y_min, y_max, x_center, well_type, rules, dbu_nm
        )
        bands["left"] = b
        elements.extend(b)

    if "right" in sides:
        x_center = x_max + clearance + band_half
        b = _build_vertical_band(
            y_min, y_max, x_center, well_type, rules, dbu_nm
        )
        bands["right"] = b
        elements.extend(b)

    return TapBandResult(elements=elements, bands=bands)
