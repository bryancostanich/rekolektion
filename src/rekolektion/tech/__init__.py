"""Technology/PDK abstraction layer.

Single source of truth for PDK-level constants that other rekolektion
modules need to reference without hardcoding. Today this is just the
manufacturing-grid registry; future entries (preferred-routing-direction
maps, default supply voltages, valid W/L bin tables) belong here too.
"""

# Manufacturing-grid pitch in nanometers (= DBU, since rkt Units default
# to dbu_nm=1). Every coord emitted by rekolektion's layout helpers and
# the F# to-gds emitter must land on this grid. Off-grid coords trigger
# foundry sign-off DRC failures, and on SKY130 they also cause Magic to
# emit phantom 14-nm-sliver poly.2 violations under rotated SRefs.
_PDK_GRIDS_NM: dict[str, int] = {
    "sky130": 5,
    # "umc28": 1,    # add when v2_optimization needs it
}


def grid_nm(pdk: str) -> int:
    """Manufacturing grid in nanometers for a PDK.

    Raises ValueError for unknown PDKs — the registry is the authoritative
    list and silent fallback to a "default" grid is exactly how off-grid
    coords slipped in across the bias_gen session.
    """
    try:
        return _PDK_GRIDS_NM[pdk]
    except KeyError as e:
        raise ValueError(
            f"Unknown PDK {pdk!r} — register in "
            "rekolektion.tech._PDK_GRIDS_NM"
        ) from e
