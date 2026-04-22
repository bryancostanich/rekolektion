"""D.1 diagnostic — isolate each rekolektion peripheral cell and check for
unintended electrical shorts via Magic extraction with port promotion.

For every generator this script can instantiate, it:
  1. Emits a standalone GDS file.
  2. Monkey-patches the `gdstk.Label` calls so labels land on the sky130
     `.pin` sublayer (dtype 16) rather than the drawing sublayer (dtype 20)
     so Magic will promote them to subckt ports.
  3. Runs Magic extraction with `make_ports=True` and `port makeall`.
  4. Parses the extracted SPICE and counts how many labels each port
     absorbed — a port that swallows >1 label means those labels are
     electrically shorted in the layout.

Prints a table. No files committed; output is transient probe data in
`output/pvt_sweep/_d1_probe/<cell>/`.
"""
from __future__ import annotations

import re
import shutil
import sys
import textwrap
from pathlib import Path
from typing import Callable

import gdstk

from rekolektion.tech.sky130 import LAYERS
from rekolektion.verify.lvs import extract_netlist


ROOT = Path("output/pvt_sweep/_d1_probe")
ROOT.mkdir(parents=True, exist_ok=True)


# -------- label-layer patching -----------------------------------------------
# Each peripheral emits labels directly on its drawing-layer tuples
# (_MET1=(68,20), _MET2=(69,20), _POLY=(66,20), _LI1=(67,20)). To get
# Magic port promotion without editing every generator, we wrap
# gdstk.Label to redirect drawing-layer labels to the matching .pin
# sublayer (dtype 16) at emit time.

_ORIG_LABEL = gdstk.Label
_DRAWING_TO_PIN: dict[tuple[int, int], tuple[int, int]] = {
    LAYERS.POLY.as_tuple: LAYERS.POLY_LABEL.as_tuple,  # poly has no .pin; use .label for naming only
    LAYERS.LI1.as_tuple:  (67, 5),                      # li1.label — li1 has no .pin in sky130
    LAYERS.MET1.as_tuple: LAYERS.MET1_PIN.as_tuple,
    LAYERS.MET2.as_tuple: LAYERS.MET2_PIN.as_tuple,
    LAYERS.MET3.as_tuple: LAYERS.MET3_PIN.as_tuple,
}


def _patched_label(text, origin, *, layer=0, texttype=0, **kw):
    key = (layer, texttype)
    if key in _DRAWING_TO_PIN:
        layer, texttype = _DRAWING_TO_PIN[key]
    return _ORIG_LABEL(text, origin, layer=layer, texttype=texttype, **kw)


gdstk.Label = _patched_label  # type: ignore[assignment]


# -------- extraction + analysis ----------------------------------------------
def analyze_subckt(spice_path: Path) -> tuple[int, list[str], list[tuple[str, str]]]:
    """Returns (n_ports, port_list, shorts) where shorts are pairs found in
    Magic's "electrically shorted" warnings in the extract log."""
    text = spice_path.read_text()
    # Find .subckt header and its port list.
    m = re.search(r"\.subckt\s+(\S+)((?:\s+\S+)*)\s*\n", text)
    if not m:
        return 0, [], []
    header = m.group(2).strip()
    ports = header.split() if header else []

    log_path = spice_path.parent / "extract.log"
    shorts: list[tuple[str, str]] = []
    if log_path.exists():
        for line in log_path.read_text().splitlines():
            mm = re.search(r'Ports "([^"]+)" and "([^"]+)" are electrically shorted', line)
            if mm:
                shorts.append((mm.group(1), mm.group(2)))
    return len(ports), ports, shorts


def probe(name: str, emit: Callable[[Path], str]) -> dict:
    cell_dir = ROOT / name
    if cell_dir.exists():
        shutil.rmtree(cell_dir)
    cell_dir.mkdir(parents=True)

    gds_path = cell_dir / f"{name}.gds"
    top_cell = emit(gds_path)

    try:
        spice = extract_netlist(
            gds_path,
            cell_name=top_cell,
            output_dir=cell_dir / "extract",
            make_ports=True,
            timeout=120,
        )
    except Exception as exc:
        return {"name": name, "top": top_cell, "status": f"EXTRACT_FAILED: {exc}"}

    n_ports, ports, shorts = analyze_subckt(spice)

    # Count how many distinct labels each surviving port absorbed, by grep'ing
    # the shorts pairs. Each pair means one label was merged into the other's net.
    lib = gdstk.read_gds(str(gds_path))
    top = next(c for c in lib.cells if c.name == top_cell)
    label_count = len(top.labels)

    absorbed = 0
    for _a, _b in shorts:
        absorbed += 1

    return {
        "name": name,
        "top": top_cell,
        "labels": label_count,
        "ports": n_ports,
        "shorted_pairs": len(shorts),
        "verdict": (
            "FUNCTIONAL" if n_ports >= label_count - 1 and not shorts
            else f"SHORTED ({label_count} labels -> {n_ports} ports)"
        ),
    }


