"""Automated DRC verification using Magic for SKY130.

Runs Magic in batch mode to check GDS files against the SKY130 DRC deck.
Requires:
- Magic installed and on PATH
- SKY130 PDK installed (set PDK_ROOT env var or pass pdk_root)
"""

import os
import re
import subprocess
import tempfile
from dataclasses import dataclass, field
from pathlib import Path


# Known-waiver rule IDs + per-rule spatial margin (µm). These are tight
# SRAM/bitcell rules the foundry accepts in silicon via COREID waivers;
# every tiling of the foundry sky130_fd_bd_sram__sram_sp_cell_opt1 cell
# trips them. The margin is how far past a primitive's bbox a tile
# centre can sit and still count as a foundry waiver:
#
#   * 0.0 — width / area / min-area / contact / overlap rules. The
#     violation is fully contained in a polygon; a tile OUTSIDE the
#     primitive bbox is always a real bug, even if the rule ID matches
#     a foundry waiver. Sub-min met1 / li1 width in the user's parent
#     paint must NOT be silently classified as a foundry tile.
#
#   * >0 — spacing rules. A tile centre can sit slightly past the
#     primitive's bbox because the spacing violation straddles two
#     polygons, one of which lives at the cell edge. The margin is
#     scaled by the rule's minimum spacing: small for met / li / poly
#     spacing rules (≈ rule_min × 2), large for well / dnwell spacing
#     rules (≈ 1.5 µm to cover the nwell.2a 1.27 µm worst case).
#
# This replaces the original flat `_KNOWN_WAIVER_RULES` set + global
# `margin_um` knob on `compute_primitive_footprints`. The previous
# 0.5 µm flat halo around every primitive silently waived sub-min met1
# wires in the routing channel within 500 nm of any primitive edge.
# Confirmed via a compose probe — a 100 nm met1 wire 200 nm above an
# nfet's bbox had its met1.1 tile silently classified as a waiver.
_WAIVER_RULE_MARGIN_UM: dict[str, float] = {
    # --- WIDTH / AREA / OVERLAP / ENCLOSURE rules (margin 0.0) ---
    # The violation is contained in a single polygon or in an
    # intra-cell overlap; if a tile lands outside the primitive bbox
    # it is a real user-routing bug, not a foundry waiver.
    "li.1":     0.0,   # LI width
    "li.c1":    0.0,   # core LI width
    "li.6":     0.0,   # LI min area
    "met1.1":   0.0,   # Metal1 width
    "met1.6":   0.0,   # Metal1 min area
    "met2.1":   0.0,   # Metal2 width
    "met2.6":   0.0,   # Metal2 min area
    "mcon.1":   0.0,   # mcon width
    "licon.1":  0.0,   # poly/diff contact width
    "poly.1a":  0.0,   # poly width
    # Diffusion/transistor widths & intra-cell enclosures
    "diff/tap.1": 0.0, # diffusion width
    "diff/tap.2": 0.0, # transistor width
    "diff/tap.8": 0.0, # nwell overlap of p-diff (intra-cell)
    "diff/tap.9": 0.0, # n-diff to nwell (intra-cell)
    "nwell.1":  0.0,   # nwell width
    "dnwell.2": 0.0,   # dnwell width
    # Poly relations to diff / tap / transistor (all intra-cell)
    "poly.4":   0.0,   # poly to diffusion
    "poly.5":   0.0,   # poly to tap
    "poly.7":   0.0,   # ndiff overhang of nfet
    "poly.8":   0.0,   # poly overhang of transistor
    "poly.11":  0.0,   # no bends in transistors
    # Contacts (overlap / enclosure inside primitive)
    "licon.5a": 0.0,   # p-diff overlap of p-diff contact
    "licon.5b": 0.0,   # similar
    "licon.5c": 0.0,   # n-diff overlap of n-diff contact (one direction)
    "licon.7":  0.0,   # tap contact overlap
    "licon.8":  0.0,   # poly overlap of poly contact
    "licon.8a": 0.0,
    "licon.9":  0.0,
    "licon.10": 0.0,   # diff contact to varactor gate (MIM-cap fp)
    "licon.11": 0.0,   # diff contact to gate
    "licon.14": 0.0,
    "psd.5a":   0.0,
    "psd.5b":   0.0,
    "psd.10b":  0.0,   # P-tap min area
    "nsd.10b":  0.0,
    "psdm.5a":  0.0,
    "hvtp.4":   0.0,
    # MIM cap false positives (Magic sees CAPM as varactor)
    "var.1":    0.0,
    "var.2":    0.0,
    "var.4":    0.0,
    # Non-Manhattan li in foundry bitcells
    "x.2":      0.0,
    # Via overlap / enclosure (intra-cell column_mux narrow-stack pattern)
    "met1.5":   0.0,   # met1 overlap of LICON1 < 0.06 in one direction
    "met2.4":   0.0,   # via1 directional surround (met2 side)
    "met2.5":   0.0,   # met2 overlap of via1
    "via.4a":   0.0,   # via1 directional surround relaxation
    "via.5a":   0.0,   # met1/met2 overlap of via1 < 0.06 one direction
    # MIM cap layer spacing (cap-cell, not user routing)
    "met4.2":   0.0,
    # ReRAM internal-macro (RERAM layer only appears inside foundry IP)
    "rr1.1":    0.0,   # ReRAM width
    "rr1.2":    0.0,   # ReRAM-to-ReRAM spacing
    #
    # --- SPACING rules — small per-rule margin (≈ rule_min × 1.5) ---
    # A spacing violation tile straddles two polygons; the centre can
    # sit just past the cell edge when one participant is at the
    # boundary. The margin allows that overhang to still count as a
    # foundry waiver inside the cell while user-routing violations a
    # few hundred nm farther out still escalate to real errors.
    "li.3":     0.25,  # LI spacing (rule 0.17 µm)
    "li.c2":    0.25,  # Core LI spacing
    "met1.2":   0.25,  # Metal1 spacing (rule 0.14 µm)
    "met2.2":   0.25,  # Metal2 spacing (rule 0.14 µm)
    "mcon.2":   0.25,  # mcon spacing
    "licon.2":  0.25,  # licon spacing
    "poly.2":   0.30,  # poly spacing (rule 0.21 µm)
    "via.2":    0.25,  # via1 spacing
    "diff/tap.3": 0.30, # diffusion spacing (rule 0.27 µm)
    #
    # --- CROSS-CELL WELL / IMPLANT / SPECIAL-DIFF SPACING (large margin) ---
    # These rules can legitimately fire at primitive boundaries inside
    # stdcells (LV-vs-MV, abutted nwell, etc.). The margin must cover
    # the worst-case nwell.2a spacing of 1.27 µm.
    "nwell.2a":  1.50, # nwell spacing (same-potential, 1.27 µm)
    "nwell.7":   1.50, # dnwell to nwell
    "dnwell.3":  1.50, # dnwell spacing
    "diff/tap.15a": 0.50, # MV-vs-MV diffusion spacing at primitive
                          # boundary inside HV stdcells.
    "diff/tap.22": 0.50, # LV-vs-MV diffusion spacing
    "diff/tap.23": 0.50, # — same boundary class
    "diff/tap.24": 0.50, # N-diff to N-well across primitive boundary
}


