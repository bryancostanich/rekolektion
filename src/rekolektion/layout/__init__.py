"""DRC-aware layout helpers for composing blocks from primitives.

The goal is to make the legal placement the *easy* placement. Every
helper here encodes one of the structural DRC traps documented in
`docs/workflows/rkt_primitive_workflow.md`:

  - `nwell.2a` (no-man's-land between same-type wells) — fixed by
    `place_row` (Pattern A: abut) and `place_tub` (Pattern B: parent-
    painted shared well).
  - Mixed well-type placement — refused as a hard error.

Helpers return plain `rekolektion.io.rkt` constructs (SRefs, Rects);
the caller assembles them into a `Cell`. No new persistence format,
no parallel data model.
"""

from rekolektion.layout.placement import (
    PrimitiveInfo,
    TubResult,
    inspect_primitive,
    place_row,
    place_tub,
)
from rekolektion.layout.rail import place_rail
from rekolektion.layout.taps import TapBandResult, place_taps_around

__all__ = [
    "PrimitiveInfo",
    "TubResult",
    "TapBandResult",
    "inspect_primitive",
    "place_rail",
    "place_row",
    "place_tub",
    "place_taps_around",
]
