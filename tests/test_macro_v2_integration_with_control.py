"""Full-stack integration fixture: array + peripherals + decoder + control.

Exits Phase C5 with a rough-placed stack of all the v2 SRAM blocks —
proper wiring + floorplan comes in C6 (assembler). The goal is only to
confirm no block blows up DRC when placed together.
"""
import gdstk
import pytest

from rekolektion.macro_v2.bitcell_array import BitcellArray
from rekolektion.macro_v2.column_mux_row import ColumnMuxRow
from rekolektion.macro_v2.control_logic import ControlLogic
from rekolektion.macro_v2.precharge_row import PrechargeRow
from rekolektion.macro_v2.row_decoder import RowDecoder
from rekolektion.macro_v2.sense_amp_row import SenseAmpRow
from rekolektion.macro_v2.write_driver_row import WriteDriverRow


@pytest.mark.magic
def test_full_stack_mux4_integration_drc_bounded(tmp_path):
    """All v2 blocks (array + 4 peripheral rows + decoder + control)
    placed in one top cell, DRC < 1000 errors.

    Layout — rough, no routing yet:
      * x = 0  : row decoder (tall column) + control block (above it)
      * x ~ 40 : array column (peripherals below/above array)
    """
    from rekolektion.verify.drc import run_drc

    bits = 8
    mux = 4
    rows = 32
    cols = bits * mux

    array = BitcellArray(rows=rows, cols=cols, name="fs_array")
    prech = PrechargeRow(bits=bits, mux_ratio=mux, name="fs_pre")
    colmx = ColumnMuxRow(bits=bits, mux_ratio=mux, name="fs_mux")
    sa_r = SenseAmpRow(bits=bits, mux_ratio=mux, name="fs_sa")
    wd_r = WriteDriverRow(bits=bits, mux_ratio=mux, name="fs_wd")
    dec = RowDecoder(num_rows=rows, name="fs_dec")
    ctrl = ControlLogic(name="fs_ctrl")

    lib = gdstk.Library(name="fs_lib")
    top = gdstk.Cell("fs_top")

    seen: set[str] = set()
    for sub in (
        wd_r.build(), sa_r.build(), colmx.build(), array.build(),
        prech.build(), dec.build(), ctrl.build(),
    ):
        for c in sub.cells:
            if c.name in seen:
                continue
            lib.add(c.copy(c.name))
            seen.add(c.name)

    # Array column at x_array, peripherals stacked bottom→top.
    x_array = 40.0
    y = 0.0
    for row_obj in (wd_r, sa_r, colmx, array, prech):
        top.add(gdstk.Reference(
            next(c for c in lib.cells if c.name == row_obj.top_cell_name),
            origin=(x_array, y),
        ))
        y += row_obj.height + 0.5

    # Row decoder: left of the array, at y=0 so its NAND column aligns
    # with the array rows (proper WL routing happens in C6).
    top.add(gdstk.Reference(
        next(c for c in lib.cells if c.name == dec.top_cell_name),
        origin=(0.0, 0.0),
    ))

    # Control block: above the row decoder.
    top.add(gdstk.Reference(
        next(c for c in lib.cells if c.name == ctrl.top_cell_name),
        origin=(0.0, y + 2.0),
    ))

    lib.add(top)
    gds = tmp_path / "fs.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="fs_top", output_dir=tmp_path)
    # Waivers from foundry SRAM cells can be ~hundreds of thousands; the
    # signal is real (non-waiver) errors. <1000 reals here is a loose
    # upper bound on unrouted inter-block violations.
    assert result.real_error_count < 1000, (
        f"Too many REAL DRC errors ({result.real_error_count}); "
        f"likely a broken block. (waivers: {result.waiver_error_count})"
    )
    print(
        f"\nFull-stack DRC: real={result.real_error_count} "
        f"waivers={result.waiver_error_count}"
    )
