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


# Audit-2026-05-03 / task #64: explicit allow-lists for what the CIM
# aligner is permitted to reconcile.  Each entry documents *one*
# specific known disagreement between the source reference SPICE and
# Magic's extract for CIM macros.  Anything outside these patterns
# means a new disagreement appeared; that should be investigated, not
# silently absorbed.
#
# Drops (in source ref but missing from extract): the Magic ext2spice
# port-promotion-through-hierarchy limitation documented in commit
# b09c441 / CLAUDE.md "Known traps".  MWL_EN[r] labels exist correctly
# inside cim_mwl_driver_col_<rows>; standalone extract sees them as
# ports; macro-top hierarchical extract drops them regardless of
# parent .pin shape placement.
#
# Adds (in extract but not in source ref): legacy macro-top MBL_<c>
# strap labels that Magic finds in the layout.  Older versions of the
# generator emitted them; current generator may or may not (the
# aligner accepts the difference either way).  Folding them in keeps
# LVS topology comparison consistent.
_CIM_ALLOWED_DROPS = re.compile(r"^MWL_EN\[\d+\]$")
_CIM_ALLOWED_ADDS = re.compile(r"^MBL_\d+$")


def _check_alignment_drift(
    src_ports: list[str], ext_ports: list[str], cell_name: str,
) -> None:
    """Compare source-ref vs extracted port lists; fail on any
    disagreement outside the documented CIM allow-lists.

    Raises SystemExit with a precise diagnostic so an unexpected drift
    cannot be silently absorbed by the aligner.
    """
    src_set, ext_set = set(src_ports), set(ext_ports)
    dropped = src_set - ext_set
    added = ext_set - src_set
    bad_drops = [p for p in sorted(dropped) if not _CIM_ALLOWED_DROPS.match(p)]
    bad_adds = [p for p in sorted(added) if not _CIM_ALLOWED_ADDS.match(p)]
    if bad_drops or bad_adds:
        msg = [
            f"[{cell_name}] aligner drift outside documented allow-lists.",
            f"  Source ref has {len(src_set)} ports; extract has {len(ext_set)}.",
        ]
        if bad_drops:
            msg.append(f"  UNEXPECTED DROPS (in ref, missing from extract): {bad_drops}")
            msg.append(f"  Allowed-drop pattern: {_CIM_ALLOWED_DROPS.pattern}")
        if bad_adds:
            msg.append(f"  UNEXPECTED ADDS (in extract, missing from ref): {bad_adds}")
            msg.append(f"  Allowed-add pattern: {_CIM_ALLOWED_ADDS.pattern}")
        msg.append(
            "Investigate before extending the allow-list.  See "
            "audit/hack_inventory.md A1 + task #64 + CLAUDE.md."
        )
        raise SystemExit("\n".join(msg))


def _align_ref_ports(extracted: Path, ref_sp: Path, out_dir: Path,
                     cell_name: str) -> Path:
    """Rewrite ref_sp's `.subckt cell_name` line to use only the ports
    that the extracted SPICE has, in the extracted order.  Returns the
    path to the rewritten reference (under `out_dir`).

    KNOWN TRAP — DO NOT "FIX" BY ADDING LABELS TO THE MACRO TOP.

    Magic's ext2spice port-promotion through hierarchy is broken in
    this codebase.  E.g. `MWL_EN[r]` labels exist correctly inside
    `cim_mwl_driver_col_<rows>` (layer 67/5, li1) — standalone extract
    finds 64 ports — but macro-top hierarchical extract drops them.
    Adding macro-top `.pin` shapes + labels at matching coordinates
    does NOT make Magic promote them.  This was tried in commit
    b09c441 (F12) and the fix was flat extraction; that was reverted
    in a97f56f because flat hides issue #7 per-cell drain floats.

    See `audit/hack_inventory.md` entry A1, tasks #64 and #110, and
    `CLAUDE.md` "Known traps" section before touching this.

    Audit 2026-05-03: the rewrite is now guarded by an explicit
    allow-list (`_CIM_ALLOWED_DROPS`, `_CIM_ALLOWED_ADDS`).  Any port
    disagreement outside the documented patterns aborts the run with
    a precise diagnostic — silently absorbing new drift is no longer
    possible.
    """
    ext_ordered = _parse_subckt_ports(extracted, cell_name)
    src_ordered = _parse_subckt_ports(ref_sp, cell_name)
    _check_alignment_drift(src_ordered, ext_ordered, cell_name)
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


def _preflight_disk_space(output_root: Path, variant: str, rows: int) -> None:
    """Abort early if free disk space looks insufficient for extraction.

    Magic ext2spice writes per-cell `.ext` files into `output_root/<top
    cell name>/`.  Observed sizes on this codebase (2026-05-03):
      - 64-row CIM variant (SRAM-D):  cim_array.ext ≈ 220 MB
      - 256-row CIM variant (SRAM-A): would be ~4× ≈ 0.9 GB per macro
    Plus per-cell intermediates, hierarchical extracts, and netgen
    output.  Budget ~2 GB per 256-row macro and ~0.5 GB per 64-row.
    """
    import shutil
    free_gb = shutil.disk_usage(output_root if output_root.exists() else
                                output_root.parent).free / 1e9
    needed_gb = 2.0 if rows >= 128 else 0.5
    if free_gb < needed_gb:
        raise SystemExit(
            f"[{variant}] insufficient disk: {free_gb:.2f} GB free, "
            f"need ~{needed_gb:.1f} GB for {rows}-row variant extraction. "
            f"Magic .ext files for a 256-row macro can exceed 1 GB. "
            f"Free space (e.g. `rm -rf output/lvs_cim/cim_sram_*/*.ext` "
            f"for old runs) before retrying."
        )


def _lvs_one(variant: str, input_root: Path, output_root: Path) -> dict:
    p = CIMMacroParams.from_variant(variant)
    _preflight_disk_space(output_root, variant, p.rows)
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
    # Flatten CIM's intermediate hierarchical subckts on both sides so
    # netgen's instance-prefix naming aligns.  Without this, the layout's
    # `cim_array_<v>_<rxc>_0/bl_0_<c>` doesn't match the reference's
    # `bl_0_<c>` (or vice versa) when one side flattens and the other
    # doesn't.  Both circuits have these subckts in the hierarchy, so
    # explicit pre-comparison flatten produces matching prefixes.
    cim_extra_flatten = [
        f"cim_array_{p.variant.lower().replace('-', '_')}_{p.rows}x{p.cols}",
        f"cim_mbl_precharge_row_{p.cols}",
        f"cim_mbl_sense_row_{p.cols}",
        f"cim_mwl_driver_col_{p.rows}",
    ]
    result = run_lvs(
        gds_path=gds,
        schematic_path=aligned_ref,
        cell_name=p.top_cell_name,
        output_dir=out_dir,
        extracted_netlist=extracted,
        netgen_timeout=netgen_timeout,
        extra_flatten_cells=cim_extra_flatten,
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
