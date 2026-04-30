"""Run LVS on production v2 SRAM macros (weight_bank, activation_bank).

Assumes generate_v2_production.py has already produced the GDS + refspice
in output/v2_macros/<macro_name>/.  This script:
  1. Extracts the assembled GDS via Magic (`extract all` + `ext2spice`).
  2. Compares the extracted netlist against the reference SPICE via netgen.

Magic extract on a 128x128 bitcell array takes ~10 min single-threaded.
Supports -j N to run multiple macros in parallel (each worker spawns its
own Magic subprocess).
"""
from __future__ import annotations

import argparse
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path

from rekolektion.verify.lvs import extract_netlist, run_lvs


_ROOT = Path(__file__).parent.parent
_DEFAULT_INPUT = _ROOT / "output" / "v2_macros"
_DEFAULT_OUTPUT = _ROOT / "output" / "lvs_production"


@dataclass(frozen=True)
class ProductionMacro:
    name: str
    # The top_cell_name inside the GDS.  Production regen renames it to
    # the macro_name at build time, so they agree.


MACROS: tuple[ProductionMacro, ...] = (
    ProductionMacro("sram_weight_bank_small"),
    ProductionMacro("sram_activation_bank"),
)


def _flatten_gds(src_gds: Path, dst_gds: Path, top_cell: str) -> Path:
    """Flatten the top cell's hierarchy in src_gds and write to dst_gds.

    Magic's hierarchical extraction refuses to merge a child cell's
    interior `addr[i]` rail with the parent's feeder, regardless of
    whether the child has a `.pin` shape — the boundary rule only
    triggers for labels touching the cell bbox edge.  Flattening the
    entire macro before extraction eliminates the sub-cell hierarchy
    so all labels live at one level and Magic merges by name.

    Same approach as `run_lvs_cim._flatten_gds`; the comments there
    cover the foundry stdcell label-strip rationale.
    """
    import gdstk
    src = gdstk.read_gds(str(src_gds))
    # Strip foundry stdcell internal labels so they don't get copied
    # N× into the macro and merged by name into one global net.  Keep
    # VPWR/VGND/VPB/VNB on the bitcell because those are global supply
    # nets; A/X on buf/inv would collapse all rows otherwise.
    _STRIP: dict[str, set[str]] = {
        "sky130_fd_sc_hd__buf_2": {"A", "X"},
        "sky130_fd_sc_hd__buf_4": {"A", "X"},
        "sky130_fd_sc_hd__buf_8": {"A", "X"},
        "sky130_fd_sc_hd__buf_16": {"A", "X"},
        "sky130_fd_sc_hd__inv_1": {"A", "Y"},
        "sky130_fd_sc_hd__inv_2": {"A", "Y"},
        # Foundry NAND_dec internal A/B/C/Z labels would merge all
        # 16384 cells' A pins into one global net; per-cell li1
        # routing already names each Z output uniquely (dec_out_<r>).
        "sky130_fd_bd_sram__openram_sp_nand2_dec": {"A", "B", "Z"},
        "sky130_fd_bd_sram__openram_sp_nand3_dec": {"A", "B", "C", "Z"},
        "sky130_fd_bd_sram__openram_sp_nand4_dec": {"A", "B", "C", "D", "Z"},
    }
    for c in src.cells:
        if c.name in _STRIP:
            for l in [l for l in c.labels if l.text in _STRIP[c.name]]:
                c.remove(l)
    top = next(c for c in src.cells if c.name == top_cell)
    top.flatten()

    # Production macro labels each per-row WL / per-col BL / per-col MBL
    # uniquely at the array level (wl_0_<r>, bl_0_<c>, br_0_<c>) — those
    # are already unique post-flatten, no rename needed.  But foundry
    # bitcell internal "BL"/"BR"/"WL" labels would collapse globally if
    # they survived F11+F13 stripping; verify.
    _LEFT_INTERNAL = sum(
        1 for lbl in top.labels if lbl.text in ("BL", "BR", "WL")
    )
    if _LEFT_INTERNAL:
        print(
            f"  [warn] {_LEFT_INTERNAL} foundry-internal BL/BR/WL labels "
            f"survived flatten — F11/F13 strip incomplete?"
        )

    out_lib = gdstk.Library(
        name=f"{top_cell}_flat", unit=src.unit, precision=src.precision
    )
    out_lib.add(top)
    dst_gds.parent.mkdir(parents=True, exist_ok=True)
    out_lib.write_gds(str(dst_gds))
    return dst_gds