# Backward-compat: existing callers reference `_KNOWN_WAIVER_RULES`.
_KNOWN_WAIVER_RULES: frozenset[str] = frozenset(_WAIVER_RULE_MARGIN_UM.keys())


# Rule messages that don't carry a "(id)" suffix but are still foundry
# bitcell COREID waivers. Matched by exact message text.
_KNOWN_WAIVER_MESSAGES: frozenset[str] = frozenset({
    "Can't overlap those layers",
    "This layer can't abut or partially overlap between subcells",
})


# Regex to pluck the rule-id out of a Magic rule message.
# Examples:
#   "Local interconnect spacing < 0.17um (li.3)"
#   "Metal1 overlap of Via1 < 0.03um in one direction (via.5a - via.4a)"
#   "Metal3 overlap of via2 < %d (met3.4)"
# We want the LAST "(<id>)" at end-of-string, and split on " - " or "+"
# to handle composite rules (e.g. "via.5a - via.4a" -> ["via.5a","via.4a"]).
_RULE_ID_RE = re.compile(r"\(([^()]+)\)\s*$")


def _extract_rule_ids(message: str) -> list[str]:
    """Pull rule IDs out of a Magic DRC rule message. Returns [] if none."""
    m = _RULE_ID_RE.search(message)
    if not m:
        return []
    inner = m.group(1).strip()
    # Split on separators that Magic uses to link related rules.
    parts = [s.strip() for s in re.split(r"\s*[-+]\s*", inner) if s.strip()]
    # Magic emits composite rules like "via.2 - 2 * via.4a" where the
    # operand carries a numeric scale factor.  Strip leading "N *"
    # prefixes so the bare rule ID can be matched against the waiver
    # set ("2 * via.4a" → "via.4a").
    cleaned: list[str] = []
    _MUL_RE = re.compile(r"^\s*\d+(\.\d+)?\s*\*\s*")
    for part in parts:
        cleaned.append(_MUL_RE.sub("", part).strip())
    return [c for c in cleaned if c]


