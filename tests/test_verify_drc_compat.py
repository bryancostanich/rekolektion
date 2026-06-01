"""Tests for the compat= routing on `rekolektion.verify.verify_drc`.

Pure-Python tests (monkeypatched engines) run without Magic or
KLayout installed.  End-to-end tests gated on tool availability run
the orchestrator against a minimal real .rkt to confirm both compat
paths reach the engine.
"""
from __future__ import annotations

import shutil
import warnings
from pathlib import Path

import pytest


# Reusable stub DRCResult — matches the shape callers depend on.
def _stub_result(cell_name: str = "stub") -> object:
    from rekolektion.verify.drc import DRCResult
    return DRCResult(
        clean=True,
        error_count=0,
        real_error_count=0,
        waiver_error_count=0,
        errors=[],
        real_errors=[],
        log_path=Path("/tmp/stub.log"),
        cell_name=cell_name,
    )


@pytest.fixture
def _patch_engines(monkeypatch, tmp_path):
    """Replace both run_drc + run_drc_klayout with recording stubs and
    short-circuit the GDS conversion + grid check.  Yields a dict
    capturing which engine was called and with what kwargs."""
    seen: dict[str, dict] = {}

    def _fake_magic(gds_path, **kw):
        seen["magic"] = {"gds_path": gds_path, **kw}
        return _stub_result("magic_stub")

    def _fake_klayout(gds_path, **kw):
        seen["klayout"] = {"gds_path": gds_path, **kw}
        return _stub_result("klayout_stub")

    def _fake_convert(rkt, gds):
        # Touch the target so downstream existence checks pass.
        Path(gds).write_bytes(b"")

    def _fake_footprints(*args, **kwargs):
        return []

    # Grid check stub: always clean, sky130 grid.
    class _StubGrid:
        off_grid: list = []
        grid: int = 5

    def _fake_grid(rkt):
        return _StubGrid()

    import rekolektion.verify.rkt_drc as mod
    monkeypatch.setattr(mod, "run_drc", _fake_magic)
    monkeypatch.setattr(mod, "run_drc_klayout", _fake_klayout)
    monkeypatch.setattr(mod, "_convert_rkt_to_gds", _fake_convert)
    monkeypatch.setattr(mod, "compute_primitive_footprints", _fake_footprints)
    monkeypatch.setattr(mod, "verify_grid", _fake_grid)
    return seen


def _touch_rkt(tmp_path: Path) -> Path:
    rkt = tmp_path / "stub.rkt"
    rkt.write_text(
        "(layout (version 1) (pdk sky130)\n"
        "  (units (dbu_nm 1) (uu_um 1))\n"
        "  (top empty)\n"
        "  (cell empty))\n"
    )
    return rkt


# ---------------------------------------------------------------------------
# Routing logic
# ---------------------------------------------------------------------------

def test_verify_drc_default_routes_to_klayout(_patch_engines, tmp_path):
    from rekolektion.verify import verify_drc

    rkt = _touch_rkt(tmp_path)
    verify_drc(rkt)
    assert "klayout" in _patch_engines and "magic" not in _patch_engines


def test_verify_drc_compat_magic_routes_to_run_drc(_patch_engines, tmp_path):
    from rekolektion.verify import verify_drc

    rkt = _touch_rkt(tmp_path)
    verify_drc(rkt, compat="magic")
    assert "magic" in _patch_engines and "klayout" not in _patch_engines


def test_verify_drc_compat_klayout_explicit(_patch_engines, tmp_path):
    from rekolektion.verify import verify_drc

    rkt = _touch_rkt(tmp_path)
    verify_drc(rkt, compat="klayout")
    assert "klayout" in _patch_engines and "magic" not in _patch_engines


def test_verify_drc_invalid_compat_raises(_patch_engines, tmp_path):
    from rekolektion.verify import verify_drc

    rkt = _touch_rkt(tmp_path)
    with pytest.raises(ValueError, match="compat must be"):
        verify_drc(rkt, compat="calibre")  # type: ignore[arg-type]


def test_verify_drc_external_false_raises_not_implemented(_patch_engines, tmp_path):
    """Phase 5 will land external=False — until then, fail loudly."""
    from rekolektion.verify import verify_drc

    rkt = _touch_rkt(tmp_path)
    with pytest.raises(NotImplementedError, match="Phase 5"):
        verify_drc(rkt, external=False)


# ---------------------------------------------------------------------------
# full=True semantics
# ---------------------------------------------------------------------------

def test_full_true_forwarded_to_magic(_patch_engines, tmp_path):
    from rekolektion.verify import verify_drc

    rkt = _touch_rkt(tmp_path)
    verify_drc(rkt, compat="magic", full=True)
    assert _patch_engines["magic"]["full"] is True


