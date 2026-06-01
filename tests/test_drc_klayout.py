"""Tests for `rekolektion.verify.drc_klayout`.

Pure-Python tests (lyrdb parser, rule-id translation, waiver
classification) run without KLayout installed.  End-to-end tests
that actually invoke KLayout are marked `@pytest.mark.klayout` and
skip gracefully when the binary is missing.
"""
from __future__ import annotations

import textwrap
from pathlib import Path

import pytest


def _write_lyrdb(path: Path, body: str) -> Path:
    """Write a minimal RVE-format report. `body` is the inner content
    of `<report-database>…</report-database>`."""
    xml = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<report-database>\n"
        + textwrap.dedent(body).strip("\n")
        + "\n</report-database>\n"
    )
    path.write_text(xml)
    return path


# ---------------------------------------------------------------------------
# Pure-Python tests — no KLayout binary required.
# ---------------------------------------------------------------------------

def test_normalize_rule_translates_difftap_family():
    from rekolektion.verify.drc_klayout import _normalize_rule_id

    assert _normalize_rule_id("difftap.3") == "diff/tap.3"
    assert _normalize_rule_id("difftap.1_c") == "diff/tap.1"


def test_normalize_rule_passes_through_unknown_names():
    from rekolektion.verify.drc_klayout import _normalize_rule_id

    # No mapping → identity.
    assert _normalize_rule_id("poly.2") == "poly.2"
    assert _normalize_rule_id("MR_lvtn.OVL.2") == "MR_lvtn.OVL.2"


def test_normalize_rule_translates_metal_family():
    from rekolektion.verify.drc_klayout import _normalize_rule_id

    # KLayout uses `m{N}.*`; Magic uses `met{N}.*`.
    assert _normalize_rule_id("m1.1") == "met1.1"
    assert _normalize_rule_id("m1.2") == "met1.2"
    assert _normalize_rule_id("m2.6") == "met2.6"
    assert _normalize_rule_id("m3.3cd") == "met3.3cd"
    assert _normalize_rule_id("m5.x") == "met5.x"


def test_centroid_of_value_edge_pair():
    """Width/spacing violations come back as edge-pairs:
    `edge-pair: (3,0;0,0)|(0,0.1;3,0.1)`. The centroid must average
    across BOTH edges' endpoints so it sits between the violating
    edges — the location a spatial waiver-footprint check needs."""
    from rekolektion.verify.drc_klayout import _centroid_of_value

    out = _centroid_of_value("edge-pair: (3,0;0,0)|(0,0.1;3,0.1)")
    assert out is not None
    cx, cy = out
    # Points: (3,0), (0,0), (0,0.1), (3,0.1).
    # cx = (3+0+0+3)/4 = 1.5; cy = (0+0+0.1+0.1)/4 = 0.05
    assert cx == pytest.approx(1.5)
    assert cy == pytest.approx(0.05)


def test_is_waiver_rule_recognizes_known_magic_rules():
    from rekolektion.verify.drc_klayout import _is_waiver_rule

    assert _is_waiver_rule("li.1") is True
    assert _is_waiver_rule("nwell.2a") is True
    # Translates through difftap → diff/tap.
    assert _is_waiver_rule("difftap.3") is True


def test_is_waiver_rule_rejects_unknown():
    from rekolektion.verify.drc_klayout import _is_waiver_rule

    # KLayout-only rule with no Magic counterpart → not a waiver.
    assert _is_waiver_rule("MR_thkox.CON.1") is False


def test_waiver_margin_um_uses_normalized_rule():
    from rekolektion.verify.drc_klayout import _waiver_margin_um

    assert _waiver_margin_um("nwell.2a") == pytest.approx(1.5)
    # difftap.3 → diff/tap.3 → 0.30 µm
    assert _waiver_margin_um("difftap.3") == pytest.approx(0.30)
    # Unknown → 0.
    assert _waiver_margin_um("MR_thkox.CON.1") == pytest.approx(0.0)


def test_centroid_of_value_polygon():
    from rekolektion.verify.drc_klayout import _centroid_of_value

    # Square at (0,0)..(1,1) → centroid (0.5, 0.5).
    out = _centroid_of_value("polygon: (0.0,0.0;1.0,0.0;1.0,1.0;0.0,1.0)")
    assert out is not None
    cx, cy = out
    assert cx == pytest.approx(0.5)
    assert cy == pytest.approx(0.5)


def test_centroid_of_value_edge():
    from rekolektion.verify.drc_klayout import _centroid_of_value

    out = _centroid_of_value("edge: (0.0,0.0;2.0,0.0)")
    assert out is not None
    cx, cy = out
    assert cx == pytest.approx(1.0)
    assert cy == pytest.approx(0.0)


def test_centroid_of_value_ignores_text():
    from rekolektion.verify.drc_klayout import _centroid_of_value

    assert _centroid_of_value("text: 'min spacing violation'") is None
    assert _centroid_of_value("not a geometry value") is None


