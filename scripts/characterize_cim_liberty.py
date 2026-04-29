"""SPICE characterisation harness for the CIM macro Liberty file.

Generates an ngspice testbench around a CIM SRAM macro, runs a transient
sim, and emits a JSON result file with the analog Liberty surrogates
(V_quiescent, V_compute, V_swing, t_settle) plus the input config so
downstream tools (`cim_liberty_generator.py`) can build NLDM tables.

PHASE 1 SCOPE (per track 05 audit plan):

- Parametrised write pattern (all_zero / all_one / alt_col / alt_row /
  checker / random) with pattern-aware grouped writes — rows that share
  the same column pattern are written in a single WL pulse with a shared
  BL/BLB drive; rows with unique column patterns serialise.  Random
  patterns approach one-pulse-per-row; uniform / periodic patterns
  collapse to 1 or 2 pulses.
- Parametrised corner triple `(model, vpwr, temp)` — TT/1.80/25,
  SS/1.60/-40, FF/1.95/125 are the standard SKY130 sign-off corners.
  Corner drives the `.lib` model selection, the Vpwr source value, and
  the `.temp` directive in the testbench.
- Parametrised input slew (MWL_EN edge rise time) and output load
  (MBL_OUT capacitance) for NLDM grid characterisation.
- Adaptive `FIND AT=` sample ladder: 30 log-spaced time points from
  compute-phase start to t_compute + 25 ns; Python interpolates
  t_settle against the (V_quiescent + V_compute)/2 midpoint without
  needing to know V_quiescent ahead of time.
- Per-sim JSON output `output/spice_char/<macro>/<slug>.json` with
  schema versioning, atomic write, RESPIN flag if v_swing < 50 mV.
- Bias-sensitivity sub-study harness — 3x3 VBIAS/VREF grid at TT/25
  for the audit report's macro→chip-top contract numbers.

JSON SCHEMA (v1):

    {
      "schema_version": 1,
      "variant": "SRAM-D",
      "config": {
        "pattern": "all_one",
        "corner": {"model": "tt", "vpwr": 1.80, "temp_c": 25.0},
        "slew_ps": 100.0, "load_fF": 10.0,
        "active_rows": [0], "measure_cols": [0],
        "seed": 0, "vbias": 0.7, "vref": 1.5
      },
      "result": {
        "ok": true,
        "v_quiescent": 1.7234,
        "v_compute":   {"0": 1.1660},
        "v_swing":     {"0": 0.5574},
        "t_settle_ns": {"0": 0.250},
        "samples":     {"0": [[5.05, 1.72], [5.10, 1.65], ...]},
        "flags": []
      },
      "tb_path": "tb.sp", "log_path": "ngspice.log",
      "wallclock_s": 1680.5
    }

CLI:

    # Single point (defaults: pattern=all_one, corner=tt_25, slew=100ps,
    # load=10fF, active_rows=[0], measure_cols=[0]):
    python3 scripts/characterize_cim_liberty.py SRAM-D

    # Pattern sweep at TT/25 (per phase-1 plan):
    python3 scripts/characterize_cim_liberty.py SRAM-D --pattern-sweep

    # Single point overriding individual params:
    python3 scripts/characterize_cim_liberty.py SRAM-D \
        --pattern random --seed 42 --corner ss_cold \
        --slew-ps 200 --load-ff 50

    # NLDM grid (16 slew/load × 3 corners — overnight):
    python3 scripts/characterize_cim_liberty.py SRAM-D --nldm-grid

    # Bias sensitivity (3x3 VBIAS/VREF at TT/25):
    python3 scripts/characterize_cim_liberty.py SRAM-D --bias-sweep

PRESERVED ngspice CONVENTIONS:

- The 6T cross-coupled inverter Q/QB nodes can't be DC-resolved
  without a write phase; the WRITE phase pulses WL with BL/BLB driven
  to latch a known Q.  We keep this approach (vs `.ic` injection) so
  the write port is electrically exercised as a side-benefit.
- BL/BLB shared across rows means non-uniform-by-row patterns (alt_row,
  checker, random) need sequential WL pulses with BL/BLB transitioning
  between groups.  The grouping logic collapses identical column
  patterns to a single pulse.
- `wrdata` trace dump is intentionally NOT used — it slows ngspice past
  the 1-hour timeout on 256-row variants.  The threshold ladder + Python
  interpolation gets ~50 ps t_settle precision, which is fine for
  8-30 ns Liberty arcs.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import random
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Callable

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from rekolektion.bitcell.sky130_6t_lr_cim import CIM_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams


SCHEMA_VERSION = 1

_PDK_NGSPICE = Path(
    "~/.volare/volare/sky130/versions/0fe599b2afb6708d281543108caf8310912f54af/sky130B/libs.tech/ngspice/sky130.lib.spice"
).expanduser()
_OUT_ROOT = Path("output/spice_char")

# Stop-condition threshold from track 05 audit plan.
RESPIN_V_SWING_FLOOR = 0.050  # 50 mV minimum sense margin


# ---------------------------------------------------------------------------
# Corners — (model, vpwr, temp) triples.  Vpwr derate per SKY130 sign-off.
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Corner:
    model: str          # tt / ss / ff / sf / fs (model selector in .lib)
    vpwr: float         # supply voltage in V
    temp_c: float       # temperature in C

    @property
    def slug(self) -> str:
        v_str = f"{self.vpwr:.2f}".replace(".", "p")
        t_int = int(round(self.temp_c))
        return f"{self.model}_{v_str}_{t_int:+d}"


CORNER_TT_25 = Corner(model="tt", vpwr=1.80, temp_c=25.0)
CORNER_SS_COLD = Corner(model="ss", vpwr=1.60, temp_c=-40.0)
CORNER_FF_HOT = Corner(model="ff", vpwr=1.95, temp_c=125.0)

CORNER_BY_NAME: dict[str, Corner] = {
    "tt_25": CORNER_TT_25,
    "ss_cold": CORNER_SS_COLD,
    "ff_hot": CORNER_FF_HOT,
}


# ---------------------------------------------------------------------------
# Patterns — pattern_fn(rows, cols, seed) -> 2D list of 0/1
# ---------------------------------------------------------------------------

def _pat_all_zero(r, c, seed=0):
    return [[0] * c for _ in range(r)]

def _pat_all_one(r, c, seed=0):
    return [[1] * c for _ in range(r)]

def _pat_alt_col(r, c, seed=0):
    return [[col % 2 for col in range(c)] for _ in range(r)]

def _pat_alt_row(r, c, seed=0):
    return [[row % 2] * c for row in range(r)]

def _pat_checker(r, c, seed=0):
    return [[(row + col) % 2 for col in range(c)] for row in range(r)]

def _pat_random(r, c, seed=0):
    rng = random.Random(seed)
    return [[rng.randint(0, 1) for _ in range(c)] for _ in range(r)]


PATTERNS: dict[str, Callable[[int, int, int], list[list[int]]]] = {
    "all_zero": _pat_all_zero,
    "all_one": _pat_all_one,
    "alt_col": _pat_alt_col,
    "alt_row": _pat_alt_row,
    "checker": _pat_checker,
    "random": _pat_random,
}


def group_rows_by_col_pattern(
    weights: list[list[int]],
) -> list[tuple[tuple[int, ...], list[int]]]:
    """Collapse rows with identical column patterns into one write group.

    Uniform patterns (all_zero/all_one/alt_col): 1 group → 1 WL pulse.
    Periodic-by-row (alt_row/checker): 2 groups → 2 pulses.
    Random: ~N groups → effectively per-row sequential.
    """
    groups: dict[tuple[int, ...], list[int]] = {}
    for r, row in enumerate(weights):
        key = tuple(row)
        groups.setdefault(key, []).append(r)
    return sorted(groups.items(), key=lambda kv: kv[1][0])


# ---------------------------------------------------------------------------
# SimConfig — one characterisation point.
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class SimConfig:
    variant: str
    pattern: str = "all_one"
    corner: Corner = CORNER_TT_25
    slew_ps: float = 100.0          # MWL_EN edge rise
    load_fF: float = 10.0           # MBL_OUT capacitive load
    active_rows: tuple[int, ...] = (0,)
    measure_cols: tuple[int, ...] = (0,)
    seed: int = 0
    vbias: float = 0.7
    vref: float = 1.5
    probe_internals: bool = False    # diagnostic: probe internal cell nodes

    @property
    def slug(self) -> str:
        rows_slug = (
            "all" if len(self.active_rows) > 8
            else "_".join(str(r) for r in self.active_rows)
        )
        cols_slug = (
            "all" if len(self.measure_cols) > 4
            else "_".join(str(c) for c in self.measure_cols)
        )
        return (
            f"{self.pattern}__{self.corner.slug}"
            f"__slew{int(round(self.slew_ps))}ps"
            f"__load{int(round(self.load_fF))}fF"
            f"__rows{rows_slug}__cols{cols_slug}__seed{self.seed}"
        )


# ---------------------------------------------------------------------------
# Adaptive sample ladder — 30 log-spaced points spanning compute window.
# ---------------------------------------------------------------------------

_LADDER_N = 30
_LADDER_DT_MIN_NS = 0.05
_LADDER_DT_MAX_NS = 25.0


def _ladder_offsets_ns() -> list[float]:
    """Geometric spacing from DT_MIN to DT_MAX with N points."""
    ratio = (_LADDER_DT_MAX_NS / _LADDER_DT_MIN_NS) ** (1.0 / (_LADDER_N - 1))
    return [_LADDER_DT_MIN_NS * (ratio ** i) for i in range(_LADDER_N)]


# ---------------------------------------------------------------------------
# Testbench builder
# ---------------------------------------------------------------------------

def _node(port: str) -> str:
    """Map macro port name (with brackets) to ngspice flat node name."""
    return port.replace("[", "_").replace("]", "").lower()


def _macro_port_list(rows: int, cols: int) -> list[str]:
    """Macro .subckt port order — must match cim_spice_generator output.

    Canonical order (from cim_spice_generator.py:99-102):
      MWL_EN[0..r-1] | MBL_PRE | VREF | VBIAS | MBL_OUT[0..c-1]
      | BL_* | BLB_* | WL_* | MWL_* | MBL_* | VPWR | VGND
    """
    ports: list[str] = []
    ports += [f"MWL_EN[{i}]" for i in range(rows)]
    ports += ["MBL_PRE", "VREF", "VBIAS"]
    ports += [f"MBL_OUT[{i}]" for i in range(cols)]
    ports += [f"BL_{i}" for i in range(cols)]
    ports += [f"BLB_{i}" for i in range(cols)]
    ports += [f"WL_{i}" for i in range(rows)]
    ports += [f"MWL_{i}" for i in range(rows)]
    ports += [f"MBL_{i}" for i in range(cols)]
    ports += ["VPWR", "VGND"]
    return ports


def _emit_write_phase(
    cfg: SimConfig,
    p: CIMMacroParams,
    weights: list[list[int]],
) -> tuple[list[str], float]:
    """Emit pattern-aware grouped WRITE phase. Returns (lines, t_end_ns).

    Groups rows by column pattern; emits one WL pulse per group with BL/BLB
    set to that group's column pattern.  WLs in the same group fire in the
    same pulse window.
    """
    groups = group_rows_by_col_pattern(weights)

    rows = p.rows
    cols = p.cols
    vpwr = cfg.corner.vpwr

    # Per-group timing (ns):
    #   t_start = group start
    #   BL/BLB step at t_start
    #   WL ramp:   t_start+0.10 → t_start+0.15 to vpwr  (50 ps rise)
    #              hold to       t_start+1.15
    #              fall          t_start+1.20         (50 ps fall)
    GROUP_PERIOD_NS = 1.50
    BL_STEP_NS = 0.05
    BL_STEP_DT_NS = 0.001        # 1 ps PWL "step" width to keep transitions sharp
    WL_RISE_START_NS = 0.10
    WL_RISE_END_NS = 0.15
    WL_FALL_START_NS = 1.15
    WL_FALL_END_NS = 1.20

    # Initialise PWL trains.  WL/BL/BLB all start at 0/0/0 at t=0; first
    # group transition seeds the desired Q for that group.
    wl_pwl: dict[int, list[tuple[float, float]]] = {
        r: [(0.0, 0.0)] for r in range(rows)
    }
    bl_pwl: dict[int, list[tuple[float, float]]] = {
        c: [(0.0, 0.0)] for c in range(cols)
    }
    blb_pwl: dict[int, list[tuple[float, float]]] = {
        c: [(0.0, 0.0)] for c in range(cols)
    }

    t_group = 1.0  # first group starts 1 ns in (DC settle margin)

    for col_pattern, row_list in groups:
        # Step BL/BLB to the column pattern's drive levels.
        # Q=1 → BL=Vpwr, BLB=0;  Q=0 → BL=0, BLB=Vpwr.
        # Emit a hold-then-step pair only when the value actually changes;
        # otherwise PWL would linearly ramp across the group boundary.
        t_step = t_group + BL_STEP_NS
        t_hold = t_step - BL_STEP_DT_NS
        for c in range(cols):
            bl_target = vpwr if col_pattern[c] == 1 else 0.0
            blb_target = 0.0 if col_pattern[c] == 1 else vpwr
            prev_bl = bl_pwl[c][-1][1]
            prev_blb = blb_pwl[c][-1][1]
            if bl_target != prev_bl:
                bl_pwl[c].append((t_hold, prev_bl))
                bl_pwl[c].append((t_step, bl_target))
            if blb_target != prev_blb:
                blb_pwl[c].append((t_hold, prev_blb))
                blb_pwl[c].append((t_step, blb_target))

        # Pulse WL for every row in this group, simultaneously.
        for r in row_list:
            wl_pwl[r].append((t_group + WL_RISE_START_NS, 0.0))
            wl_pwl[r].append((t_group + WL_RISE_END_NS, vpwr))
            wl_pwl[r].append((t_group + WL_FALL_START_NS, vpwr))
            wl_pwl[r].append((t_group + WL_FALL_END_NS, 0.0))

        t_group += GROUP_PERIOD_NS

    # After last group, ensure WL is back at 0 and hold BL/BLB at last
    # state until precharge starts.  No additional segments needed —
    # PWL holds the last value to infinity.
    t_write_end_ns = t_group + 0.5  # 500 ps margin after last group

    lines: list[str] = []
    lines.append(
        f"* === Phase 1: WRITE ({cfg.pattern}, {len(groups)} group{'' if len(groups) == 1 else 's'}, "
        f"{t_write_end_ns:.2f} ns) ==="
    )
    for r in range(rows):
        pwl = " ".join(f"{t:.3f}n {v:.4f}" for t, v in wl_pwl[r])
        lines.append(f"Vwl{r} wl_{r} 0 PWL({pwl})")
    for c in range(cols):
        pwl = " ".join(f"{t:.3f}n {v:.4f}" for t, v in bl_pwl[c])
        lines.append(f"Vbl{c} bl_{c} 0 PWL({pwl})")
    for c in range(cols):
        pwl = " ".join(f"{t:.3f}n {v:.4f}" for t, v in blb_pwl[c])
        lines.append(f"Vblb{c} blb_{c} 0 PWL({pwl})")

    return lines, t_write_end_ns


def _emit_compute_phase(
    cfg: SimConfig,
    p: CIMMacroParams,
    t_compute_start_ns: float,
) -> tuple[list[str], float]:
    """Emit PRECHARGE+COMPUTE phase. Returns (lines, t_compute_edge_ns).

    MBL_PRE is ACTIVE LOW: cim_mbl_precharge gates a single PFET with
    G=MBL_PRE, S=MBL, D=VREF.  PFET conducts when MBL_PRE is low,
    pulling MBL to VREF.  So MBL_PRE = 0 during precharge phase (PFET
    on, MBL = VREF) and rises to Vpwr at t_edge (PFET off, MBL floats,
    charge-sharing with MIM caps via T7 produces the compute output).

    MWL_EN[r] for r in active_rows rises at t_edge with the configured
    slew, simultaneous with MBL_PRE rising.
    """
    vpwr = cfg.corner.vpwr
    rise_ns_pre = 0.05                # MBL_PRE de-assert edge
    rise_ns_mwl = cfg.slew_ps / 1000.0  # ps → ns

    t_edge = t_compute_start_ns
    t_pre_high = t_edge + rise_ns_pre
    t_mwl_high = t_edge + rise_ns_mwl

    lines: list[str] = []
    lines.append(
        f"* === Phase 2/3: PRECHARGE+COMPUTE (edge @ {t_edge:.2f} ns, "
        f"slew {cfg.slew_ps:.0f} ps, active rows {list(cfg.active_rows)}) ==="
    )
    lines.append(
        f"Vmpre mbl_pre 0 PWL(0 0 {t_edge:.3f}n 0 "
        f"{t_pre_high:.3f}n {vpwr:.4f})"
    )
    for r in range(p.rows):
        if r in cfg.active_rows:
            lines.append(
                f"Vmwl{r} mwl_en_{r} 0 PWL(0 0 {t_edge:.3f}n 0 "
                f"{t_mwl_high:.3f}n {vpwr:.4f})"
            )
        else:
            lines.append(f"Vmwl{r} mwl_en_{r} 0 0")
    return lines, t_edge


def _build_testbench(cfg: SimConfig, work: Path) -> tuple[Path, dict]:
    """Write the ngspice testbench and return (path, meta)."""
    p = CIMMacroParams.from_variant(cfg.variant)
    macro = p.top_cell_name
    macro_sp = (Path("output/cim_macros") / macro / f"{macro}.sp").resolve()
    if not macro_sp.exists():
        raise FileNotFoundError(
            f"missing {macro_sp} — run scripts/generate_cim_production.py"
        )

    weights = PATTERNS[cfg.pattern](p.rows, p.cols, cfg.seed)

    lines: list[str] = []
    lines.append(f"* CIM characterisation testbench")
    lines.append(f"* macro={macro} pattern={cfg.pattern} corner={cfg.corner.slug}")
    lines.append(f"* slew={cfg.slew_ps:.0f}ps load={cfg.load_fF:.1f}fF "
                 f"active_rows={list(cfg.active_rows)} seed={cfg.seed}")
    lines.append(f".lib {_PDK_NGSPICE} {cfg.corner.model}")
    lines.append(f".include {macro_sp}")
    lines.append(f".temp {cfg.corner.temp_c}")
    lines.append("")
    lines.append("* --- Supplies ---")
    lines.append(f"Vpwr   vpwr   0 {cfg.corner.vpwr}")
    lines.append("Vgnd   vgnd   0 0")
    lines.append(f"Vvref  vref   0 {cfg.vref}")
    lines.append(f"Vvbias vbias  0 {cfg.vbias}")
    lines.append("")

    write_lines, t_write_end_ns = _emit_write_phase(cfg, p, weights)
    lines += write_lines
    lines.append("")

    # Precharge holds from t=0 through t_compute_start.  Pick t_compute_start
    # as a small margin past write end so MBL has settled to vpwr-precharge.
    t_compute_start_ns = t_write_end_ns + 0.5

    compute_lines, t_edge_ns = _emit_compute_phase(
        cfg, p, t_compute_start_ns
    )
    lines += compute_lines
    lines.append("")

    # High-Z ties on internal MWL_<r> and MBL_<c> ports so ngspice has a
    # node and singular-matrix warnings don't surface.  The buf_2 inside
    # the macro dominates these nets at ~5 kΩ; 1 MΩ tie is a no-op.
    lines.append("* --- Internal-port high-Z ties ---")
    for r in range(p.rows):
        lines.append(f"Rmwl{r}_tie mwl_{r} 0 1Meg")
    for c in range(p.cols):
        lines.append(f"Rmbl{c}_tie mbl_{c} 0 1Meg")
    lines.append("")

    # MBL_OUT loads — per-column capacitor with the configured load.
    lines.append(
        f"* --- MBL_OUT loads (load_fF={cfg.load_fF:.2f} per column) ---"
    )
    for c in range(p.cols):
        lines.append(f"Cmblo{c} mbl_out_{c} 0 {cfg.load_fF}f")
    lines.append("")

    # DUT instantiation.
    ports = _macro_port_list(p.rows, p.cols)
    inst_nodes = " ".join(_node(port) for port in ports)
    lines.append(f"Xdut {inst_nodes} {macro}")
    lines.append("")

    # Transient analysis — runs until 25 ns past compute edge.
    t_end_ns = t_edge_ns + _LADDER_DT_MAX_NS
    lines.append("* --- Analyses ---")
    lines.append(f".tran 0.05n {t_end_ns:.3f}n")
    lines.append("")

    # V_quiescent: sample MBL_OUT just before the compute edge.
    # V_compute:   sample MBL_OUT at end of sim (settled).
    # Sample ladder: 30 log-spaced FIND AT= points after the edge so the
    # parser can reconstruct the trace and interpolate t_settle.
    t_quiescent_ns = t_edge_ns - 0.05
    t_compute_ns = t_end_ns - 0.05
    offsets = _ladder_offsets_ns()

    lines.append("* --- Measurements per measure_col ---")
    for c in cfg.measure_cols:
        lines.append(
            f".measure tran v_quiescent_{c} FIND v(mbl_out_{c}) "
            f"AT={t_quiescent_ns:.4f}n"
        )
        lines.append(
            f".measure tran v_compute_{c} FIND v(mbl_out_{c}) "
            f"AT={t_compute_ns:.4f}n"
        )
        for i, dt in enumerate(offsets):
            t_sample = t_edge_ns + dt
            lines.append(
                f".measure tran v_at_{c}_{i:02d} FIND v(mbl_out_{c}) "
                f"AT={t_sample:.4f}n"
            )

    # Optional diagnostic: probe internal cell nodes for measure_cols.
    # Path is xbc_0_<c>.<internal_net>.  Internal net names come from the
    # extracted .ext.spice and mostly look like a_<x>_<y>#.
    if cfg.probe_internals:
        lines.append("* --- Internal-node probes (diagnostic) ---")
        # Sample at: t_quiescent (pre-edge) and at every 5th ladder point
        sample_times_ns = [t_quiescent_ns] + [
            t_edge_ns + offsets[i] for i in range(0, len(offsets), 5)
        ]
        cell_nodes = [
            "a_36_272#",   # bitcell internal — Q candidate (gate of X6 PMOS)
            "a_36_372#",   # bitcell internal — QB candidate (gate of X1 PMOS)
            "a_36_164#",   # T7 source (= MBL when T7 on)
            "a_62_616#",   # MIM cap bottom plate (= weight node)
        ]
        for c in cfg.measure_cols:
            for ni, node in enumerate(cell_nodes):
                node_safe = node.replace("#", "_h").replace(".", "_")
                node_path = f"xbc_0_{c}.{node}"
                for ti, t in enumerate(sample_times_ns):
                    lines.append(
                        f".measure tran probe_{c}_{ni}_{ti} FIND "
                        f"v({node_path}) AT={t:.4f}n"
                    )
            # Also probe MBL[c] (the analog node before the source follower).
            for ti, t in enumerate(sample_times_ns):
                lines.append(
                    f".measure tran probe_mbl_{c}_{ti} FIND v(mbl_{c}) "
                    f"AT={t:.4f}n"
                )

    lines.append(".end")

    tb = work / "tb.sp"
    tb.write_text("\n".join(lines))
    meta = {
        "groups": len(group_rows_by_col_pattern(weights)),
        "t_write_end_ns": t_write_end_ns,
        "t_edge_ns": t_edge_ns,
        "t_end_ns": t_end_ns,
        "ladder_offsets_ns": offsets,
    }
    return tb, meta


# ---------------------------------------------------------------------------
# ngspice runner
# ---------------------------------------------------------------------------

def _run_ngspice(
    tb: Path, work: Path, timeout: int = 14400,
) -> tuple[bool, str, float]:
    """Run ngspice on `tb`. Returns (ok, stdout, wallclock_seconds).

    Default 4 hr timeout — covers SRAM-A/B (256-row, 16k+ cells) under
    super-linear ngspice solver scaling with margin.  Empirical SRAM-D
    (64×64) wallclock is ~28 min; linear scaling × 4 = 112 min, but
    BSIM4 model evaluation + sparse-matrix factorisation on 16k-cell
    macros tends super-linear in practice.  4 hr leaves headroom for
    a 1.5× super-linear factor without paying the operational cost of
    a multi-day hung-sim wait.
    """
    log = work / "ngspice.log"
    t0 = time.time()
    res = subprocess.run(
        ["ngspice", "-b", str(tb.resolve())],
        capture_output=True, text=True, timeout=timeout, cwd=str(work),
    )
    wall = time.time() - t0
    log.write_text(res.stdout + "\n--- STDERR ---\n" + res.stderr)
    return (res.returncode == 0, res.stdout, wall)


# ---------------------------------------------------------------------------
# Result parsing
# ---------------------------------------------------------------------------

_MEAS_PAT = re.compile(
    r"^\s*(v_quiescent_\d+|v_compute_\d+|v_at_\d+_\d+|probe_\w+_\d+_\d+|probe_mbl_\d+_\d+)\s*=\s*([0-9.eE+\-]+)",
    re.M,
)


def _parse_measurements(
    stdout: str, cfg: SimConfig, t_edge_ns: float, offsets: list[float],
) -> dict:
    """Extract per-column V_q / V_c / sample trace and interpolate t_settle."""
    raw: dict[str, float] = {}
    for m in _MEAS_PAT.finditer(stdout):
        raw[m.group(1)] = float(m.group(2))

    per_col: dict[str, dict] = {}
    flags: list[str] = []
    for c in cfg.measure_cols:
        vq = raw.get(f"v_quiescent_{c}")
        vc = raw.get(f"v_compute_{c}")
        if vq is None or vc is None:
            per_col[str(c)] = {"v_quiescent": None, "v_compute": None,
                               "v_swing": None, "t_settle_ns": None,
                               "samples": []}
            continue

        v_swing = vc - vq
        v_mid = 0.5 * (vq + vc)

        # Reconstruct sample trace.
        samples: list[tuple[float, float]] = []
        for i, dt in enumerate(offsets):
            v = raw.get(f"v_at_{c}_{i:02d}")
            if v is None:
                continue
            samples.append((t_edge_ns + dt, v))

        # Interpolate t_settle: first sample whose value crosses v_mid
        # (in either direction depending on sign of v_swing).  Linear
        # interpolation between the bracket samples.
        t_settle_ns: float | None = None
        for k in range(1, len(samples)):
            t0, v0 = samples[k - 1]
            t1, v1 = samples[k]
            if (v0 - v_mid) * (v1 - v_mid) <= 0 and v1 != v0:
                frac = (v_mid - v0) / (v1 - v0)
                t_settle_ns = t0 + frac * (t1 - t0) - t_edge_ns
                break

        if abs(v_swing) < RESPIN_V_SWING_FLOOR:
            flags.append(f"RESPIN_LOW_SWING_col{c}")

        per_col[str(c)] = {
            "v_quiescent": vq,
            "v_compute": vc,
            "v_swing": v_swing,
            "t_settle_ns": t_settle_ns,
            "samples": samples,
        }

    return {"per_col": per_col, "flags": flags}


# ---------------------------------------------------------------------------
# Output writing — atomic JSON
# ---------------------------------------------------------------------------

def _atomic_write_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(payload, indent=2, sort_keys=True))
    os.replace(tmp, path)


def _config_to_dict(cfg: SimConfig) -> dict:
    return {
        "pattern": cfg.pattern,
        "corner": {
            "model": cfg.corner.model,
            "vpwr": cfg.corner.vpwr,
            "temp_c": cfg.corner.temp_c,
        },
        "slew_ps": cfg.slew_ps,
        "load_fF": cfg.load_fF,
        "active_rows": list(cfg.active_rows),
        "measure_cols": list(cfg.measure_cols),
        "seed": cfg.seed,
        "vbias": cfg.vbias,
        "vref": cfg.vref,
        "probe_internals": cfg.probe_internals,
    }


# ---------------------------------------------------------------------------
# Single-point characterisation
# ---------------------------------------------------------------------------

def char_one(cfg: SimConfig) -> dict:
    p = CIMMacroParams.from_variant(cfg.variant)
    work = _OUT_ROOT / p.top_cell_name / cfg.slug
    work.mkdir(parents=True, exist_ok=True)
    tb, meta = _build_testbench(cfg, work)
    print(f"[{cfg.variant}] {cfg.slug} → ngspice ...", flush=True)
    ok, stdout, wall = _run_ngspice(tb, work)
    json_path = work / "result.json"

    if not ok:
        payload = {
            "schema_version": SCHEMA_VERSION,
            "variant": cfg.variant,
            "config": _config_to_dict(cfg),
            "result": {"ok": False, "flags": ["NGSPICE_FAIL"]},
            "tb_path": str(tb),
            "log_path": str(work / "ngspice.log"),
            "wallclock_s": wall,
        }
        _atomic_write_json(json_path, payload)
        return payload

    parsed = _parse_measurements(
        stdout, cfg, meta["t_edge_ns"], meta["ladder_offsets_ns"]
    )

    # Reshape per-col into separate top-level dicts for easy consumption.
    v_q = {c: d["v_quiescent"] for c, d in parsed["per_col"].items()}
    v_c = {c: d["v_compute"] for c, d in parsed["per_col"].items()}
    v_s = {c: d["v_swing"] for c, d in parsed["per_col"].items()}
    t_s = {c: d["t_settle_ns"] for c, d in parsed["per_col"].items()}
    samp = {c: d["samples"] for c, d in parsed["per_col"].items()}

    payload = {
        "schema_version": SCHEMA_VERSION,
        "variant": cfg.variant,
        "config": _config_to_dict(cfg),
        "result": {
            "ok": True,
            "v_quiescent": v_q,
            "v_compute": v_c,
            "v_swing": v_s,
            "t_settle_ns": t_s,
            "samples": samp,
            "flags": parsed["flags"],
            "meta": {
                "groups": meta["groups"],
                "t_write_end_ns": meta["t_write_end_ns"],
                "t_edge_ns": meta["t_edge_ns"],
                "t_end_ns": meta["t_end_ns"],
            },
        },
        "tb_path": str(tb),
        "log_path": str(work / "ngspice.log"),
        "wallclock_s": wall,
    }
    _atomic_write_json(json_path, payload)
    return payload


# ---------------------------------------------------------------------------
# Multi-point sweeps
# ---------------------------------------------------------------------------

def _pattern_sweep_configs(variant: str) -> list[SimConfig]:
    """Phase-1 plan: bound MBL swing range across plausible weight patterns
    at TT/25 with default slew/load.  Single active row (row 0)."""
    return [
        SimConfig(variant=variant, pattern=p, corner=CORNER_TT_25)
        for p in ("all_zero", "all_one", "alt_col", "random")
    ]


def _nldm_grid_configs(
    variant: str, pattern: str, corner: Corner,
) -> list[SimConfig]:
    """4 input slews × 4 output loads = 16 sims per (pattern, corner)."""
    slews_ps = [50.0, 100.0, 200.0, 500.0]
    loads_fF = [1.0, 5.0, 20.0, 100.0]
    return [
        SimConfig(variant=variant, pattern=pattern, corner=corner,
                  slew_ps=s, load_fF=l)
        for s in slews_ps for l in loads_fF
    ]


def _bias_sweep_configs(variant: str) -> list[SimConfig]:
    """3x3 VBIAS/VREF sensitivity grid at TT/25 (audit deliverable for
    macro→chip-top contract numbers)."""
    vbias_pts = [0.65, 0.70, 0.75]
    vref_pts = [1.45, 1.50, 1.55]
    return [
        SimConfig(variant=variant, pattern="all_one", corner=CORNER_TT_25,
                  vbias=vb, vref=vr)
        for vb in vbias_pts for vr in vref_pts
    ]


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _parse_active_rows(s: str, rows: int) -> tuple[int, ...]:
    if s == "all":
        return tuple(range(rows))
    return tuple(int(x) for x in s.split(","))


def _parse_measure_cols(s: str, cols: int) -> tuple[int, ...]:
    if s == "all":
        return tuple(range(cols))
    if s == "edges_mid":
        return (0, cols // 2, cols - 1)
    return tuple(int(x) for x in s.split(","))


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("variants", nargs="+", help="Variants (e.g. SRAM-D)")
    ap.add_argument("--pattern", default="all_one",
                    choices=sorted(PATTERNS.keys()))
    ap.add_argument("--seed", type=int, default=0,
                    help="PRNG seed for random pattern")
    ap.add_argument("--corner", default="tt_25",
                    choices=sorted(CORNER_BY_NAME.keys()))
    ap.add_argument("--slew-ps", type=float, default=100.0)
    ap.add_argument("--load-ff", type=float, default=10.0)
    ap.add_argument("--active-rows", default="0",
                    help="comma list, or 'all'")
    ap.add_argument("--measure-cols", default="0",
                    help="comma list, 'all', or 'edges_mid'")
    ap.add_argument("--vbias", type=float, default=0.7)
    ap.add_argument("--vref", type=float, default=1.5)
    ap.add_argument("--pattern-sweep", action="store_true",
                    help="Run phase-1 pattern sweep (4 patterns at TT/25)")
    ap.add_argument("--nldm-grid", action="store_true",
                    help="Run 16-point NLDM grid (overnight)")
    ap.add_argument("--nldm-corners", default="tt_25,ss_cold,ff_hot",
                    help="Corners for --nldm-grid (comma list)")
    ap.add_argument("--bias-sweep", action="store_true",
                    help="Run 3x3 VBIAS/VREF sensitivity at TT/25")
    ap.add_argument("--probe-internals", action="store_true",
                    help="Diagnostic: probe internal cell nodes (Q/QB/MBL/MIM)")
    args = ap.parse_args(argv[1:])

    for v in args.variants:
        if v not in CIM_VARIANTS:
            raise SystemExit(
                f"Unknown variant {v!r}. Valid: {sorted(CIM_VARIANTS)}"
            )
    if not shutil.which("ngspice"):
        raise SystemExit("ngspice not on PATH")
    if not _PDK_NGSPICE.exists():
        raise SystemExit(f"sky130 ngspice models not at {_PDK_NGSPICE}")

    # Build the run list.
    configs: list[SimConfig] = []
    for variant in args.variants:
        p = CIMMacroParams.from_variant(variant)
        active_rows = _parse_active_rows(args.active_rows, p.rows)
        measure_cols = _parse_measure_cols(args.measure_cols, p.cols)

        if args.pattern_sweep:
            configs += [
                SimConfig(
                    variant=variant, pattern=cfg.pattern, corner=cfg.corner,
                    slew_ps=args.slew_ps, load_fF=args.load_ff,
                    active_rows=active_rows, measure_cols=measure_cols,
                    seed=args.seed, vbias=args.vbias, vref=args.vref,
                    probe_internals=args.probe_internals,
                )
                for cfg in _pattern_sweep_configs(variant)
            ]
        if args.nldm_grid:
            corners = [CORNER_BY_NAME[n] for n in args.nldm_corners.split(",")]
            for corner in corners:
                configs += [
                    SimConfig(
                        variant=variant, pattern=args.pattern, corner=corner,
                        slew_ps=cfg.slew_ps, load_fF=cfg.load_fF,
                        active_rows=active_rows, measure_cols=measure_cols,
                        seed=args.seed, vbias=args.vbias, vref=args.vref,
                        probe_internals=args.probe_internals,
                    )
                    for cfg in _nldm_grid_configs(variant, args.pattern, corner)
                ]
        if args.bias_sweep:
            configs += [
                SimConfig(
                    variant=variant, pattern="all_one", corner=CORNER_TT_25,
                    slew_ps=args.slew_ps, load_fF=args.load_ff,
                    active_rows=active_rows, measure_cols=measure_cols,
                    seed=args.seed, vbias=cfg.vbias, vref=cfg.vref,
                    probe_internals=args.probe_internals,
                )
                for cfg in _bias_sweep_configs(variant)
            ]
        if not (args.pattern_sweep or args.nldm_grid or args.bias_sweep):
            configs.append(SimConfig(
                variant=variant, pattern=args.pattern,
                corner=CORNER_BY_NAME[args.corner],
                slew_ps=args.slew_ps, load_fF=args.load_ff,
                active_rows=active_rows, measure_cols=measure_cols,
                seed=args.seed, vbias=args.vbias, vref=args.vref,
                probe_internals=args.probe_internals,
            ))

    print(f"Queue: {len(configs)} sim{'' if len(configs) == 1 else 's'}")
    results = [char_one(cfg) for cfg in configs]

    # Summary table.
    print("\n=== CIM SPICE Characterisation ===")
    print(f"{'variant':<10} {'pattern':<10} {'corner':<18} "
          f"{'slew_ps':>8} {'load_fF':>8} "
          f"{'V_q':>8} {'V_c':>8} {'swing':>8} {'t_set_ns':>10} {'wall_s':>8}  flags")
    any_respin = False
    for r in results:
        cfg = r["config"]
        corner = cfg["corner"]
        corner_str = f"{corner['model']}/{corner['vpwr']}/{corner['temp_c']}"
        if not r["result"]["ok"]:
            print(f"{r['variant']:<10} {cfg['pattern']:<10} {corner_str:<18} "
                  f"{'-':>8} {'-':>8} {'-':>8} {'-':>8} {'-':>8} {'-':>10} "
                  f"{r['wallclock_s']:>8.0f}  FAIL")
            continue
        # Pick measure_col 0 for the summary line (or first available).
        first_col = next(iter(r["result"]["v_quiescent"]))
        vq = r["result"]["v_quiescent"][first_col]
        vc = r["result"]["v_compute"][first_col]
        vs = r["result"]["v_swing"][first_col]
        ts = r["result"]["t_settle_ns"][first_col]
        flags = ",".join(r["result"]["flags"]) or "-"
        if "RESPIN" in flags:
            any_respin = True
        ts_str = f"{ts:.3f}" if ts is not None else "  --"
        print(f"{r['variant']:<10} {cfg['pattern']:<10} {corner_str:<18} "
              f"{cfg['slew_ps']:>8.0f} {cfg['load_fF']:>8.1f} "
              f"{vq:>8.4f} {vc:>8.4f} {vs:>8.4f} {ts_str:>10} "
              f"{r['wallclock_s']:>8.0f}  {flags}")

    if any_respin:
        print("\n*** RESPIN flag(s) raised — see audit plan stop conditions. ***")
    return 0 if all(r["result"]["ok"] for r in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
