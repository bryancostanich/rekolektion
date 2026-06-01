"""Phase 4 equivalency harness — F# vs external, per compat target.

For each corpus cell, runs FOUR DRC checks and assembles a 2×2 matrix:

                | external KLayout | external Magic |
    F# Klayout  |   GATE           |   informational |
    F# Magic    |   informational  |   GATE          |

Diagonal cells are the gates that promote a rule to F#-primary in
Phase 5. Off-diagonal cells are the engine deltas — the differences
between Magic and KLayout interpretations that motivate keeping both
compat targets.

Per-rule status (which rules are F#-Klayout / F#-Magic equivalent
to their external counterparts on the diagonal) feeds the table at
`docs/internals/drc_rule_equivalency.md`.

Usage:

    from rekolektion.verify.drc_equivalency import run_corpus, render_report
    results = run_corpus('tests/drc_corpus')
    print(render_report(results))
"""
from __future__ import annotations

import re
import shutil
import subprocess
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

from rekolektion.verify.drc import run_drc
from rekolektion.verify.drc_klayout import (
    klayout_binary,
    run_drc_klayout,
    _normalize_rule_id,
)


# ---------------------------------------------------------------------------
# Per-cell run + matrix
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class EngineRun:
    """One engine's verdict on one cell. Counts are total tiles
    (matching the existing DRCResult.error_count convention) and a
    per-rule histogram so we can compare engines rule-by-rule."""
    label: str                          # "F#-Klayout", "ext-Magic", ...
    total: int
    per_rule: dict[str, int]


@dataclass(frozen=True)
class CellResult:
    """Four-way result for one corpus cell."""
    cell_name: str
    cell_path: Path
    f_klayout: EngineRun
    f_magic:   EngineRun
    e_klayout: EngineRun
    e_magic:   EngineRun

    # Diagonal gates.  True iff F# total == external total AND per-rule
    # counts match (after normalizing KLayout rule IDs to Magic-style).
    @property
    def klayout_gate(self) -> bool:
        return _matches(self.f_klayout, self.e_klayout)

    @property
    def magic_gate(self) -> bool:
        return _matches(self.f_magic, self.e_magic)

    @property
    def all_gates_green(self) -> bool:
        return self.klayout_gate and self.magic_gate


# ---------------------------------------------------------------------------
# Engine runners
# ---------------------------------------------------------------------------

# Matches the existing run_drc / run_drc_klayout message form:
#     Violation (N tiles): ... (rule_id)
_VIOL_RE = re.compile(r"Violation \((\d+) tiles\): .*\(([^()]+)\)\s*$")
# Extract the rule ID from a composite message — the LAST (id) wins,
# matching `_extract_rule_ids` in `verify/drc.py`.
_LAST_PAREN_RE = re.compile(r"\(([^()]+)\)\s*$")


def _per_rule_from_messages(messages: list[str]) -> dict[str, int]:
    """Parse a list of `Violation (N tiles): ... (rule)` strings into
    a histogram keyed by NORMALIZED rule ID (so KLayout `m1.2` and
    Magic `met1.2` collide into one bucket the diagonal can compare)."""
    out: dict[str, int] = {}
    for m in messages:
        match = _VIOL_RE.search(m)
        if not match:
            continue
        n = int(match.group(1))
        rule_id = match.group(2).strip().split()[-1]  # last token after spaces
        normalized = _normalize_rule_id(rule_id)
        out[normalized] = out.get(normalized, 0) + n
    return out


def _run_external_klayout(gds: Path, cell_name: str, output_dir: Path) -> EngineRun:
    r = run_drc_klayout(
        gds, cell_name=cell_name, output_dir=output_dir, offgrid=False,
    )
    return EngineRun(
        label="ext-KLayout",
        total=r.error_count,
        per_rule=_per_rule_from_messages(r.errors),
    )


