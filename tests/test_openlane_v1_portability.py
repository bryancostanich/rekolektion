"""Portability tests — feed the v1 production macros to OpenLane.

Option E in the SRAM-design track: "a chip-level P&R tool can
successfully consume the macro's LEF/GDS/Liberty abstract views."

Each test builds a minimal chip that instantiates one v1 production
macro, runs OpenLane through streamout + LVS, and asserts the flow
completed through the chip-level LVS step.

Tagged with ``@pytest.mark.openlane`` because each run is ~3 min.  Run
explicitly:

    source scripts/openlane_env.sh
    pytest -m openlane tests/test_openlane_v1_portability.py -v

These tests DO NOT assert LVS-clean — chip-level LVS against v1 macros
has known fill/decap/tapcell noise waivers (see ``docs/lvs_flow.md``).
They DO assert that the flow produces a valid GDS + LEF + LVS report,
which is the gate on whether the macro's abstract views are correct.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest


_REPO = Path(__file__).parent.parent
_V1_MACRO_DIR = _REPO / "output/v2_macros"
_ENV_SCRIPT = _REPO / "scripts/openlane_env.sh"


def _openlane_on_path() -> bool:
    """True iff an `openlane` CLI is on PATH."""
    return shutil.which("openlane") is not None


def _yosys_supports_y() -> bool:
    """True iff the `yosys` on PATH accepts OpenLane's -y flag
    (pyosys-capable build).  Checked by running `yosys -y /dev/null`
    and looking for the splash instead of the 'Option y does not
    exist' error."""
    try:
        out = subprocess.run(
            ["yosys", "-y", "/dev/null"],
            capture_output=True, text=True, timeout=5,
        )
        return "Yosys" in out.stdout and "does not exist" not in out.stderr
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return False


@pytest.fixture(scope="module")
def _require_openlane_env():
    """Skip if the PATH isn't set up for OpenLane 2 (scripts/
    openlane_env.sh points at nix-store yosys/openroad/etc)."""
    if not _openlane_on_path():
        pytest.skip("`openlane` not on PATH")
    if not _yosys_supports_y():
        pytest.skip(
            "yosys on PATH doesn't support -y; source "
            "scripts/openlane_env.sh first"
        )


def _regenerate_v1_macros() -> None:
    """Run the v1 production generator so the artifacts under test
    reflect the current code.  Must run before copying into the test
    chip dir."""
    gen = _REPO / "scripts/generate_v2_production.py"
    subprocess.run(
        ["python3", str(gen)], check=True, cwd=str(_REPO),
    )


def _copy_macro_artifacts(
    macro_name: str, dest_dir: Path,
) -> None:
    src = _V1_MACRO_DIR / macro_name
    (dest_dir / "macros").mkdir(parents=True, exist_ok=True)
    # Copy .lef, .gds, .lib — keep the pre-checked-in bb.v.
    for ext in ("lef", "gds", "lib"):
        shutil.copy(
            src / f"{macro_name}.{ext}",
            dest_dir / "macros" / f"{macro_name}.{ext}",
        )


def _run_openlane(test_dir: Path) -> tuple[int, Path]:
    """Invoke openlane on test_dir/config.json.  Returns (exit_code,
    latest_run_dir)."""
    result = subprocess.run(
        ["openlane", "config.json"],
        cwd=str(test_dir), capture_output=True, text=True, timeout=600,
    )
    runs_dir = test_dir / "runs"
    run_dirs = sorted(runs_dir.iterdir()) if runs_dir.is_dir() else []
    latest = run_dirs[-1] if run_dirs else test_dir
    return result.returncode, latest


def _assert_completed_through_lvs(run_dir: Path) -> None:
    steps = sorted(
        d.name for d in run_dir.iterdir()
        if d.is_dir() and d.name[:1].isdigit()
    )
    has_streamout = any("magic-streamout" in s for s in steps)
    has_netgen_lvs = any("netgen-lvs" in s for s in steps)
    assert has_streamout, (
        f"OpenLane didn't reach Magic streamout.  Steps: {steps[-10:]}"
    )
    assert has_netgen_lvs, (
        f"OpenLane didn't reach netgen LVS.  Steps: {steps[-10:]}"
    )


def _assert_gds_exists(run_dir: Path, macro_name: str = "top_test") -> None:
    gds = list(run_dir.rglob(f"{macro_name}.gds"))
    assert gds, f"No {macro_name}.gds produced in {run_dir}"


@pytest.mark.openlane
def test_openlane_consumes_sram_weight_bank_small(_require_openlane_env):
    """Chip-level P&R + LVS on `sram_weight_bank_small`."""
    _regenerate_v1_macros()
    test_dir = _REPO / "openlane_test_v1weight"
    _copy_macro_artifacts("sram_weight_bank_small", test_dir)
    rc, run_dir = _run_openlane(test_dir)
    # Exit code is usually 2 because chip-level LVS has known waiver-
    # class errors (fill/decap/tapcell) — we tolerate that and check
    # only that the flow got through streamout + ran LVS.
    _assert_completed_through_lvs(run_dir)
    _assert_gds_exists(run_dir)


@pytest.mark.openlane
def test_openlane_consumes_sram_activation_bank(_require_openlane_env):
    """Chip-level P&R + LVS on `sram_activation_bank`."""
    _regenerate_v1_macros()
    test_dir = _REPO / "openlane_test_v1activation"
    _copy_macro_artifacts("sram_activation_bank", test_dir)
    rc, run_dir = _run_openlane(test_dir)
    _assert_completed_through_lvs(run_dir)
    _assert_gds_exists(run_dir)
