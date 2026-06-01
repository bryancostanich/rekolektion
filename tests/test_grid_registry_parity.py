"""Cross-language parity for the PDK grid registry.

The Python source of truth is `rekolektion.tech._PDK_GRIDS_NM`; the
F# mirror lives at `tools/viz/src/Rekolektion.Viz.Core/Tech.fs`.  The
two MUST agree — a coord that Python's `place_*` helpers snap but
F#'s `to-gds` emitter doesn't (or vice-versa) lands off-grid in the
exported GDS and ships to the foundry.

We compare by parsing the F# `gridDbu` map literal out of the source
file with a regex.  This catches:
- Missing keys on either side.
- Differing values for the same key (5 vs 1 — totally wrong grid).
- Format changes that would break automated parsing (renames, layout
  drift), so future refactors deliberately update the parity test
  rather than silently breaking the contract.
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

from rekolektion.tech import _PDK_GRIDS_NM


_TECH_FS = (
    Path(__file__).resolve().parents[1]
    / "tools" / "viz" / "src" / "Rekolektion.Viz.Core" / "Tech.fs"
)

# Map entries look like:
#     "sky130", 5L
# possibly with leading whitespace, trailing comma, or trailing
# `//`-style comment.  Match the quoted key and the `<digits>L` value.
_ENTRY_RE = re.compile(r'"([A-Za-z0-9_]+)"\s*,\s*(\d+)L')


def _parse_fsharp_registry() -> dict[str, int]:
    text = _TECH_FS.read_text()
    # Narrow to the gridDbu block to avoid catching unrelated tuples
    # elsewhere in the file.
    block_re = re.compile(
        r"gridDbu\s*:\s*Map<string,\s*int64>\s*="
        r"\s*Map\.ofList\s*\[\s*(.+?)\s*\]",
        re.DOTALL,
    )
    m = block_re.search(text)
    assert m is not None, (
        f"could not find gridDbu Map.ofList block in {_TECH_FS}. "
        "The parity test parses this verbatim — if the F# layout "
        "changed deliberately, update this regex."
    )
    # Strip F# `//`-style line comments before parsing entries; a
    # commented-out future PDK (e.g. `// "umc28", 1L`) must NOT count
    # as an active registry entry.
    body = m.group(1)
    lines = []
    for line in body.splitlines():
        idx = line.find("//")
        lines.append(line if idx < 0 else line[:idx])
    stripped = "\n".join(lines)
    return {k: int(v) for k, v in _ENTRY_RE.findall(stripped)}


def test_fsharp_tech_file_exists() -> None:
    assert _TECH_FS.is_file(), (
        f"expected F# Tech module at {_TECH_FS}. Either the path moved "
        "or the F# side wasn't checked in."
    )


def test_registries_have_same_keys() -> None:
    fs = _parse_fsharp_registry()
    py = _PDK_GRIDS_NM
    assert set(fs) == set(py), (
        f"PDK registry key drift!  python={set(py)} f#={set(fs)}. "
        "Both registries must list the same PDKs, or coords from "
        "one PDK will be snapped on one side and not the other."
    )


def test_registries_have_same_values() -> None:
    fs = _parse_fsharp_registry()
    py = _PDK_GRIDS_NM
    mismatches = {k: (py[k], fs[k]) for k in py if py[k] != fs[k]}
    assert not mismatches, (
        f"PDK grid VALUE drift!  python vs f# mismatches: {mismatches}. "
        "Same PDK must resolve to identical grid pitch on both sides."
    )


def test_sky130_is_5nm() -> None:
    """Smoke check — independent of either registry, the sky130 grid
    is 5 nm by foundry spec.  If this ever changes, Track 01 needs to
    be re-evaluated end-to-end."""
    assert _PDK_GRIDS_NM["sky130"] == 5
    assert _parse_fsharp_registry()["sky130"] == 5