def _is_waiver(message: str) -> bool:
    """True if every rule ID in the message is in the known-waiver set.

    A composite message like "(via.5a - via.4a)" is only a waiver if
    BOTH component rules are waivers — if any part is a real rule, the
    error is real.
    Rule-less messages (no "(id)" suffix) match against
    _KNOWN_WAIVER_MESSAGES by exact text.
    """
    ids = _extract_rule_ids(message)
    if not ids:
        return message.strip() in _KNOWN_WAIVER_MESSAGES
    return all(rid in _KNOWN_WAIVER_RULES for rid in ids)


@dataclass
class DRCResult:
    """Result of a DRC run.

    `clean` means zero REAL (non-waiver) errors. Foundry SRAM cell
    waivers (COREID) and tilings thereof can still accumulate large
    `waiver_error_count` values while `clean` is True.
    """
    clean: bool
    error_count: int                # total tiles (real + waiver)
    real_error_count: int           # tiles from non-waiver rules
    waiver_error_count: int         # tiles from known-waiver rules
    errors: list[str]               # all rule messages (with tile counts)
    real_errors: list[str]          # only non-waiver rule messages
    log_path: Path
    cell_name: str

    def summary(self) -> str:
        if self.clean:
            w = self.waiver_error_count
            suffix = "" if w == 0 else f" ({w} waiver tiles)"
            return f"DRC CLEAN: {self.cell_name}{suffix}"
        return (
            f"DRC FAILED: {self.cell_name} — {self.real_error_count} real "
            f"errors ({self.waiver_error_count} waivers)"
        )


def find_pdk_root() -> Path:
    """Locate the SKY130 PDK root directory."""
    from rekolektion.tech.sky130 import pdk_path
    # pdk_path() returns the variant dir (e.g. .volare/sky130B).
    # Return its parent as PDK_ROOT for backward compat.
    return pdk_path().parent