def _run_external_magic(gds: Path, cell_name: str, output_dir: Path) -> EngineRun:
    r = run_drc(
        gds, cell_name=cell_name, output_dir=output_dir,
        # Don't auto-classify foundry waivers — corpus cells have no
        # foundry primitives. Strict default counts every tile.
        waiver_footprints=[],
        allow_global_waivers=False,
    )
    return EngineRun(
        label="ext-Magic",
        total=r.error_count,
        per_rule=_per_rule_from_messages(r.errors),
    )


# F# CLI is invoked via `dotnet run -- drc --compat <c> <rkt>`. Output
# format (TSV): a header line, then one row per violation:
#     rule\tlayer\tlimit_dbu\tmeasured_dbu\tbbox_a\tbbox_b
# Total appears on stderr: "=== N violations (compat=...) ===".
_FSHARP_TOTAL_RE = re.compile(r"===\s+(\d+)\s+violations")


def _viz_cli_path() -> Path:
    """Locate Rekolektion.Viz.Cli project; same walk as rkt_drc."""
    here = Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        cli = ancestor / "tools" / "viz" / "src" / "Rekolektion.Viz.Cli"
        if cli.is_dir():
            return cli
    raise RuntimeError("couldn't locate Rekolektion.Viz.Cli from " + str(here))


def _run_fsharp(rkt: Path, compat: str) -> EngineRun:
    """Run F# DRC under the given compat target. Returns EngineRun
    with total + per-rule histogram parsed from the TSV output."""
    cli = _viz_cli_path()
    proc = subprocess.run(
        ["dotnet", "run", "--project", str(cli), "--",
         "drc", "--compat", compat, str(rkt)],
        capture_output=True, text=True, cwd=cli.parents[3],
    )
    if proc.returncode != 0:
        raise RuntimeError(
            f"F# drc --compat {compat} failed on {rkt}:\n"
            f"stdout={proc.stdout}\nstderr={proc.stderr}"
        )
    # Parse total from stderr.
    total = 0
    for line in proc.stderr.splitlines():
        m = _FSHARP_TOTAL_RE.search(line)
        if m:
            total = int(m.group(1))
            break
    # Parse per-rule from stdout. Each TSV row: rule \t layer \t ...
    per_rule: dict[str, int] = {}
    for line in proc.stdout.splitlines()[1:]:  # skip header
        cols = line.split("\t")
        if not cols or not cols[0]:
            continue
        rule = _normalize_rule_id(cols[0])
        per_rule[rule] = per_rule.get(rule, 0) + 1
    return EngineRun(
        label=f"F#-{compat.capitalize()}",
        total=total,
        per_rule=per_rule,
    )


# ---------------------------------------------------------------------------
# Matrix matching
# ---------------------------------------------------------------------------

def _matches(a: EngineRun, b: EngineRun) -> bool:
    """True iff totals AND per-rule histograms are identical after
    normalization. The diagonal F#-Klayout ≡ ext-KLayout must hold
    exactly; off-diagonal comparisons use this same predicate to
    surface informational deltas."""
    return a.total == b.total and a.per_rule == b.per_rule


# ---------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------