def test_full_true_under_klayout_warns_and_ignored(_patch_engines, tmp_path):
    """KLayout has no fast/full split; passing full=True should emit a
    DeprecationWarning and NOT forward the parameter (KLayout's engine
    signature doesn't have one)."""
    from rekolektion.verify import verify_drc

    rkt = _touch_rkt(tmp_path)
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        verify_drc(rkt, compat="klayout", full=True)
    deprecations = [w for w in caught if issubclass(w.category, DeprecationWarning)]
    assert len(deprecations) == 1
    assert "Magic-only" in str(deprecations[0].message)
    # KLayout engine doesn't accept `full`; the stub records whatever it
    # was called with — must not include `full`.
    assert "full" not in _patch_engines["klayout"]


# ---------------------------------------------------------------------------
# KLayout-side wiring: offgrid suppressed (Track 01 owns the grid check)
# ---------------------------------------------------------------------------

def test_klayout_engine_called_with_offgrid_false(_patch_engines, tmp_path):
    from rekolektion.verify import verify_drc

    rkt = _touch_rkt(tmp_path)
    verify_drc(rkt)  # default compat=klayout
    assert _patch_engines["klayout"]["offgrid"] is False


# ---------------------------------------------------------------------------
# CLI smoke
# ---------------------------------------------------------------------------

def test_cli_verify_drc_help_includes_compat():
    """Sanity: --compat shows up in the subcommand help."""
    import subprocess
    import sys

    out = subprocess.run(
        [sys.executable, "-m", "rekolektion.cli", "verify-drc", "--help"],
        capture_output=True,
        text=True,
        cwd=Path(__file__).resolve().parents[1],
    )
    assert out.returncode == 0, f"CLI help failed: stderr={out.stderr}"
    assert "--compat" in out.stdout
    assert "klayout" in out.stdout
    assert "magic" in out.stdout


def test_cli_verify_drc_dispatches_to_orchestrator(monkeypatch, tmp_path):
    """CLI subcommand resolves --compat into the orchestrator's call
    signature.  We patch verify_drc to record kwargs and invoke main()
    directly (no subprocess hop)."""
    from rekolektion import cli

    rkt = _touch_rkt(tmp_path)
    captured: dict = {}

    def _fake_verify(rkt_path, **kw):
        captured["rkt"] = rkt_path
        captured.update(kw)
        return _stub_result()

    monkeypatch.setattr("rekolektion.verify.verify_drc", _fake_verify)

    cli.main([
        "verify-drc", str(rkt),
        "--compat", "magic",
        "--full",
        "--no-strict-grid",
    ])
    assert captured["compat"] == "magic"
    assert captured["full"] is True
    assert captured["strict_grid"] is False


# ---------------------------------------------------------------------------
# End-to-end — gated on tool availability + viz CLI build
# ---------------------------------------------------------------------------

def _klayout_available() -> bool:
    try:
        from rekolektion.verify.drc_klayout import klayout_binary
        from rekolektion.tech.sky130 import klayout_deck
        klayout_binary()
        return klayout_deck().exists()
    except (FileNotFoundError, RuntimeError):
        return False


def _dotnet_available() -> bool:
    return shutil.which("dotnet") is not None


klayout_e2e = pytest.mark.skipif(
    not (_klayout_available() and _dotnet_available()),
    reason="KLayout + dotnet (viz CLI build) required for end-to-end test",
)


@pytest.mark.klayout
@klayout_e2e
def test_verify_drc_klayout_end_to_end(tmp_path):
    """Build a tiny .rkt with a sub-min met1 wire, run through the full
    orchestrator (rkt → GDS → KLayout DRC → DRCResult), confirm it
    catches the violation."""
    from rekolektion.io import rkt as rkt_io
    from rekolektion.verify import verify_drc

    doc = rkt_io.Document(
        cells=[
            rkt_io.Cell(
                name="thinwire_e2e",
                elements=[
                    rkt_io.Rect(
                        layer=rkt_io.named("sky130", "met1"),
                        x1=0, y1=0, x2=3000, y2=100,   # 100 nm height < 140 nm rule
                    ),
                ],
            ),
        ],
        top_cell="thinwire_e2e",
    )
    rkt_path = tmp_path / "thinwire_e2e.rkt"
    rkt_path.write_text(rkt_io.write(doc))

    result = verify_drc(
        rkt_path,
        cell_name="thinwire_e2e",
        compat="klayout",
        output_dir=tmp_path,
        strict_grid=False,    # focus on width — coords are on-grid anyway
    )
    assert result.error_count >= 1, (
        f"expected ≥1 KLayout violation; summary={result.summary()}"
    )
    assert any("met1.1" in line for line in result.errors), (
        f"expected met1.1 in errors; got: {result.errors}"
    )
