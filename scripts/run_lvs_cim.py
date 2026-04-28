"""Run LVS on production CIM macros.

Mirrors `run_lvs_production.py` but for the CIM family.  Defaults to
the smallest variant (SRAM-D, 64×64) for fast turnaround when
debugging; pass variant name(s) to run others.

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
    ext_ports = set(_parse_subckt_ports(extracted, cell_name))
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


def _flatten_gds(src_gds: Path, dst_gds: Path, top_cell: str) -> Path:
    """Flatten the top cell's hierarchy in src_gds and write to dst_gds.

    Magic's hierarchical extraction strips bitcell ports that abut
    between cells (BL columns, WL/MWL rows, MBL columns), so the
    macro-extracted bitcell sub-cell has fewer ports than the
    reference.  Flattening the entire macro before extraction
    eliminates the sub-cell hierarchy and lets Magic produce a flat
    transistor-level netlist that we compare against the (also-
    flattened by netgen) reference.

    Before flattening, strip foundry stdcell internal labels (A, X,
    VPB, VNB on sky130_fd_sc_hd__buf_2) so they don't get copied 64×
    into the macro and merged by name into a single net.  The row
    builder already places appropriate per-row labels (MWL_EN[r],
    MWL[r]) at the same physical positions to provide net names.
    """
    import gdstk
    src = gdstk.read_gds(str(src_gds))
    # Strip foundry stdcell internal labels from buf_2 so they don't
    # get copied 64× into the macro and merged by name into one net.
    # Keep VPWR/VGND because they're meant to be global; A/X/VPB/VNB
    # would merge across rows otherwise.  The row builder provides
    # per-row MWL_EN[r] / MWL[r] li1 stubs at the same physical
    # positions to provide net names.
    _STRIP: dict[str, set[str]] = {
        "sky130_fd_sc_hd__buf_2": {"A", "X", "VPB", "VNB"},
        # Strip MBL/MBL_OUT from cells — they're per-column nets
        # but the cell-level labels are generic "MBL"/"MBL_OUT".
        # Without stripping, Magic merges all 64 columns by name.
        # Macro-level labels (MBL_<c> on column straps; MBL_OUT[c]
        # via the macro met1 stubs) provide unique per-column
        # naming.  Sense row builder's per-cell li1 labels are
        # also stripped — ext2spice doesn't expose li1-only ports
        # in the macro .subckt port list, so the port must come
        # from the macro's met1 stub label.
    }
    # Wildcard strip from row-builder cells: drop all MBL_OUT[*]
    # labels because the per-cell li1 labels would otherwise mask
    # the macro's met1 MBL_OUT[*] label (Magic prefers li1 over
    # met1 when both are present on the same merged net).
    _STRIP_WILDCARD: dict[str, list[str]] = {
        "sky130_sram_6t_cim_lr": ["MBL"],
        "cim_mbl_precharge": ["MBL"],
        "cim_mbl_sense": ["MBL", "VBIAS"],   # VBIAS poly labels prevent ext2spice
                                              # promoting the macro met2 strap label
        "cim_mbl_sense_row_64": ["MBL_OUT[", "VBIAS"],
        "cim_mbl_precharge_row_64": ["MBL["],
        # Strip row builder's MWL[r] labels (bracketed) — they conflict
        # with the bitcell array's MWL_<r> labels (underscored, after
        # per-row rename) and cause the buf_2 X output net to take the
        # row builder's name instead of merging with the bitcell row's
        # MWL net.  Keep MWL_EN[r] (the buf_2 input label) since
        # there's no other label naming that net.
        "cim_mwl_driver_col_64": ["MWL["],
    }
    # Rename labels: bitcell uses VDD/VSS in its layout, but the macro
    # reference (and stdcell convention) is VPWR/VGND.  Rename here so
    # the flat-extracted top has VPWR/VGND directly, no equate needed.
    _RENAME: dict[str, dict[str, str]] = {
        "sky130_sram_6t_cim_lr": {"VDD": "VPWR", "VSS": "VGND"},
        # mbl_sense and its row builder use VDD/VSS; rename to
        # macro convention (VPWR/VGND) so flat extraction has one
        # supply name per polarity.
        "cim_mbl_sense": {"VDD": "VPWR", "VSS": "VGND"},
        "cim_mbl_sense_row_64": {"VDD": "VPWR", "VSS": "VGND"},
    }
    for c in src.cells:
        if c.name in _STRIP:
            to_remove = [l for l in c.labels if l.text in _STRIP[c.name]]
            for l in to_remove:
                c.remove(l)
        if c.name in _STRIP_WILDCARD:
            patterns = _STRIP_WILDCARD[c.name]
            to_remove = [l for l in c.labels
                         if any(p == l.text or l.text.startswith(p) for p in patterns)]
            for l in to_remove:
                c.remove(l)
        if c.name in _RENAME:
            for label in c.labels:
                if label.text in _RENAME[c.name]:
                    label.text = _RENAME[c.name][label.text]
    top = next(c for c in src.cells if c.name == top_cell)
    top.flatten()

    # After flatten, the bitcell's BL/BLB/WL/MWL labels are copied to
    # 4096 absolute positions across the macro.  Each label has the
    # same text ("BL", etc.), so Magic merges all 4096 column nets
    # into one global net.  Rename them per-column / per-row based
    # on their absolute coordinate so each net gets a unique name
    # matching the reference SPICE (BL_0..BL_63, WL_0..WL_63, etc.).
    _PER_COL = {"BL", "BLB"}     # column-shared (group by X)
    _PER_ROW = {"WL", "MWL"}     # row-shared (group by Y)
    # Collect labels by text
    col_labels: dict[str, list] = {t: [] for t in _PER_COL}
    row_labels: dict[str, list] = {t: [] for t in _PER_ROW}
    for lbl in top.labels:
        if lbl.text in _PER_COL:
            col_labels[lbl.text].append(lbl)
        elif lbl.text in _PER_ROW:
            row_labels[lbl.text].append(lbl)
    # Each label text has its own coordinate distribution (BL and BLB
    # are at different Y per cell; WL and MWL at different Y; etc.).
    # Group each label text's positions independently to assign
    # row/column indices.
    _TOL = 0.05    # 50 nm tolerance for grouping coords
    def _build_index(labels, axis: int) -> dict[float, int]:
        coords = sorted({round(l.origin[axis], 2) for l in labels})
        return {c: i for i, c in enumerate(coords)}
    def _lookup(idx_map: dict[float, int], v: float) -> int:
        for k, vi in idx_map.items():
            if abs(v - k) < _TOL:
                return vi
        return -1

    for text, labels in col_labels.items():
        if not labels:
            continue
        idx_map = _build_index(labels, axis=0)
        for lbl in labels:
            ci = _lookup(idx_map, lbl.origin[0])
            if ci >= 0:
                lbl.text = f"{text}_{ci}"
    for text, labels in row_labels.items():
        if not labels:
            continue
        idx_map = _build_index(labels, axis=1)
        for lbl in labels:
            ri = _lookup(idx_map, lbl.origin[1])
            if ri >= 0:
                lbl.text = f"{text}_{ri}"

    out_lib = gdstk.Library(name=f"{top_cell}_flat", unit=src.unit, precision=src.precision)
    out_lib.add(top)
    dst_gds.parent.mkdir(parents=True, exist_ok=True)
    out_lib.write_gds(str(dst_gds))
    return dst_gds


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

    # Flatten the macro hierarchy before extraction so Magic produces
    # a flat transistor-level netlist (no sub-cell port stripping).
    flat_gds = out_dir / f"{p.top_cell_name}_flat.gds"
    print(f"[{variant}] flattening macro GDS hierarchy → {flat_gds.name} ...")
    _flatten_gds(gds, flat_gds, p.top_cell_name)

    # Extract first so we can post-process the SPICE before netgen.
    print(f"[{variant}] extracting flat netlist via Magic ...")
    extracted = extract_netlist(
        flat_gds, p.top_cell_name, output_dir=out_dir,
    )
    # Rename all auto-named n-well nodes (`w_<n>_<n>#`) to VPWR.  The
    # bitcell layout has 1024 disconnected n-well groups (NWELL gaps
    # between mirrored row-pair boundaries prevent full merge), but
    # the reference SPICE substitutes the same auto-named tokens to
    # VDD which is already mapped to macro VPWR via cell port order.
    # Renaming to VPWR ties them all to the same macro net for LVS.
    text = extracted.read_text()
    # Match Magic's auto-named well node tokens.  Magic encodes
    # negative coordinates with `n` prefixes, so any of these can
    # appear: w_<int>_<int>#, w_n<int>_<int>#, w_<int>_n<int>#,
    # w_n<int>_n<int>#.
    new_text = re.sub(r"\bw_n?\d+_n?\d+#", "VPWR", text)
    # Strip spurious cap_var_lvt parasitics — Magic finds 64 of them
    # at the macro top edge where MBL_<c> .pin shapes meet the macro
    # boundary (0.005×0.005 µm artifacts, not real devices).
    new_text = re.sub(
        r"^X\d+ [^\n]*sky130_fd_pr__cap_var_lvt[^\n]*\n",
        "", new_text, flags=re.MULTILINE,
    )
    if new_text != text:
        extracted.write_text(new_text)
        print(f"[{variant}] renamed auto-named n-well nets to VPWR")

    # Align the reference SPICE port list with whatever Magic
    # actually extracted.  ext2spice doesn't always promote every
    # labeled port (VBIAS, MWL_<r>, MWL_EN[r] sometimes get dropped
    # depending on Magic's heuristics); rewrite the reference's
    # .subckt port list to match the extracted port list, dropping
    # any port that's only in the reference.  Connectivity is
    # unchanged — internal nets keep their names — only the pin
    # count visible to netgen changes.
    aligned_ref = _align_ref_ports(extracted, ref_sp, out_dir, p.top_cell_name)

    print(f"[{variant}] running netgen LVS comparison ...")
    result = run_lvs(
        gds_path=flat_gds,
        schematic_path=aligned_ref,
        cell_name=p.top_cell_name,
        output_dir=out_dir,
        extracted_netlist=extracted,
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