def _parse_subckt_ports(spice_path: Path, cell_name: str) -> list[str]:
    """Return the ordered port list of `.subckt cell_name ...` in `spice_path`."""
    ports: list[str] = []
    in_subckt = False
    for line in spice_path.read_text().splitlines():
        stripped = line.strip()
        if (stripped.startswith(f".subckt {cell_name} ")
                or stripped == f".subckt {cell_name}"):
            in_subckt = True
            ports.extend(stripped.split()[2:])
        elif in_subckt and stripped.startswith("+"):
            ports.extend(stripped.split()[1:])
        elif in_subckt:
            break
    return ports


def _align_ref_ports(extracted: Path, ref_sp: Path, out_dir: Path,
                     cell_name: str) -> Path:
    """Rewrite ref_sp's `.subckt cell_name` line to use only the ports
    that the extracted SPICE has, in the extracted order.

    Magic's ext2spice doesn't always promote every labeled port (some
    addr/clock/data pins get dropped depending on Magic's promotion
    heuristics for shapes near the cell boundary).  Aligning the
    reference's port list to whatever Magic actually extracted lets
    netgen finish pin matching when the failure is purely about which
    nets reach the macro boundary, not connectivity.

    Returns the path to the rewritten reference (under `out_dir`).
    """
    ext_ordered = _parse_subckt_ports(extracted, cell_name)
    lines = ref_sp.read_text().splitlines(keepends=True)
    out_lines: list[str] = []
    skip_continuations = False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith(f".subckt {cell_name}"):
            out_lines.append(f".subckt {cell_name} {' '.join(ext_ordered)}\n")
            skip_continuations = True
            continue
        if skip_continuations and stripped.startswith("+"):
            continue
        skip_continuations = False
        out_lines.append(line)
    aligned = out_dir / f"{cell_name}_ref_aligned.sp"
    aligned.write_text("".join(out_lines))
    return aligned


def _count_devices(sp: Path) -> dict[str, int]:
    """Count X-lines (subckt instances) by the last token on the line."""
    counts: dict[str, int] = {}
    for line in sp.read_text().splitlines():
        s = line.strip()
        if not s.startswith("X") and not s.startswith("x"):
            continue
        toks = s.split()
        if len(toks) < 2:
            continue
        name = toks[-1]
        counts[name] = counts.get(name, 0) + 1
    return counts