def run_corpus(corpus_dir: Path | str) -> list[CellResult]:
    """For each .rkt in `corpus_dir`, run all four engines and assemble
    a CellResult. Skips F# runs if `dotnet` is missing; skips external-
    KLayout if the binary is missing. Raises on first hard failure (a
    runnable engine reports an error)."""
    corpus = Path(corpus_dir)
    if not corpus.is_dir():
        raise FileNotFoundError(f"corpus dir not found: {corpus}")

    dotnet_ok = shutil.which("dotnet") is not None
    try:
        klayout_binary()
        klayout_ok = True
    except FileNotFoundError:
        klayout_ok = False

    if not (dotnet_ok and klayout_ok):
        raise RuntimeError(
            "equivalency harness needs dotnet + klayout installed; "
            f"dotnet={dotnet_ok} klayout={klayout_ok}"
        )

    results: list[CellResult] = []
    cells = sorted(corpus.glob("*.rkt"))
    for rkt in cells:
        with tempfile.TemporaryDirectory(prefix="drc_eq_") as d:
            workdir = Path(d)
            # Materialize GDS once for both external engines via the
            # existing rkt_drc helper.  Use the .rkt's top cell as the
            # cell name for both external engines.
            from rekolektion.io import rkt as rkt_io
            from rekolektion.verify.rkt_drc import _convert_rkt_to_gds
            doc = rkt_io.read_file(rkt)
            cell_name = doc.top_cell or rkt.stem
            gds = workdir / f"{rkt.stem}.gds"
            _convert_rkt_to_gds(rkt, gds)

            fk = _run_fsharp(rkt, "klayout")
            fm = _run_fsharp(rkt, "magic")
            ek = _run_external_klayout(gds, cell_name, workdir / "klayout")
            em = _run_external_magic(gds, cell_name, workdir / "magic")
            results.append(CellResult(
                cell_name=cell_name,
                cell_path=rkt,
                f_klayout=fk,
                f_magic=fm,
                e_klayout=ek,
                e_magic=em,
            ))
    return results


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

def render_report(results: list[CellResult]) -> str:
    """Markdown 2×2 per cell + per-rule equivalency summary.  Used by
    the CLI subcommand and the status-doc generator."""
    lines: list[str] = []
    lines.append("# DRC equivalency report")
    lines.append("")
    lines.append(f"Corpus: {len(results)} cells.")
    klayout_green = sum(1 for r in results if r.klayout_gate)
    magic_green = sum(1 for r in results if r.magic_gate)
    lines.append(
        f"Gates: F#-Klayout ≡ ext-KLayout on {klayout_green}/{len(results)} cells; "
        f"F#-Magic ≡ ext-Magic on {magic_green}/{len(results)} cells."
    )
    lines.append("")
    lines.append("## Per-cell matrix")
    lines.append("")
    lines.append("| Cell | F#-Klayout | F#-Magic | ext-KLayout | ext-Magic | Klayout gate | Magic gate |")
    lines.append("|---|---:|---:|---:|---:|:---:|:---:|")
    for r in results:
        kgate = "OK" if r.klayout_gate else "FAIL"
        mgate = "OK" if r.magic_gate else "FAIL"
        lines.append(
            f"| `{r.cell_name}` | {r.f_klayout.total} | {r.f_magic.total} | "
            f"{r.e_klayout.total} | {r.e_magic.total} | {kgate} | {mgate} |"
        )
    lines.append("")

    # Per-rule equivalency aggregated across the corpus.  A rule is
    # green on the Klayout side iff every cell whose ext-KLayout
    # reported it has matching F#-Klayout counts; same for Magic.
    rules: set[str] = set()
    for r in results:
        rules.update(r.e_klayout.per_rule)
        rules.update(r.e_magic.per_rule)
        rules.update(r.f_klayout.per_rule)
        rules.update(r.f_magic.per_rule)

    if rules:
        lines.append("## Per-rule equivalency (normalized rule IDs)")
        lines.append("")
        lines.append("| Rule | F#-Klayout ≡ ext-KLayout | F#-Magic ≡ ext-Magic |")
        lines.append("|---|:---:|:---:|")
        for rule in sorted(rules):
            k_ok = all(
                r.f_klayout.per_rule.get(rule, 0) == r.e_klayout.per_rule.get(rule, 0)
                for r in results
            )
            m_ok = all(
                r.f_magic.per_rule.get(rule, 0) == r.e_magic.per_rule.get(rule, 0)
                for r in results
            )
            lines.append(
                f"| `{rule}` | {'OK' if k_ok else 'FAIL'} | {'OK' if m_ok else 'FAIL'} |"
            )
        lines.append("")

    return "\n".join(lines)