def run_drc(
    gds_path: str | Path,
    cell_name: str = "",
    pdk_root: str | Path | None = None,
    output_dir: str | Path | None = None,
    waiver_footprints: list[tuple[str, float, float, float, float]] | None = None,
    allow_global_waivers: bool = False,
) -> DRCResult:
    """Run Magic DRC on a GDS file.

    Audit-2026-05-03 / task #111: spatial waiver filtering is now the
    default.  The legacy "global rule-id filter" (silently waive every
    tile of a known-waiver rule no matter where it sits in the layout)
    is dangerous — a met1.2 tile outside the foundry COREID would be
    silently absorbed.  The default now is **no waivers** unless either
    `waiver_footprints` is provided (spatial check) or
    `allow_global_waivers=True` is explicitly passed (legacy mode,
    preserved for backward compat).

    Args:
        gds_path: Path to the GDS file to check.
        cell_name: Top cell name. If empty, uses the first cell found.
        pdk_root: Path to PDK root. Auto-detected if not provided.
        output_dir: Directory for DRC output files. Uses temp dir if not provided.
        waiver_footprints: Optional list of `(name, x0, y0, x1, y1)` µm
            rectangles defining where rule-id-based waivers are
            allowed.  When supplied, a tile from a known-waiver rule
            is counted as a waiver ONLY if its centre falls inside
            one of these footprints; tiles outside (e.g. user-routing
            channels between foundry cells) escalate to real errors
            and trip `clean=False`.  Strongly recommended for any
            macro that contains foundry SRAM/bitcell IP.
        allow_global_waivers: when True, falls back to the legacy
            global rule-id filter (silently waive every tile of a
            known-waiver rule regardless of position).  Strongly
            discouraged — only use for macros that contain foundry
            cells across their entire footprint, where spatial
            footprints would equal the macro bbox.  When False
            (default), `waiver_footprints=None` means NO waivers
            at all (every known-waiver-rule tile counts as real).

    Returns:
        DRCResult with error count and details.
    """
    gds_path = Path(gds_path)
    if not gds_path.exists():
        raise FileNotFoundError(f"GDS file not found: {gds_path}")

    if pdk_root is None:
        pdk_root = find_pdk_root()
    pdk_root = Path(pdk_root)

    if output_dir is None:
        output_dir = Path(tempfile.mkdtemp(prefix="rekolektion_drc_"))
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    from rekolektion.tech.sky130 import magic_techfile, magic_rcfile
    techfile = magic_techfile(pdk_root)
    magicrc = magic_rcfile(pdk_root)

    # Build the Magic TCL script. Count via `drc listall why` (walks the
    # full cell hierarchy) rather than `drc count`, which only reports
    # tiles in the currently loaded cell's own geometry and misses all
    # errors inside referenced sub-cells.
    # Magic resolves all paths relative to its own CWD, which is the
    # subprocess `cwd` (Python may set it differently from the caller).
    # Resolve to absolute so the script is portable regardless of where
    # Magic is launched from.
    gds_abs = Path(gds_path).resolve()
    log_path = (output_dir / "drc_results.log").resolve()
    tcl_script = f"""\
# DRC script generated by rekolektion
tech load {techfile}
gds read {gds_abs}
{"" if not cell_name else f"load {cell_name}"}
select top cell
# Grow the box past the cell bbox on all four sides. `drc listall why`
# filters results by the current box, and `select top cell` sets the
# box flush with the cell's geometry — edge-effect violations
# (met1.1 min width, nwell.2a spacing across the boundary, etc.) land
# outside that tight box and get silently dropped. Without the grow,
# rekolektion (and CLAUDE.md's interactive recipe) under-report DRC
# tiles vs what Magic actually computes. Tile coordinates are in
# Magic-internal units; 100 internal units == 50 nm at the sky130B
# scale factor of 2, so 2000 ≈ 1 µm of margin in every direction.
box grow n 2000
box grow s 2000
box grow e 2000
box grow w 2000
drc catchup
set why_list [drc listall why]

# Count tiles across all rules, and write detailed log.
set total 0
set f [open {log_path} w]
puts $f "DRC Results for {gds_path.name}"
puts $f "Cell: {cell_name or '(top)'}"
puts $f "==============================="
foreach {{msg box_list}} $why_list {{
    set n [llength $box_list]
    incr total $n
    puts $f "\\nViolation ($n tiles): $msg"
    foreach box $box_list {{
        puts $f "  at: $box"
    }}
}}
puts $f "\\n==============================="
puts $f "Total DRC errors: $total"
close $f

puts "DRC_ERROR_COUNT: $total"
quit -noprompt
"""
    tcl_path = (output_dir / "run_drc.tcl").resolve()
    tcl_path.write_text(tcl_script)

    # Run Magic.  cmd path arguments must be ABSOLUTE because we set
    # subprocess `cwd=output_dir`; a relative tcl_path would otherwise
    # be re-resolved against output_dir/output_dir and Magic would
    # silently fail to load the script (printing nothing to stdout
    # and producing no log).
    cmd = ["magic", "-dnull", "-noconsole"]
    if magicrc.exists():
        cmd.extend(["-rcfile", str(magicrc)])
    cmd.append(str(tcl_path))

    # sky130B.magicrc's fallback PDK_ROOT is a build-machine path that
    # doesn't exist on other systems. Even though we pass `tech load`
    # explicitly in Tcl (which would work), the rcfile also sources a
    # sky130B.tcl that uses $PDK_ROOT. Keep the env var populated so
    # everything resolves consistently.
    env = os.environ.copy()
    env["PDK_ROOT"] = str(pdk_root)
    # Timeout scales with GDS size — production macros (128 rows × 128
    # cols = 16K bitcells) can take minutes on `drc catchup`; tiny test
    # macros return in under a second. Use generous upper bound.
    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=1800,
            cwd=str(output_dir),
            env=env,
        )
    except FileNotFoundError:
        raise RuntimeError(
            "Magic not found on PATH. Install Magic: "
            "http://opencircuitdesign.com/magic/"
        )
    except subprocess.TimeoutExpired:
        raise RuntimeError(f"Magic DRC timed out after 1800s on {gds_path}")

    # Parse results
    error_count = 0
    for line in result.stdout.splitlines():
        if "DRC_ERROR_COUNT:" in line:
            try:
                error_count = int(line.split(":")[-1].strip())
            except ValueError:
                pass

    # Parse detailed errors from log. Headers are "Violation (N tiles):
    # <msg>" followed by N "  at: x0 y0 x1 y1" rows.  Coordinates are
    # Magic DBU = 1/200 µm.
    #
    # Without footprints: classify all N tiles together by rule-id
    # (legacy global filter).
    # With footprints: for waiver rules, classify each tile
    # individually based on whether its centre is inside any
    # footprint.  Tiles outside the footprints escalate to real even
    # if the rule is on the waiver list.
    errors: list[str] = []
    real_errors: list[str] = []
    waiver_tiles = 0
    real_tiles = 0
    suspect_outside = 0    # waiver rule, tile outside any footprint
    line_re = re.compile(r"^Violation \((\d+) tiles\): (.*)$")
    tile_re = re.compile(r"^\s*at:\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s*$")
    _DBU = 200.0
    if log_path.exists():
        log_lines = log_path.read_text().splitlines()
        i = 0
        while i < len(log_lines):
            header = log_lines[i]
            i += 1
            if not header.startswith("Violation "):
                continue
            errors.append(header)
            m = line_re.match(header)
            if not m:
                real_errors.append(header)
                continue
            n = int(m.group(1))
            msg = m.group(2)
            rule_is_waiver = _is_waiver(msg)
            # Pull the next N "at:" lines.
            tiles: list[tuple[float, float]] = []
            while len(tiles) < n and i < len(log_lines):
                tm = tile_re.match(log_lines[i])
                i += 1
                if tm is None:
                    continue
                x0, y0, x1, y1 = (int(c) / _DBU for c in tm.groups())
                tiles.append(((x0 + x1) / 2.0, (y0 + y1) / 2.0))
            if not rule_is_waiver:
                real_tiles += n
                real_errors.append(header)
                continue
            if not waiver_footprints:
                if allow_global_waivers:
                    # Legacy: every tile from this rule waived globally.
                    # Caller explicitly opted in to the audit-flagged
                    # global filter (see task #111).
                    waiver_tiles += n
                    continue
                # Strict default (audit-2026-05-03 / task #111): with
                # neither spatial footprints nor an explicit
                # `allow_global_waivers=True`, treat every known-waiver-
                # rule tile as a REAL error.  Forces callers to make
                # the spatial-vs-global decision explicitly.
                real_tiles += n
                real_errors.append(
                    f"{header}  -- no spatial footprints provided + "
                    f"allow_global_waivers=False (strict default)"
                )
                continue
            # Spatial check: only tiles inside a footprint are waivers.
            # The footprint bbox is expanded by a PER-RULE margin —
            # width / area / overlap rules use 0 (a violation outside
            # the primitive bbox is always a real bug), while spacing
            # rules use a small margin (the violation tile straddles two
            # polygons, one of which can be at the cell edge).
            # Composite messages take the MAX margin across components.
            ids = _extract_rule_ids(msg)
            if ids:
                rule_margin = max(
                    _WAIVER_RULE_MARGIN_UM.get(rid, 0.0) for rid in ids
                )
            else:
                rule_margin = 0.0
            inside_n = 0
            for cx, cy in tiles:
                hit = False
                for _name, fx0, fy0, fx1, fy1 in waiver_footprints:
                    if (fx0 - rule_margin) <= cx <= (fx1 + rule_margin) and \
                       (fy0 - rule_margin) <= cy <= (fy1 + rule_margin):
                        hit = True
                        break
                if hit:
                    inside_n += 1
            outside_n = n - inside_n
            waiver_tiles += inside_n
            real_tiles += outside_n
            suspect_outside += outside_n
            if outside_n > 0:
                real_errors.append(
                    f"{header}  -- {outside_n}/{n} tiles outside foundry "
                    f"footprints (suspect)"
                )

    return DRCResult(
        clean=(real_tiles == 0),
        error_count=error_count,
        real_error_count=real_tiles,
        waiver_error_count=waiver_tiles,
        errors=errors,
        real_errors=real_errors,
        log_path=log_path,
        cell_name=cell_name or "(top)",
    )
