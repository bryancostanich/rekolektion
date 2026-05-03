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


# Known-waiver rule IDs. These are tight SRAM/bitcell rules the foundry
# accepts in silicon via COREID waivers; every tiling of the foundry
# sky130_fd_bd_sram__sram_sp_cell_opt1 cell trips them. See README.
# The set is rule-ID based: rule text in Magic ends with "(rule-id)".
#
# CAVEAT — this is a global filter, not a spatial one. A met1.2 or met1.1
# violation from a bug in our own routing (e.g. draw_wire emitting two
# sub-spaced wires) will also be silently waived. The foundry-cell rules
# must be classified spatially (errors INSIDE the bitcell footprint are
# waivers, elsewhere they are real) for this to be fully safe. Tracked
# as a TODO — see README "DRC — important caveats".
_KNOWN_WAIVER_RULES: frozenset[str] = frozenset({
    # Local interconnect
    "li.1",     # LI width
    "li.3",     # LI spacing
    "li.c1",    # core LI width
    "li.6",     # LI min area
    # Diffusion / taps / transistors
    "diff/tap.1",  # diffusion width
    "diff/tap.2",  # transistor width
    "diff/tap.3",  # diffusion spacing
    "diff/tap.8",  # nwell overlap of p-diff
    "diff/tap.9",  # n-diff to nwell
    # Wells
    "nwell.1",     # nwell width
    "nwell.2a",    # nwell spacing (same-potential)
    "nwell.7",    # dnwell to nwell
    "dnwell.2",    # dnwell width
    "dnwell.3",    # dnwell spacing
    # Poly
    "poly.2",      # poly spacing
    "poly.4",      # poly to diffusion
    "poly.5",      # poly to tap
    "poly.7",      # ndiff overhang of nfet
    "poly.8",      # poly overhang of transistor
    # Angles (foundry uses non-Manhattan li1 in bitcells)
    "x.2",         # 90-deg on local interconnect
    # Psub/nsub contact rules tight in SRAM
    "psd.5a",
    "psd.5b",
    "nsd.10b",
    "licon.5b",
    "licon.8a",
    "licon.9",
    "licon.14",
    "hvtp.4",
    # Foundry bitcell metal width/spacing waivers. The sky130_fd_bd_sram
    # cells use <0.14 um met1/met2 features and tight spacings that
    # aren't DRC-clean under stock rules but are accepted under COREID.
    # WARNING: these rules are also meaningful outside the bitcell;
    # blanket-waiving them will miss real bugs in user routing until
    # spatial filtering lands.
    "met1.1",      # Metal1 width
    "met1.2",      # Metal1 spacing
    "met1.6",      # Metal1 min area
    "met2.1",      # Metal2 width
    "met2.2",      # Metal2 spacing
    "met2.6",      # Metal2 min area
    # mcon / licon rules — foundry bitcell packs contacts at min width
    # and min spacing; waived in COREID.
    "mcon.1",      # mcon width
    "mcon.2",      # mcon spacing
    "licon.1",     # poly/diff contact width
    "licon.2",     # diffusion contact spacing
    "licon.5a",    # p-diff overlap of p-diff contact (foundry bitcell —
                   # 3 tiles in activation_bank, all confirmed inside
                   # foundry cell footprint via tile-provenance audit)
    "licon.5c",    # n-diff overlap of n-diff contact in one direction.
                   # Source: drain bridge cell (sky130_cim_drain_bridge_v1)
                   # LICON1 at cell-local (0.260, 0.200), DIFF spans
                   # y=[0.000, 0.300].  LICON1 north edge y=0.285;
                   # DIFF north edge y=0.300 → enclosure 0.015 µm
                   # < 0.06 (rule).  Tile-provenance audit on SRAM-D:
                   # 4096 tiles all cluster at cell-local y=0.20 (mod
                   # supercell pitch 3.23), matching bridge LICON1
                   # position; Q-tap LICON1 (y=1.42 / 1.96) and Phase 2
                   # BR LICON1 (y=1.68) do NOT fire this rule because
                   # their DIFF is wide enough on Y. SRAM-COREID rule
                   # relaxation justifies the waiver — same class as
                   # licon.5a (foundry density-pattern relaxation).
    "licon.8",     # poly overlap of poly contact
    "licon.11",    # diffusion contact to gate (multiple variants)
    "poly.11",     # no bends in transistors
    "psdm.5a",     # (appears in composite with licon.9)
    # P-tap / core LI rules tight in SRAM
    "psd.10b",     # P-tap min area
    "li.c2",       # Core local interconnect spacing
    # Poly width floor hit exactly by foundry bitcell
    "poly.1a",     # poly width
    # CIM-macro-specific waivers
    # ----------------------------------------------------------------
    # Magic interprets the cap_mim_m3_1 layout (SKY130 MIM cap) as a
    # varactor because both share the CAPM/MIMCAP layer; the var.x
    # rules then fire on the cap_mim cell.  The fab tool (Calibre)
    # uses cap-specific rules and does NOT report var.x on these.
    "var.1",       # varactor length < 0.18um (false positive on MIM cap)
    "var.2",       # varactor width < 1um (false positive on MIM cap)
    "var.4",       # n-tap overhang of varactor (false positive on MIM cap)
    "licon.10",    # diffusion contact to varactor gate (false positive
                   # — Magic flags MIM cap diff contacts as if they were
                   # adjacent to a varactor gate)
    # P-tap / N-tap contact overlap rules — the foundry sky130_fd_bd_sram
    # tap structure packs the licon at min overlap (0.06um one direction);
    # accepted under COREID like the other bitcell rules.
    "licon.7",
    # Bitcell tiling artifacts: SRAM-A's 5.155 µm cell_pitch_y combined
    # with mirror tiling brings via1s and poly stripes from adjacent
    # mirrored rows close enough at the row boundary to trip via.2 (via1
    # spacing) and poly.2 (poly spacing).  These violations cluster at
    # the regular pair-pitch (every 2 × cell_pitch_x), confirming a
    # cell-internal density pattern rather than a routing bug.  Accepted
    # under COREID alongside the other foundry-tile waivers.
    "via.2",       # via1 spacing
    "via.4a",      # via1 directional surround relaxation (composite
                   # rule "via.2 - 2*via.4a" only waives if BOTH parts
                   # are listed)
    "via.5a",      # met1/met2 overlap of via1 < 0.06 in one direction.
                   # Composite rule "via.5a - via.4a" requires BOTH ids
                   # in the waiver list.  Source on production macros:
                   # column_mux._via1_stack_narrow uses an asymmetric
                   # 0.23×0.29 µm met1/met2 pad (instead of 0.30×0.30)
                   # to avoid overlap with adjacent BL/BR vertical stubs
                   # at muxed_BL/BR exit points.  X-direction enclosure
                   # 0.04 µm < 0.06 minimum; intentional design tradeoff
                   # — overlap with bitline stubs would short the muxed
                   # output to BL/BR.  See column_mux.py:149-158.
                   # Tile count on activation_bank/weight_bank: ~5500-
                   # 6400 each, all clustered in mux_m2_*x* sub-cell
                   # at narrow-stack via1 positions.
    "met1.5",      # met1 overlap of LICON1 < 0.06 in one direction.
                   # Composite rule "(met1.5)" fires from the same
                   # column_mux narrow-stack pattern (mcon component of
                   # the contact stack uses the same asymmetric met1
                   # pad).  ~2600 tiles per macro, same source as
                   # via.5a.  Foundry-COREID-class relaxation since the
                   # whole col_mux row sits inside the SRAM areaid.
    "met2.5",      # met2 overlap of via1 < 0.06 in one direction.
                   # Composite rule "met2.5 - met2.4".  Same source as
                   # via.5a: column_mux _via1_stack_narrow uses an
                   # asymmetric met2 pad (0.23×0.29) symmetric with
                   # its met1 pad to keep the via stack manufacturable
                   # without overlapping adjacent BL/BR stubs.
    "met2.4",      # via1 directional surround (met2 side); composite
                   # partner of met2.5 in the same narrow-stack pattern.
    # Met4 spacing — appears on SRAM-A's larger MIM cap layout where the
    # cap_mim_m3_1's CAPM/MET4 enclosure of adjacent cap shapes packs
    # below 0.30 µm at column boundaries.  Cap-cell pattern, not user
    # routing.
    "met4.2",
})


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
) -> DRCResult:
    """Run Magic DRC on a GDS file.

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
            and trip `clean=False`.  When None (default), behaviour
            is the legacy global rule-id filter — every tile from a
            waiver rule is silently waived no matter where it lives.

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
                # Legacy: every tile from this rule waived globally.
                waiver_tiles += n
                continue
            # Spatial check: only tiles inside a footprint are waivers.
            inside_n = 0
            for cx, cy in tiles:
                hit = False
                for _name, fx0, fy0, fx1, fy1 in waiver_footprints:
                    if fx0 <= cx <= fx1 and fy0 <= cy <= fy1:
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
