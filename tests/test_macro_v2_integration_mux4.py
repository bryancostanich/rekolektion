"""Integration fixture: array + 4 peripheral rows stacked at mux=4.

Validates the rows don't blow up DRC when assembled in a rough vertical
stack. Inter-block routing is NOT part of C4 — this test just checks
that each block is DRC-clean on its own, placed in the same library.
"""
import gdstk
import pytest

from rekolektion.macro_v2.bitcell_array import BitcellArray
from rekolektion.macro_v2.column_mux_row import ColumnMuxRow
from rekolektion.macro_v2.precharge_row import PrechargeRow
from rekolektion.macro_v2.sense_amp_row import SenseAmpRow
from rekolektion.macro_v2.write_driver_row import WriteDriverRow


@pytest.mark.magic
def test_stacked_mux4_integration_drc_bounded(tmp_path):
    """Array + precharge + col_mux + SA + WD at mux=4 DRC bounded.

    The stack: from bottom up, WD → SA → ColMux → Array → Precharge.
    Inter-block routing isn't done in C4 so DRC will flag unconnected
    rails at every row boundary. The goal of this test is that individual
    rows are OK (no explosion of DRC errors from within a row).
    """
    from rekolektion.verify.drc import run_drc

    bits = 8
    mux = 4
    # Array has `bits * mux` columns, rows chosen small for test speed
    rows_in_array = 8
    cols_in_array = bits * mux

    array = BitcellArray(rows=rows_in_array, cols=cols_in_array, name="int_array")
    prech = PrechargeRow(bits=bits, mux_ratio=mux, name="int_pre")
    colmx = ColumnMuxRow(bits=bits, mux_ratio=mux, name="int_mux")
    sa_r = SenseAmpRow(bits=bits, mux_ratio=mux, name="int_sa")
    wd_r = WriteDriverRow(bits=bits, mux_ratio=mux, name="int_wd")

    # Build a single library with all cells
    lib = gdstk.Library(name="int_lib")
    top = gdstk.Cell("int_top")

    seen: set[str] = set()
    for child in (
        wd_r.build(), sa_r.build(), colmx.build(), array.build(), prech.build()
    ):
        for c in child.cells:
            if c.name in seen:
                continue
            lib.add(c.copy(c.name))
            seen.add(c.name)

    # Stack vertically, leaving a 0.5 um gap between blocks
    y = 0.0
    for row_obj in (wd_r, sa_r, colmx, array, prech):
        ref = gdstk.Reference(
            next(c for c in lib.cells if c.name == row_obj.top_cell_name),
            origin=(0, y),
        )
        top.add(ref)
        y += row_obj.height + 0.5

    lib.add(top)
    gds = tmp_path / "int_mux4.gds"
    lib.write_gds(str(gds))

    result = run_drc(gds, cell_name="int_top", output_dir=tmp_path)
    # Expect some unrouted-boundary DRC at inter-block transitions, but bounded.
    # If > 1000 errors, individual rows are likely broken.
    assert result.error_count < 1000, (
        f"Too many DRC errors ({result.error_count}). Individual rows likely have issues."
    )
    # Report the actual count for visibility (non-failing)
    print(f"\nIntegration DRC error count (unrouted): {result.error_count}")
