"""Helper for asserting `rekolektion.verify.drc.run_drc` matches raw Magic.

The harness intent (per Bryan, 2026-05-31): the magic-marked tests
verify rekolektion's DRC implementation, NOT the cleanliness of the
layouts under test. If a layout has 54 met2.2 tiles in Magic, rek
should also report 54 met2.2 tiles. The test passes when both agree
and fails when rek under-reports or over-reports.

`assert_drc_matches_magic` runs:

  raw  — a stripped-down `magic -dnull` recipe, modelled on the
         project's CLAUDE.md (gds read; load; select top cell;
         box grow on every side so edge-effect tiles fall inside
         the listall window; drc catchup; drc listall why; tally
         per-rule tile counts).
  rek  — the real `rekolektion.verify.drc.run_drc` on the same GDS.

…and asserts:
  * total tile count matches (`raw_total == rek.error_count`)
  * per-rule message-count breakdown matches exactly

Returns the rekolektion `DRCResult` so callers can inspect waiver vs
real classification if they need to.

The helper has the same `magic` dependency the existing tests already
declare via `@pytest.mark.magic`. It does not change a test's marker.
"""
from __future__ import annotations

import os
import re
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Optional


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


@dataclass
class _RawDrc:
    total: int
    by_msg: dict[str, int]


def _run_raw_magic(gds: Path, cell_name: str) -> _RawDrc:
    from rekolektion.tech.sky130 import magic_techfile, magic_rcfile, pdk_path

    tech = magic_techfile()
    rc = magic_rcfile()
    workdir = Path(tempfile.mkdtemp(prefix="parity_raw_"))
    try:
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

        res = subprocess.run(
            cmd, capture_output=True, text=True, timeout=1800,
            cwd=str(workdir), env=env,
        )
        if res.returncode != 0:
            raise RuntimeError(
                f"raw Magic returned {res.returncode}.\n"
                f"stdout tail:\n{res.stdout[-500:]}\n"
                f"stderr tail:\n{res.stderr[-500:]}"
            )
    finally:
        # leave workdir for inspection only if something below raises
        pass

    by_msg: dict[str, int] = {}
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
            by_msg[msg] = by_msg.get(msg, 0) + int(n)

    if total < 0:
        raise RuntimeError(
            "raw Magic produced no RAW_PARITY_TOTAL marker — recipe "
            "is broken. stdout tail:\n" + res.stdout[-500:]
        )

    import shutil
    shutil.rmtree(workdir, ignore_errors=True)
    return _RawDrc(total=total, by_msg=by_msg)


_LINE_RE = re.compile(r"^Violation \((\d+) tiles\): (.*)$")


def _per_rule_from_errors(errors: list[str]) -> dict[str, int]:
    """Sum per-rule tile counts from the `Violation (N tiles): <msg>`
    headers in DRCResult.errors. (.errors keeps the header lines; the
    per-tile coordinates follow them in the log file, not in this list.)
    """
    out: dict[str, int] = {}
    for line in errors:
        m = _LINE_RE.match(line)
        if m:
            n = int(m.group(1))
            msg = m.group(2)
            out[msg] = out.get(msg, 0) + n
    return out


def assert_drc_matches_magic(
    gds_path: Path | str,
    cell_name: str = "",
    *,
    output_dir: Optional[Path] = None,
):
    """Run rekolektion.run_drc + raw Magic on `gds_path`, assert they
    agree on total tile count and per-rule breakdown. Return the
    rekolektion `DRCResult` for any further inspection the caller
    wants to do.
    """
    from rekolektion.verify.drc import run_drc

    gds_path = Path(gds_path)
    raw = _run_raw_magic(gds_path, cell_name)
    rek = run_drc(gds_path, cell_name=cell_name, output_dir=output_dir)

    rek_by_msg = _per_rule_from_errors(rek.errors)

    # Both totals + breakdowns must agree.
    if raw.total != rek.error_count or raw.by_msg != rek_by_msg:
        # Build a focused diff for the assertion message.
        all_msgs = sorted(set(raw.by_msg) | set(rek_by_msg))
        lines = [
            f"DRC parity MISMATCH for {gds_path.name} "
            f"(cell={cell_name or '(top)'}):",
            f"  raw_total       = {raw.total}",
            f"  rek.error_count = {rek.error_count}",
            "  per-rule deltas (raw vs rek):",
        ]
        for msg in all_msgs:
            r = raw.by_msg.get(msg, 0)
            k = rek_by_msg.get(msg, 0)
            marker = "  " if r == k else "!="
            lines.append(f"    {marker}  raw={r:>6}  rek={k:>6}  {msg}")
        raise AssertionError("\n".join(lines))

    return rek
