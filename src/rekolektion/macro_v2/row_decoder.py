"""Hierarchical row decoder for v2 SRAM macros.

Parametric over `num_rows`. Splits the address bits into 2-3 predecoder
groups (each 2 or 3 bits wide) producing one-hot outputs, then a final
stage of NAND gates ANDs one predecoder output per stage to pick exactly
one WL per address.

Split table — chosen to minimise final-stage fan-in while keeping
predecoders at 2- or 3-input (no NAND4 predecoders; reserved for the
final stage at very large N).

All cells are foundry NAND_k from `sky130_fd_bd_sram__openram_sp_nand*_dec`.
Inverters (for NOT-addr lines) are constructed at routing time by tying
both inputs of a NAND2 together — no dedicated inverter cell in the
sky130_fd_bd_sram library.
"""
from __future__ import annotations

from pathlib import Path


# num_rows → (widths of each predecoder, in bits)
# Constraint: sum(widths) = log2(num_rows); each width in {2, 3};
#             len(widths) = final-stage fan-in (pick the lowest feasible)
_SPLIT_TABLE: dict[int, tuple[int, ...]] = {
    4: (2,),
    8: (3,),
    16: (2, 2),
    32: (2, 3),
    64: (3, 3),
    128: (2, 2, 3),
    256: (2, 3, 3),
    512: (3, 3, 3),
    1024: (2, 2, 3, 3),
}


# Foundry NAND cells (key = fan-in, value = cell name)
_NAND_CELL_NAMES: dict[int, str] = {
    2: "sky130_fd_bd_sram__openram_sp_nand2_dec",
    3: "sky130_fd_bd_sram__openram_sp_nand3_dec",
    4: "sky130_fd_bd_sram__openram_sp_nand4_dec",
}

_CELLS_DIR: Path = Path(__file__).parent.parent / "peripherals/cells"

_NAND_GDS_PATHS: dict[int, Path] = {
    k: _CELLS_DIR / f"{name}.gds" for k, name in _NAND_CELL_NAMES.items()
}


def num_addr_bits_for_rows(num_rows: int) -> int:
    """Return the number of address bits required to select `num_rows`."""
    if num_rows not in _SPLIT_TABLE:
        raise ValueError(
            f"num_rows {num_rows} not in split table; valid values: "
            f"{sorted(_SPLIT_TABLE.keys())}"
        )
    return sum(_SPLIT_TABLE[num_rows])