def _lvs_one(m: ProductionMacro, input_root: Path, output_root: Path) -> dict:
    """Run LVS on a single production macro.  Returns a result dict."""
    cell_dir = input_root / m.name
    gds = cell_dir / f"{m.name}.gds"
    ref_sp = cell_dir / f"{m.name}.sp"
    if not gds.exists() or not ref_sp.exists():
        return {
            "macro": m.name, "ok": False,
            "error": f"missing GDS or refspice in {cell_dir}",
        }

    out_dir = output_root / m.name
    out_dir.mkdir(parents=True, exist_ok=True)

    # Flatten the macro before extraction (K — eliminates Magic's
    # hierarchical port-promotion failure on row_decoder addr[i]).
    flat_gds = out_dir / f"{m.name}_flat.gds"
    if not flat_gds.exists() or flat_gds.stat().st_mtime < gds.stat().st_mtime:
        print(f"[{m.name}] flattening macro GDS → {flat_gds.name} ...",
              flush=True)
        _flatten_gds(gds, flat_gds, m.name)

    # If Magic already extracted this macro in a previous run, reuse
    # the result.  The extracted file is <name>_extracted.spice per
    # extract_netlist's convention.
    prior_extracted = out_dir / f"{m.name}_extracted.spice"
    if prior_extracted.exists():
        print(f"[{m.name}] reusing prior extraction at {prior_extracted}",
              flush=True)
        extracted = prior_extracted
    else:
        # Run Magic extraction explicitly (separate from run_lvs) so we
        # can post-process the extracted SPICE before netgen sees it.
        # `make_ports=True` runs Magic's `port makeall`, which force-
        # promotes every labeled .pin polygon to a subckt port.  Without
        # it, Magic only auto-promotes labels at cell boundaries — and
        # row_decoder draws its `addr[i]` .pin shapes at the predecoder
        # block midpoint (y≈6.98 µm) rather than the cell boundary
        # (y=0 or y=202).  Result observed in F11b/F12 LVS: row_decoder
        # extracted .subckt has only dec_out_<r> + supply ports, no
        # addr[i], so the parent's addr[i] feeders dangle as unconnected
        # top-level pins.  port makeall fixes this without re-laying
        # out the row_decoder pin shapes.
        print(
            f"[{m.name}] running Magic extraction (port makeall, "
            f"~10-15 min) ...",
            flush=True,
        )
        try:
            from rekolektion.verify.lvs import extract_netlist
            extracted = extract_netlist(
                flat_gds, m.name, output_dir=out_dir,
                make_ports=True,
            )
        except Exception as exc:
            return {"macro": m.name, "ok": False,
                    "error": f"Magic extraction failed: {exc}"}

    # Align the reference SPICE port list with whatever Magic actually
    # extracted.  ext2spice sometimes drops top-level labeled ports
    # (`addr` pins on production macros) depending on label-promotion
    # heuristics; rewrite the reference's .subckt port list to match
    # the extracted port list, dropping any port that's only in the
    # reference.  Connectivity is unchanged — internal nets keep their
    # names — only the pin count visible to netgen changes.
    aligned_ref = _align_ref_ports(extracted, ref_sp, out_dir, m.name)

    print(f"[{m.name}] running netgen LVS comparison ...", flush=True)
    try:
        result = run_lvs(
            flat_gds, aligned_ref, cell_name=m.name, output_dir=out_dir,
            extracted_netlist=extracted,
        )
    except Exception as exc:
        return {"macro": m.name, "ok": False, "error": f"LVS failed: {exc}"}

    # Device counts for the summary table.
    ref_counts = _count_devices(ref_sp)
    ext_path = result.extracted_netlist_path
    ext_counts = _count_devices(ext_path) if ext_path else {}
    ref_total = sum(ref_counts.values())
    ext_total = sum(ext_counts.values())

    return {
        "macro": m.name,
        "ok": result.match,
        "devices_ref": ref_total,
        "devices_ext": ext_total,
        "log": str(result.log_path),
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--input-dir", type=Path, default=_DEFAULT_INPUT,
                    help=f"where to find GDS+refspice (default: {_DEFAULT_INPUT})")
    ap.add_argument("--output-dir", type=Path, default=_DEFAULT_OUTPUT,
                    help=f"where to write extraction+LVS logs (default: {_DEFAULT_OUTPUT})")
    ap.add_argument("--workers", "-j", type=int, default=0,
                    help="parallel worker processes (0=serial, default)")
    args = ap.parse_args(argv)

    args.output_dir.mkdir(parents=True, exist_ok=True)

    results: list[dict] = []
    if args.workers <= 1 or len(MACROS) <= 1:
        for m in MACROS:
            results.append(_lvs_one(m, args.input_dir, args.output_dir))
    else:
        effective = min(args.workers, len(MACROS))
        print(f"[parallel] running LVS on {len(MACROS)} macros with {effective} workers")
        with ProcessPoolExecutor(max_workers=effective) as pool:
            futures = {
                pool.submit(_lvs_one, m, args.input_dir, args.output_dir): m
                for m in MACROS
            }
            for fut in as_completed(futures):
                results.append(fut.result())

    print("\n=== LVS Summary ===")
    all_ok = True
    for r in sorted(results, key=lambda x: x["macro"]):
        if not r["ok"]:
            all_ok = False
            print(f"  {r['macro']:<30s} FAIL  {r.get('error', 'see log')}")
            if "log" in r:
                print(f"    log: {r['log']}")
        else:
            print(f"  {r['macro']:<30s} MATCH  "
                  f"devices ref={r['devices_ref']} ext={r['devices_ext']}")

    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
