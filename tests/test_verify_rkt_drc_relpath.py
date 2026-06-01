"""Regression: `verify_drc` must accept relative rkt_path values.

The helper shells out to viz CLI with `cwd=rekolektion_root`. A
relative `rkt_path` left unresolved gets re-rooted against the
rekolektion tree instead of the caller's cwd — the loader then
fails with "Could not find a part of the path" and exits 134.

Verify the fix by calling `verify_drc` with a relative path from
a cwd OUTSIDE the rekolektion repo and assert the path handed to
the GDS converter is absolute. The conversion + DRC subprocesses
are stubbed; we're testing the path-normalization contract, not
the full magic pipeline.
"""
from __future__ import annotations

from pathlib import Path

from rekolektion.verify import rkt_drc
from rekolektion.verify.drc import DRCResult


def test_verify_drc_resolves_relative_rkt_path(tmp_path, monkeypatch):
    # Drop a stub .rkt in a tmp dir, then cd somewhere unrelated.
    fixture_dir = tmp_path / "fixtures"
    fixture_dir.mkdir()
    stub = fixture_dir / "block.rkt"
    stub.write_text("(layout (version 1) (top stub) (cell stub))\n")
    other_cwd = tmp_path / "elsewhere"
    other_cwd.mkdir()
    monkeypatch.chdir(other_cwd)

    seen: dict[str, Path] = {}

    def fake_convert(rkt_path: Path, gds_path: Path) -> None:
        seen["rkt"] = rkt_path
        gds_path.write_bytes(b"")  # subsequent run_drc stub doesn't read this

    def fake_run_drc(*_args, **_kwargs):  # noqa: ANN002, ANN003
        return DRCResult(
            clean=True,
            error_count=0,
            real_error_count=0,
            waiver_error_count=0,
            errors=[],
            real_errors=[],
            log_path=Path("/dev/null"),
            cell_name="stub",
        )

    monkeypatch.setattr(rkt_drc, "_convert_rkt_to_gds", fake_convert)
    monkeypatch.setattr(rkt_drc, "run_drc", fake_run_drc)
    monkeypatch.setattr(
        rkt_drc, "compute_primitive_footprints", lambda *_a, **_k: []
    )

    rel = Path("../fixtures/block.rkt")
    rkt_drc.verify_drc(rel)

    assert seen["rkt"].is_absolute(), (
        f"verify_drc handed a non-absolute path to the GDS converter: "
        f"{seen['rkt']!r}. The subprocess runs with cwd=rekolektion_root, "
        "so relative paths break."
    )
    assert seen["rkt"] == stub.resolve()
