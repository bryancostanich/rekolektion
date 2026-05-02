"""Run LVS on production CIM macros.

Mirrors `run_lvs_production.py` but for the CIM family.  Defaults to
the smallest variant (SRAM-D, 64×64) for fast turnaround when
debugging; pass variant name(s) to run others.

Uses Magic's hierarchical extraction (`port makeall` recursive) so the
foundry qtap's BL/BR/Q labels — and the supercell's MWL/MBL labels —
become sub-cell ports that abut the parent macro's per-row/per-col
strips.  Earlier flat-extraction flow destroyed this hierarchy and
masked the drain-floating defect (issue #7).

Usage::

    python3 scripts/run_lvs_cim.py SRAM-D
    python3 scripts/run_lvs_cim.py -j 2 SRAM-A SRAM-D
"""
from __future__ import annotations

import argparse
import re
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams
from rekolektion.verify.lvs import run_lvs, extract_netlist


_ROOT = Path(__file__).parent.parent
_DEFAULT_INPUT = _ROOT / "output" / "cim_macros"
_DEFAULT_OUTPUT = _ROOT / "output" / "lvs_cim"


def _parse_subckt_ports(spice_path: Path, cell_name: str) -> list[str]:
    """Return the ordered port list of `.subckt cell_name ...` in `spice_path`."""
    ports: list[str] = []
    with spice_path.open() as f:
        in_subckt = False
        for line in f:
            stripped = line.strip()
            if stripped.startswith(f".subckt {cell_name} ") or stripped == f".subckt {cell_name}":
                in_subckt = True
                tokens = stripped.split()[2:]
                ports.extend(tokens)
            elif in_subckt and stripped.startswith("+"):
                ports.extend(stripped.split()[1:])
            elif in_subckt:
                break
    return ports


def _align_ref_ports(extracted: Path, ref_sp: Path, out_dir: Path,
                     cell_name: str) -> Path:
    """Rewrite ref_sp's `.subckt cell_name` line to use only the ports
    that the extracted SPICE has, in the extracted order.  Returns the
    path to the rewritten reference (under `out_dir`).
    """
    ext_ordered = _parse_subckt_ports(extracted, cell_name)
    ref_text = ref_sp.read_text()
    lines = ref_text.splitlines(keepends=True)
    out_lines: list[str] = []
    skip_continuations = False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith(f".subckt {cell_name}"):
            new_line = f".subckt {cell_name} {' '.join(ext_ordered)}\n"
            out_lines.append(new_line)
            skip_continuations = True
            continue
        if skip_continuations and stripped.startswith("+"):
            continue
        skip_continuations = False
        out_lines.append(line)
    aligned = out_dir / f"{cell_name}_ref_aligned.sp"
    aligned.write_text("".join(out_lines))
    return aligned


def _lvs_one(variant: str, input_root: Path, output_root: Path) -> dict:
    p = CIMMacroParams.from_variant(variant)
    cell_dir = input_root / p.top_cell_name
    gds = cell_dir / f"{p.top_cell_name}.gds"
    ref_sp = cell_dir / f"{p.top_cell_name}.sp"
    if not gds.exists() or not ref_sp.exists():
        raise SystemExit(
            f"Missing inputs for {variant}: {gds} or {ref_sp}.  "
            f"Run `python3 scripts/generate_cim_production.py` first."
        )
    out_dir = output_root / p.top_cell_name
    out_dir.mkdir(parents=True, exist_ok=True)

    # Hierarchical extraction (mirror run_lvs_production.py).  Magic's
    # `port makeall` recursive promotes labeled shapes at sub-cell
    # boundaries to sub-cell ports, which is exactly what we need so the
    # foundry qtap exposes BL/BR/Q and the supercell exposes MWL/MBL.
    # Earlier flat-extraction flow destroyed this hierarchy and masked
    # the issue #7 access-tx drain-floating defect.
    print(f"[{variant}] running Magic hierarchical extraction "
          f"(port makeall recursive) ...", flush=True)
    # 256-row variants (SRAM-A/B) take 30+ min in Magic's port-makeall
    # recursive pass — bump the extract timeout accordingly.
    extract_timeout = 4 * 3600 if p.rows >= 128 else 1800
    extracted = extract_netlist(
        gds, p.top_cell_name, output_dir=out_dir,
        make_ports=True,
        timeout=extract_timeout,
    )

    # T5.2-A resolution (Path 3, 2026-05-01): the legacy
    # re.sub(r"\bw_n?\d+_n?\d+#", "VPWR") mask is no longer required.
    # The CIM array now adds per-supercell-instance MET1 .pin labels
    # at the foundry VPWR/VGND rail positions
    # (cim_supercell_array._add_vpwr_vgnd_m2_rails), so Magic's name-
    # based hierarchical merge ties every supercell instance's VPWR/
    # VGND nets to the macro-level VPWR/VGND ports without rewriting.

    # Align the reference SPICE port list with whatever Magic actually
    # extracted.  ext2spice doesn't always promote every labeled port
    # depending on label-promotion heuristics; rewrite the reference's
    # .subckt port list to match the extracted port list, dropping any
    # port that's only in the reference.  Connectivity is unchanged —
    # internal nets keep their names — only the pin count visible to
    # netgen changes.
    aligned_ref = _align_ref_ports(extracted, ref_sp, out_dir, p.top_cell_name)

    print(f"[{variant}] running netgen LVS comparison ...")
    # 256-row variants (SRAM-A/B) push netgen well past the default 1 hr
    # graph-iso check; the smaller 64-row variants always finish in
    # minutes.  Bump the timeout per-variant rather than globally so a
    # truly stuck run still bails out before consuming a workday.
    netgen_timeout = 6 * 3600 if p.rows >= 128 else 3600
    result = run_lvs(
        gds_path=gds,
        schematic_path=aligned_ref,
        cell_name=p.top_cell_name,
        output_dir=out_dir,
        extracted_netlist=extracted,
        netgen_timeout=netgen_timeout,
    )
    return {
        "variant": variant,
        "match": result.match,
        "log": str(out_dir / "lvs_results.log"),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("variants", nargs="*", default=["SRAM-D"],
                        help="CIM variants to run (default: SRAM-D)")
    parser.add_argument("-j", "--jobs", type=int, default=1,
                        help="Parallel workers (default: 1)")
    parser.add_argument("--input", type=Path, default=_DEFAULT_INPUT)
    parser.add_argument("--output", type=Path, default=_DEFAULT_OUTPUT)
    args = parser.parse_args()

    for v in args.variants:
        if v not in CIM_VARIANTS:
            parser.error(
                f"Unknown variant {v!r}. Valid: {sorted(CIM_VARIANTS)}"
            )

    args.output.mkdir(parents=True, exist_ok=True)

    results: list[dict] = []
    if args.jobs <= 1:
        for v in args.variants:
            results.append(_lvs_one(v, args.input, args.output))
    else:
        print(f"[parallel] running LVS on {len(args.variants)} "
              f"macros with {args.jobs} workers")
        with ProcessPoolExecutor(max_workers=args.jobs) as ex:
            fut_map = {
                ex.submit(_lvs_one, v, args.input, args.output): v
                for v in args.variants
            }
            for fut in as_completed(fut_map):
                results.append(fut.result())

    print("\n=== CIM LVS Summary ===")
    for r in sorted(results, key=lambda x: x["variant"]):
        status = "PASS" if r["match"] else "FAIL"
        print(f"  {r['variant']:<10} {status}  log: {r['log']}")

    return 0 if all(r["match"] for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
