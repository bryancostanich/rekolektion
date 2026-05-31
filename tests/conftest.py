"""Pytest hooks for DRC parity audit.

When `REK_DRC_PARITY=1` is set, every `run_drc` call in the test
suite is wrapped: the original call still runs, but in parallel we
invoke a stripped-down "authoritative" Magic recipe modelled on the
project's CLAUDE.md and compare totals + per-rule breakdowns.

Discrepancies are printed and recorded in `REK_DRC_PARITY_LOG`
(default `scratch/drc_parity/parity.log`). Test outcomes are NOT
altered — this is an observation pass.
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

import pytest


_PARITY_ENABLED = os.environ.get("REK_DRC_PARITY") == "1"
_PARITY_LOG = Path(
    os.environ.get(
        "REK_DRC_PARITY_LOG",
        str(Path(__file__).resolve().parents[1] / "scratch" / "drc_parity" / "parity.log"),
    )
)


_RAW_TCL = """\
tech load {tech}
gds read {gds}
{load_line}
select top cell
box grow n 2000
box grow s 2000
box grow e 2000
box grow w 2000
drc catchup
set why_list [drc listall why]
puts "RAW_PARITY_BEGIN"
set total 0
foreach {{msg box_list}} $why_list {{
    set n [llength $box_list]
    incr total $n
    puts "RULE\\t$n\\t$msg"
}}
puts "RAW_PARITY_TOTAL $total"
puts "RAW_PARITY_END"
quit -noprompt
"""


def _run_raw_magic(gds: Path, cell_name: str) -> dict[str, Any]:
    from rekolektion.tech.sky130 import magic_techfile, magic_rcfile, pdk_path

    tech = magic_techfile()
    rc = magic_rcfile()
    workdir = Path(tempfile.mkdtemp(prefix="parity_raw_"))
    script = _RAW_TCL.format(
        tech=tech, gds=Path(gds).resolve(),
        load_line=(f"load {cell_name}" if cell_name else ""),
    )
    tcl = workdir / "raw.tcl"
    tcl.write_text(script)

    env = os.environ.copy()
    env["PDK_ROOT"] = str(pdk_path().parent)

    cmd = ["magic", "-dnull", "-noconsole"]
    if rc.exists():
        cmd.extend(["-rcfile", str(rc)])
    cmd.append(str(tcl))

    try:
        res = subprocess.run(
            cmd, capture_output=True, text=True, timeout=1800,
            cwd=str(workdir), env=env,
        )
    except Exception as exc:
        return {"total": -1, "rules": [], "error": str(exc)}

    rules: list[tuple[int, str]] = []
    total = -1
    in_block = False
    for line in res.stdout.splitlines():
        if line.startswith("RAW_PARITY_BEGIN"):
            in_block = True
            continue
        if line.startswith("RAW_PARITY_END"):
            in_block = False
            continue
        if line.startswith("RAW_PARITY_TOTAL "):
            total = int(line.split()[-1])
            continue
        if in_block and line.startswith("RULE\t"):
            _, n, msg = line.split("\t", 2)
            rules.append((int(n), msg))

    return {
        "total": total,
        "rules": rules,
        "stdout_tail": res.stdout[-500:],
        "stderr_tail": res.stderr[-500:],
        "returncode": res.returncode,
        "workdir": workdir,
    }


_LINE_RE = re.compile(r"^Violation \((\d+) tiles\): (.*)$")


def _per_rule_from_errors(errors: list[str]) -> dict[str, int]:
    out: dict[str, int] = {}
    for line in errors:
        m = _LINE_RE.match(line)
        if m:
            n = int(m.group(1))
            msg = m.group(2)
            out[msg] = out.get(msg, 0) + n
    return out


_parity_records: list[dict[str, Any]] = []


def _wrap_run_drc(orig):
    def wrapper(gds_path, cell_name="", *args, **kwargs):
        result = orig(gds_path, cell_name, *args, **kwargs)
        try:
            raw = _run_raw_magic(Path(gds_path), cell_name)
        except Exception as exc:
            raw = {"total": -2, "rules": [], "error": f"raw_magic_exception: {exc}"}

        raw_total = raw["total"]
        rek_total = result.error_count
        raw_by: dict[str, int] = {}
        for n, msg in raw["rules"]:
            raw_by[msg] = raw_by.get(msg, 0) + n
        rek_by = _per_rule_from_errors(result.errors)

        same_total = raw_total == rek_total
        same_breakdown = raw_by == rek_by

        # Per-rule deltas for the record
        all_msgs = sorted(set(raw_by) | set(rek_by))
        deltas = [
            (msg, raw_by.get(msg, 0), rek_by.get(msg, 0))
            for msg in all_msgs
            if raw_by.get(msg, 0) != rek_by.get(msg, 0)
        ]

        rec = {
            "gds": str(gds_path),
            "cell_name": cell_name,
            "raw_total": raw_total,
            "rek_error_count": rek_total,
            "rek_real_error_count": result.real_error_count,
            "rek_waiver_error_count": result.waiver_error_count,
            "rek_clean": result.clean,
            "same_total": same_total,
            "same_breakdown": same_breakdown,
            "deltas": deltas,
            "raw_returncode": raw.get("returncode"),
            "raw_error": raw.get("error"),
            "raw_by_msg": raw_by,
            "rek_by_msg": rek_by,
        }
        _parity_records.append(rec)
        return result

    return wrapper


@pytest.fixture(autouse=True)
def _drc_parity_wrap(monkeypatch):
    if not _PARITY_ENABLED:
        yield
        return

    import rekolektion.verify.drc as drc_mod

    orig = drc_mod.run_drc
    wrapped = _wrap_run_drc(orig)
    monkeypatch.setattr(drc_mod, "run_drc", wrapped)
    # Also patch the symbol importers may have already grabbed
    import sys as _sys
    for mod_name, mod in list(_sys.modules.items()):
        if mod is None:
            continue
        if mod_name.startswith("rekolektion"):
            if hasattr(mod, "run_drc") and getattr(mod, "run_drc") is orig:
                monkeypatch.setattr(mod, "run_drc", wrapped)
    yield


def pytest_sessionfinish(session, exitstatus):
    if not _PARITY_ENABLED or not _parity_records:
        return
    _PARITY_LOG.parent.mkdir(parents=True, exist_ok=True)
    lines: list[str] = []
    lines.append(f"# DRC parity report — {len(_parity_records)} run_drc calls")
    lines.append(f"# raw  = stripped-down Magic recipe (CLAUDE.md style)")
    lines.append(f"# rek  = rekolektion run_drc raw error_count")
    lines.append(f"# delta = per-rule msg counts differ")
    lines.append("")
    miss = 0
    for r in _parity_records:
        head = (
            f"gds={Path(r['gds']).name}  cell={r['cell_name'] or '(top)'}  "
            f"raw={r['raw_total']}  rek={r['rek_error_count']}  "
            f"real={r['rek_real_error_count']}  "
            f"agree={'Y' if r['same_total'] and r['same_breakdown'] else 'N'}"
        )
        lines.append(head)
        if r["raw_error"]:
            lines.append(f"  raw_error: {r['raw_error']}")
        if r["raw_returncode"] not in (None, 0):
            lines.append(f"  raw_returncode: {r['raw_returncode']}")
        if r["deltas"]:
            lines.append(f"  deltas:")
            for msg, rr, rk in r["deltas"]:
                lines.append(f"    raw={rr:>5}  rek={rk:>5}  {msg}")
        # Per-rule breakdown (raw == rek by construction here)
        if r["raw_by_msg"]:
            lines.append("  per-rule (raw):")
            for msg, n in sorted(r["raw_by_msg"].items(), key=lambda kv: -kv[1]):
                lines.append(f"    {n:>5}  {msg}")
        if not (r["same_total"] and r["same_breakdown"]):
            miss += 1
    lines.append("")
    lines.append(f"# disagreements: {miss}/{len(_parity_records)}")
    text = "\n".join(lines) + "\n"
    _PARITY_LOG.write_text(text)
    print(f"\n[parity] {len(_parity_records)} calls observed, "
          f"{miss} disagreements — see {_PARITY_LOG}",
          file=sys.stderr)
