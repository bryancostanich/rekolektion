"""Liberty (.lib) generator for v2 SRAM macros.

C8 scope: regenerate per-macro Liberty using the same analytical timing
model that rekolektion v1 used. Real 9-corner SPICE characterisation
is blocked by D3 (no transistor-level SPICE). Analytical timing
is not tapeout-grade but accurate enough for P&R to converge — which
is what this phase's exit gate actually requires.

Approach: build a thin v1 MacroParams adapter from v2 MacroV2Params,
then call the existing v1 `generate_liberty()`.
"""
from __future__ import annotations

import math
from pathlib import Path

from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
from rekolektion.macro.assembler import MacroParams as _V1MacroParams
from rekolektion.macro.liberty_generator import generate_liberty as _v1_generate
from rekolektion.macro_v2.assembler import MacroV2Params, build_floorplan


def _v2_to_v1(p: MacroV2Params) -> _V1MacroParams:
    """Build a v1-style MacroParams from v2 params + floorplan."""
    fp = build_floorplan(p)
    bc = load_foundry_sp_bitcell()
    macro_w, macro_h = fp.macro_size
    num_row_bits = int(math.log2(p.rows))
    num_col_bits = int(math.log2(p.mux_ratio))
    return _V1MacroParams(
        words=p.words,
        bits=p.bits,
        mux_ratio=p.mux_ratio,
        rows=p.rows,
        cols=p.cols,
        num_addr_bits=p.num_addr_bits,
        num_row_bits=num_row_bits,
        num_col_bits=num_col_bits,
        cell_name=bc.cell_name,
        cell_width=bc.cell_width,
        cell_height=bc.cell_height,
        macro_width=macro_w,
        macro_height=macro_h,
    )


def generate_liberty(
    p: MacroV2Params,
    output_path: str | Path,
    macro_name: str | None = None,
    *,
    uppercase_ports: bool = True,
) -> Path:
    """Write a Liberty (.lib) file for the v2 macro.

    Defaults to `uppercase_ports=True` to match the LEF emitted by
    `macro_v2.lef_generator.generate_lef` (which also defaults to
    uppercase to stay aligned with v1 OpenLane conventions).
    """
    v1_params = _v2_to_v1(p)
    return _v1_generate(
        v1_params,
        output_path,
        macro_name=macro_name or p.top_cell_name,
        uppercase_ports=uppercase_ports,
    )