# -------- generators --------------------------------------------------------
def _emit_precharge(path: Path) -> str:
    from rekolektion.peripherals.precharge import generate_precharge
    cell, _ = generate_precharge(num_pairs=4, output_path=path)
    return cell.name


def _emit_column_mux(path: Path) -> str:
    from rekolektion.peripherals.column_mux import generate_column_mux
    cell, _ = generate_column_mux(num_pairs=4, mux_ratio=2, output_path=path)
    return cell.name


def _emit_wl_gate(path: Path) -> str:
    from rekolektion.peripherals.wl_gate import generate_wl_gate
    cell, _ = generate_wl_gate(output_path=path)
    return cell.name


def _emit_wl_mux(path: Path) -> str:
    from rekolektion.peripherals.wl_mux import generate_wl_mux
    cell, _ = generate_wl_mux(output_path=path)
    return cell.name


def _emit_power_switch(path: Path) -> str:
    from rekolektion.peripherals.power_switch import generate_power_switches
    cell, _ = generate_power_switches(num_switches=2, macro_width=10.0, output_path=path)
    return cell.name


def _emit_write_enable_gate(path: Path) -> str:
    from rekolektion.peripherals.write_enable_gate import generate_write_enable_gates
    cell, _ = generate_write_enable_gates(num_bits=8, ben_bits=4, output_path=path)
    return cell.name


def _emit_cim_mbl_precharge(path: Path) -> str:
    from rekolektion.peripherals.cim_mbl_precharge import generate_mbl_precharge
    cell, lib = generate_mbl_precharge()
    lib.write_gds(str(path))
    return cell.name


def _emit_cim_mwl_driver(path: Path) -> str:
    from rekolektion.peripherals.cim_mwl_driver import generate_mwl_driver
    cell, lib = generate_mwl_driver()
    lib.write_gds(str(path))
    return cell.name


def _emit_cim_ring_osc(path: Path) -> str:
    from rekolektion.peripherals.cim_ring_osc import generate_ring_osc
    cell, lib = generate_ring_osc()
    lib.write_gds(str(path))
    return cell.name


def _emit_cim_mbl_sense(path: Path) -> str:
    from rekolektion.peripherals.cim_mbl_sense import generate_mbl_sense
    cell, lib = generate_mbl_sense()
    lib.write_gds(str(path))
    return cell.name


TARGETS: list[tuple[str, Callable[[Path], str]]] = [
    ("precharge",          _emit_precharge),
    ("column_mux",         _emit_column_mux),
    ("wl_gate",            _emit_wl_gate),
    ("wl_mux",             _emit_wl_mux),
    ("power_switch",       _emit_power_switch),
    ("write_enable_gate",  _emit_write_enable_gate),
    ("cim_mbl_precharge",  _emit_cim_mbl_precharge),
    ("cim_mwl_driver",     _emit_cim_mwl_driver),
    ("cim_ring_osc",       _emit_cim_ring_osc),
    ("cim_mbl_sense",      _emit_cim_mbl_sense),
]


def main() -> int:
    results = []
    for name, emit in TARGETS:
        print(f"-- probing {name} --", flush=True)
        try:
            results.append(probe(name, emit))
        except Exception as exc:
            results.append({"name": name, "status": f"FAIL: {exc}"})
    print()
    print(f'{"cell":<22} {"labels":>7} {"ports":>6} {"shorts":>7}  verdict')
    print("-" * 80)
    for r in results:
        if "verdict" in r:
            print(f'{r["name"]:<22} {r["labels"]:>7} {r["ports"]:>6} {r["shorted_pairs"]:>7}  {r["verdict"]}')
        else:
            print(f'{r["name"]:<22}   {r.get("status", "???")}')
    return 0


if __name__ == "__main__":
    sys.exit(main())
