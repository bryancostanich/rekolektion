"""Automated DRC verification using KLayout for SKY130.

Runs KLayout in batch mode against the SKY130 KLayout DRC deck
(`sky130{A,B}_mr.drc`) and parses the resulting RVE-format `.lyrdb`
report into the same `DRCResult` shape `verify.drc.run_drc` returns,
so call sites can swap engines without restructuring.

This is the **external-tool** path for KLayout — it shells out to
the `klayout` binary. Track 02 Phase 5 will introduce an F# native
path that runs the same rule semantics in-process; `external=True`
on the public `verify_drc` API keeps this path available as a
double-check.

Requires:
- KLayout installed (macOS Homebrew cask: `klayout`, finds the
  bundle at `/Applications/KLayout/klayout.app/Contents/MacOS/klayout`;
  Linux: `klayout` on PATH).
- SKY130 PDK installed (set `PDK_ROOT` or pass `pdk_root`).
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

from rekolektion.verify.drc import (
    DRCResult,
    _KNOWN_WAIVER_RULES,
    _WAIVER_RULE_MARGIN_UM,
)


# ---------------------------------------------------------------------------
# KLayout binary discovery
# ---------------------------------------------------------------------------

_MACOS_BUNDLE_BIN = Path("/Applications/KLayout/klayout.app/Contents/MacOS/klayout")


def klayout_binary() -> Path:
    """Locate the `klayout` executable.

    macOS Homebrew installs the app bundle at `/Applications/KLayout/`
    and does NOT put the binary on `PATH` by default. Linux installs
    typically land `klayout` on PATH.

    Raises FileNotFoundError with an install hint if neither is found.
    """
    on_path = shutil.which("klayout")
    if on_path:
        return Path(on_path)
    if _MACOS_BUNDLE_BIN.exists():
        return _MACOS_BUNDLE_BIN
    raise FileNotFoundError(
        "klayout not found on PATH or at "
        f"{_MACOS_BUNDLE_BIN}. Install: macOS `brew install --cask klayout`; "
        "Linux: https://www.klayout.de/build.html"
    )


# ---------------------------------------------------------------------------
# Rule-ID translation
# ---------------------------------------------------------------------------
# KLayout's SKY130 deck names rules differently from Magic in a handful
# of structurally-related cases. The corpus harness in Phase 4 builds
# the full equivalency table; for Phase 1 we ship the obvious ones so
# the existing waiver list (`_KNOWN_WAIVER_RULES`) covers the common
# foundry-COREID waivers under both engines.

_KLAYOUT_TO_MAGIC_RULE: dict[str, str] = {
    # KLayout collapses "diff" and "tap" into a single `difftap.*` rule
    # family; Magic keeps them under `diff/tap.*`.
    "difftap.1":   "diff/tap.1",
    "difftap.1_c": "diff/tap.1",
    "difftap.2":   "diff/tap.2",
    "difftap.3":   "diff/tap.3",
    "difftap.8":   "diff/tap.8",
    "difftap.9":   "diff/tap.9",
    # Add more as Phase 4 corpus surfaces them.
}

# KLayout names metal-layer rules `m1.*` / `m2.*` / ...; Magic uses
# `met1.*` / `met2.*` / ....  The translation is purely lexical at the
# family prefix, so we apply it via a regex rather than enumerating
# every rule.  Both the waiver-list lookup AND the consumer-facing
# rule ID in error messages run through this normalization, so
# pattern-matching like `"met1.1" in line` works across engines.
_M_PREFIX_RE = re.compile(r"^m([1-9])\.")


def _normalize_rule_id(klayout_rule: str) -> str:
    """Translate a KLayout rule name to its Magic equivalent.

    Order of resolution:
        1. Exact match in `_KLAYOUT_TO_MAGIC_RULE`.
        2. Regex `m{N}.*` → `met{N}.*` family rewrite.
        3. Identity (no translation; KLayout-only rule passes through).
    """
    if klayout_rule in _KLAYOUT_TO_MAGIC_RULE:
        return _KLAYOUT_TO_MAGIC_RULE[klayout_rule]
    m = _M_PREFIX_RE.match(klayout_rule)
    if m:
        return f"met{m.group(1)}." + klayout_rule[m.end():]
    return klayout_rule


def _is_waiver_rule(klayout_rule: str) -> bool:
    """True if the (Magic-normalized) rule ID is in the known-waiver set."""
    return _normalize_rule_id(klayout_rule) in _KNOWN_WAIVER_RULES


def _waiver_margin_um(klayout_rule: str) -> float:
    """Per-rule waiver-footprint expansion margin (µm), Magic-side table."""
    return _WAIVER_RULE_MARGIN_UM.get(_normalize_rule_id(klayout_rule), 0.0)


# ---------------------------------------------------------------------------
# .lyrdb parser
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class _RawViolation:
    """One <item> in an RVE-format report database."""
    rule: str           # category name (rule ID like "poly.2" / "MR_li.WID.4")
    cell: str           # cell where the violation lives
    description: str    # human-readable message from the category
    cx_um: float        # centroid x in microns
    cy_um: float        # centroid y in microns


# KLayout RVE-format value strings:
#   "polygon: (x1,y1;x2,y2;...)"
#   "edge: (x1,y1;x2,y2)"
#   "box: (x1,y1;x2,y2)"
#   "edge-pair: (x1,y1;x2,y2)|(x3,y3;x4,y4)"   ← width/spacing violations
#   "text: '...'"                              ← non-geometric
# All coords are in microns.
#
# edge-pair is the dominant shape for SKY130 — width / spacing / enclosure
# checks all emit an edge-pair pointing to the two parallel edges that
# violate the rule.  We compute the centroid across BOTH edges' endpoints
# so the resulting (cx, cy) sits between the violating edges.
_GEOM_RE = re.compile(
    r"^(?:polygon|edge-pair|edge|box):\s*(.*)$",
    re.DOTALL,
)
_QUOTED_RE = re.compile(r"^'(.*)'$")


def _strip_quotes(s: str | None) -> str:
    if s is None:
        return ""
    s = s.strip()
    m = _QUOTED_RE.match(s)
    return m.group(1) if m else s


def _centroid_of_value(value_text: str) -> tuple[float, float] | None:
    """Parse a single <value> string and return its centroid in microns
    if it's a polygon/edge/edge-pair/box. Returns None for non-geometric
    values.

    Coords appear as `(x1,y1;x2,y2;...)`; multi-shape values (edge-pair)
    separate shapes by `|`, e.g. `edge-pair: (3,0;0,0)|(0,0.1;3,0.1)`.
    The centroid is averaged across ALL points of ALL sub-shapes, which
    places the result between the violating edges (the location the
    caller's spatial waiver-footprint check needs)."""
    m = _GEOM_RE.match(value_text.strip())
    if not m:
        return None
    body = m.group(1).strip()
    # Split into one-or-more shape groups separated by `|`. Each shape is
    # surrounded by `(...)`.  We accept literal `|` between shapes; any
    # other format means the value is malformed and we bail.
    pts: list[tuple[float, float]] = []
    for shape in body.split("|"):
        shape = shape.strip()
        if not shape.startswith("(") or not shape.endswith(")"):
            return None
        inner = shape[1:-1]
        for tok in inner.split(";"):
            tok = tok.strip()
            if not tok:
                continue
            parts = tok.split(",")
            if len(parts) < 2:
                continue
            try:
                x = float(parts[0])
                y = float(parts[1])
            except ValueError:
                return None
            pts.append((x, y))
    if not pts:
        return None
    cx = sum(p[0] for p in pts) / len(pts)
    cy = sum(p[1] for p in pts) / len(pts)
    return (cx, cy)


def parse_lyrdb(report_path: Path) -> tuple[list[_RawViolation], dict[str, str]]:
    """Parse an RVE-format .lyrdb XML report.

    Returns (violations, rule_descriptions). `rule_descriptions` maps
    rule-name → the deck's human-readable description (used to build
    Magic-shaped error messages like "Violation (N tiles): <rule>: <description>").
    """
    tree = ET.parse(report_path)
    root = tree.getroot()

    rule_descriptions: dict[str, str] = {}
    for cat in root.findall("./categories/category"):
        name = _strip_quotes((cat.findtext("name") or "").strip())
        desc = (cat.findtext("description") or "").strip()
        if name:
            rule_descriptions[name] = desc

    violations: list[_RawViolation] = []
    for item in root.findall("./items/item"):
        rule = _strip_quotes((item.findtext("category") or "").strip())
        cell = _strip_quotes((item.findtext("cell") or "").strip())
        # Find the first geometry-bearing value; centroid that.
        centroid: tuple[float, float] | None = None
        text_desc = ""
        for v in item.findall("./values/value"):
            text = (v.text or "").strip()
            if not text:
                continue
            if centroid is None:
                c = _centroid_of_value(text)
                if c is not None:
                    centroid = c
                    continue
            # First non-geometric "text:" value becomes the description.
            if not text_desc and text.startswith("text:"):
                text_desc = _strip_quotes(text[len("text:"):].strip())
        if rule == "" or centroid is None:
            # Skip items without a usable geometry; KLayout occasionally
            # emits informational entries (e.g. "rule X executed in N s")
            # that have no spatial signature.
            continue
        violations.append(
            _RawViolation(
                rule=rule,
                cell=cell,
                description=text_desc or rule_descriptions.get(rule, ""),
                cx_um=centroid[0],
                cy_um=centroid[1],
            )
        )
    return violations, rule_descriptions


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def run_drc_klayout(
    gds_path: str | Path,
    cell_name: str = "",
    pdk_root: str | Path | None = None,
    output_dir: str | Path | None = None,
    waiver_footprints: list[tuple[str, float, float, float, float]] | None = None,
    allow_global_waivers: bool = False,
    feol: bool = True,
    beol: bool = True,
    offgrid: bool = True,
) -> DRCResult:
    """Run KLayout DRC on a GDS file.

    API parity with `rekolektion.verify.drc.run_drc` (the Magic path).
    Returns the same `DRCResult` shape.

    Args:
        gds_path: Path to the GDS file to check.
        cell_name: Top cell name. Required by the SKY130 KLayout deck
            (`$top_cell`); if empty, KLayout uses the GDS's top cell.
        pdk_root: Path to PDK root. Auto-detected if not provided.
        output_dir: Directory for the .lyrdb report and KLayout stdout
            log. Uses a tempdir if not provided.
        waiver_footprints: Optional list of `(name, x0, y0, x1, y1)` µm
            rectangles defining where rule-id-based waivers are allowed.
            Same semantics as `run_drc`: a tile from a known-waiver rule
            counts as a waiver only if its centroid falls inside one of
            these footprints (expanded by the per-rule margin from
            `_WAIVER_RULE_MARGIN_UM`).  Tiles outside escalate to real
            errors.
        allow_global_waivers: legacy mode — when `waiver_footprints`
            is None, classify every known-waiver-rule tile as a waiver
            regardless of position. Default False (strict).
        feol / beol / offgrid: forwarded to the KLayout deck as
            `-rd feol=true|false -rd beol=... -rd offgrid=...`.
            Default to True (full check); set offgrid=False if you
            want to suppress grid-violation noise (Track 01 owns the
            grid check separately).

    Returns:
        DRCResult with `.clean`, `.real_error_count`, `.real_errors`,
        etc. Same surface as `run_drc`.
    """
    gds_path = Path(gds_path).resolve()
    if not gds_path.exists():
        raise FileNotFoundError(f"GDS file not found: {gds_path}")

    from rekolektion.tech.sky130 import klayout_deck, pdk_path
    if pdk_root is None:
        pdk_root_resolved = pdk_path().parent
    else:
        # Accept either the root or the variant dir, mirroring pdk_path's behavior.
        pdk_root_resolved = Path(pdk_root)
    deck = klayout_deck(pdk_root_resolved)
    if not deck.exists():
        raise FileNotFoundError(
            f"KLayout SKY130 deck not found at {deck}. "
            "Check PDK_ROOT and the active sky130 PDK variant."
        )

    if output_dir is None:
        output_dir = Path(tempfile.mkdtemp(prefix="rekolektion_drc_klayout_"))
    output_dir = Path(output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    report_path = output_dir / "drc_klayout.lyrdb"
    log_path = output_dir / "drc_klayout.log"

    klayout = klayout_binary()
    cmd: list[str] = [
        str(klayout),
        "-b",
        "-rd", f"input={gds_path}",
        "-rd", f"report={report_path}",
        "-rd", f"feol={'true' if feol else 'false'}",
        "-rd", f"beol={'true' if beol else 'false'}",
        "-rd", f"offgrid={'true' if offgrid else 'false'}",
        "-r", str(deck),
    ]
    if cell_name:
        cmd.extend(["-rd", f"top_cell={cell_name}"])

    env = os.environ.copy()
    # The deck doesn't read PDK_ROOT, but keep it consistent with the
    # Magic path so callers don't get a different env between engines.
    env["PDK_ROOT"] = str(pdk_root_resolved)

    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=1800,
            cwd=str(output_dir),
            env=env,
        )
    except subprocess.TimeoutExpired:
        raise RuntimeError(f"KLayout DRC timed out after 1800s on {gds_path}")

    # Persist KLayout stdout for the caller's debugging.  Don't gate
    # on a non-zero returncode — the SKY130 deck exits non-zero
    # whenever any rule fires, even though it produced a valid report.
    log_path.write_text(
        f"$ {' '.join(cmd)}\n\nreturncode={result.returncode}\n\n"
        f"--- stdout ---\n{result.stdout}\n--- stderr ---\n{result.stderr}\n"
    )

    if not report_path.exists():
        raise RuntimeError(
            f"KLayout produced no .lyrdb report at {report_path}; "
            f"see {log_path} for KLayout output."
        )

    raw_violations, rule_descriptions = parse_lyrdb(report_path)

    # Aggregate by (rule, description) → list of centroids, matching the
    # Magic path's "Violation (N tiles): <message>" line shape.
    by_rule: dict[tuple[str, str], list[tuple[float, float]]] = {}
    for v in raw_violations:
        key = (v.rule, v.description)
        by_rule.setdefault(key, []).append((v.cx_um, v.cy_um))

    errors: list[str] = []
    real_errors: list[str] = []
    waiver_tiles = 0
    real_tiles = 0
    total = 0

    for (rule, description), tiles in by_rule.items():
        n = len(tiles)
        total += n
        # Magic-shape message so consumers that pattern-match on
        # "Violation (N tiles): ... (rule_id)" continue to work.  The
        # rule ID in parens is NORMALIZED (e.g. KLayout `m1.1` →
        # Magic-equivalent `met1.1`) so existing tests / call sites
        # that grep `"met1.1" in line` keep working under either engine.
        normalized = _normalize_rule_id(rule)
        msg = (
            f"Violation ({n} tiles): "
            f"{description or rule} ({normalized})"
        )
        errors.append(msg)
        rule_is_waiver = _is_waiver_rule(rule)
        if not rule_is_waiver:
            real_tiles += n
            real_errors.append(msg)
            continue
        if not waiver_footprints:
            if allow_global_waivers:
                waiver_tiles += n
                continue
            real_tiles += n
            real_errors.append(
                f"{msg}  -- no spatial footprints provided + "
                f"allow_global_waivers=False (strict default)"
            )
            continue
        margin = _waiver_margin_um(rule)
        inside_n = 0
        for cx, cy in tiles:
            hit = False
            for _name, fx0, fy0, fx1, fy1 in waiver_footprints:
                if (fx0 - margin) <= cx <= (fx1 + margin) and \
                   (fy0 - margin) <= cy <= (fy1 + margin):
                    hit = True
                    break
            if hit:
                inside_n += 1
        outside_n = n - inside_n
        waiver_tiles += inside_n
        real_tiles += outside_n
        if outside_n > 0:
            real_errors.append(
                f"{msg}  -- {outside_n}/{n} tiles outside foundry "
                f"footprints (suspect)"
            )

    return DRCResult(
        clean=(real_tiles == 0),
        error_count=total,
        real_error_count=real_tiles,
        waiver_error_count=waiver_tiles,
        errors=errors,
        real_errors=real_errors,
        log_path=log_path,
        cell_name=cell_name or "(top)",
    )
