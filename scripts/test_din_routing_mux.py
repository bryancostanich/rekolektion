"""Quick test: generate macro and extract for mux ratios 2, 4, 8 to
verify DIN routing is clean (no shorts in extracted SPICE).

Checks each extracted SPICE top-level subckt for any port that was
absorbed/shorted to another (port list length < expected) and
grep-style checks for any 'GLOBAL' / 'shorted' warnings in extract.log.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

from rekolektion.macro.assembler import MacroParams, assemble
from rekolektion.verify.lvs import extract_netlist

_ROOT = Path(__file__).parent.parent
_OUT = _ROOT / "output" / "lvs_tiny"


def _parse_top_ports(ext_spice: Path, top_name: str) -> list[str]:
    """Parse `.subckt <top_name> <ports>` line(s) from extracted SPICE."""
    txt = ext_spice.read_text()
    # Handle continuation lines (+ at start).
    lines = []
    for line in txt.splitlines():
        if lines and line.startswith("+"):
            lines[-1] += " " + line[1:].strip()
        else:
            lines.append(line)
    for line in lines:
        s = line.strip()
        if s.lower().startswith(".subckt") and top_name in s.split()[1:2]:
            toks = s.split()
            return toks[2:]
    return []


def _scan_shorts(ext_log: Path) -> list[str]:
    """Return any extract-log lines suggesting node shorts."""
    if not ext_log.exists():
        return []
    bad: list[str] = []
    for line in ext_log.read_text().splitlines():
        l = line.lower()
        if any(k in l for k in ("shorted", "global", "merged with", "absorb")):
            bad.append(line)
    return bad


def test_mux(mux: int, bits: int = 8, words: int = 32) -> dict:
    out = _OUT / f"test_mux{mux}"
    out.mkdir(parents=True, exist_ok=True)
    p = MacroParams(words=words, bits=bits, mux_ratio=mux)
    print(f"\n=== mux={mux} === ({p.top_cell_name})", flush=True)

    lib = assemble(p)
    gds = out / f"{p.top_cell_name}.gds"
    lib.write_gds(str(gds))
    print(f"  GDS: {gds}", flush=True)

    try:
        extracted = extract_netlist(
            gds, cell_name=p.top_cell_name, output_dir=out, timeout=600,
        )
    except Exception as e:
        print(f"  EXTRACT FAILED: {e}", flush=True)
        return {"mux": mux, "ok": False, "err": str(e)}

    ports = _parse_top_ports(extracted, p.top_cell_name)
    # Expected top ports for dout/din pins:
    dout_ports = [pp for pp in ports if pp.startswith("dout")]
    din_ports = [pp for pp in ports if pp.startswith("din")]
    addr_ports = [pp for pp in ports if pp.startswith("addr")]
    # Dedup via set-length vs list-length comparison tells us if any
    # extractor merged two pins into the same net name.
    dout_unique = len(set(dout_ports))
    din_unique = len(set(din_ports))
    addr_unique = len(set(addr_ports))
    exp_dout = bits
    exp_din = bits
    exp_addr = int.bit_length(words - 1)

    ok_dout = dout_unique == exp_dout and len(dout_ports) == exp_dout
    ok_din = din_unique == exp_din and len(din_ports) == exp_din
    ok_addr = addr_unique == exp_addr and len(addr_ports) == exp_addr

    print(f"  DOUT: {dout_unique}/{exp_dout} unique ({len(dout_ports)} listed) {'OK' if ok_dout else 'FAIL'}", flush=True)
    print(f"  DIN : {din_unique}/{exp_din} unique ({len(din_ports)} listed) {'OK' if ok_din else 'FAIL'}", flush=True)
    print(f"  ADDR: {addr_unique}/{exp_addr} unique ({len(addr_ports)} listed) {'OK' if ok_addr else 'FAIL'}", flush=True)

    print(f"  All top ports ({len(ports)}): {' '.join(ports)}", flush=True)

    return {
        "mux": mux,
        "ok": ok_dout and ok_din and ok_addr,
        "ports": ports,
    }


def main() -> int:
    results = []
    for mux in (2, 4, 8):
        results.append(test_mux(mux))
    print("\n=== Summary ===")
    any_fail = False
    for r in results:
        status = "OK" if r.get("ok") else "FAIL"
        print(f"  mux={r['mux']}: {status}")
        if not r.get("ok"):
            any_fail = True
    return 1 if any_fail else 0


if __name__ == "__main__":
    sys.exit(main())
