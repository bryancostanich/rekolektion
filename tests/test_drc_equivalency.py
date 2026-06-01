"""Tests for `rekolektion.verify.drc_equivalency`.

Pure-Python tests for the matrix-matching and report-rendering
logic.  The full corpus run (which invokes dotnet + klayout + magic
subprocesses) is gated on tool availability and tagged
`@pytest.mark.klayout` + `@pytest.mark.magic` so CI can opt in.
"""
from __future__ import annotations

import shutil
from pathlib import Path

import pytest

from rekolektion.verify.drc_equivalency import (
    CellResult,
    EngineRun,
    _matches,
    _per_rule_from_messages,
    render_report,
    run_corpus,
)


# --- Matrix logic ---------------------------------------------------------

def _engine(label: str, total: int, per_rule: dict[str, int]) -> EngineRun:
    return EngineRun(label=label, total=total, per_rule=per_rule)


def _cell(name: str, fk, fm, ek, em) -> CellResult:
    return CellResult(
        cell_name=name,
        cell_path=Path(name),
        f_klayout=fk,
        f_magic=fm,
        e_klayout=ek,
        e_magic=em,
    )


def test_matches_identical_runs():
    a = _engine("a", 3, {"met1.1": 1, "met1.2": 2})
    b = _engine("b", 3, {"met1.1": 1, "met1.2": 2})
    assert _matches(a, b)


def test_matches_total_mismatch():
    a = _engine("a", 3, {"met1.1": 1, "met1.2": 2})
    b = _engine("b", 4, {"met1.1": 1, "met1.2": 2})
    assert not _matches(a, b)


def test_matches_per_rule_mismatch():
    a = _engine("a", 3, {"met1.1": 1, "met1.2": 2})
    b = _engine("b", 3, {"met1.1": 2, "met1.2": 1})  # same total, diff dist
    assert not _matches(a, b)


def test_klayout_gate_green_only_on_diagonal_match():
    e0 = _engine("e", 0, {})
    e1 = _engine("e", 1, {"met1.1": 1})
    cell = _cell("c", fk=e1, fm=e1, ek=e1, em=e0)
    # F#-Klayout matches ext-KLayout → green; F#-Magic mismatches ext-Magic
    assert cell.klayout_gate is True
    assert cell.magic_gate is False
    assert cell.all_gates_green is False


# --- Per-rule histogram parsing -------------------------------------------

def test_per_rule_from_messages_extracts_rule_id():
    msgs = [
        "Violation (1 tiles): m1.1 : min. m1 width : 0.14um (met1.1)",
        "Violation (2 tiles): m1.6 : min. m1 area : 0.083um² (met1.6)",
    ]
    h = _per_rule_from_messages(msgs)
    assert h == {"met1.1": 1, "met1.6": 2}


def test_per_rule_from_messages_normalizes_klayout_metal_family():
    """Even if a message somehow comes through with the raw KLayout
    name `m1.1`, the histogram bucket is the Magic-style normalized
    name `met1.1` so cross-engine comparison works."""
    msgs = ["Violation (1 tiles): m1.1 : min. m1 width : 0.14um (m1.1)"]
    h = _per_rule_from_messages(msgs)
    assert h == {"met1.1": 1}


def test_per_rule_from_messages_handles_composite_rules():
    # Magic composite: `(via.5a - via.4a)`. The last token in the
    # parens after splitting on " - " is `via.4a`.  Magic also writes
    # `(via.5a)` plain — both should parse.
    msgs = ["Violation (3 tiles): X (via.4a)"]
    h = _per_rule_from_messages(msgs)
    assert h == {"via.4a": 3}


def test_per_rule_from_messages_skips_unparseable():
    h = _per_rule_from_messages(["random line", "(stray parens)"])
    assert h == {}


# --- Report rendering -----------------------------------------------------

def test_render_report_includes_headline_counts():
    cells = [
        _cell("clean",  _engine("f", 0, {}), _engine("f", 0, {}),
                       _engine("e", 0, {}), _engine("e", 0, {})),
        _cell("viol",   _engine("f", 0, {}), _engine("f", 1, {"met1.1": 1}),
                       _engine("e", 1, {"met1.1": 1}), _engine("e", 1, {"met1.1": 1})),
    ]
    report = render_report(cells)
    assert "Corpus: 2 cells" in report
    assert "F#-Klayout ≡ ext-KLayout on 1/2 cells" in report
    assert "F#-Magic ≡ ext-Magic on 2/2 cells" in report
    assert "`met1.1`" in report
    assert "Per-cell matrix" in report
    assert "Per-rule equivalency" in report


def test_render_report_uses_plain_text_status_markers():
    """No emojis per user preference; PASS/FAIL plain text."""
    cells = [
        _cell("c", _engine("f", 0, {}), _engine("f", 0, {}),
                   _engine("e", 0, {}), _engine("e", 0, {})),
    ]
    report = render_report(cells)
    assert "✅" not in report
    assert "🔴" not in report
    assert "OK" in report      # green label
    # No assertion on "FAIL" because the all-clean case won't emit it.


# --- End-to-end corpus run ------------------------------------------------

def _klayout_available() -> bool:
    try:
        from rekolektion.verify.drc_klayout import klayout_binary
        from rekolektion.tech.sky130 import klayout_deck
        klayout_binary()
        return klayout_deck().exists()
    except (FileNotFoundError, RuntimeError):
        return False


def _magic_available() -> bool:
    return shutil.which("magic") is not None


def _dotnet_available() -> bool:
    return shutil.which("dotnet") is not None


tools_required = pytest.mark.skipif(
    not (_klayout_available() and _magic_available() and _dotnet_available()),
    reason="harness needs dotnet + klayout + magic installed",
)


@pytest.mark.klayout
@pytest.mark.magic
@tools_required
def test_corpus_smoke_seeds():
    """End-to-end: harness runs on the seed corpus and produces a
    report with the expected matrix shape.  Specific gate states
    depend on the F# Klayout ruleset's population state (Phase 3 =
    empty), so we assert shape + presence of seed rules but not
    color."""
    results = run_corpus(Path(__file__).resolve().parents[1] / "tests" / "drc_corpus")
    assert len(results) >= 3   # at least the 3 viol cells + legal
    names = {r.cell_name for r in results}
    assert "legal_met1_clean" in names
    assert "viol_met1_1_subwidth" in names
    report = render_report(results)
    assert "met1.1" in report
    # legal cell must pass BOTH gates regardless of phase 3 state.
    legal = next(r for r in results if r.cell_name == "legal_met1_clean")
    assert legal.all_gates_green
