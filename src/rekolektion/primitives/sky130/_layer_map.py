"""GDS (layer, datatype) → PDK name lookup for SKY130.

Inverse of `rekolektion.tech.sky130.SKY130Layers`. Used by the
primitive runner when translating Magic's GDS output back to `.rkt`
with named layer references rather than raw integer pairs.

Pairs we don't recognize fall through as `rkt.unknown(n, d)` —
visible to the editor, round-trippable through tape-out, named
later when we extend the map.
"""

from __future__ import annotations

from rekolektion.io import rkt

# (layer, datatype) → sky130 name. Sourced from SKY130Layers plus the
# additional CIF outputs Magic emits (implants, well taps, contact
# cuts). Keep in sync with `Layout.Layer.bySky130Number` in F#.
_SKY130_PAIR_TO_NAME: dict[tuple[int, int], str] = {
    (64, 5):   "nwell_label",
    (64, 16):  "nwell_pin",
    (64, 18):  "dnwell",
    (64, 20):  "nwell",
    (65, 20):  "diff",
    (65, 44):  "tap",
    (66, 5):   "poly_label",
    (66, 16):  "poly_pin",
    (66, 20):  "poly",
    (66, 44):  "licon1",
    (67, 5):   "li1_label",
    (67, 16):  "li1_pin",
    (67, 20):  "li1",
    (67, 44):  "mcon",
    (68, 5):   "met1_label",
    (68, 16):  "met1_pin",
    (68, 20):  "met1",
    (68, 44):  "via",
    (69, 5):   "met2_label",
    (69, 16):  "met2_pin",
    (69, 20):  "met2",
    (69, 44):  "via2",
    (70, 5):   "met3_label",
    (70, 16):  "met3_pin",
    (70, 20):  "met3",
    (75, 20):  "hvi",
    (78, 44):  "hvntm",
    (81, 2):   "areaid_core",
    (81, 53):  "areaid_lowtapdensity",
    (93, 44):  "nsdm",
    (94, 5):   "psdm_label",
    (94, 20):  "psdm",
    (122, 16): "cfom_drawing",
    (125, 20): "nwell_drawing",
    (235, 4):  "boundary",
}


def layer_for_pair(number: int, datatype: int) -> rkt.Layer:
    """Return a `rkt.Layer` for a GDS `(number, datatype)` pair.

    Hit → `Named("sky130", name)`. Miss → `Unknown(number, datatype)`
    so the geometry stays visible / round-trippable.
    """

    name = _SKY130_PAIR_TO_NAME.get((number, datatype))
    if name is not None:
        return rkt.named("sky130", name)
    return rkt.unknown(number, datatype)