def test_parse_lyrdb_basic(tmp_path):
    from rekolektion.verify.drc_klayout import parse_lyrdb

    lyrdb = _write_lyrdb(
        tmp_path / "basic.lyrdb",
        """
        <categories>
         <category>
          <name>'poly.2'</name>
          <description>poly.2 : min. poly spacing : 0.21um</description>
         </category>
        </categories>
        <cells><cell><name>'top'</name></cell></cells>
        <items>
         <item>
          <category>'poly.2'</category>
          <cell>'top'</cell>
          <values>
           <value>polygon: (1.0,2.0;3.0,2.0;3.0,4.0;1.0,4.0)</value>
          </values>
         </item>
        </items>
        """,
    )
    violations, descs = parse_lyrdb(lyrdb)
    assert len(violations) == 1
    v = violations[0]
    assert v.rule == "poly.2"
    assert v.cell == "top"
    assert v.cx_um == pytest.approx(2.0)
    assert v.cy_um == pytest.approx(3.0)
    assert descs["poly.2"].startswith("poly.2")


def test_parse_lyrdb_skips_items_without_geometry(tmp_path):
    from rekolektion.verify.drc_klayout import parse_lyrdb

    lyrdb = _write_lyrdb(
        tmp_path / "noinfo.lyrdb",
        """
        <categories>
         <category><name>'info'</name><description>note</description></category>
        </categories>
        <items>
         <item>
          <category>'info'</category>
          <cell>'top'</cell>
          <values>
           <value>text: 'just a note'</value>
          </values>
         </item>
        </items>
        """,
    )
    violations, _ = parse_lyrdb(lyrdb)
    assert violations == []


def test_parse_lyrdb_multiple_rules(tmp_path):
    from rekolektion.verify.drc_klayout import parse_lyrdb

    lyrdb = _write_lyrdb(
        tmp_path / "multi.lyrdb",
        """
        <categories>
         <category><name>'poly.2'</name><description>poly.2 : ...</description></category>
         <category><name>'met1.2'</name><description>met1.2 : ...</description></category>
        </categories>
        <items>
         <item><category>'poly.2'</category><cell>'top'</cell>
          <values><value>polygon: (0,0;1,0;1,1;0,1)</value></values></item>
         <item><category>'poly.2'</category><cell>'top'</cell>
          <values><value>polygon: (2,2;3,2;3,3;2,3)</value></values></item>
         <item><category>'met1.2'</category><cell>'top'</cell>
          <values><value>polygon: (5,5;6,5;6,6;5,6)</value></values></item>
        </items>
        """,
    )
    violations, _ = parse_lyrdb(lyrdb)
    assert len(violations) == 3
    by_rule = {}
    for v in violations:
        by_rule.setdefault(v.rule, 0)
        by_rule[v.rule] += 1
    assert by_rule == {"poly.2": 2, "met1.2": 1}


# ---------------------------------------------------------------------------
# End-to-end tests — require KLayout binary + SKY130 PDK.
# ---------------------------------------------------------------------------

def _klayout_available() -> bool:
    try:
        from rekolektion.verify.drc_klayout import klayout_binary
        klayout_binary()
        from rekolektion.tech.sky130 import klayout_deck
        return klayout_deck().exists()
    except (FileNotFoundError, RuntimeError):
        return False


klayout_required = pytest.mark.skipif(
    not _klayout_available(),
    reason="KLayout binary or SKY130 KLayout deck not installed",
)


@pytest.mark.klayout
@klayout_required
def test_klayout_binary_resolves():
    """Sanity: the locator finds something executable."""
    from rekolektion.verify.drc_klayout import klayout_binary

    p = klayout_binary()
    assert p.exists()


@pytest.mark.klayout
@klayout_required
def test_run_drc_klayout_flags_sub_min_met1(tmp_path):
    """A 100 nm × 3 µm met1 rect violates met1 min-width (0.14 µm rule).
    The KLayout deck must flag at least one violation; result must
    match the DRCResult shape consumers depend on."""
    gdstk = pytest.importorskip("gdstk")
    from rekolektion.verify.drc_klayout import run_drc_klayout

    lib = gdstk.Library(name="thinwire_klayout")
    cell = gdstk.Cell("thinwire_kl")
    cell.add(gdstk.rectangle((0.0, 0.0), (3.0, 0.1), layer=68, datatype=20))
    lib.add(cell)
    gds = tmp_path / "thinwire_kl.gds"
    lib.write_gds(str(gds))

    result = run_drc_klayout(
        gds,
        cell_name="thinwire_kl",
        output_dir=tmp_path,
        offgrid=False,         # focus on width; off-grid is Track 01
    )
    assert result.error_count >= 1, (
        f"expected ≥1 KLayout violation on sub-min met1, got "
        f"error_count={result.error_count}; "
        f"see {result.log_path} for KLayout output."
    )
    # min-width family — KLayout names this `met1.1` (matching Magic).
    assert any("met1.1" in line for line in result.errors), (
        f"expected a met1.1 message; got: {result.errors}"
    )
    # Sub-min width is on the Magic waiver list (rule "met1.1" has
    # margin 0.0 — a tile OUTSIDE any foundry footprint is real).
    # Without footprints + strict default, it escalates to real.
    assert not result.clean
